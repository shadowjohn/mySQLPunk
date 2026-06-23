using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Text;

namespace mySQLPunk.lib
{
    public static class DatabaseDumpService
    {
        public static void WriteDatabaseDump(IDatabase db, string databaseName, string targetPath)
        {
            if (string.IsNullOrWhiteSpace(targetPath)) throw new ArgumentException(Localization.T("Common.TargetPathRequired"), nameof(targetPath));

            string dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            if (File.Exists(targetPath)) File.Delete(targetPath);
            File.WriteAllText(targetPath, BuildDatabaseDump(db, databaseName), Encoding.UTF8);
        }

        public static string BuildDatabaseDump(IDatabase db, string databaseName)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (IsProvider(db, "mysql"))
            {
                return MySqlExportService.BuildExportSql(db, databaseName, new MySqlExportOptions
                {
                    IncludeCreateDatabase = true,
                    IncludeUseDatabase = true,
                    IncludeDropStatements = true,
                    DisableForeignKeyChecks = true,
                    RemoveDefiner = true,
                    InsertBatchSize = 1000
                });
            }

            StringBuilder builder = new StringBuilder();
            builder.AppendLine("-- mySQLPunk Database Backup");
            builder.AppendLine("-- Provider: " + db.ProviderName);
            builder.AppendLine("-- Database: " + databaseName);
            builder.AppendLine("-- Created: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            builder.AppendLine();

            foreach (string tableName in db.GetTables(databaseName))
            {
                builder.AppendLine(BuildTableDump(db, databaseName, tableName, false));
            }

            foreach (string viewName in db.GetViews(databaseName))
            {
                builder.AppendLine(BuildViewDump(db, databaseName, viewName));
            }

            return builder.ToString();
        }

