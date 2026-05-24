using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

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

        public static List<SpatiaLiteDiagnosticRow> BuildRows(string runtimeDir, string loadError, string repositoryRoot)
        {
            List<SpatiaLiteDiagnosticRow> rows = new List<SpatiaLiteDiagnosticRow>();
            string safeRuntimeDir = runtimeDir ?? string.Empty;
            string dllPath = Path.Combine(safeRuntimeDir, "mod_spatialite.dll");
            string manifestPath = Path.Combine(safeRuntimeDir, RuntimeManifestFileName);
            string scriptPath = GetBuildScriptPath(repositoryRoot);

            rows.Add(CreateRow("SpatiaLite Runtime Manifest", File.Exists(manifestPath) ? "Ready" : "Warning", manifestPath));
            rows.Add(CreateRow("SpatiaLite Repair Script", File.Exists(scriptPath) ? "Ready" : "Warning", scriptPath));
            rows.Add(CreateRow("SpatiaLite Repair Command", File.Exists(scriptPath) ? "Info" : "Warning", BuildRepairCommand(repositoryRoot)));
            rows.Add(CreateRow("SpatiaLite Repair Log", "Info", BuildRepairLogPath()));
            rows.Add(CreateRow("SpatiaLite Repair Guide", "Info", "執行修復命令會從 Gaia-SINS 官方 libspatialite 5.1.0 原始碼重建 runtime，並輸出到 " + safeRuntimeDir));

            if (!File.Exists(dllPath))
            {
                rows.Add(CreateRow("SpatiaLite Missing DLL", "Warning", dllPath));
            }

            if (!string.IsNullOrWhiteSpace(loadError))
            {
                rows.Add(CreateRow("SpatiaLite Load Error", "Warning", loadError));
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

        private static string EscapePowerShellSingleQuoted(string value)
        {
            return (value ?? string.Empty).Replace("'", "''");
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
