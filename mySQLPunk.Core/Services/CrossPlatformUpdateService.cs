using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MySqlPunk.Core.Models;

namespace MySqlPunk.Core.Services;

public sealed class CrossPlatformUpdateService
{
    public const string DefaultOwner = "shadowjohn";
    public const string DefaultRepository = "mySQLPunk";
    private const int MaximumReleaseJsonCharacters = 2 * 1024 * 1024;
    private const int MaximumChecksumBytes = 4 * 1024;
    private const long MaximumPackageBytes = 512L * 1024 * 1024;
    private static readonly Regex VersionPattern = new(
        @"^\d+\.\d+\.\d+(\.\d+)?$",
        RegexOptions.CultureInvariant);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
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

    public async Task<CrossPlatformUpdateDownload> DownloadPackageAsync(
        CrossPlatformUpdateInfo update,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (!Path.IsPathFullyQualified(destinationPath))
        {
            throw new ArgumentException("更新下載位置必須是完整路徑。", nameof(destinationPath));
        }

        var expectedPackageName = BuildPackageFileName(update.LatestVersionText, update.RuntimeIdentifier);
        if (!string.Equals(update.PackageFileName, expectedPackageName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("更新資產名稱與版本／平台不一致。");
        }

        var packageUri = update.PackageDownloadUri is null
            ? throw new InvalidOperationException("Release 沒有目前平台的安裝包。")
            : ParseTrustedRepositoryUri(update.PackageDownloadUri.AbsoluteUri, "package URL");
        var checksumUri = update.Sha256DownloadUri is null
            ? throw new InvalidOperationException("Release 沒有目前平台安裝包的 SHA-256。")
            : ParseTrustedRepositoryUri(update.Sha256DownloadUri.AbsoluteUri, "SHA-256 URL");
        ValidateAssetUriFileName(packageUri, expectedPackageName);
        ValidateAssetUriFileName(checksumUri, expectedPackageName + ".sha256");

        var checksumBytes = await DownloadSmallFileAsync(
            checksumUri,
            MaximumChecksumBytes,
            cancellationToken).ConfigureAwait(false);
        var expectedHash = ParseChecksumSidecar(checksumBytes, expectedPackageName);

        var destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            throw new ArgumentException("無法定位更新下載目錄。", nameof(destinationPath));
        }
        Directory.CreateDirectory(destinationDirectory);
        var temporaryPath = Path.Combine(
            destinationDirectory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            var (bytes, actualHash) = await DownloadPackageFileAsync(
                packageUri,
                temporaryPath,
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(expectedHash, actualHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"下載檔案 SHA-256 不符；預期 {expectedHash}，實際 {actualHash}。既有檔案未被覆蓋。");
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destinationPath, true);
            return new CrossPlatformUpdateDownload(destinationPath, bytes, actualHash);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
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

    public static string ParseChecksumSidecar(ReadOnlySpan<byte> contents, string expectedPackageName)
    {
        if (contents.IsEmpty || contents.Length > MaximumChecksumBytes)
        {
            throw new InvalidDataException("SHA-256 sidecar 為空或超過安全大小限制。");
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(contents).Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("SHA-256 sidecar 不是有效 UTF-8。", exception);
        }
        if (text.Contains('\n'))
        {
            throw new InvalidDataException("SHA-256 sidecar 必須只包含一筆檔案雜湊。");
        }

        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !Regex.IsMatch(parts[0], "^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant))
        {
            throw new InvalidDataException("SHA-256 sidecar 格式不正確。");
        }

        var sidecarFileName = parts[1].StartsWith('*') ? parts[1][1..] : parts[1];
        if (!string.Equals(sidecarFileName, expectedPackageName, StringComparison.Ordinal) ||
            !string.Equals(Path.GetFileName(sidecarFileName), sidecarFileName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("SHA-256 sidecar 指向非預期的安裝包名稱。");
        }
        return parts[0].ToLowerInvariant();
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

    private async Task<byte[]> DownloadSmallFileAsync(
        Uri uri,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var response = await SendDownloadRequestAsync(uri, cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is > 0 && response.Content.Headers.ContentLength > maximumBytes)
        {
            throw new InvalidDataException("下載內容超過安全大小限制。");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            if (output.Length + read > maximumBytes)
            {
                throw new InvalidDataException("下載內容超過安全大小限制。");
            }
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private async Task<(long Bytes, string Sha256)> DownloadPackageFileAsync(
        Uri uri,
        string temporaryPath,
        CancellationToken cancellationToken)
    {
        using var response = await SendDownloadRequestAsync(uri, cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is > MaximumPackageBytes)
        {
            throw new InvalidDataException("更新安裝包超過 512 MiB 安全大小限制。");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        long totalBytes = 0;
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                totalBytes = checked(totalBytes + read);
                if (totalBytes > MaximumPackageBytes)
                {
                    throw new InvalidDataException("更新安裝包超過 512 MiB 安全大小限制。");
                }
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (totalBytes == 0)
        {
            throw new InvalidDataException("更新安裝包不可為空檔案。");
        }
        return (totalBytes, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private async Task<HttpResponseMessage> SendDownloadRequestAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("mySQLPunk-cross-platform-update-download");
        var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        try
        {
            response.EnsureSuccessStatusCode();
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private static void ValidateAssetUriFileName(Uri uri, string expectedFileName)
    {
        var actualFileName = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
        if (!string.Equals(actualFileName, expectedFileName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("更新資產 URL 的檔名與 Release metadata 不一致。");
        }
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