        public static string BuildTableDump(IDatabase db, string databaseName, string tableName, bool dataOnly)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));

            StringBuilder builder = new StringBuilder();
            string insertTarget = BuildDumpInsertTargetName(db, databaseName, tableName);

            builder.AppendLine("-- mySQLPunk SQL Dump");
            builder.AppendLine("-- Provider: " + db.ProviderName);
            builder.AppendLine("-- Database: " + databaseName);
            builder.AppendLine("-- Table: " + tableName);
            if (db is my_mysql)
            {
                builder.AppendLine("SET NAMES utf8mb4;");
                builder.AppendLine("USE " + QuoteIdentifier(db, databaseName) + ";");
            }
            builder.AppendLine();

            if (!dataOnly)
            {
                string ddl = db.GetTableCreateStatement(databaseName, tableName);
                if (!string.IsNullOrWhiteSpace(ddl))
                {
                    builder.AppendLine(ddl.TrimEnd().TrimEnd(';') + ";");
                    builder.AppendLine();
                }

                string sqliteColumnComments = BuildSqliteColumnCommentsDump(db, databaseName, tableName);
                if (!string.IsNullOrWhiteSpace(sqliteColumnComments))
                {
                    builder.AppendLine(sqliteColumnComments.TrimEnd());
                    builder.AppendLine();
                }
            }

            long total = db.CountRows(databaseName, tableName);
            long copied = 0;
            const int batchSize = 1000;
            while (copied < total)
            {
                DataTable dataTable = db.SelectTablePage(databaseName, tableName, copied, batchSize);
                if (dataTable == null || dataTable.Rows.Count == 0) break;

                foreach (DataRow row in dataTable.Rows)
                {
                    builder.Append("INSERT INTO ");
                    builder.Append(insertTarget);
                    builder.Append(" (");

                    for (int i = 0; i < dataTable.Columns.Count; i++)
                    {
                        if (i > 0) builder.Append(", ");
                        builder.Append(QuoteIdentifier(db, dataTable.Columns[i].ColumnName));
                    }

                    builder.Append(") VALUES (");

                    for (int i = 0; i < dataTable.Columns.Count; i++)
                    {
                        if (i > 0) builder.Append(", ");
                        builder.Append(ToSqlLiteral(db, row[i]));
                    }

                    builder.AppendLine(");");
                }

                copied += dataTable.Rows.Count;
            }

            return builder.ToString();
        }

        public static string BuildViewDump(IDatabase db, string databaseName, string viewName)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));

            StringBuilder builder = new StringBuilder();
            builder.AppendLine("-- mySQLPunk SQL Dump");
            builder.AppendLine("-- Provider: " + db.ProviderName);
            builder.AppendLine("-- Database: " + databaseName);
            builder.AppendLine("-- View: " + viewName);
            if (db is my_mysql)
            {
                builder.AppendLine("SET NAMES utf8mb4;");
                builder.AppendLine("USE " + QuoteIdentifier(db, databaseName) + ";");
            }
            builder.AppendLine();

            string ddl = db.GetViewCreateStatement(databaseName, viewName);
            if (!string.IsNullOrWhiteSpace(ddl))
            {
                builder.AppendLine(ddl.TrimEnd().TrimEnd(';') + ";");
            }

            return builder.ToString();
        }

        public static string QuoteIdentifier(IDatabase db, string name)
        {
            name = name ?? string.Empty;
            if (IsProvider(db, "mysql"))
            {
                return "`" + name.Replace("`", "``") + "`";
            }
            if (IsProvider(db, "mssql") || IsProvider(db, "sqlserver"))
            {
                return "[" + name.Replace("]", "]]") + "]";
            }
            if (IsProvider(db, "postgresql") || IsProvider(db, "sqlite") || IsProvider(db, "oracle"))
            {
                return "\"" + name.Replace("\"", "\"\"") + "\"";
            }
            return name;
        }

        public static string BuildQualifiedObjectName(IDatabase db, string databaseName, string objectName)
        {
            if (IsProvider(db, "mysql"))
            {
                return QuoteIdentifier(db, databaseName) + "." + QuoteIdentifier(db, objectName);
            }
            if (IsProvider(db, "mssql") || IsProvider(db, "sqlserver"))
            {
                SqlServerObjectName target = ParseSqlServerObjectName(objectName);
                return QuoteIdentifier(db, databaseName) + "." + QuoteIdentifier(db, target.Schema) + "." + QuoteIdentifier(db, target.Name);
            }
            if (IsProvider(db, "postgresql"))
            {
                PostgreSqlObjectName target = ParsePostgreSqlObjectName(objectName);
                return QuoteIdentifier(db, target.Schema) + "." + QuoteIdentifier(db, target.Name);
            }
            if (IsProvider(db, "sqlite"))
            {
                return QuoteIdentifier(db, objectName);
            }
            if (IsProvider(db, "oracle"))
            {
                return QuoteIdentifier(db, databaseName) + "." + QuoteIdentifier(db, objectName);
            }
            return QuoteIdentifier(db, objectName);
        }

        public static string ToSqlLiteral(IDatabase db, object value)
        {
            if (value == null || value == DBNull.Value)
            {
                return "NULL";
            }

            byte[] bytes = value as byte[];
            if (bytes != null)
            {
                StringBuilder hex = new StringBuilder(bytes.Length * 2);
                for (int i = 0; i < bytes.Length; i++)
                {
                    hex.Append(bytes[i].ToString("X2"));
                }

                if (IsProvider(db, "oracle")) return "HEXTORAW('" + hex + "')";
                if (IsProvider(db, "postgresql")) return "'\\x" + hex + "'";
                if (IsProvider(db, "sqlite")) return "X'" + hex + "'";
                return "0x" + hex;
            }

            if (value is bool)
            {
                return ((bool)value) ? "1" : "0";
            }

            if (IsProvider(db, "oracle") && value is DateTime)
            {
                DateTime oracleDateTime = (DateTime)value;
                return "TO_TIMESTAMP('" +
                       oracleDateTime.ToString("yyyy-MM-dd HH:mm:ss.fffffff", System.Globalization.CultureInfo.InvariantCulture) +
                       "', 'YYYY-MM-DD HH24:MI:SS.FF7')";
            }

            if (value is string || value is char || value is DateTime || value is Guid)
            {
                return "'" + value.ToString().Replace("'", "''") + "'";
            }

            IFormattable formattable = value as IFormattable;
            if (formattable != null)
            {
                return formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture);
            }

            return "'" + value.ToString().Replace("'", "''") + "'";
        }

        public static bool IsProvider(IDatabase db, string providerName)
        {
            return db != null && string.Equals(db.ProviderName, providerName, StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildSqliteColumnCommentsDump(IDatabase db, string databaseName, string tableName)
        {
            if (!IsProvider(db, "sqlite") || string.IsNullOrWhiteSpace(tableName)) return "";

            DataTable columns;
            try
            {
                columns = db.GetColumns(databaseName, tableName);
            }
            catch
            {
                return "";
            }

            if (columns == null || columns.Rows.Count == 0) return "";

            List<string> inserts = new List<string>();
            foreach (DataRow row in columns.Rows)
            {
                string columnName = FirstColumnValue(row, "name", "Name", "COLUMN_NAME", "column_name", "Field");
                string comment = FirstColumnValue(row, "Comment", "comment");
                if (string.IsNullOrWhiteSpace(columnName) || string.IsNullOrWhiteSpace(comment)) continue;

                inserts.Add("INSERT OR REPLACE INTO " + QuoteSqliteIdentifier(my_sqlite.ColumnCommentTableName) +
                            " (table_name, column_name, comment) VALUES (" +
                            "'" + EscapeSqlLiteral(tableName) + "', " +
                            "'" + EscapeSqlLiteral(columnName) + "', " +
                            "'" + EscapeSqlLiteral(comment) + "');");
            }

            if (inserts.Count == 0) return "";

            StringBuilder builder = new StringBuilder();
            builder.AppendLine("-- SQLite column comments sidecar metadata");
            builder.AppendLine("CREATE TABLE IF NOT EXISTS " + QuoteSqliteIdentifier(my_sqlite.ColumnCommentTableName) + " (" +
                               "table_name TEXT NOT NULL, " +
                               "column_name TEXT NOT NULL, " +
                               "comment TEXT NOT NULL, " +
                               "PRIMARY KEY (table_name, column_name));");
            foreach (string insert in inserts)
            {
                builder.AppendLine(insert);
            }

            return builder.ToString();
        }

        private static string BuildDumpInsertTargetName(IDatabase db, string databaseName, string tableName)
        {
            if (IsProvider(db, "oracle"))
            {
                return BuildQualifiedObjectName(db, databaseName, tableName);
            }

            return QuoteIdentifier(db, tableName);
        }

        private static string FirstColumnValue(DataRow row, params string[] names)
        {
            if (row == null || row.Table == null) return "";
            foreach (string name in names)
            {
                if (row.Table.Columns.Contains(name) && row[name] != DBNull.Value)
                {
                    return row[name].ToString();
                }
            }
            return "";
        }

        private static string QuoteSqliteIdentifier(string name)
        {
            return "\"" + (name ?? string.Empty).Replace("\"", "\"\"") + "\"";
        }

        private static string EscapeSqlLiteral(string value)
        {
            return (value ?? string.Empty).Replace("'", "''");
        }

        private struct SqlServerObjectName
        {
            public string Schema;
            public string Name;
        }

        private struct PostgreSqlObjectName
        {
            public string Schema;
            public string Name;
        }

        private static SqlServerObjectName ParseSqlServerObjectName(string objectName)
        {
            string value = (objectName ?? string.Empty).Trim();
            int dotIndex = value.IndexOf('.');
            if (dotIndex > 0 && dotIndex < value.Length - 1)
            {
                return new SqlServerObjectName
                {
                    Schema = value.Substring(0, dotIndex).Trim(),
                    Name = value.Substring(dotIndex + 1).Trim()
                };
            }

            return new SqlServerObjectName { Schema = "dbo", Name = value };
        }

        private static PostgreSqlObjectName ParsePostgreSqlObjectName(string objectName)
        {
            string value = (objectName ?? string.Empty).Trim();
            int dotIndex = value.IndexOf('.');
            if (dotIndex > 0 && dotIndex < value.Length - 1)
            {
                return new PostgreSqlObjectName
                {
                    Schema = value.Substring(0, dotIndex).Trim(),
                    Name = value.Substring(dotIndex + 1).Trim()
                };
            }

            return new PostgreSqlObjectName { Schema = "public", Name = value };
        }
    }
}
