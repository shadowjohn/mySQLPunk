using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace mySQLPunk.lib
{
    public sealed class BackupIntegrityScheduleReport
    {
        public int TotalFiles { get; set; }
        public int VerifiedFiles { get; set; }
        public int FailedFiles { get; set; }
        public int SkippedFiles { get; set; }
        public DateTime VerifiedAtUtc { get; set; }
        public BackupIntegrityQuarantineResult QuarantineResult { get; set; }
        public List<BackupIntegrityResult> FailedResults { get; private set; }

        public BackupIntegrityScheduleReport()
        {
            FailedResults = new List<BackupIntegrityResult>();
            VerifiedAtUtc = DateTime.UtcNow;
        }

        public bool HasFailures
        {
            get { return FailedFiles > 0; }
        }
    }

    public sealed class BackupIntegrityQuarantineResult
    {
        public int TotalCandidates { get; set; }
        public int MovedFiles { get; set; }
        public int SkippedFiles { get; set; }
        public int FailedMoves { get; set; }
        public int DeletedOldFiles { get; set; }
        public string QuarantineDirectory { get; set; }
        public string ManifestPath { get; set; }
        public List<string> MovedPaths { get; private set; }
        public List<BackupIntegrityQuarantineEntry> Entries { get; private set; }

        public BackupIntegrityQuarantineResult()
        {
            QuarantineDirectory = "";
            ManifestPath = "";
            MovedPaths = new List<string>();
            Entries = new List<BackupIntegrityQuarantineEntry>();
        }
    }

    public sealed class BackupIntegrityQuarantineEntry
    {
        public string OriginalPath { get; set; }
        public string QuarantinedPath { get; set; }
        public string Message { get; set; }
        public long SizeBytes { get; set; }
        public DateTime QuarantinedAtUtc { get; set; }
    }

    public static class BackupIntegrityScheduleService
    {
        public const int DefaultIntervalHours = 24;
        private const int DefaultMaxFiles = 200;

        public static bool IsDue(bool enabled, DateTime lastVerifiedUtc, int intervalHours, DateTime nowUtc)
        {
            if (!enabled) return false;
            if (intervalHours <= 0) intervalHours = DefaultIntervalHours;
            if (lastVerifiedUtc == DateTime.MinValue) return true;
            return nowUtc.ToUniversalTime() - lastVerifiedUtc.ToUniversalTime() >= TimeSpan.FromHours(intervalHours);
        }

        public static BackupIntegrityScheduleReport VerifyDirectories(
            IEnumerable<string> directories,
            Func<string, int> countStatements)
        {
            return VerifyDirectories(directories, countStatements, DefaultMaxFiles);
        }

        public static BackupIntegrityScheduleReport VerifyDirectories(
            IEnumerable<string> directories,
            Func<string, int> countStatements,
            int maxFiles)
        {
            BackupIntegrityScheduleReport report = new BackupIntegrityScheduleReport();
            int skippedFiles;
            List<FileInfo> files = FindBackupFiles(directories, maxFiles, out skippedFiles);
            report.TotalFiles = files.Count + skippedFiles;
            report.SkippedFiles = skippedFiles;

            foreach (FileInfo file in files)
            {
                try
                {
                    BackupIntegrityResult result = BackupIntegrityService.VerifyBackup(file.FullName, countStatements);
                    if (result.IsValid)
                    {
                        report.VerifiedFiles++;
                    }
                    else
                    {
                        report.FailedFiles++;
                        result.SourcePath = file.FullName;
                        if (string.IsNullOrWhiteSpace(result.EntryName)) result.EntryName = file.FullName;
                        report.FailedResults.Add(result);
                    }
                }
                catch (Exception ex)
                {
                    report.FailedFiles++;
                    report.FailedResults.Add(new BackupIntegrityResult
                    {
                        IsValid = false,
                        Kind = Path.GetExtension(file.Name).TrimStart('.').ToLowerInvariant(),
                        EntryName = file.FullName,
                        SourcePath = file.FullName,
                        Message = ex.Message,
                        SizeBytes = file.Exists ? file.Length : 0
                    });
                }
            }

            return report;
        }

        public static BackupIntegrityQuarantineResult QuarantineFailedBackups(
            BackupIntegrityScheduleReport report,
            string quarantineDirectory)
        {
            return QuarantineFailedBackups(report, quarantineDirectory, 0);
        }

        public static BackupIntegrityQuarantineResult QuarantineFailedBackups(
            BackupIntegrityScheduleReport report,
            string quarantineDirectory,
            int retainCount)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));
            if (string.IsNullOrWhiteSpace(quarantineDirectory)) throw new ArgumentException("Quarantine directory is required.", nameof(quarantineDirectory));

            BackupIntegrityQuarantineResult result = new BackupIntegrityQuarantineResult
            {
                QuarantineDirectory = quarantineDirectory
            };
            Directory.CreateDirectory(quarantineDirectory);

            HashSet<string> handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (BackupIntegrityResult failure in report.FailedResults)
            {
                string sourcePath = failure == null ? string.Empty : failure.SourcePath;
                if (string.IsNullOrWhiteSpace(sourcePath) || !handled.Add(sourcePath))
                {
                    result.SkippedFiles++;
                    continue;
                }

                result.TotalCandidates++;
                try
                {
                    if (!File.Exists(sourcePath))
                    {
                        result.SkippedFiles++;
                        continue;
                    }

                    string destinationPath = BuildUniqueQuarantinePath(quarantineDirectory, sourcePath);
                    File.Move(sourcePath, destinationPath);
                    result.MovedFiles++;
                    result.MovedPaths.Add(destinationPath);
                    result.Entries.Add(new BackupIntegrityQuarantineEntry
                    {
                        OriginalPath = sourcePath,
                        QuarantinedPath = destinationPath,
                        Message = failure.Message ?? string.Empty,
                        SizeBytes = failure.SizeBytes,
                        QuarantinedAtUtc = DateTime.UtcNow
                    });
                    failure.SourcePath = destinationPath;
                    failure.Message = failure.Message + " Quarantined from: " + sourcePath;
                }
                catch
                {
                    result.FailedMoves++;
                }
            }

            string manifestPath = Path.Combine(
                quarantineDirectory,
                "backup-quarantine_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + ".json");
            result.ManifestPath = manifestPath;
            File.WriteAllText(manifestPath, JsonConvert.SerializeObject(result, Formatting.Indented), Encoding.UTF8);
            if (retainCount > 0)
            {
                result.DeletedOldFiles = PruneQuarantine(quarantineDirectory, retainCount);
                File.WriteAllText(manifestPath, JsonConvert.SerializeObject(result, Formatting.Indented), Encoding.UTF8);
            }
            report.QuarantineResult = result;
            return result;
        }

        public static int PruneQuarantine(string quarantineDirectory, int retainCount)
        {
            if (string.IsNullOrWhiteSpace(quarantineDirectory) || !Directory.Exists(quarantineDirectory)) return 0;
            if (retainCount <= 0) return 0;

            FileInfo[] files = new DirectoryInfo(quarantineDirectory).GetFiles();
            List<FileInfo> managedFiles = new List<FileInfo>();
            foreach (FileInfo file in files)
            {
                if (IsManagedQuarantineFile(file.Name))
                {
                    managedFiles.Add(file);
                }
            }

            managedFiles.Sort((left, right) => right.LastWriteTimeUtc.CompareTo(left.LastWriteTimeUtc));

            int deleted = 0;
            for (int i = retainCount; i < managedFiles.Count; i++)
            {
                try
                {
                    managedFiles[i].Delete();
                    deleted++;
                }
                catch
                {
                }
            }

            return deleted;
        }

        public static string WriteReport(BackupIntegrityScheduleReport report, string reportDirectory)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));
            if (string.IsNullOrWhiteSpace(reportDirectory)) throw new ArgumentException("Report directory is required.", nameof(reportDirectory));

            Directory.CreateDirectory(reportDirectory);
            string fileName = "backup-integrity-report_" + report.VerifiedAtUtc.ToString("yyyyMMdd_HHmmss") + ".json";
            string path = Path.Combine(reportDirectory, fileName);
            File.WriteAllText(path, JsonConvert.SerializeObject(report, Formatting.Indented), Encoding.UTF8);
            return path;
        }

        private static string BuildUniqueQuarantinePath(string quarantineDirectory, string sourcePath)
        {
            string fileName = Path.GetFileName(sourcePath);
            if (string.IsNullOrWhiteSpace(fileName)) fileName = "backup.bin";

            string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            string candidate = Path.Combine(quarantineDirectory, timestamp + "_" + fileName);
            if (!File.Exists(candidate)) return candidate;

            string name = Path.GetFileNameWithoutExtension(fileName);
            string extension = Path.GetExtension(fileName);
            int suffix = 2;
            do
            {
                candidate = Path.Combine(quarantineDirectory, timestamp + "_" + name + "_" + suffix + extension);
                suffix++;
            }
            while (File.Exists(candidate));
            return candidate;
        }

        private static bool IsManagedQuarantineFile(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return false;
            if (fileName.StartsWith("backup-quarantine_", StringComparison.OrdinalIgnoreCase)) return true;
            if (fileName.Length < 16 || fileName[15] != '_') return false;

            for (int i = 0; i < 15; i++)
            {
                if (i == 8)
                {
                    if (fileName[i] != '_') return false;
                }
                else if (!char.IsDigit(fileName[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static List<FileInfo> FindBackupFiles(IEnumerable<string> directories, int maxFiles, out int skippedFiles)
        {
            skippedFiles = 0;
            Dictionary<string, FileInfo> files = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
            if (directories == null) return new List<FileInfo>();

            foreach (string directory in directories)
            {
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) continue;

                foreach (string path in Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly))
                {
                    if (!IsSupportedBackupFile(path)) continue;
                    if (!files.ContainsKey(path)) files[path] = new FileInfo(path);
                }
            }

            List<FileInfo> ordered = new List<FileInfo>(files.Values);
            ordered.Sort((left, right) => right.LastWriteTimeUtc.CompareTo(left.LastWriteTimeUtc));

            if (maxFiles > 0 && ordered.Count > maxFiles)
            {
                skippedFiles = ordered.Count - maxFiles;
                ordered.RemoveRange(maxFiles, skippedFiles);
            }

            return ordered;
        }

        private static bool IsSupportedBackupFile(string path)
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();
            return extension == ".sql" ||
                   extension == ".zip" ||
                   extension == ".sqlite" ||
                   extension == ".sqlite3" ||
                   extension == ".db";
        }
    }
}
