using System;
using System.Data;
using System.Data.SQLite;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace mySQLPunk.lib
{
    public sealed class BackupIntegrityResult
    {
        public bool IsValid { get; set; }
        public string Kind { get; set; }
        public string EntryName { get; set; }
        public string SourcePath { get; set; }
        public string Message { get; set; }
        public int StatementCount { get; set; }
        public long SizeBytes { get; set; }
    }

    public static class BackupIntegrityService
    {
        public static BackupIntegrityResult VerifyBackup(string sourcePath, Func<string, int> countStatements)
        {
            BackupIntegrityResult result = new BackupIntegrityResult
            {
                IsValid = false,
                Kind = "",
                EntryName = "",
                SourcePath = sourcePath ?? "",
                Message = "",
                StatementCount = 0,
                SizeBytes = 0
            };

            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                result.Message = "Backup path is empty.";
                return result;
            }

            if (!File.Exists(sourcePath))
            {
                result.Message = "Backup file does not exist.";
                return result;
            }

            result.SizeBytes = new FileInfo(sourcePath).Length;
            if (result.SizeBytes <= 0)
            {
                result.Message = "Backup file is empty.";
                return result;
            }

            string extension = Path.GetExtension(sourcePath).ToLowerInvariant();
            if (extension == ".zip")
            {
                return VerifyZipBackup(sourcePath, countStatements, result);
            }

            if (extension == ".sql")
            {
                return VerifySqlBackup(File.ReadAllText(sourcePath, Encoding.UTF8), Path.GetFileName(sourcePath), countStatements, result);
            }

            if (IsSqliteExtension(extension))
            {
                return VerifySqliteBackup(sourcePath, Path.GetFileName(sourcePath), result);
            }

            result.Kind = "unknown";
            result.Message = "Unsupported backup file type.";
            return result;
        }

        private static BackupIntegrityResult VerifyZipBackup(string sourcePath, Func<string, int> countStatements, BackupIntegrityResult result)
        {
            result.Kind = "zip";
            using (ZipArchive archive = ZipFile.OpenRead(sourcePath))
            {
                ZipArchiveEntry selected = null;
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (entry == null || string.IsNullOrWhiteSpace(entry.Name)) continue;
                    string extension = Path.GetExtension(entry.Name).ToLowerInvariant();
                    if (extension != ".sql" && !IsSqliteExtension(extension)) continue;
                    if (selected == null || entry.Length > selected.Length) selected = entry;
                }

                if (selected == null)
                {
                    result.Message = "Zip backup does not contain a SQL or SQLite backup entry.";
                    return result;
                }

                result.EntryName = selected.FullName;
                string selectedExtension = Path.GetExtension(selected.Name).ToLowerInvariant();
                if (selectedExtension == ".sql")
                {
                    using (Stream stream = selected.Open())
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
                    {
                        return VerifySqlBackup(reader.ReadToEnd(), selected.FullName, countStatements, result);
                    }
                }

                string tempPath = Path.Combine(Path.GetTempPath(), "mysqlpunk_verify_" + Guid.NewGuid().ToString("N") + selectedExtension);
                try
                {
                    selected.ExtractToFile(tempPath);
                    return VerifySqliteBackup(tempPath, selected.FullName, result);
                }
                finally
                {
                    try
                    {
                        if (File.Exists(tempPath)) File.Delete(tempPath);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static BackupIntegrityResult VerifySqlBackup(string script, string entryName, Func<string, int> countStatements, BackupIntegrityResult result)
        {
            result.Kind = "sql";
            result.EntryName = entryName ?? "";
            if (string.IsNullOrWhiteSpace(script))
            {
                result.Message = "SQL backup is empty.";
                return result;
            }

            int statements = countStatements == null ? 1 : countStatements(script);
            result.StatementCount = statements;
            if (statements <= 0)
            {
                result.Message = "SQL backup has no executable statements.";
                return result;
            }

            result.IsValid = true;
            result.Message = "SQL backup is readable.";
            return result;
        }

        private static BackupIntegrityResult VerifySqliteBackup(string sqlitePath, string entryName, BackupIntegrityResult result)
        {
            result.Kind = "sqlite";
            result.EntryName = entryName ?? "";
            using (SQLiteConnection connection = new SQLiteConnection("Data Source=" + sqlitePath + ";Version=3;Read Only=True;"))
            using (SQLiteCommand command = new SQLiteCommand("PRAGMA integrity_check;", connection))
            {
                connection.Open();
                object value = command.ExecuteScalar();
                string text = value == null || value == DBNull.Value ? "" : value.ToString();
                if (!string.Equals(text, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    result.Message = "SQLite integrity_check failed: " + text;
                    return result;
                }
            }

            result.IsValid = true;
            result.Message = "SQLite backup passed integrity_check.";
            return result;
        }

        private static bool IsSqliteExtension(string extension)
        {
            return extension == ".sqlite" || extension == ".sqlite3" || extension == ".db";
        }
    }
}
