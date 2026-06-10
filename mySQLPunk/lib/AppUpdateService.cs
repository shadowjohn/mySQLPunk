using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
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
        public string PortableZipDownloadUrl { get; set; }
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
            AppUpdateCheckResult result = new AppUpdateCheckResult
            {
                CurrentVersion = ParseVersion(currentVersion),
                LatestVersion = ParseVersion(tagName),
                ReleaseName = (string)release["name"] ?? tagName,
                ReleasePageUrl = (string)release["html_url"] ?? "",
                ReleaseNotes = (string)release["body"] ?? "",
                IsPrerelease = (bool?)release["prerelease"] ?? false,
                InstallerDownloadUrl = FindInstallerAssetUrl(assets),
                PortableZipDownloadUrl = FindPortableZipAssetUrl(assets),
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

        public static string WritePortableUpdateApplyScript(string portableZipPath, string applicationDirectory, string executablePath, int processId, string scriptDirectory)
        {
            if (string.IsNullOrWhiteSpace(portableZipPath)) throw new ArgumentException(Localization.T("AppUpdate.PortableZipPathRequired"), nameof(portableZipPath));
            if (!File.Exists(portableZipPath)) throw new FileNotFoundException(Localization.Format("AppUpdate.PortableZipMissing", portableZipPath), portableZipPath);
            if (string.IsNullOrWhiteSpace(applicationDirectory)) throw new ArgumentException(Localization.T("AppUpdate.ApplicationDirectoryRequired"), nameof(applicationDirectory));
            if (string.IsNullOrWhiteSpace(executablePath)) throw new ArgumentException(Localization.T("Common.FilePathRequired"), nameof(executablePath));
            if (string.IsNullOrWhiteSpace(scriptDirectory)) throw new ArgumentException(Localization.T("Common.DownloadDirectoryRequired"), nameof(scriptDirectory));

            Directory.CreateDirectory(scriptDirectory);
            string scriptPath = Path.Combine(scriptDirectory, "apply-portable-update-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + ".ps1");
            File.WriteAllText(scriptPath, BuildPortableUpdateApplyScript(portableZipPath, applicationDirectory, executablePath, processId), new UTF8Encoding(false));
            return scriptPath;
        }

        public static string BuildPortableUpdateApplyScript(string portableZipPath, string applicationDirectory, string executablePath, int processId)
        {
            if (string.IsNullOrWhiteSpace(portableZipPath)) throw new ArgumentException(Localization.T("AppUpdate.PortableZipPathRequired"), nameof(portableZipPath));
            if (string.IsNullOrWhiteSpace(applicationDirectory)) throw new ArgumentException(Localization.T("AppUpdate.ApplicationDirectoryRequired"), nameof(applicationDirectory));
            if (string.IsNullOrWhiteSpace(executablePath)) throw new ArgumentException(Localization.T("Common.FilePathRequired"), nameof(executablePath));

            StringBuilder script = new StringBuilder();
            script.AppendLine("$ErrorActionPreference = 'Stop'");
            script.AppendLine("$zipPath = '" + EscapePowerShellSingleQuotedString(portableZipPath) + "'");
            script.AppendLine("$appDir = '" + EscapePowerShellSingleQuotedString(applicationDirectory) + "'");
            script.AppendLine("$exePath = '" + EscapePowerShellSingleQuotedString(executablePath) + "'");
            script.AppendLine("$processIdToWait = " + Math.Max(0, processId));
            script.AppendLine("if ($processIdToWait -gt 0) {");
            script.AppendLine("    try { Wait-Process -Id $processIdToWait -Timeout 120 -ErrorAction SilentlyContinue } catch { }");
            script.AppendLine("}");
            script.AppendLine("$staging = Join-Path ([System.IO.Path]::GetTempPath()) ('mysqlpunk-update-' + [System.Guid]::NewGuid().ToString('N'))");
            script.AppendLine("New-Item -ItemType Directory -Path $staging -Force | Out-Null");
            script.AppendLine("try {");
            script.AppendLine("    Expand-Archive -LiteralPath $zipPath -DestinationPath $staging -Force");
            script.AppendLine("    $source = $staging");
            script.AppendLine("    $children = @(Get-ChildItem -LiteralPath $staging -Directory)");
            script.AppendLine("    if ($children.Count -eq 1 -and (Test-Path -LiteralPath (Join-Path $children[0].FullName 'mySQLPunk.exe'))) {");
            script.AppendLine("        $source = $children[0].FullName");
            script.AppendLine("    }");
            script.AppendLine("    Get-ChildItem -LiteralPath $source -Force | ForEach-Object {");
            script.AppendLine("        Copy-Item -LiteralPath $_.FullName -Destination $appDir -Recurse -Force");
            script.AppendLine("    }");
            script.AppendLine("}");
            script.AppendLine("finally {");
            script.AppendLine("    Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue");
            script.AppendLine("}");
            script.AppendLine("if (Test-Path -LiteralPath $exePath) {");
            script.AppendLine("    Start-Process -FilePath $exePath");
            script.AppendLine("}");
            return script.ToString();
        }

        public static ProcessStartInfo BuildPortableUpdateApplyProcessStartInfo(string scriptPath)
        {
            if (string.IsNullOrWhiteSpace(scriptPath)) throw new ArgumentException(Localization.T("Common.FilePathRequired"), nameof(scriptPath));

            return new ProcessStartInfo("powershell.exe")
            {
                Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + scriptPath.Replace("\"", "\\\"") + "\"",
                UseShellExecute = true
            };
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

        private static string FindInstallerAssetUrl(JArray assets)
        {
            if (assets == null) return "";

            foreach (JToken asset in assets)
            {
                string name = ((string)asset["name"] ?? "").ToLowerInvariant();
                string url = (string)asset["browser_download_url"] ?? "";
                if (string.IsNullOrWhiteSpace(url)) continue;
                if (name.EndsWith(".exe") || name.EndsWith(".msi") || name.EndsWith(".msix") || name.EndsWith(".appinstaller"))
                {
                    return url;
                }
            }

            return "";
        }

        private static string FindPortableZipAssetUrl(JArray assets)
        {
            if (assets == null) return "";

            string fallbackZipUrl = "";
            foreach (JToken asset in assets)
            {
                string name = ((string)asset["name"] ?? "").ToLowerInvariant();
                string url = (string)asset["browser_download_url"] ?? "";
                if (string.IsNullOrWhiteSpace(url)) continue;
                if (!name.EndsWith(".zip")) continue;

                if (name.Contains("portable") && name.Contains("mysqlpunk"))
                {
                    return url;
                }

                if (string.IsNullOrWhiteSpace(fallbackZipUrl) && name.Contains("mysqlpunk"))
                {
                    fallbackZipUrl = url;
                }
            }

            return fallbackZipUrl;
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
            string normalized = (value ?? "").Trim().Replace("-", "").ToLowerInvariant();
            return normalized.Length == 64 ? normalized : "";
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
