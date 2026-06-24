using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace mySQLPunk.lib
{
    public sealed class DatabaseRenameOptions
    {
        public bool CopyMySqlObjects { get; set; }
        public int BatchSize { get; set; }
        public string SqliteFilePath { get; set; }

        public DatabaseRenameOptions()
        {
            CopyMySqlObjects = true;
            BatchSize = 1000;
        }
    }

    public sealed class DatabaseRenameResult
    {
        public string ProviderName { get; set; }
        public string OldName { get; set; }
        public string NewName { get; set; }
        public bool OldDatabaseRetained { get; set; }
        public int TablesCopied { get; set; }
        public int ViewsCopied { get; set; }
        public int RoutinesCopied { get; set; }
        public int TriggersCopied { get; set; }
        public string OldSqlitePath { get; set; }
        public string NewSqlitePath { get; set; }
        public readonly List<string> Messages = new List<string>();
    }

    public static class DatabaseRenameService
    {
        public static DatabaseRenameResult Rename(IDatabase db, string oldName, string newName, DatabaseRenameOptions options, Action<string> progress)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            options = options ?? new DatabaseRenameOptions();
            oldName = (oldName ?? string.Empty).Trim();
            newName = (newName ?? string.Empty).Trim();

            string provider = NormalizeProvider(db.ProviderName);
            ValidateDatabaseName(provider, newName);
            if (string.Equals(oldName, newName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(Localization.T("DatabaseRename.SameName"));
            }
            if (provider != "sqlite")
            {
                EnsureTargetDatabaseDoesNotExist(db, newName);
            }

            DatabaseRenameResult result = new DatabaseRenameResult
            {
                ProviderName = db.ProviderName,
                OldName = oldName,
                NewName = newName,
                OldDatabaseRetained = false
            };

            if (provider == "mysql")
            {
                RenameMySqlByCopy(db, oldName, newName, options, progress, result);
                return result;
            }

            if (provider == "postgresql")
            {
                ExecuteChecked(db, "ALTER DATABASE " + QuotePostgreSqlIdentifier(oldName) + " RENAME TO " + QuotePostgreSqlIdentifier(newName) + ";");
                AddMessage(result, progress, Localization.Format("DatabaseRename.NativeRenameDone", oldName, newName));
                return result;
            }

            if (provider == "mssql" || provider == "sqlserver")
            {
                ExecuteChecked(db, "ALTER DATABASE " + QuoteSqlServerIdentifier(oldName) + " MODIFY NAME = " + QuoteSqlServerIdentifier(newName) + ";");
                AddMessage(result, progress, Localization.Format("DatabaseRename.NativeRenameDone", oldName, newName));
                return result;
            }

            if (provider == "sqlite")
            {
                RenameSqliteFile(db, newName, options, progress, result);
                return result;
            }

            throw new NotSupportedException(Localization.Format("DatabaseRename.ProviderUnsupported", db.ProviderName));
        }

        public static void ValidateDatabaseName(string providerName, string name)
        {
            providerName = NormalizeProvider(providerName);
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException(Localization.T("DatabaseRename.NameRequired"));
            }

            if (name.IndexOf('\0') >= 0 || name.Any(char.IsControl))
            {
                throw new ArgumentException(Localization.T("DatabaseRename.NameHasControlCharacters"));
            }

            if (providerName == "mysql")
            {
                if (name.Length > 64) throw new ArgumentException(Localization.T("DatabaseRename.MySqlNameTooLong"));
                if (name.IndexOfAny(new[] { '/', '\\', '.' }) >= 0) throw new ArgumentException(Localization.T("DatabaseRename.MySqlInvalidName"));
                return;
            }

            if (providerName == "postgresql")
            {
                if (name.Length > 63) throw new ArgumentException(Localization.T("DatabaseRename.PostgreSqlNameTooLong"));
                return;
            }

            if (providerName == "mssql" || providerName == "sqlserver")
            {
                if (name.Length > 128) throw new ArgumentException(Localization.T("DatabaseRename.SqlServerNameTooLong"));
                return;
            }

            if (providerName == "sqlite")
            {
                char[] invalid = Path.GetInvalidFileNameChars()
                    .Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' })
                    .Distinct()
                    .ToArray();
                if (name.IndexOfAny(invalid) >= 0) throw new ArgumentException(Localization.T("DatabaseRename.SqliteInvalidName"));
            }
        }

        public static string BuildFailureMessage(string providerName, string oldName, string newName, Exception ex)
        {
            string reason = ex == null || string.IsNullOrWhiteSpace(ex.Message) ? Localization.T("Common.UnknownError") : ex.Message.Trim();
            return Localization.Format("DatabaseRename.FailedDetail", providerName ?? string.Empty, oldName ?? string.Empty, newName ?? string.Empty, reason);
        }

        private static void RenameMySqlByCopy(IDatabase db, string oldName, string newName, DatabaseRenameOptions options, Action<string> progress, DatabaseRenameResult result)
        {
            if (!options.CopyMySqlObjects)
            {
                throw new NotSupportedException(Localization.T("DatabaseRename.MySqlNativeUnsupported"));
            }

            AddMessage(result, progress, Localization.T("DatabaseRename.MySqlCopyNotice"));
            ExecuteChecked(db, "CREATE DATABASE " + QuoteMySqlIdentifier(newName) + ";");
            AddMessage(result, progress, Localization.Format("DatabaseRename.DatabaseCreated", newName));

            DatabaseCopyService copyService = new DatabaseCopyService(options.BatchSize);
            foreach (string tableName in SafeList(db.GetTables(oldName)))
            {
                DatabaseCopyItem source = new DatabaseCopyItem
                {
                    Database = db,
                    DatabaseName = oldName,
                    ObjectName = tableName,
                    ObjectKind = "table",
                    ProviderName = db.ProviderName
                };
                DatabaseCopyItem target = new DatabaseCopyItem
                {
                    Database = db,
                    DatabaseName = newName,
                    ObjectName = tableName,
                    ObjectKind = "table",
                    ProviderName = db.ProviderName
                };
                copyService.Copy(source, target, p => AddMessage(result, progress, p == null ? string.Empty : p.Message));
                result.TablesCopied++;
            }

            foreach (string viewName in SafeList(db.GetViews(oldName)))
            {
                DatabaseCopyItem source = new DatabaseCopyItem
                {
                    Database = db,
                    DatabaseName = oldName,
                    ObjectName = viewName,
                    ObjectKind = "view",
                    ProviderName = db.ProviderName
                };
                DatabaseCopyItem target = new DatabaseCopyItem
                {
                    Database = db,
                    DatabaseName = newName,
                    ObjectName = viewName,
                    ObjectKind = "view",
                    ProviderName = db.ProviderName
                };
                copyService.Copy(source, target, p => AddMessage(result, progress, p == null ? string.Empty : p.Message));
                result.ViewsCopied++;
            }

            CopyMySqlRoutinesAndTriggers(db, oldName, newName, result, progress);

            result.OldDatabaseRetained = true;
            AddMessage(result, progress, Localization.Format(
                "DatabaseRename.MySqlCopyDone",
                result.TablesCopied,
                result.ViewsCopied,
                result.RoutinesCopied,
                result.TriggersCopied));
            AddMessage(result, progress, Localization.T("DatabaseRename.MySqlOldDatabaseRetained"));
        }

        private static void CopyMySqlRoutinesAndTriggers(IDatabase db, string oldName, string newName, DatabaseRenameResult result, Action<string> progress)
        {
            MySqlExportOptions exportOptions = new MySqlExportOptions
            {
                IncludeStructure = true,
                IncludeData = false,
                IncludeDropStatements = false,
                IncludeCreateDatabase = false,
                IncludeUseDatabase = false,
                DisableForeignKeyChecks = false,
                IncludeTables = false,
                IncludeViews = false,
                IncludeRoutines = true,
                IncludeTriggers = true,
                RemoveDefiner = true
            };

            MySqlExportResult export = MySqlExportService.BuildExport(db, oldName, exportOptions, message => AddMessage(result, progress, message));
            result.RoutinesCopied = export.RoutineCount;
            result.TriggersCopied = export.TriggerCount;
            if (result.RoutinesCopied + result.TriggersCopied == 0) return;

            string script = "USE " + QuoteMySqlIdentifier(newName) + ";\r\n" + export.Sql;
            using (StringReader reader = new StringReader(script))
            {
                MySqlImportService.Execute(db, reader, new MySqlImportOptions(), message => AddMessage(result, progress, message));
            }
        }

        private static void RenameSqliteFile(IDatabase db, string newName, DatabaseRenameOptions options, Action<string> progress, DatabaseRenameResult result)
        {
            string oldPath = options == null ? string.Empty : options.SqliteFilePath;
            if (string.IsNullOrWhiteSpace(oldPath))
            {
                throw new NotSupportedException(Localization.T("DatabaseRename.SqliteRequiresConnectionPath"));
            }

            oldPath = Path.GetFullPath(oldPath);
            if (!File.Exists(oldPath))
            {
                throw new FileNotFoundException(Localization.Format("Database.SqliteFileMissing", oldPath), oldPath);
            }

            string newPath = BuildSqliteRenamePath(oldPath, newName);
            if (File.Exists(newPath))
            {
                throw new InvalidOperationException(Localization.Format("Object.TargetNameExists", newPath));
            }

            string oldConnectionString = "";
            my_sqlite sqlite = db as my_sqlite;
            if (sqlite != null && sqlite.MCT != null)
            {
                oldConnectionString = sqlite.MCT.ConnectionString;
            }

            db.Close();
            System.Data.SQLite.SQLiteConnection.ClearAllPools();
            File.Move(oldPath, newPath);

            string newConnectionString = BuildSqliteConnectionString(oldConnectionString, newPath);
            try
            {
                db.SetConn(newConnectionString);
                db.Open();
            }
            catch
            {
                try
                {
                    db.Close();
                    System.Data.SQLite.SQLiteConnection.ClearAllPools();
                    if (File.Exists(newPath) && !File.Exists(oldPath)) File.Move(newPath, oldPath);
                    if (!string.IsNullOrWhiteSpace(oldConnectionString)) db.SetConn(oldConnectionString);
                }
                catch { }
                throw;
            }

            result.OldSqlitePath = oldPath;
            result.NewSqlitePath = newPath;
            AddMessage(result, progress, Localization.Format("DatabaseRename.SqliteFileRenamed", oldPath, newPath));
        }

        private static string BuildSqliteRenamePath(string oldPath, string newName)
        {
            string fileName = (newName ?? string.Empty).Trim();
            ValidateDatabaseName("sqlite", fileName);
            if (string.IsNullOrEmpty(Path.GetExtension(fileName)))
            {
                fileName += Path.GetExtension(oldPath);
            }

            string directory = Path.GetDirectoryName(oldPath);
            if (string.IsNullOrEmpty(directory)) directory = Directory.GetCurrentDirectory();
            return Path.Combine(directory, fileName);
        }

        private static string BuildSqliteConnectionString(string oldConnectionString, string newPath)
        {
            try
            {
                System.Data.SQLite.SQLiteConnectionStringBuilder builder =
                    string.IsNullOrWhiteSpace(oldConnectionString)
                        ? new System.Data.SQLite.SQLiteConnectionStringBuilder()
                        : new System.Data.SQLite.SQLiteConnectionStringBuilder(oldConnectionString);
                builder.DataSource = newPath;
                return builder.ConnectionString;
            }
            catch
            {
                return "Data Source=" + newPath + ";Version=3;";
            }
        }

        private static void EnsureTargetDatabaseDoesNotExist(IDatabase db, string newName)
        {
            List<string> databases = db.GetDatabases();
            if (databases != null && databases.Any(name => string.Equals(name, newName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(Localization.Format("Object.TargetNameExists", newName));
            }
        }

        private static IEnumerable<string> SafeList(IEnumerable<string> values)
        {
            return values == null ? Enumerable.Empty<string>() : values.Where(v => !string.IsNullOrWhiteSpace(v));
        }

        private static void ExecuteChecked(IDatabase db, string sql)
        {
            Dictionary<string, string> result = db.ExecSQL(sql);
            if (result == null || !result.ContainsKey("status") || !string.Equals(result["status"], "OK", StringComparison.OrdinalIgnoreCase))
            {
                string reason = result != null && result.ContainsKey("message") ? result["message"] : Localization.T("Common.UnknownError");
                throw new InvalidOperationException(reason);
            }
        }

        private static void AddMessage(DatabaseRenameResult result, Action<string> progress, string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            if (result != null) result.Messages.Add(message);
            progress?.Invoke(message);
        }

        private static string NormalizeProvider(string providerName)
        {
            return (providerName ?? string.Empty).Trim().ToLowerInvariant();
        }

        private static string QuoteMySqlIdentifier(string name)
        {
            return "`" + (name ?? string.Empty).Replace("`", "``") + "`";
        }

        private static string QuotePostgreSqlIdentifier(string name)
        {
            return "\"" + (name ?? string.Empty).Replace("\"", "\"\"") + "\"";
        }

        private static string QuoteSqlServerIdentifier(string name)
        {
            return "[" + (name ?? string.Empty).Replace("]", "]]" ) + "]";
        }
    }
}
