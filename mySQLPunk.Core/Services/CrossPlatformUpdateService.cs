using System.Buffers;
using System.Diagnostics;
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
    private const int MaximumApplyResultBytes = 4 * 1024;
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

    public ProcessStartInfo BuildLinuxApplyStartInfo(
        CrossPlatformUpdateInfo update,
        CrossPlatformUpdateDownload download,
        string applyScriptPath,
        int processId,
        string? lockToken = null)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(download);
        if (!update.UpdateAvailable)
        {
            throw new InvalidOperationException("指定的 Release 並不是較新的版本。");
        }
        if (!update.RuntimeIdentifier.StartsWith("linux-", StringComparison.Ordinal) ||
            update.RuntimeIdentifier is not ("linux-x64" or "linux-arm64"))
        {
            throw new PlatformNotSupportedException("安全自動套用目前只支援 Linux x64 與 ARM64。");
        }
        if (!Path.IsPathFullyQualified(download.Path) || !File.Exists(download.Path))
        {
            throw new FileNotFoundException("找不到已驗證的 Linux 更新安裝包。", download.Path);
        }
        if (!Regex.IsMatch(download.Sha256, "^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant))
        {
            throw new InvalidDataException("Linux 更新安裝包的 SHA-256 格式不正確。");
        }
        if (!Path.IsPathFullyQualified(applyScriptPath) || !File.Exists(applyScriptPath))
        {
            throw new FileNotFoundException("目前安裝內容缺少 Linux 安全更新腳本。", applyScriptPath);
        }
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId), "等待的程序識別碼必須大於零。");
        }
        if (lockToken is not null &&
            !Regex.IsMatch(lockToken, "^[0-9a-f]{32}$", RegexOptions.CultureInvariant))
        {
            throw new ArgumentException("Linux 更新 lock token 格式不正確。", nameof(lockToken));
        }

        var expectedPackageName = BuildPackageFileName(
            update.LatestVersionText,
            update.RuntimeIdentifier);
        if (!string.Equals(update.PackageFileName, expectedPackageName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("更新資產名稱與 Linux 套用參數不一致。");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = applyScriptPath,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
                 {
                     "--archive", download.Path,
                     "--sha256", download.Sha256.ToLowerInvariant(),
                     "--version", update.LatestVersionText,
                     "--runtime", update.RuntimeIdentifier,
                     "--wait-pid", processId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }
        if (lockToken is not null)
        {
            startInfo.ArgumentList.Add("--lock-token");
            startInfo.ArgumentList.Add(lockToken);
        }
        return startInfo;
    }

    public Process StartLinuxApply(
        CrossPlatformUpdateInfo update,
        CrossPlatformUpdateDownload download,
        string applyScriptPath,
        int processId)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Linux 安全更新程序只能在 Linux 上啟動。");
        }
        var currentRuntime = ResolveCurrentRuntimeIdentifier();
        if (!string.Equals(update.RuntimeIdentifier, currentRuntime, StringComparison.Ordinal))
        {
            throw new PlatformNotSupportedException(
                $"更新 RID {update.RuntimeIdentifier} 與目前平台 {currentRuntime} 不一致。");
        }

        var lockToken = Guid.NewGuid().ToString("N");
        var lockPath = ResolveLinuxApplyLockPath();
        AcquireLinuxApplyLock(lockPath, lockToken, processId);
        try
        {
            var startInfo = BuildLinuxApplyStartInfo(
                update,
                download,
                applyScriptPath,
                processId,
                lockToken);
            return Process.Start(startInfo) ??
                   throw new InvalidOperationException("無法啟動 Linux 安全更新程序。");
        }
        catch
        {
            ReleaseLinuxApplyLock(lockPath, lockToken);
            throw;
        }
    }

    public LinuxUpdateApplyResult? ReadAndClearLinuxApplyResult(string? resultPath = null)
    {
        resultPath ??= ResolveLinuxApplyResultPath();
        if (!Path.IsPathFullyQualified(resultPath))
        {
            throw new ArgumentException("Linux 更新結果位置必須是完整路徑。", nameof(resultPath));
        }
        if (!File.Exists(resultPath))
        {
            return null;
        }

        try
        {
            var resultLength = new FileInfo(resultPath).Length;
            if (resultLength is <= 0 or > MaximumApplyResultBytes)
            {
                throw new InvalidDataException("Linux 更新結果為空或超過安全大小限制。");
            }
            var bytes = File.ReadAllBytes(resultPath);
            if (bytes.Length != resultLength || bytes.Length > MaximumApplyResultBytes)
            {
                throw new InvalidDataException("Linux 更新結果在讀取時發生變更。");
            }
            string text;
            try
            {
                text = StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException("Linux 更新結果不是有效 UTF-8。", exception);
            }
            return ParseLinuxApplyResult(text);
        }
        finally
        {
            try
            {
                File.Delete(resultPath);
            }
            catch (IOException)
            {
                // A stale result is less harmful than hiding the original update failure.
            }
            catch (UnauthorizedAccessException)
            {
                // The caller can still show the parsed failure for this launch.
            }
        }
    }

    public static LinuxUpdateApplyResult ParseLinuxApplyResult(string text)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.Length == 0)
            {
                continue;
            }
            var separator = line.IndexOf('=');
            if (separator <= 0 || !fields.TryAdd(line[..separator], line[(separator + 1)..]))
            {
                throw new InvalidDataException("Linux 更新結果格式不正確。");
            }
        }

        var allowedFields = new[] { "status", "version", "runtime", "message", "log" };
        if (fields.Count != allowedFields.Length || fields.Keys.Any(key => !allowedFields.Contains(key, StringComparer.Ordinal)))
        {
            throw new InvalidDataException("Linux 更新結果欄位不完整或包含未知欄位。");
        }

        var status = fields["status"];
        if (status is not ("failed" or "rollback"))
        {
            throw new InvalidDataException("Linux 更新結果狀態不正確。");
        }
        ParseVersion(fields["version"], "version", out var version);
        if (fields["runtime"] is not ("linux-x64" or "linux-arm64"))
        {
            throw new InvalidDataException("Linux 更新結果 RID 不正確。");
        }
        var message = fields["message"].Trim();
        if (message.Length is 0 or > 500 || message.Contains('\r') || message.Contains('\n'))
        {
            throw new InvalidDataException("Linux 更新結果訊息不正確。");
        }
        var logPath = fields["log"];
        if (!Path.IsPathFullyQualified(logPath) || logPath.Contains('\r') || logPath.Contains('\n'))
        {
            throw new InvalidDataException("Linux 更新 log 位置不正確。");
        }

        return new LinuxUpdateApplyResult(status, version, fields["runtime"], message, logPath);
    }

    public static string ResolveLinuxApplyResultPath()
    {
        var stateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (string.IsNullOrWhiteSpace(stateHome))
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            stateHome = Path.Combine(userProfile, ".local", "state");
        }
        if (!Path.IsPathFullyQualified(stateHome))
        {
            throw new InvalidOperationException("XDG_STATE_HOME 必須是完整路徑。");
        }
        return Path.Combine(stateHome, "mySQLPunk", "updates", "last-apply-result");
    }

    public static string ResolveLinuxApplyLockPath()
    {
        return Path.Combine(
            Path.GetDirectoryName(ResolveLinuxApplyResultPath())!,
            "apply.lock");
    }

    private static void AcquireLinuxApplyLock(string lockPath, string token, int processId)
    {
        var directory = Path.GetDirectoryName(lockPath)!;
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(
                    directory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch (IOException)
            {
                // Continue with the filesystem's existing user-state directory permissions.
            }
            catch (UnauthorizedAccessException)
            {
                // CreateNew below still determines whether exclusive ownership is available.
            }
        }
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    lockPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None);
                var contents = StrictUtf8.GetBytes($"token={token}\npid={processId}\n");
                stream.Write(contents);
                stream.Flush(true);
                if (!OperatingSystem.IsWindows())
                {
                    try
                    {
                        File.SetUnixFileMode(lockPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    }
                    catch (IOException)
                    {
                        // The lock is still exclusive even if a filesystem cannot change Unix mode bits.
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // The containing state directory remains user-scoped.
                    }
                }
                return;
            }
            catch (IOException) when (File.Exists(lockPath))
            {
                var ownerPid = TryReadLinuxApplyLockPid(lockPath);
                if (ownerPid is null)
                {
                    throw new InvalidOperationException("Linux 更新 lock 已存在，但無法安全驗證擁有者。");
                }
                if (IsProcessRunning(ownerPid.Value))
                {
                    throw new InvalidOperationException("另一個 mySQLPunk 視窗正在準備或套用 Linux 更新。");
                }

                try
                {
                    File.Delete(lockPath);
                }
                catch (FileNotFoundException)
                {
                    // Another stale-lock recovery won the race; retry CreateNew once.
                }
            }
        }

        throw new InvalidOperationException("無法取得 Linux 更新的獨佔 lock。");
    }

    private static int? TryReadLinuxApplyLockPid(string lockPath)
    {
        try
        {
            var info = new FileInfo(lockPath);
            if (info.Length is <= 0 or > 256)
            {
                return null;
            }
            foreach (var line in File.ReadAllLines(lockPath, StrictUtf8))
            {
                if (line.StartsWith("pid=", StringComparison.Ordinal) &&
                    int.TryParse(line[4..], out var processId))
                {
                    return processId;
                }
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
        return null;
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void ReleaseLinuxApplyLock(string lockPath, string token)
    {
        try
        {
            var contents = File.ReadAllText(lockPath, StrictUtf8);
            if (contents.Split('\n').Contains($"token={token}", StringComparer.Ordinal))
            {
                File.Delete(lockPath);
            }
        }
        catch (FileNotFoundException)
        {
            // The updater may already have claimed and released the reservation.
        }
        catch (IOException)
        {
            // A live updater must retain ownership if the reservation cannot be read safely.
        }
        catch (UnauthorizedAccessException)
        {
            // Do not delete a lock whose ownership cannot be verified.
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
