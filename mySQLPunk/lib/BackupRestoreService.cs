using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace mySQLPunk.lib
{
    public sealed class BackupRestorePackage
    {
        public string SourcePath { get; set; }
        public string EntryName { get; set; }
        public string Script { get; set; }
        public int StatementCount { get; set; }
        public long SizeBytes { get; set; }
        public bool IsZip { get; set; }
    }

    public static class BackupRestoreService
    {
        public static BackupRestorePackage LoadRestorePackage(string sourcePath, Func<string, int> countStatements)
        {
            if (string.IsNullOrWhiteSpace(sourcePath)) throw new ArgumentException("sourcePath");
            if (!File.Exists(sourcePath)) throw new FileNotFoundException(Localization.T("Backup.FileNotFound"), sourcePath);

            string extension = Path.GetExtension(sourcePath);
            string script;
            string entryName = Path.GetFileName(sourcePath);
            bool isZip = string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase);
            long sizeBytes = new FileInfo(sourcePath).Length;

            if (isZip)
            {
                script = ReadSqlFromZip(sourcePath, out entryName, out sizeBytes);
            }
            else
            {
                script = File.ReadAllText(sourcePath, Encoding.UTF8);
            }

            if (string.IsNullOrWhiteSpace(script)) throw new InvalidOperationException(Localization.T("Backup.RestoreSqlEmpty"));

            return new BackupRestorePackage
            {
                SourcePath = sourcePath,
                EntryName = entryName,
                Script = script,
                IsZip = isZip,
                SizeBytes = sizeBytes,
                StatementCount = countStatements == null ? 0 : countStatements(script)
            };
        }

        private static string ReadSqlFromZip(string sourcePath, out string entryName, out long sizeBytes)
        {
            using (ZipArchive archive = ZipFile.OpenRead(sourcePath))
            {
                ZipArchiveEntry selected = null;
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (entry == null || string.IsNullOrWhiteSpace(entry.Name)) continue;
                    if (!string.Equals(Path.GetExtension(entry.Name), ".sql", StringComparison.OrdinalIgnoreCase)) continue;
                    if (selected == null || entry.Length > selected.Length) selected = entry;
                }

                if (selected == null) throw new InvalidOperationException(Localization.T("Backup.RestoreZipNoSqlEntry"));

                entryName = selected.FullName;
                sizeBytes = selected.Length;
                using (Stream stream = selected.Open())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
                {
                    return reader.ReadToEnd();
                }
            }
        }
    }
}
