using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace mySQLPunk.lib
{
    public sealed class AppUpdateCheckResult
    {
        public Version CurrentVersion { get; set; }
        public Version LatestVersion { get; set; }
        public string ReleaseName { get; set; }
        public string ReleasePageUrl { get; set; }
        public string InstallerDownloadUrl { get; set; }
        public string InstallerSha256 { get; set; }
        public string PortableZipDownloadUrl { get; set; }
        public string PortableZipSha256 { get; set; }
        public string ReleaseManifestDownloadUrl { get; set; }
        public string ReleaseNotes { get; set; }
        public bool IsPrerelease { get; set; }

        public bool UpdateAvailable
        {
            get
            {
                if (CurrentVersion == null || LatestVersion == null) return false;
                return NormalizeVersion(LatestVersion).CompareTo(NormalizeVersion(CurrentVersion)) > 0;
            }
        }

        private static Version NormalizeVersion(Version version)
        {
            if (version == null) return new Version(0, 0, 0, 0);
            return new Version(
                Math.Max(0, version.Major),
                Math.Max(0, version.Minor),
                Math.Max(0, version.Build),
                Math.Max(0, version.Revision));
        }
    }

    public static class AppUpdateService
    {
        static AppUpdateService()
        {
            EnsureModernTls();
        }

        /// <summary>
        /// GitHub API 要求 TLS 1.2 以上；部分環境的 .NET Framework 預設交涉不起來，
        /// 會直接丟「無法建立 SSL/TLS 的安全通道」。這裡明確啟用 TLS 1.2 與 1.3
        ///（舊版 Windows 不支援 1.3 時忽略），檢查更新與下載才能通。
        /// </summary>
        public static void EnsureModernTls()
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }
            catch (NotSupportedException) { }
            try
            {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)12288; // TLS 1.3
            }
            catch (NotSupportedException) { }
        }

        public const string DefaultOwner = "shadowjohn";
        public const string DefaultRepository = "mySQLPunk";

        public static string BuildGitHubLatestReleaseApiUrl(string owner, string repository)
        {
            if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException(Localization.T("AppUpdate.GitHubOwnerRequired"), nameof(owner));
            if (string.IsNullOrWhiteSpace(repository)) throw new ArgumentException(Localization.T("AppUpdate.GitHubRepositoryRequired"), nameof(repository));
            return "https://api.github.com/repos/" + owner.Trim() + "/" + repository.Trim() + "/releases/latest";
        }

        public static AppUpdateCheckResult CheckGitHubLatestRelease(string currentVersion)
        {
            return CheckGitHubLatestRelease(DefaultOwner, DefaultRepository, currentVersion);
        }

        public static AppUpdateCheckResult CheckGitHubLatestRelease(string owner, string repository, string currentVersion)
        {
            using (WebClient client = new WebClient())
            {
                // DownloadString 預設用系統 ANSI 解碼；GitHub 回應是 UTF-8，
                // release 名稱含中文時 Big5 的雙位元組解讀會吞掉字串結尾的引號、JSON 解析直接壞掉
                client.Encoding = Encoding.UTF8;
                client.Headers[HttpRequestHeader.UserAgent] = "mySQLPunk-update-check";
                client.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
                IWebProxy proxy = ConnectionProxySettingsService.CreateWebProxyFromOptions();
                if (proxy != null) client.Proxy = proxy;
                string json = client.DownloadString(BuildGitHubLatestReleaseApiUrl(owner, repository));
                return ParseGitHubLatestRelease(json, currentVersion);
            }
        }

        public static AppUpdateCheckResult ParseGitHubLatestRelease(string json, string currentVersion)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException(Localization.T("AppUpdate.ReleaseJsonRequired"), nameof(json));

            JObject release = JObject.Parse(json);
            string tagName = (string)release["tag_name"] ?? "";
            JArray assets = release["assets"] as JArray;
            JToken installerAsset = FindInstallerAsset(assets);
            JToken portableZipAsset = FindPortableZipAsset(assets);
            AppUpdateCheckResult result = new AppUpdateCheckResult
            {
                CurrentVersion = ParseVersion(currentVersion),
                LatestVersion = ParseVersion(tagName),
                ReleaseName = (string)release["name"] ?? tagName,
                ReleasePageUrl = (string)release["html_url"] ?? "",
                ReleaseNotes = (string)release["body"] ?? "",
                IsPrerelease = (bool?)release["prerelease"] ?? false,
                InstallerDownloadUrl = GetAssetDownloadUrl(installerAsset),
                InstallerSha256 = GetAssetSha256(installerAsset),
                PortableZipDownloadUrl = GetAssetDownloadUrl(portableZipAsset),
                PortableZipSha256 = GetAssetSha256(portableZipAsset),
                ReleaseManifestDownloadUrl = FindReleaseManifestAssetUrl(assets)
            };

            return result;
        }

        public static Version ParseVersion(string value)
        {
            string normalized = (value ?? "").Trim();
            if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase)) normalized = normalized.Substring(1);

            int suffixIndex = normalized.IndexOfAny(new[] { '-', '+', ' ' });
            if (suffixIndex >= 0) normalized = normalized.Substring(0, suffixIndex);

            Version version;
            return Version.TryParse(normalized, out version) ? version : new Version(0, 0, 0, 0);
        }

        public static string BuildInstallerDownloadPath(AppUpdateCheckResult result, string directory)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException(Localization.T("Common.DownloadDirectoryRequired"), nameof(directory));

            string fileName = GetInstallerFileName(result);
            return Path.Combine(directory, fileName);
        }

        public static string BuildPortableZipDownloadPath(AppUpdateCheckResult result, string directory)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException(Localization.T("Common.DownloadDirectoryRequired"), nameof(directory));

            string fileName = GetPortableZipFileName(result);
            return Path.Combine(directory, fileName);
        }

        public static async Task DownloadVerifiedUpdateAsync(WebClient client, AppUpdateCheckResult result,
            string downloadUrl, string targetPath, Action onVerifying, CancellationToken cancellationToken)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (result == null) throw new ArgumentNullException(nameof(result));
            if (string.IsNullOrWhiteSpace(targetPath)) throw new ArgumentException(Localization.T("Common.FilePathRequired"), nameof(targetPath));

            string fileName = Path.GetFileName(targetPath);
            string stagingPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".partial";
            using (cancellationToken.Register(client.CancelAsync))
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string expectedSha256 = GetExpectedAssetSha256(result, fileName);
                    if (string.IsNullOrEmpty(expectedSha256) && !string.IsNullOrWhiteSpace(result.ReleaseManifestDownloadUrl))
                    {
                        client.Encoding = Encoding.UTF8;
                        string manifestJson = await client.DownloadStringTaskAsync(new Uri(result.ReleaseManifestDownloadUrl));
                        expectedSha256 = FindExpectedSha256InReleaseManifest(manifestJson, fileName);
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.IsNullOrEmpty(expectedSha256))
                        throw new InvalidDataException(Localization.Format("Update.HashMissing", fileName));

                    await client.DownloadFileTaskAsync(new Uri(downloadUrl), stagingPath);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (onVerifying != null) onVerifying();
                    string actualSha256 = await Task.Run(() => ComputeFileSha256(stagingPath), cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!string.Equals(expectedSha256, actualSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(Localization.Format("Update.HashMismatch", fileName,
                            expectedSha256.Substring(0, 12), actualSha256.Substring(0, 12)));
                    }

                    // 校驗完成才取代既有下載，取消或下載失敗不留下可執行的半成品。
                    if (File.Exists(targetPath)) File.Replace(stagingPath, targetPath, null);
                    else File.Move(stagingPath, targetPath);
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
                finally
                {
                    try { if (File.Exists(stagingPath)) File.Delete(stagingPath); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        public static string WritePortableUpdateApplyScript(string portableZipPath, string applicationDirectory, string executablePath, int processId, string scriptDirectory)
        {
            if (string.IsNullOrWhiteSpace(portableZipPath)) throw new ArgumentException(Localization.T("AppUpdate.PortableZipPathRequired"), nameof(portableZipPath));
            if (!File.Exists(portableZipPath)) throw new FileNotFoundException(Localization.Format("AppUpdate.PortableZipMissing", portableZipPath), portableZipPath);
            if (string.IsNullOrWhiteSpace(applicationDirectory)) throw new ArgumentException(Localization.T("AppUpdate.ApplicationDirectoryRequired"), nameof(applicationDirectory));
            if (string.IsNullOrWhiteSpace(executablePath)) throw new ArgumentException(Localization.T("Common.FilePathRequired"), nameof(executablePath));
            if (string.IsNullOrWhiteSpace(scriptDirectory)) throw new ArgumentException(Localization.T("Common.DownloadDirectoryRequired"), nameof(scriptDirectory));

            Directory.CreateDirectory(scriptDirectory);
            string scriptPath = Path.Combine(scriptDirectory, "apply-portable-update-" + Guid.NewGuid().ToString("N") + ".ps1");
            // Windows PowerShell 5.1 對「無 BOM」的 .ps1 用系統 ANSI 解碼，
            // 使用者名稱或安裝路徑含中文時腳本裡的路徑會變亂碼、更新必失敗
            File.WriteAllText(scriptPath, BuildPortableUpdateApplyScript(portableZipPath, applicationDirectory, executablePath, processId), new UTF8Encoding(true));
            return scriptPath;
        }

        public static string BuildPortableUpdateApplyScript(string portableZipPath, string applicationDirectory, string executablePath, int processId)
        {
            if (string.IsNullOrWhiteSpace(portableZipPath)) throw new ArgumentException(Localization.T("AppUpdate.PortableZipPathRequired"), nameof(portableZipPath));
            if (string.IsNullOrWhiteSpace(applicationDirectory)) throw new ArgumentException(Localization.T("AppUpdate.ApplicationDirectoryRequired"), nameof(applicationDirectory));
            if (string.IsNullOrWhiteSpace(executablePath)) throw new ArgumentException(Localization.T("Common.FilePathRequired"), nameof(executablePath));

            StringBuilder script = new StringBuilder();
            AppendUpdateProcessWaitScript(script, processId);
            script.AppendLine("$zipPath = '" + EscapePowerShellSingleQuotedString(portableZipPath) + "'");
            script.AppendLine("$appDir = '" + EscapePowerShellSingleQuotedString(applicationDirectory) + "'");
            script.AppendLine("$exePath = '" + EscapePowerShellSingleQuotedString(executablePath) + "'");
            AppendPortableUpdateApplyScriptBody(script);
            return script.ToString();
        }

        private static void AppendPortableUpdateApplyScriptBody(StringBuilder script)
        {
            // 備份僅包含這次套件會覆寫的檔案；不整份搬動程式目錄或使用者資料。
            // journal 供復原失敗時查核，不提供斷電或強制中止後的自動續跑。
            script.AppendLine(@"
function Assert-NoReparse([string]$Path) {
    $current = [System.IO.Path]::GetFullPath($Path)
    while ($current) {
        try {
            if (([System.IO.File]::GetAttributes($current) -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Reparse points are not supported by the portable updater.' }
        } catch [System.IO.FileNotFoundException] { } catch [System.IO.DirectoryNotFoundException] { }
        $current = [System.IO.Path]::GetDirectoryName($current)
    }
}
function Get-ChildPath([string]$Path, [string]$Root) {
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $prefix = [System.IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    if (-not $fullPath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'Update path is outside its expected directory.' }
    Assert-NoReparse $fullPath
    return $fullPath
}
function Get-SafeTree([string]$Root) {
    Assert-NoReparse $Root
    $pending = New-Object 'System.Collections.Generic.Queue[string]'
    $items = New-Object 'System.Collections.Generic.List[object]'
    $pending.Enqueue($Root)
    while ($pending.Count -gt 0) {
        foreach ($item in @(Get-ChildItem -LiteralPath $pending.Dequeue() -Force)) {
            $null = Get-ChildPath $item.FullName $Root
            $items.Add($item)
            if ($item.PSIsContainer) { $pending.Enqueue($item.FullName) }
        }
    }
    return $items.ToArray()
}
function Get-UpdateHash([string]$Path) {
    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try {
        $stream = [System.IO.File]::OpenRead($Path)
        try { return [System.BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '') }
        finally { $stream.Dispose() }
    } finally { $algorithm.Dispose() }
}
function Write-UpdateFile([string]$Source, [string]$Target, [bool]$Replace, [string]$ExpectedHash) {
    $null = Get-ChildPath $Source $staging
    $null = Get-ChildPath $Target $appDir
    $temporary = Get-ChildPath ($Target + '.update-' + [System.Guid]::NewGuid().ToString('N') + '.tmp') $appDir
    $temporaryFiles.Add($temporary)
    try {
        [System.IO.File]::Copy($Source, $temporary, $false)
        if ((Get-UpdateHash $temporary) -ne $ExpectedHash) { throw 'The staged update file is incomplete.' }
        $null = Get-ChildPath $Target $appDir
        if ($Replace) { [System.IO.File]::Replace($temporary, $Target, [NullString]::Value) }
        else { [System.IO.File]::Move($temporary, $Target) }
    } finally {
        $null = Get-ChildPath $temporary $appDir
        if ([System.IO.File]::Exists($temporary)) { [System.IO.File]::Delete($temporary) }
    }
}
function Save-RecoveryJournal([string]$Status) {
    $journalPath = Get-ChildPath (Join-Path $staging 'journal.json') $staging
    $journal = @{ schemaVersion = 1; status = $Status; files = @($files | Select-Object Relative, Existed, OldHash, NewHash, Attempted); createdDirectories = @($createdDirectories | ForEach-Object { $_.Substring($appPrefix.Length) }) }
    [System.IO.File]::WriteAllText($journalPath, ($journal | ConvertTo-Json -Depth 5), (New-Object System.Text.UTF8Encoding($false)))
}
function Restore-PortableUpdate {
    $complete = $true
    for ($index = $files.Count - 1; $index -ge 0; $index--) {
        $file = $files[$index]
        if (-not $file.Attempted) { continue }
        try {
            $null = Get-ChildPath $file.Target $appDir
            if ([System.IO.Directory]::Exists($file.Target)) { throw 'A directory replaced an update target.' }
            $exists = [System.IO.File]::Exists($file.Target)
            $currentHash = if ($exists) { Get-UpdateHash $file.Target } else { '' }
            if ($file.Existed) {
                if ($currentHash -ne $file.OldHash) {
                    if ($exists -and $currentHash -ne $file.NewHash) { throw 'An update target changed outside the updater.' }
                    Write-UpdateFile $file.Backup $file.Target $exists $file.OldHash
                }
                if ([System.IO.File]::GetLastWriteTimeUtc($file.Target).Ticks -ne $file.LastWriteTicks) {
                    [System.IO.File]::SetLastWriteTimeUtc($file.Target, (New-Object System.DateTime($file.LastWriteTicks, [System.DateTimeKind]::Utc)))
                }
                if ([System.IO.File]::GetAttributes($file.Target) -ne $file.Attributes) { [System.IO.File]::SetAttributes($file.Target, $file.Attributes) }
                if ((Get-UpdateHash $file.Target) -ne $file.OldHash) { throw 'Restored file checksum mismatch.' }
            } elseif ($exists) {
                if ($currentHash -ne $file.NewHash) { throw 'A newly created update target changed outside the updater.' }
                [System.IO.File]::Delete($file.Target)
            }
        } catch { $complete = $false }
    }
    for ($index = $createdDirectories.Count - 1; $index -ge 0; $index--) {
        try {
            $directory = Get-ChildPath $createdDirectories[$index] $appDir
            if ([System.IO.Directory]::Exists($directory)) { [System.IO.Directory]::Delete($directory, $false) }
        } catch { $complete = $false }
    }
    foreach ($file in $files) {
        try {
            $null = Get-ChildPath $file.Target $appDir
            if ($file.Existed) {
                if ((Get-UpdateHash $file.Target) -ne $file.OldHash) { $complete = $false }
            } elseif ([System.IO.File]::Exists($file.Target) -or [System.IO.Directory]::Exists($file.Target)) { $complete = $false }
        } catch { $complete = $false }
    }
    return $complete
}
$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$appDir = [System.IO.Path]::GetFullPath($appDir).TrimEnd('\')
$appPrefix = $appDir + '\'
$staging = Get-ChildPath (Join-Path $tempRoot ('mysqlpunk-update-' + [System.Guid]::NewGuid().ToString('N'))) $tempRoot
New-Item -ItemType Directory -Path $staging | Out-Null
$payload = Join-Path $staging 'payload'
$backupRoot = Join-Path $staging 'backup'
$files = New-Object 'System.Collections.Generic.List[object]'
$directories = New-Object 'System.Collections.Generic.List[string]'
$createdDirectories = New-Object 'System.Collections.Generic.List[string]'
$temporaryFiles = New-Object 'System.Collections.Generic.List[string]'
$updateExitCode = 0
$mutating = $false
$keepRecovery = $false
try {
    Assert-NoReparse $appDir
    Assert-NoReparse $zipPath
    if (-not [System.IO.Directory]::Exists($appDir)) { throw 'The application directory is missing.' }
    $null = Get-ChildPath $exePath $appDir
    if (-not [string]::Equals([System.IO.Path]::GetFullPath($exePath), (Join-Path $appDir 'mySQLPunk.exe'), [System.StringComparison]::OrdinalIgnoreCase)) { throw 'The application executable must be at the package root.' }
    Expand-Archive -LiteralPath $zipPath -DestinationPath $payload -Force
    $tree = @(Get-SafeTree $payload)
    $executables = @($tree | Where-Object { -not $_.PSIsContainer -and $_.Name -ieq 'mySQLPunk.exe' })
    if ($executables.Count -ne 1) { throw 'The portable package must contain exactly one mySQLPunk.exe.' }
    $source = $payload
    if (-not [System.IO.File]::Exists((Join-Path $payload 'mySQLPunk.exe'))) {
        $entries = @(Get-ChildItem -LiteralPath $payload -Force)
        if ($entries.Count -ne 1 -or -not $entries[0].PSIsContainer -or -not [System.IO.File]::Exists((Join-Path $entries[0].FullName 'mySQLPunk.exe'))) { throw 'The portable package must use its root or a single wrapper directory.' }
        $source = $entries[0].FullName
    }
    $sourcePrefix = $source.TrimEnd('\') + '\'
    $sourceItems = @(Get-SafeTree $source)
    $relativeFiles = New-Object 'System.Collections.Generic.List[string]'
    foreach ($item in $sourceItems) {
        $relative = $item.FullName.Substring($sourcePrefix.Length)
        $top = $relative.Split('\')[0].ToLowerInvariant()
        if ($top -in @('setting.ini', 'connection-profile.txt', 'connection_profiles', 'autocomplete-cache.json') -or $top.EndsWith('.log') -or $top.Contains('.corrupt-')) { throw 'The package contains user settings or runtime data.' }
        $target = Get-ChildPath (Join-Path $appDir $relative) $appDir
        if ($item.PSIsContainer) {
            if ([System.IO.File]::Exists($target)) { throw 'A payload directory conflicts with an installed file.' }
            $directories.Add($target)
        } else {
            if ([System.IO.Directory]::Exists($target)) { throw 'A payload file conflicts with an installed directory.' }
            $relativeFiles.Add($relative)
        }
    }
    $relativeFiles.Sort([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($relative in $relativeFiles) {
        $target = Get-ChildPath (Join-Path $appDir $relative) $appDir
        $sourceFile = Get-ChildPath (Join-Path $source $relative) $source
        $files.Add([pscustomobject]@{ Relative = $relative; Source = $sourceFile; Target = $target; Existed = [System.IO.File]::Exists($target); Backup = (Join-Path $backupRoot $relative); OldHash = ''; NewHash = (Get-UpdateHash $sourceFile); Attributes = 0; LastWriteTicks = 0; Attempted = $false })
    }
    foreach ($file in $files) {
        if (-not $file.Existed) { continue }
        $null = Get-ChildPath $file.Target $appDir
        $null = Get-ChildPath $file.Backup $staging
        $null = [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($file.Backup))
        $file.Attributes = [System.IO.File]::GetAttributes($file.Target)
        $file.LastWriteTicks = [System.IO.File]::GetLastWriteTimeUtc($file.Target).Ticks
        [System.IO.File]::Copy($file.Target, $file.Backup, $false)
        $file.OldHash = Get-UpdateHash $file.Backup
        if ((Get-UpdateHash $file.Target) -ne $file.OldHash) { throw 'An installed file changed while being backed up.' }
    }
    Save-RecoveryJournal 'prepared'
    $mutating = $true
    foreach ($directory in @($directories | Sort-Object Length)) {
        $null = Get-ChildPath $directory $appDir
        if (-not [System.IO.Directory]::Exists($directory)) {
            $null = [System.IO.Directory]::CreateDirectory($directory)
            $createdDirectories.Add($directory)
        }
    }
    foreach ($file in $files) {
        $null = Get-ChildPath $file.Target $appDir
        if ($file.Existed) {
            if ((Get-UpdateHash $file.Target) -ne $file.OldHash) { throw 'An installed file changed before replacement.' }
        } elseif ([System.IO.File]::Exists($file.Target) -or [System.IO.Directory]::Exists($file.Target)) { throw 'An update target was created by another process.' }
        $file.Attempted = $true
        Write-UpdateFile $file.Source $file.Target $file.Existed $file.NewHash
        if ((Get-UpdateHash $file.Target) -ne $file.NewHash) { throw 'Installed file checksum mismatch.' }
    }
} catch {
    $updateExitCode = 1
    if ($mutating -and -not (Restore-PortableUpdate)) { $keepRecovery = $true; $updateExitCode = 2 }
} finally {
    foreach ($temporary in $temporaryFiles) {
        try {
            $null = Get-ChildPath $temporary $appDir
            if ([System.IO.File]::Exists($temporary)) { [System.IO.File]::Delete($temporary) }
        } catch { $keepRecovery = $true; $updateExitCode = 2 }
    }
    if (-not $keepRecovery) {
        try {
            $resolvedStaging = Get-ChildPath (Resolve-Path -LiteralPath $staging).ProviderPath $tempRoot
            $null = @(Get-SafeTree $resolvedStaging)
            Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
        } catch { $keepRecovery = $true; $updateExitCode = 2 }
    }
    if ($keepRecovery) { try { Save-RecoveryJournal 'recovery-required' } catch { } }
}
if ($updateExitCode -eq 0) { Start-Process -FilePath $exePath }
exit $updateExitCode
");
        }

        public static ProcessStartInfo BuildPortableUpdateApplyProcessStartInfo(string scriptPath)
        {
            if (string.IsNullOrWhiteSpace(scriptPath)) throw new ArgumentException(Localization.T("Common.FilePathRequired"), nameof(scriptPath));

            return new ProcessStartInfo("powershell.exe")
            {
                Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + scriptPath.Replace("\"", "\\\"") + "\"",
                UseShellExecute = true,
                // 自我更新腳本不需要使用者互動，藏起黑視窗讓「更新→重啟」看起來是一氣呵成
                WindowStyle = ProcessWindowStyle.Hidden
            };
        }

        public static string WriteInstallerUpdateApplyScript(string installerPath, string executablePath, int processId, string scriptDirectory)
        {
            if (string.IsNullOrWhiteSpace(installerPath)) throw new ArgumentException(Localization.T("Common.FilePathRequired"), nameof(installerPath));
            if (!File.Exists(installerPath)) throw new FileNotFoundException(Localization.Format("AppUpdate.InstallerMissing", installerPath), installerPath);
            if (string.IsNullOrWhiteSpace(executablePath)) throw new ArgumentException(Localization.T("Common.FilePathRequired"), nameof(executablePath));
            if (string.IsNullOrWhiteSpace(scriptDirectory)) throw new ArgumentException(Localization.T("Common.DownloadDirectoryRequired"), nameof(scriptDirectory));

            Directory.CreateDirectory(scriptDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(GetInstallerUpdateResultPath(executablePath)));
            string scriptPath = Path.Combine(scriptDirectory, "apply-installer-update-" + Guid.NewGuid().ToString("N") + ".ps1");
            // 同 portable 腳本：無 BOM 的 .ps1 會被 Windows PowerShell 5.1 用 ANSI 解碼，中文路徑必亂
            File.WriteAllText(scriptPath, BuildInstallerUpdateApplyScript(installerPath, executablePath, processId), new UTF8Encoding(true));
            return scriptPath;
        }

        /// <summary>
        /// 安裝版靜默更新：等 mySQLPunk 結束 → 以 /VERYSILENT 執行 Inno Setup 安裝檔（per-user、免 UAC）→
        /// 安裝至目前程式目錄；失敗時嘗試重新啟動現有程式，並保留失敗代碼。
        /// </summary>
        public static string BuildInstallerUpdateApplyScript(string installerPath, string executablePath, int processId)
        {
            if (string.IsNullOrWhiteSpace(installerPath)) throw new ArgumentException(Localization.T("Common.FilePathRequired"), nameof(installerPath));
            if (string.IsNullOrWhiteSpace(executablePath)) throw new ArgumentException(Localization.T("Common.FilePathRequired"), nameof(executablePath));

            StringBuilder script = new StringBuilder();
            AppendUpdateProcessWaitScript(script, processId);
            script.AppendLine("$installerPath = '" + EscapePowerShellSingleQuotedString(installerPath) + "'");
            script.AppendLine("$exePath = '" + EscapePowerShellSingleQuotedString(executablePath) + "'");
            script.AppendLine("$resultPath = '" + EscapePowerShellSingleQuotedString(GetInstallerUpdateResultPath(executablePath)) + "'");
            script.AppendLine("$appDir = [System.IO.Path]::GetDirectoryName($exePath)");
            script.AppendLine("$updateExitCode = 0");
            script.AppendLine("try {");
            script.AppendLine("    $installerArguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CURRENTUSER',('/DIR=\"' + $appDir + '\"'))");
            script.AppendLine("    $install = Start-Process -FilePath $installerPath -ArgumentList $installerArguments -WindowStyle Hidden -Wait -PassThru");
            script.AppendLine("    $updateExitCode = $install.ExitCode");
            script.AppendLine("}");
            script.AppendLine("catch { $updateExitCode = 1 }");
            script.AppendLine("finally {");
            // 只留下退出碼，下一次啟動讀取一次；不記安裝路徑、命令列或例外內容。
            script.AppendLine("    try {");
            script.AppendLine("        if ($updateExitCode -ne 0) {");
            script.AppendLine("            [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($resultPath)) | Out-Null");
            script.AppendLine("            [System.IO.File]::WriteAllText($resultPath, $updateExitCode.ToString([System.Globalization.CultureInfo]::InvariantCulture))");
            script.AppendLine("        } elseif (Test-Path -LiteralPath $resultPath) {");
            script.AppendLine("            Remove-Item -LiteralPath $resultPath -Force");
            script.AppendLine("        }");
            script.AppendLine("    } catch { }");
            script.AppendLine("    if (Test-Path -LiteralPath $exePath) {");
            script.AppendLine("        Start-Process -FilePath $exePath");
            script.AppendLine("    }");
            script.AppendLine("}");
            script.AppendLine("exit $updateExitCode");
            return script.ToString();
        }

        public static string GetInstallerUpdateResultPath(string executablePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath)) throw new ArgumentException(Localization.T("Common.FilePathRequired"), nameof(executablePath));

            // 同一台電腦可有多份安裝；以路徑雜湊區分，不把路徑寫入結果檔名。
            string identity = Path.GetFullPath(executablePath).ToUpperInvariant();
            string hash;
            using (SHA256 sha256 = SHA256.Create())
                hash = BitConverter.ToString(sha256.ComputeHash(Encoding.UTF8.GetBytes(identity))).Replace("-", "").ToLowerInvariant();
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "mySQLPunk", "updates", "install-result-" + hash + ".txt");
        }

        public static int? TakeInstallerUpdateFailure(string executablePath)
        {
            string resultPath = GetInstallerUpdateResultPath(executablePath);
            string claimedPath = resultPath + "." + Guid.NewGuid().ToString("N") + ".consumed";
            bool claimed = false;
            bool consumed = false;
            try
            {
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        if (!claimed)
                        {
                            if (!File.Exists(resultPath)) return null;
                            // 只有成功取走結果的實例顯示通知；短暫檔案鎖稍後再試。
                            File.Move(resultPath, claimedPath);
                            claimed = true;
                        }
                        string result = new FileInfo(claimedPath).Length <= 16 ? File.ReadAllText(claimedPath, Encoding.UTF8) : "";
                        int exitCode;
                        consumed = true;
                        return int.TryParse(result, System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out exitCode) && exitCode != 0 ? (int?)exitCode : null;
                    }
                    catch (IOException)
                    {
                        if (attempt == 4) return null;
                        System.Threading.Thread.Sleep(50);
                    }
                }
                return null;
            }
            catch (UnauthorizedAccessException) { return null; }
            finally
            {
                try
                {
                    if (claimed && File.Exists(claimedPath))
                    {
                        if (consumed) File.Delete(claimedPath);
                        // 讀取失敗保留通知，且不覆寫另一個更新剛寫入的結果。
                        else File.Move(claimedPath, resultPath);
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static void AppendUpdateProcessWaitScript(StringBuilder script, int processId)
        {
            script.AppendLine("param([ValidateRange(1, 120)][int]$WaitTimeoutSeconds = 120)");
            script.AppendLine("$ErrorActionPreference = 'Stop'");
            script.AppendLine("$processIdToWait = " + Math.Max(0, processId));
            script.AppendLine("if ($processIdToWait -gt 0) {");
            script.AppendLine("    $runningProcess = Get-Process -Id $processIdToWait -ErrorAction SilentlyContinue");
            script.AppendLine("    if ($null -ne $runningProcess) {");
            script.AppendLine("        try {");
            script.AppendLine("            if (-not $runningProcess.WaitForExit($WaitTimeoutSeconds * 1000)) { exit 1 }");
            script.AppendLine("        }");
            script.AppendLine("        finally { $runningProcess.Dispose() }");
            script.AppendLine("    }");
            script.AppendLine("}");
        }

        public static string GetInstallerFileName(AppUpdateCheckResult result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));

            string fileName = GetFileNameFromUrl(result.InstallerDownloadUrl);

            if (string.IsNullOrWhiteSpace(fileName))
            {
                string version = result.LatestVersion == null ? "latest" : result.LatestVersion.ToString();
                fileName = "mySQLPunk-Setup-" + version + ".exe";
            }

            return SanitizeFileName(fileName);
        }

        public static string GetPortableZipFileName(AppUpdateCheckResult result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));

            string fileName = GetFileNameFromUrl(result.PortableZipDownloadUrl);

            if (string.IsNullOrWhiteSpace(fileName))
            {
                string version = result.LatestVersion == null ? "latest" : result.LatestVersion.ToString();
                fileName = "mySQLPunk-" + version + "-win-x64-portable.zip";
            }

            return SanitizeFileName(fileName);
        }

        public static string GetExpectedAssetSha256(AppUpdateCheckResult result, string fileName)
        {
            if (result == null || string.IsNullOrWhiteSpace(fileName)) return "";

            string normalizedFileName = SanitizeFileName(Path.GetFileName(fileName));
            if (!string.IsNullOrWhiteSpace(result.InstallerDownloadUrl) &&
                string.Equals(normalizedFileName, GetInstallerFileName(result), StringComparison.OrdinalIgnoreCase))
            {
                return NormalizeSha256(result.InstallerSha256);
            }

            if (!string.IsNullOrWhiteSpace(result.PortableZipDownloadUrl) &&
                string.Equals(normalizedFileName, GetPortableZipFileName(result), StringComparison.OrdinalIgnoreCase))
            {
                return NormalizeSha256(result.PortableZipSha256);
            }

            return "";
        }

        private static JToken FindInstallerAsset(JArray assets)
        {
            if (assets == null) return null;

            foreach (JToken asset in assets)
            {
                string name = ((string)asset["name"] ?? "").ToLowerInvariant();
                string url = (string)asset["browser_download_url"] ?? "";
                if (string.IsNullOrWhiteSpace(url)) continue;
                if (name.EndsWith(".exe") || name.EndsWith(".msi") || name.EndsWith(".msix") || name.EndsWith(".appinstaller"))
                {
                    return asset;
                }
            }

            return null;
        }

        private static JToken FindPortableZipAsset(JArray assets)
        {
            if (assets == null) return null;

            JToken fallbackZipAsset = null;
            foreach (JToken asset in assets)
            {
                string name = ((string)asset["name"] ?? "").ToLowerInvariant();
                string url = (string)asset["browser_download_url"] ?? "";
                if (string.IsNullOrWhiteSpace(url)) continue;
                // Linux/macOS 也會發布 ZIP；只接受 Windows x64 的可攜包與舊版檔名。
                if (!Regex.IsMatch(name, @"\Amysqlpunk-(?:\d+\.){2,3}\d+-win-x64(?:-portable)?\.zip\z")) continue;

                if (name.Contains("portable") && name.Contains("mysqlpunk"))
                {
                    return asset;
                }

                if (fallbackZipAsset == null && name.Contains("mysqlpunk"))
                {
                    fallbackZipAsset = asset;
                }
            }

            return fallbackZipAsset;
        }

        private static string GetAssetDownloadUrl(JToken asset)
        {
            return asset == null ? "" : ((string)asset["browser_download_url"] ?? "");
        }

        private static string GetAssetSha256(JToken asset)
        {
            return asset == null ? "" : NormalizeSha256((string)asset["digest"] ?? "");
        }

        private static string FindReleaseManifestAssetUrl(JArray assets)
        {
            if (assets == null) return "";

            foreach (JToken asset in assets)
            {
                string name = ((string)asset["name"] ?? "").ToLowerInvariant();
                string url = (string)asset["browser_download_url"] ?? "";
                if (string.IsNullOrWhiteSpace(url)) continue;
                if (name == "release-manifest.json" || name.EndsWith("-manifest.json"))
                {
                    return url;
                }
            }

            return "";
        }

        public static string FindExpectedSha256InReleaseManifest(string manifestJson, string fileName)
        {
            if (string.IsNullOrWhiteSpace(manifestJson) || string.IsNullOrWhiteSpace(fileName)) return "";

            JObject manifest = JObject.Parse(manifestJson);
            string normalizedFileName = SanitizeFileName(Path.GetFileName(fileName));

            string packageName = SanitizeFileName((string)manifest["package"] ?? "");
            string packageSha256 = NormalizeSha256((string)manifest["sha256"] ?? "");
            if (!string.IsNullOrWhiteSpace(packageName) &&
                string.Equals(packageName, normalizedFileName, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(packageSha256))
            {
                return packageSha256;
            }

            string arrayMatch = FindExpectedSha256InManifestArray(manifest["files"] as JArray, normalizedFileName);
            if (!string.IsNullOrWhiteSpace(arrayMatch)) return arrayMatch;

            arrayMatch = FindExpectedSha256InManifestArray(manifest["assets"] as JArray, normalizedFileName);
            if (!string.IsNullOrWhiteSpace(arrayMatch)) return arrayMatch;

            return FindExpectedSha256InManifestArray(manifest["packages"] as JArray, normalizedFileName);
        }

        public static string ComputeFileSha256(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException(Localization.T("Common.FilePathRequired"), nameof(path));
            using (FileStream stream = File.OpenRead(path))
            using (SHA256 sha256 = SHA256.Create())
            {
                return BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            }
        }

        public static bool VerifyFileSha256(string path, string expectedSha256, out string actualSha256)
        {
            actualSha256 = ComputeFileSha256(path);
            string normalizedExpected = NormalizeSha256(expectedSha256);
            return !string.IsNullOrWhiteSpace(normalizedExpected) &&
                   string.Equals(actualSha256, normalizedExpected, StringComparison.OrdinalIgnoreCase);
        }

        private static string FindExpectedSha256InManifestArray(JArray items, string normalizedFileName)
        {
            if (items == null) return "";

            foreach (JToken item in items)
            {
                string name = SanitizeFileName((string)item["name"] ?? (string)item["fileName"] ?? (string)item["package"] ?? "");
                if (!string.Equals(name, normalizedFileName, StringComparison.OrdinalIgnoreCase)) continue;

                string sha256 = NormalizeSha256((string)item["sha256"] ?? (string)item["SHA256"] ?? (string)item["hash"] ?? "");
                if (!string.IsNullOrWhiteSpace(sha256)) return sha256;
            }

            return "";
        }

        private static string NormalizeSha256(string value)
        {
            string normalized = (value ?? "").Trim().ToLowerInvariant();
            if (normalized.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring("sha256:".Length);
            }
            return Regex.IsMatch(normalized, @"\A[0-9a-f]{64}\z") ? normalized : "";
        }

        private static string GetFileNameFromUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "";

            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return "";
            return Uri.UnescapeDataString(Path.GetFileName(uri.LocalPath));
        }

        private static string SanitizeFileName(string fileName)
        {
            string sanitized = fileName ?? "";
            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                sanitized = sanitized.Replace(invalid, '_');
            }
            return sanitized;
        }

        private static string EscapePowerShellSingleQuotedString(string value)
        {
            return (value ?? "").Replace("'", "''");
        }
    }
}
