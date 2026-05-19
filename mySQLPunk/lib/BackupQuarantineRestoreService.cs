using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace mySQLPunk.lib
{
    public sealed class BackupQuarantineRestoreCandidate
    {
        public string QuarantinedPath { get; set; }
        public string OriginalPath { get; set; }
        public string ManifestPath { get; set; }
        public long SizeBytes { get; set; }
        public DateTime QuarantinedAtUtc { get; set; }

        public bool HasOriginalPath
        {
            get { return !string.IsNullOrWhiteSpace(OriginalPath); }
        }
    }

    public sealed class BackupQuarantineRestoreResult
    {
        public string SourcePath { get; set; }
        public string RestoredPath { get; set; }
        public long SizeBytes { get; set; }
    }

    public sealed class BackupQuarantineRestorePreview
    {
        public BackupQuarantineRestoreCandidate Candidate { get; set; }
        public BackupIntegrityResult IntegrityResult { get; set; }
        public string DestinationDiffSummary { get; set; }

        public bool PassedIntegrityCheck
        {
            get { return IntegrityResult != null && IntegrityResult.IsValid; }
        }
    }

    public sealed class BackupQuarantineBatchRestoreResult
    {
        public int TotalCandidates { get; set; }
        public int RestoredFiles { get; set; }
        public int SkippedNoOriginalPath { get; set; }
        public int SkippedExistingDestination { get; set; }
        public int FailedFiles { get; set; }
        public List<BackupQuarantineRestoreResult> RestoredResults { get; private set; }
        public List<string> Messages { get; private set; }

        public BackupQuarantineBatchRestoreResult()
        {
            RestoredResults = new List<BackupQuarantineRestoreResult>();
            Messages = new List<string>();
        }
    }

    public static class BackupQuarantineRestoreService
    {
        public static List<BackupQuarantineRestoreCandidate> FindCandidates(string quarantineDirectory)
        {
            Dictionary<string, BackupQuarantineRestoreCandidate> candidates =
                new Dictionary<string, BackupQuarantineRestoreCandidate>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(quarantineDirectory) || !Directory.Exists(quarantineDirectory))
            {
                return new List<BackupQuarantineRestoreCandidate>();
            }

            foreach (string manifestPath in Directory.EnumerateFiles(quarantineDirectory, "backup-quarantine_*.json", SearchOption.TopDirectoryOnly))
            {
                AddManifestCandidates(candidates, manifestPath);
            }

            foreach (string path in Directory.EnumerateFiles(quarantineDirectory, "*.*", SearchOption.TopDirectoryOnly))
            {
                if (!IsSupportedBackupFile(path)) continue;
                if (candidates.ContainsKey(path)) continue;

                FileInfo info = new FileInfo(path);
                candidates[path] = new BackupQuarantineRestoreCandidate
                {
                    QuarantinedPath = path,
                    OriginalPath = "",
                    ManifestPath = "",
                    SizeBytes = info.Exists ? info.Length : 0,
                    QuarantinedAtUtc = info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue
                };
            }

            List<BackupQuarantineRestoreCandidate> ordered = new List<BackupQuarantineRestoreCandidate>(candidates.Values);
            ordered.Sort((left, right) => right.QuarantinedAtUtc.CompareTo(left.QuarantinedAtUtc));
            return ordered;
        }

        public static BackupQuarantineRestoreCandidate FindCandidate(string quarantinedPath, string quarantineDirectory)
        {
            string fullPath = Path.GetFullPath(quarantinedPath ?? string.Empty);
            foreach (BackupQuarantineRestoreCandidate candidate in FindCandidates(quarantineDirectory))
            {
                if (string.Equals(Path.GetFullPath(candidate.QuarantinedPath), fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }

            if (!File.Exists(fullPath) || !IsSupportedBackupFile(fullPath)) return null;
            FileInfo info = new FileInfo(fullPath);
            return new BackupQuarantineRestoreCandidate
            {
                QuarantinedPath = fullPath,
                OriginalPath = "",
                ManifestPath = "",
                SizeBytes = info.Length,
                QuarantinedAtUtc = info.LastWriteTimeUtc
            };
        }

        public static BackupQuarantineRestorePreview BuildPreview(
            BackupQuarantineRestoreCandidate candidate,
            Func<string, int> countStatements)
        {
            if (candidate == null) throw new ArgumentNullException(nameof(candidate));
            BackupIntegrityResult result;
            try
            {
                result = BackupIntegrityService.VerifyBackup(candidate.QuarantinedPath, countStatements);
            }
            catch (Exception ex)
            {
                result = new BackupIntegrityResult
                {
                    IsValid = false,
                    Kind = Path.GetExtension(candidate.QuarantinedPath).TrimStart('.').ToLowerInvariant(),
                    EntryName = candidate.QuarantinedPath,
                    SourcePath = candidate.QuarantinedPath,
                    Message = ex.Message,
                    SizeBytes = candidate.SizeBytes
                };
            }

            return new BackupQuarantineRestorePreview
            {
                Candidate = candidate,
                IntegrityResult = result,
                DestinationDiffSummary = BuildDestinationDiffSummary(candidate)
            };
        }

        public static string BuildDestinationDiffSummary(BackupQuarantineRestoreCandidate candidate)
        {
            if (candidate == null) return string.Empty;
            if (!candidate.HasOriginalPath)
            {
                return "目標差異：沒有 manifest 原始路徑，需手動選擇還原位置。";
            }

            string originalPath = candidate.OriginalPath;
            if (!File.Exists(originalPath))
            {
                return "目標差異：原始路徑目前不存在，還原會重新建立檔案。";
            }

            FileInfo destination = new FileInfo(originalPath);
            long beforeSize = destination.Exists ? destination.Length : 0;
            long afterSize = candidate.SizeBytes;
            long delta = afterSize - beforeSize;
            string deltaText = delta == 0 ? "0" : (delta > 0 ? "+" : "") + delta.ToString();
            return "目標差異：原始路徑已有檔案，大小 " +
                   beforeSize + " -> " + afterSize + " bytes (" + deltaText + " bytes)，還原時需確認是否覆蓋。";
        }

        public static BackupQuarantineRestoreResult RestoreQuarantinedFile(string quarantinedPath, string destinationPath, bool overwrite)
        {
            if (string.IsNullOrWhiteSpace(quarantinedPath)) throw new ArgumentException("Quarantined path is required.", nameof(quarantinedPath));
            if (string.IsNullOrWhiteSpace(destinationPath)) throw new ArgumentException("Destination path is required.", nameof(destinationPath));
            if (!File.Exists(quarantinedPath)) throw new FileNotFoundException("Quarantined backup file does not exist.", quarantinedPath);
            if (!IsSupportedBackupFile(quarantinedPath)) throw new InvalidOperationException("Unsupported backup file type.");

            quarantinedPath = Path.GetFullPath(quarantinedPath);
            destinationPath = Path.GetFullPath(destinationPath);
            if (string.Equals(quarantinedPath, destinationPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Destination path must be different from the quarantined file path.");
            }

            string destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            if (File.Exists(destinationPath))
            {
                if (!overwrite) throw new IOException("Destination file already exists.");
                File.Delete(destinationPath);
            }

            long sizeBytes = new FileInfo(quarantinedPath).Length;
            File.Move(quarantinedPath, destinationPath);
            return new BackupQuarantineRestoreResult
            {
                SourcePath = quarantinedPath,
                RestoredPath = destinationPath,
                SizeBytes = sizeBytes
            };
        }

        public static BackupQuarantineBatchRestoreResult RestoreAllToOriginalPaths(
            IEnumerable<BackupQuarantineRestoreCandidate> candidates,
            bool overwrite)
        {
            BackupQuarantineBatchRestoreResult result = new BackupQuarantineBatchRestoreResult();
            if (candidates == null) return result;

            HashSet<string> handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (BackupQuarantineRestoreCandidate candidate in candidates)
            {
                if (candidate == null) continue;
                result.TotalCandidates++;

                string sourcePath = candidate.QuarantinedPath ?? string.Empty;
                if (string.IsNullOrWhiteSpace(sourcePath) || !handled.Add(Path.GetFullPath(sourcePath)))
                {
                    result.FailedFiles++;
                    continue;
                }

                if (!candidate.HasOriginalPath)
                {
                    result.SkippedNoOriginalPath++;
                    continue;
                }

                if (File.Exists(candidate.OriginalPath) && !overwrite)
                {
                    result.SkippedExistingDestination++;
                    result.Messages.Add("Skipped existing destination: " + candidate.OriginalPath);
                    continue;
                }

                try
                {
                    BackupQuarantineRestoreResult restored =
                        RestoreQuarantinedFile(candidate.QuarantinedPath, candidate.OriginalPath, overwrite);
                    result.RestoredFiles++;
                    result.RestoredResults.Add(restored);
                }
                catch (Exception ex)
                {
                    result.FailedFiles++;
                    result.Messages.Add(candidate.QuarantinedPath + ": " + ex.Message);
                }
            }

            return result;
        }

        private static void AddManifestCandidates(
            Dictionary<string, BackupQuarantineRestoreCandidate> candidates,
            string manifestPath)
        {
            try
            {
                BackupIntegrityQuarantineResult manifest =
                    JsonConvert.DeserializeObject<BackupIntegrityQuarantineResult>(File.ReadAllText(manifestPath, Encoding.UTF8));
                if (manifest == null) return;

                if (manifest.Entries != null)
                {
                    foreach (BackupIntegrityQuarantineEntry entry in manifest.Entries)
                    {
                        if (entry == null || string.IsNullOrWhiteSpace(entry.QuarantinedPath)) continue;
                        if (!File.Exists(entry.QuarantinedPath)) continue;

                        FileInfo info = new FileInfo(entry.QuarantinedPath);
                        candidates[entry.QuarantinedPath] = new BackupQuarantineRestoreCandidate
                        {
                            QuarantinedPath = entry.QuarantinedPath,
                            OriginalPath = entry.OriginalPath ?? string.Empty,
                            ManifestPath = manifestPath,
                            SizeBytes = info.Length,
                            QuarantinedAtUtc = entry.QuarantinedAtUtc == DateTime.MinValue ? info.LastWriteTimeUtc : entry.QuarantinedAtUtc
                        };
                    }
                }

                if (manifest.MovedPaths == null) return;
                foreach (string path in manifest.MovedPaths)
                {
                    if (string.IsNullOrWhiteSpace(path) || candidates.ContainsKey(path) || !File.Exists(path)) continue;
                    FileInfo info = new FileInfo(path);
                    candidates[path] = new BackupQuarantineRestoreCandidate
                    {
                        QuarantinedPath = path,
                        OriginalPath = "",
                        ManifestPath = manifestPath,
                        SizeBytes = info.Length,
                        QuarantinedAtUtc = info.LastWriteTimeUtc
                    };
                }
            }
            catch
            {
            }
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
