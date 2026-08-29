using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using MySqlPunk.Core.Models;

namespace MySqlPunk.Core.Services;

public sealed class CrossPlatformUpdateService
{
    public const string DefaultOwner = "shadowjohn";
    public const string DefaultRepository = "mySQLPunk";
    private const int MaximumReleaseJsonCharacters = 2 * 1024 * 1024;
    private static readonly Regex VersionPattern = new(
        @"^\d+\.\d+\.\d+(\.\d+)?$",
        RegexOptions.CultureInvariant);
    private static readonly HttpClient SharedHttpClient = new();

    private readonly HttpClient _httpClient;

    public CrossPlatformUpdateService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? SharedHttpClient;
    }

    public async Task<CrossPlatformUpdateInfo> CheckLatestAsync(
        string currentVersion,
        string? runtimeIdentifier = null,
        CancellationToken cancellationToken = default)
    {
        var endpoint = new Uri(
            $"https://api.github.com/repos/{DefaultOwner}/{DefaultRepository}/releases/latest");
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.UserAgent.ParseAdd("mySQLPunk-cross-platform-update-check");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (json.Length > MaximumReleaseJsonCharacters)
        {
            throw new InvalidDataException("GitHub Release 回應超過安全大小限制。");
        }

        return ParseLatestRelease(
            json,
            currentVersion,
            runtimeIdentifier ?? ResolveCurrentRuntimeIdentifier());
    }

    public static CrossPlatformUpdateInfo ParseLatestRelease(
        string json,
        string currentVersion,
        string runtimeIdentifier)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException("GitHub Release JSON 不可為空白。", nameof(json));
        }

        var current = ParseVersion(currentVersion, nameof(currentVersion), out _);
        ValidateRuntimeIdentifier(runtimeIdentifier);

        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32
        });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("GitHub Release 回應不是 JSON object。");
        }

        var tagName = GetRequiredString(root, "tag_name");
        var latest = ParseVersion(tagName, "tag_name", out var latestVersionText);
        var releasePageUri = ParseTrustedRepositoryUri(GetRequiredString(root, "html_url"), "html_url");
        var releaseName = GetOptionalString(root, "name");
        if (string.IsNullOrWhiteSpace(releaseName))
        {
            releaseName = tagName;
        }
        releaseName = releaseName.Trim();
        if (releaseName.Length > 160)
        {
            releaseName = releaseName[..160];
        }

        var packageFileName = BuildPackageFileName(latestVersionText, runtimeIdentifier);
        Uri? packageDownloadUri = null;
        Uri? sha256DownloadUri = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var assetName = GetOptionalString(asset, "name");
                if (!string.Equals(assetName, packageFileName, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(assetName, packageFileName + ".sha256", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var downloadUrl = GetOptionalString(asset, "browser_download_url");
                var downloadUri = ParseTrustedRepositoryUri(downloadUrl, $"asset {assetName}");
                if (string.Equals(assetName, packageFileName, StringComparison.OrdinalIgnoreCase))
                {
                    packageDownloadUri = downloadUri;
                }
                else
                {
                    sha256DownloadUri = downloadUri;
                }
            }
        }

        return new CrossPlatformUpdateInfo
        {
            CurrentVersion = current,
            LatestVersion = latest,
            LatestVersionText = latestVersionText,
            ReleaseName = releaseName,
            ReleasePageUri = releasePageUri,
            RuntimeIdentifier = runtimeIdentifier,
            PackageFileName = packageFileName,
            PackageDownloadUri = packageDownloadUri,
            Sha256DownloadUri = sha256DownloadUri,
            IsPrerelease = root.TryGetProperty("prerelease", out var prerelease) &&
                           prerelease.ValueKind == JsonValueKind.True
        };
    }

    public static string ResolveCurrentRuntimeIdentifier()
    {
        var architecture = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException(
                $"目前不支援 {RuntimeInformation.OSArchitecture} CPU 架構的更新資產。")
        };

        if (OperatingSystem.IsLinux())
        {
            return $"linux-{architecture}";
        }
        if (OperatingSystem.IsMacOS())
        {
            return $"osx-{architecture}";
        }

        throw new PlatformNotSupportedException("跨平台預覽版更新檢查只支援 Linux 與 macOS。");
    }

    public static string BuildPackageFileName(string version, string runtimeIdentifier)
    {
        ParseVersion(version, nameof(version), out var normalizedVersion);
        ValidateRuntimeIdentifier(runtimeIdentifier);
        var suffix = runtimeIdentifier.StartsWith("linux-", StringComparison.Ordinal)
            ? ".tar.gz"
            : ".app.zip";
        return $"mySQLPunk-{normalizedVersion}-{runtimeIdentifier}{suffix}";
    }

    private static Version ParseVersion(string value, string fieldName, out string normalized)
    {
        normalized = (value ?? string.Empty).Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
        {
            normalized = normalized[1..];
        }
        if (!VersionPattern.IsMatch(normalized) || !Version.TryParse(normalized, out var version))
        {
            throw new InvalidDataException($"{fieldName} 不是有效版本號：{value}");
        }
        return version;
    }

    private static void ValidateRuntimeIdentifier(string runtimeIdentifier)
    {
        if (runtimeIdentifier is not ("linux-x64" or "linux-arm64" or "osx-x64" or "osx-arm64"))
        {
            throw new PlatformNotSupportedException($"沒有 {runtimeIdentifier} 的更新資產。");
        }
    }

    private static Uri ParseTrustedRepositoryUri(string value, string fieldName)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.StartsWith(
                $"/{DefaultOwner}/{DefaultRepository}/",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{fieldName} 不是受信任的 mySQLPunk GitHub HTTPS URL。");
        }
        return uri;
    }

    private static string GetRequiredString(JsonElement element, string propertyName)
    {
        var value = GetOptionalString(element, propertyName);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"GitHub Release 缺少 {propertyName}。")
            : value;
    }

    private static string GetOptionalString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
}
