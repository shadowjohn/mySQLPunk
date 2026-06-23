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
            EnsureTargetDatabaseDoesNotExist(db, newName);

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
                throw new NotSupportedException(Localization.T("DatabaseRename.SqliteRequiresConnectionPath"));
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
                char[] invalid = Path.GetInvalidFileNameChars();
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
                    ObjectKind = "view",
                    ProviderName = db.ProviderName
                };
                copyService.Copy(source, target, p => AddMessage(result, progress, p == null ? string.Empty : p.Message));
                result.ViewsCopied++;
            }

            result.OldDatabaseRetained = true;
            AddMessage(result, progress, Localization.Format("DatabaseRename.MySqlCopyDone", result.TablesCopied, result.ViewsCopied));
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
