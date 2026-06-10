using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace mySQLPunk.lib
{
    public static class PreDeleteBackupArchiveService
    {
        public const int DefaultRetainCount = 20;
        public const string BackupArchivePattern = "*_before_delete_*.zip";
        public const string PreRestoreBackupArchivePattern = "*_before_restore_*.zip";

        public static string ArchiveAndPrune(string sourcePath, int retainCount = DefaultRetainCount, string searchPattern = BackupArchivePattern)
        {
            string archivePath = ArchiveBackupFile(sourcePath);
            string directory = Path.GetDirectoryName(archivePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                PruneOldArchives(directory, searchPattern, retainCount);
            }
            return archivePath;
        }

        public static string ArchiveBackupFile(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath)) throw new ArgumentException("sourcePath");
            if (!File.Exists(sourcePath)) throw new FileNotFoundException(Localization.T("Backup.FileNotFound"), sourcePath);
            if (sourcePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return sourcePath;

            string archivePath = BuildUniqueArchivePath(sourcePath);
            string directory = Path.GetDirectoryName(archivePath);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(sourcePath, Path.GetFileName(sourcePath), CompressionLevel.Optimal);
            }

            File.Delete(sourcePath);
            return archivePath;
        }

        public static int PruneOldArchives(string directory, string searchPattern = BackupArchivePattern, int retainCount = DefaultRetainCount)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return 0;
            if (retainCount < 1) retainCount = 1;
            if (string.IsNullOrWhiteSpace(searchPattern)) searchPattern = BackupArchivePattern;

            List<FileInfo> archives = new DirectoryInfo(directory)
                .GetFiles(searchPattern)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ThenByDescending(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            int deleted = 0;
            for (int i = retainCount; i < archives.Count; i++)
            {
                try
                {
                    archives[i].Delete();
                    deleted++;
                }
                catch
                {
                }
            }
            return deleted;
        }

        private static string BuildUniqueArchivePath(string sourcePath)
        {
            string directory = Path.GetDirectoryName(sourcePath);
            string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(sourcePath);
            string candidate = Path.Combine(directory ?? string.Empty, fileNameWithoutExtension + ".zip");
            if (!File.Exists(candidate)) return candidate;

            int suffix = 2;
            string next;
            do
            {
                next = Path.Combine(directory ?? string.Empty, fileNameWithoutExtension + "_" + suffix + ".zip");
                suffix++;
            }
            while (File.Exists(next));

            return next;
        }
    }
}
