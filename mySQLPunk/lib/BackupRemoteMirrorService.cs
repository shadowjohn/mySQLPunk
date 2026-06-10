using System;
using System.Collections.Generic;
using System.IO;

namespace mySQLPunk.lib
{
    public static class BackupRemoteMirrorService
    {
        public const int DefaultRetainCount = 20;

        public static string MirrorBackup(string sourcePath, string destinationDirectory)
        {
            return MirrorBackup(sourcePath, destinationDirectory, DefaultRetainCount);
        }

        public static string MirrorBackup(string sourcePath, string destinationDirectory, int retainCount)
        {
            if (string.IsNullOrWhiteSpace(sourcePath)) throw new ArgumentException("sourcePath");
            if (!File.Exists(sourcePath)) throw new FileNotFoundException(Localization.T("Backup.FileNotFound"), sourcePath);
            if (string.IsNullOrWhiteSpace(destinationDirectory)) return string.Empty;

            string sourceFullPath = Path.GetFullPath(sourcePath);
            string destinationFullDirectory = Path.GetFullPath(destinationDirectory);
            string sourceDirectory = Path.GetDirectoryName(sourceFullPath);
            if (string.Equals(sourceDirectory, destinationFullDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            Directory.CreateDirectory(destinationFullDirectory);
            string targetPath = BuildUniqueMirrorPath(destinationFullDirectory, Path.GetFileName(sourceFullPath));
            File.Copy(sourceFullPath, targetPath, false);
            PruneRemoteBackups(destinationFullDirectory, retainCount);
            return targetPath;
        }

        public static int PruneRemoteBackups(string destinationDirectory, int retainCount)
        {
            if (string.IsNullOrWhiteSpace(destinationDirectory) || !Directory.Exists(destinationDirectory)) return 0;
            if (retainCount <= 0) return 0;

            FileInfo[] files = new DirectoryInfo(destinationDirectory).GetFiles();
            Array.Sort(files, (left, right) => right.LastWriteTimeUtc.CompareTo(left.LastWriteTimeUtc));

            Dictionary<string, int> keptByKind = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int deleted = 0;
            foreach (FileInfo file in files)
            {
                string kind = GetManagedBackupKind(file.Name);
                if (string.IsNullOrWhiteSpace(kind)) continue;

                int kept;
                keptByKind.TryGetValue(kind, out kept);
                kept++;
                keptByKind[kind] = kept;
                if (kept <= retainCount) continue;

                try
                {
                    file.Delete();
                    deleted++;
                }
                catch
                {
                }
            }

            return deleted;
        }

        private static string GetManagedBackupKind(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return "";
            string lower = fileName.ToLowerInvariant();
            if (lower.Contains("_before_delete_")) return "before_delete";
            if (lower.Contains("_before_restore_")) return "before_restore";
            if (lower.Contains("_backup_")) return "backup";
            return "";
        }

        private static string BuildUniqueMirrorPath(string directory, string fileName)
        {
            string candidate = Path.Combine(directory, fileName);
            if (!File.Exists(candidate)) return candidate;

            string name = Path.GetFileNameWithoutExtension(fileName);
            string extension = Path.GetExtension(fileName);
            int suffix = 2;
            do
            {
                candidate = Path.Combine(directory, name + "_" + suffix + extension);
                suffix++;
            }
            while (File.Exists(candidate));

            return candidate;
        }
    }
}
