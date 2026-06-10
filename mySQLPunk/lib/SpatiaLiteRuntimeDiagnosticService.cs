using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace mySQLPunk.lib
{
    public sealed class SpatiaLiteDiagnosticRow
    {
        public string Item { get; set; }
        public string Status { get; set; }
        public string Detail { get; set; }
    }

    public static class SpatiaLiteRuntimeDiagnosticService
    {
        private const string BuildScriptRelativePath = "tools\\spatialite\\Build-SpatiaLiteRuntime.ps1";
        private const string RuntimeManifestFileName = "SPATIALITE_RUNTIME_MANIFEST.json";
        private const string SourceArchiveFileName = "libspatialite-5.1.0.zip";

        public static List<SpatiaLiteDiagnosticRow> BuildRows(string runtimeDir, string loadError, string repositoryRoot)
        {
            List<SpatiaLiteDiagnosticRow> rows = new List<SpatiaLiteDiagnosticRow>();
            string safeRuntimeDir = runtimeDir ?? string.Empty;
            string dllPath = Path.Combine(safeRuntimeDir, "mod_spatialite.dll");
            string manifestPath = Path.Combine(safeRuntimeDir, RuntimeManifestFileName);
            string scriptPath = GetBuildScriptPath(repositoryRoot);
            string sourceCachePath = GetSourceCachePath(repositoryRoot);
            string offlinePackagePath = FindOfflinePackagePath(repositoryRoot, safeRuntimeDir);

            rows.Add(CreateRow("SpatiaLite Runtime Manifest", File.Exists(manifestPath) ? StatusReady() : StatusWarning(), manifestPath));
            rows.Add(CreateRow("SpatiaLite Manifest Source", File.Exists(manifestPath) ? StatusInfo() : StatusWarning(), BuildManifestSourceSummary(manifestPath)));
            rows.Add(BuildManifestFileVerificationRow(manifestPath, safeRuntimeDir));
            rows.Add(CreateRow("SpatiaLite Source Cache", File.Exists(sourceCachePath) ? StatusReady() : StatusInfo(), File.Exists(sourceCachePath) ? sourceCachePath : sourceCachePath + "（尚未建立，修復腳本下載成功後會寫入）"));
            rows.Add(CreateRow("SpatiaLite Offline Package", File.Exists(offlinePackagePath) ? StatusReady() : StatusInfo(), string.IsNullOrWhiteSpace(offlinePackagePath) ? BuildOfflinePackageHint(repositoryRoot, safeRuntimeDir) : offlinePackagePath));
            rows.Add(CreateRow("SpatiaLite Repair Script", File.Exists(scriptPath) ? StatusReady() : StatusWarning(), scriptPath));
            rows.Add(CreateRow("SpatiaLite Repair Command", File.Exists(scriptPath) ? StatusInfo() : StatusWarning(), BuildRepairCommand(repositoryRoot)));
            rows.Add(CreateRow("SpatiaLite Cached Repair Command", File.Exists(scriptPath) ? StatusInfo() : StatusWarning(), BuildCachedRepairCommand(repositoryRoot, offlinePackagePath)));
            rows.Add(CreateRow("SpatiaLite Repair Log", StatusInfo(), BuildRepairLogPath()));
            rows.Add(CreateRow("SpatiaLite Repair Guide", StatusInfo(), "執行修復命令會從 Gaia-SINS 官方 libspatialite 5.1.0 原始碼重建 runtime，並輸出到 " + safeRuntimeDir + "；可使用來源快取或 -OfflinePackagePath 指向離線 zip。"));

            if (!File.Exists(dllPath))
            {
                rows.Add(CreateRow("SpatiaLite Missing DLL", StatusWarning(), dllPath));
            }

            if (!string.IsNullOrWhiteSpace(loadError))
            {
                rows.Add(CreateRow("SpatiaLite Load Error", StatusWarning(), loadError));
            }

            return rows;
        }

        public static string FindRepositoryRoot(string startDirectory)
        {
            string current = string.IsNullOrWhiteSpace(startDirectory) ? Directory.GetCurrentDirectory() : startDirectory;
            current = Path.GetFullPath(current);

            while (!string.IsNullOrWhiteSpace(current))
            {
                if (File.Exists(Path.Combine(current, BuildScriptRelativePath)))
                {
                    return current;
                }

                DirectoryInfo parent = Directory.GetParent(current);
                if (parent == null) break;
                current = parent.FullName;
            }

            return string.Empty;
        }

        public static string BuildRepairCommand(string repositoryRoot)
        {
            string scriptPath = GetBuildScriptPath(repositoryRoot);
            if (string.IsNullOrWhiteSpace(scriptPath))
            {
                return "找不到 tools\\spatialite\\Build-SpatiaLiteRuntime.ps1，請重新取得完整 source tree 或參考 tools\\spatialite\\README.md。";
            }

            return "powershell -ExecutionPolicy Bypass -File \"" + scriptPath + "\"";
        }

        public static string BuildCachedRepairCommand(string repositoryRoot, string offlinePackagePath = "")
        {
            string scriptPath = GetBuildScriptPath(repositoryRoot);
            if (string.IsNullOrWhiteSpace(scriptPath))
            {
                return "找不到 tools\\spatialite\\Build-SpatiaLiteRuntime.ps1，請重新取得完整 source tree 或參考 tools\\spatialite\\README.md。";
            }

            string command = "powershell -ExecutionPolicy Bypass -File \"" + scriptPath + "\" -PreferCachedSource";
            if (!string.IsNullOrWhiteSpace(offlinePackagePath) && File.Exists(offlinePackagePath))
            {
                command += " -OfflinePackagePath \"" + offlinePackagePath + "\"";
            }
            return command;
        }

        public static ProcessStartInfo BuildRepairProcessStartInfo(string repositoryRoot, bool keepWindowOpen = true)
        {
            string scriptPath = GetBuildScriptPath(repositoryRoot);
            if (string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath))
            {
                throw new FileNotFoundException("SpatiaLite rebuild script not found.", scriptPath);
            }

            string logPath = BuildRepairLogPath();
            Directory.CreateDirectory(Path.GetDirectoryName(logPath));
            string command = "& '" + EscapePowerShellSingleQuoted(scriptPath) + "' 2>&1 | Tee-Object -FilePath '" +
                EscapePowerShellSingleQuoted(logPath) + "'";

            return new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = (keepWindowOpen ? "-NoExit " : string.Empty) + "-ExecutionPolicy Bypass -Command \"" + command.Replace("\"", "`\"") + "\"",
                WorkingDirectory = repositoryRoot,
                UseShellExecute = true
            };
        }

        public static string BuildRepairLogPath()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "mySQLPunk",
                "spatialite-repair-logs");
            return Path.Combine(dir, "spatialite-repair-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
        }

        private static string GetBuildScriptPath(string repositoryRoot)
        {
            if (string.IsNullOrWhiteSpace(repositoryRoot)) return string.Empty;
            return Path.Combine(repositoryRoot, BuildScriptRelativePath);
        }

        private static string GetSourceCachePath(string repositoryRoot)
        {
            if (string.IsNullOrWhiteSpace(repositoryRoot)) return string.Empty;
            return Path.Combine(repositoryRoot, "tools", "spatialite", "cache", SourceArchiveFileName);
        }

        private static string FindOfflinePackagePath(string repositoryRoot, string runtimeDir)
        {
            List<string> candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(repositoryRoot))
            {
                candidates.Add(Path.Combine(repositoryRoot, "tools", "spatialite", "offline", SourceArchiveFileName));
                candidates.Add(Path.Combine(repositoryRoot, "tools", "spatialite", "cache", SourceArchiveFileName));
            }
            if (!string.IsNullOrWhiteSpace(runtimeDir))
            {
                candidates.Add(Path.Combine(runtimeDir, SourceArchiveFileName));
                candidates.Add(Path.Combine(runtimeDir, "spatialite-runtime-offline.zip"));
            }

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate)) return candidate;
            }

            return string.Empty;
        }

        private static string BuildOfflinePackageHint(string repositoryRoot, string runtimeDir)
        {
            List<string> hints = new List<string>();
            if (!string.IsNullOrWhiteSpace(repositoryRoot))
            {
                hints.Add(Path.Combine(repositoryRoot, "tools", "spatialite", "offline", SourceArchiveFileName));
            }
            if (!string.IsNullOrWhiteSpace(runtimeDir))
            {
                hints.Add(Path.Combine(runtimeDir, SourceArchiveFileName));
            }

            return hints.Count == 0
                ? "尚未偵測到離線來源 zip。"
                : "尚未偵測到離線來源 zip；建議放置位置：" + string.Join(" 或 ", hints.ToArray());
        }

        private static string BuildManifestSourceSummary(string manifestPath)
        {
            if (!File.Exists(manifestPath)) return manifestPath;

            try
            {
                JObject manifest = JObject.Parse(File.ReadAllText(manifestPath));
                string sourceUrl = (string)manifest["source_url"];
                string sourceSha256 = (string)manifest["source_sha256"];
                string builtAtUtc = (string)manifest["built_at_utc"];
                List<string> parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(sourceUrl)) parts.Add("來源：" + sourceUrl);
                if (!string.IsNullOrWhiteSpace(sourceSha256)) parts.Add("SHA-256：" + sourceSha256);
                if (!string.IsNullOrWhiteSpace(builtAtUtc)) parts.Add("建置時間：" + builtAtUtc);
                return parts.Count == 0 ? manifestPath : string.Join("；", parts.ToArray());
            }
            catch (Exception ex)
            {
                return manifestPath + "（manifest 無法解析：" + ex.Message + "）";
            }
        }

        private static SpatiaLiteDiagnosticRow BuildManifestFileVerificationRow(string manifestPath, string runtimeDir)
        {
            if (!File.Exists(manifestPath))
            {
                return CreateRow("SpatiaLite Runtime File Verification", StatusWarning(), "找不到 runtime manifest，無法校驗 runtime 檔案。");
            }

            try
            {
                JObject manifest = JObject.Parse(File.ReadAllText(manifestPath));
                JArray files = manifest["files"] as JArray;
                if (files == null || files.Count == 0)
                {
                    return CreateRow("SpatiaLite Runtime File Verification", StatusInfo(), "manifest 未包含 files 清單，無法逐檔校驗。");
                }

                int verified = 0;
                int missing = 0;
                int sizeMismatch = 0;
                int hashMismatch = 0;
                int invalid = 0;
                List<string> issues = new List<string>();

                foreach (JToken file in files)
                {
                    string name = (string)file["name"];
                    string expectedHash = ((string)file["sha256"] ?? string.Empty).Trim().ToLowerInvariant();
                    long? expectedBytes = (long?)file["bytes"];
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    if (!IsSafeManifestFileName(name))
                    {
                        invalid++;
                        issues.Add(name + " 檔名不安全");
                        continue;
                    }

                    string path = Path.Combine(runtimeDir ?? string.Empty, name);
                    if (!File.Exists(path))
                    {
                        missing++;
                        issues.Add(name + " 缺少");
                        continue;
                    }

                    FileInfo info = new FileInfo(path);
                    if (expectedBytes.HasValue && info.Length != expectedBytes.Value)
                    {
                        sizeMismatch++;
                        issues.Add(name + " 大小不符");
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(expectedHash))
                    {
                        string actualHash = ComputeSha256(path);
                        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                        {
                            hashMismatch++;
                            issues.Add(name + " SHA-256 不符");
                            continue;
                        }
                    }

                    verified++;
                }

                if (missing == 0 && sizeMismatch == 0 && hashMismatch == 0)
                {
                    return CreateRow("SpatiaLite Runtime File Verification", StatusReady(), "已校驗 " + verified + " 個 runtime 檔案。");
                }

                string summary = "已校驗 " + verified + "，缺少 " + missing + "，大小不符 " + sizeMismatch + "，SHA-256 不符 " + hashMismatch + "，檔名不安全 " + invalid;
                if (issues.Count > 0) summary += "；" + string.Join("、", issues.ToArray());
                return CreateRow("SpatiaLite Runtime File Verification", StatusWarning(), summary);
            }
            catch (Exception ex)
            {
                return CreateRow("SpatiaLite Runtime File Verification", StatusWarning(), "manifest 無法校驗：" + ex.Message);
            }
        }

        private static bool IsSafeManifestFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            if (Path.IsPathRooted(name)) return false;
            return string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal);
        }

        private static string ComputeSha256(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
            {
                byte[] hash = sha.ComputeHash(stream);
                StringBuilder builder = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash)
                {
                    builder.Append(b.ToString("x2"));
                }
                return builder.ToString();
            }
        }

        private static string EscapePowerShellSingleQuoted(string value)
        {
            return (value ?? string.Empty).Replace("'", "''");
        }

        private static string StatusReady()
        {
            return Localization.T("Diagnostic.StatusReady");
        }

        private static string StatusWarning()
        {
            return Localization.T("Diagnostic.StatusWarning");
        }

        private static string StatusInfo()
        {
            return Localization.T("Diagnostic.StatusInfo");
        }

        private static SpatiaLiteDiagnosticRow CreateRow(string item, string status, string detail)
        {
            return new SpatiaLiteDiagnosticRow
            {
                Item = item,
                Status = status,
                Detail = detail ?? string.Empty
            };
        }
    }
}
