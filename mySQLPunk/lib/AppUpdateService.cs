using System;
using System.IO;
using System.Net;
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
            if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("GitHub owner is required.", nameof(owner));
            if (string.IsNullOrWhiteSpace(repository)) throw new ArgumentException("GitHub repository is required.", nameof(repository));
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
                string json = client.DownloadString(BuildGitHubLatestReleaseApiUrl(owner, repository));
                return ParseGitHubLatestRelease(json, currentVersion);
            }
        }

        public static AppUpdateCheckResult ParseGitHubLatestRelease(string json, string currentVersion)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("Release JSON is required.", nameof(json));

            JObject release = JObject.Parse(json);
            string tagName = (string)release["tag_name"] ?? "";
            AppUpdateCheckResult result = new AppUpdateCheckResult
            {
                CurrentVersion = ParseVersion(currentVersion),
                LatestVersion = ParseVersion(tagName),
                ReleaseName = (string)release["name"] ?? tagName,
                ReleasePageUrl = (string)release["html_url"] ?? "",
                ReleaseNotes = (string)release["body"] ?? "",
                IsPrerelease = (bool?)release["prerelease"] ?? false,
                InstallerDownloadUrl = FindInstallerAssetUrl(release["assets"] as JArray)
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
            if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("Download directory is required.", nameof(directory));

            string fileName = GetInstallerFileName(result);
            return Path.Combine(directory, fileName);
        }

        public static string GetInstallerFileName(AppUpdateCheckResult result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));

            string fileName = "";
            if (!string.IsNullOrWhiteSpace(result.InstallerDownloadUrl))
            {
                Uri uri;
                if (Uri.TryCreate(result.InstallerDownloadUrl, UriKind.Absolute, out uri))
                {
                    fileName = Uri.UnescapeDataString(Path.GetFileName(uri.LocalPath));
                }
            }

            if (string.IsNullOrWhiteSpace(fileName))
            {
                string version = result.LatestVersion == null ? "latest" : result.LatestVersion.ToString();
                fileName = "mySQLPunk-Setup-" + version + ".exe";
            }

            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                fileName = fileName.Replace(invalid, '_');
            }
            return fileName;
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
    }
}
