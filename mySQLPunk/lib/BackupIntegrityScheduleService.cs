using System;
using System.Collections.Generic;
using System.IO;

namespace mySQLPunk.lib
{
    public sealed class BackupIntegrityScheduleReport
    {
        public int TotalFiles { get; set; }
        public int VerifiedFiles { get; set; }
        public int FailedFiles { get; set; }
        public int SkippedFiles { get; set; }
        public DateTime VerifiedAtUtc { get; set; }
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
                        Message = ex.Message,
                        SizeBytes = file.Exists ? file.Length : 0
                    });
                }
            }

            return report;
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
