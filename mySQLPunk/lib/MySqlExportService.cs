using System;
using System.Collections.Generic;
using System.Data;
using System.Text;
using System.Text.RegularExpressions;

namespace mySQLPunk.lib
{
    public sealed class MySqlExportOptions
    {
        public bool IncludeStructure = true;
        public bool IncludeData = true;
        public bool IncludeDropStatements = true;
        public bool IncludeCreateDatabase = false;
        public bool IncludeUseDatabase = true;
        public bool DisableForeignKeyChecks = true;
        public bool IncludeTables = true;
        public bool IncludeViews = true;
        public bool IncludeRoutines = true;
        public bool IncludeTriggers = true;
        public bool RemoveDefiner = true;
        public int InsertBatchSize = 1000;
    }

    public sealed class MySqlExportResult
    {
        public int TableCount;
        public int ViewCount;
        public int RoutineCount;
        public int TriggerCount;
        public long RowCount;
        public string Sql;
    }

    public sealed class MySqlRoutineInfo
    {
        public string Name;
        public string Type;
    }

    public sealed class MySqlTriggerInfo
    {
        public string Name;
    }

    public static class MySqlExportService
    {
        private static readonly Regex DefinerRegex = new Regex(@"\sDEFINER\s*=\s*(?:`[^`]*`@`[^`]*`|[^\s]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string BuildExportSql(IDatabase db, string databaseName, MySqlExportOptions options)
        {
            MySqlExportResult result = BuildExport(db, databaseName, options, null);
            return result.Sql;
        }

        public static MySqlExportResult BuildExport(IDatabase db, string databaseName, MySqlExportOptions options, Action<string> progress)
        {
            if (db == null) throw new ArgumentNullException("db");
            if (!string.Equals(db.ProviderName, "mysql", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("MySQL export requires a MySQL connection.");
            if (string.IsNullOrWhiteSpace(databaseName)) throw new ArgumentException("Database name is required.", "databaseName");
            options = options ?? new MySqlExportOptions();
            if (options.InsertBatchSize <= 0) options.InsertBatchSize = 1000;

            MySqlExportResult result = new MySqlExportResult();
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("-- mySQLPunk MySQL Export");
            builder.AppendLine("-- Database: " + databaseName);
            builder.AppendLine("-- Created: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            builder.AppendLine("SET NAMES utf8mb4;");
            if (options.DisableForeignKeyChecks) builder.AppendLine("SET FOREIGN_KEY_CHECKS = 0;");
            if (options.IncludeCreateDatabase) builder.AppendLine(BuildCreateDatabaseStatement(db, databaseName));
            if (options.IncludeUseDatabase) builder.AppendLine("USE " + QuoteIdentifier(databaseName) + ";");
            builder.AppendLine();

            List<string> tables = options.IncludeTables ? db.GetTables(databaseName) : new List<string>();
            List<string> views = options.IncludeViews ? db.GetViews(databaseName) : new List<string>();
            List<MySqlRoutineInfo> routines = options.IncludeRoutines ? GetRoutines(db, databaseName) : new List<MySqlRoutineInfo>();
            List<MySqlTriggerInfo> triggers = options.IncludeTriggers ? GetTriggers(db, databaseName) : new List<MySqlTriggerInfo>();

            if (options.IncludeDropStatements)
            {
                AppendDropStatements(builder, tables, views, routines, triggers);
            }

            if (options.IncludeStructure && options.IncludeTables)
            {
                foreach (string table in tables)
                {
                    if (progress != null) progress("Export table structure: " + table);
                    string ddl = db.GetTableCreateStatement(databaseName, table);
                    if (!string.IsNullOrWhiteSpace(ddl))
                    {
                        builder.AppendLine(TrimSqlTerminator(ddl) + ";");
                        builder.AppendLine();
                    }
                    result.TableCount++;
                }
            }

            if (options.IncludeData && options.IncludeTables)
            {
                foreach (string table in tables)
                {
                    if (progress != null) progress("Export table data: " + table);
                    result.RowCount += AppendTableData(builder, db, databaseName, table, options.InsertBatchSize);
                }
            }

            if (options.IncludeStructure && options.IncludeViews)
            {
                foreach (string view in views)
                {
                    if (progress != null) progress("Export view: " + view);
                    string ddl = db.GetViewCreateStatement(databaseName, view);
                    ddl = NormalizeObjectCreateSql(ddl, options.RemoveDefiner);
                    if (!string.IsNullOrWhiteSpace(ddl))
                    {
                        builder.AppendLine(TrimSqlTerminator(ddl) + ";");
                        builder.AppendLine();
                    }
                    result.ViewCount++;
                }
            }

            if (options.IncludeStructure && options.IncludeRoutines)
            {
                foreach (MySqlRoutineInfo routine in routines)
                {
                    if (progress != null) progress("Export routine: " + routine.Type + " " + routine.Name);
                    string ddl = GetRoutineCreateStatement(db, databaseName, routine);
                    AppendDelimitedObject(builder, NormalizeObjectCreateSql(ddl, options.RemoveDefiner));
                    result.RoutineCount++;
                }
            }

            if (options.IncludeStructure && options.IncludeTriggers)
            {
                foreach (MySqlTriggerInfo trigger in triggers)
                {
                    if (progress != null) progress("Export trigger: " + trigger.Name);
                    string ddl = GetTriggerCreateStatement(db, databaseName, trigger.Name);
                    AppendDelimitedObject(builder, NormalizeObjectCreateSql(ddl, options.RemoveDefiner));
                    result.TriggerCount++;
                }
            }

            if (options.DisableForeignKeyChecks) builder.AppendLine("SET FOREIGN_KEY_CHECKS = 1;");
            result.Sql = builder.ToString();
            return result;
        }

        public static List<MySqlRoutineInfo> GetRoutines(IDatabase db, string databaseName)
        {
            var parameters = new Dictionary<string, object> { { "schema", databaseName } };
            DataTable table = db.SelectSQL(
                "SELECT ROUTINE_NAME, ROUTINE_TYPE FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA = ?schema ORDER BY ROUTINE_TYPE, ROUTINE_NAME;",
                parameters);
            List<MySqlRoutineInfo> result = new List<MySqlRoutineInfo>();
            foreach (DataRow row in table.Rows)
            {
                string name = GetFirst(row, "ROUTINE_NAME", "Routine_name", "Name");
                string type = GetFirst(row, "ROUTINE_TYPE", "Routine_type", "Type").ToUpperInvariant();
                if (string.IsNullOrWhiteSpace(name) || (type != "FUNCTION" && type != "PROCEDURE")) continue;
                result.Add(new MySqlRoutineInfo { Name = name, Type = type });
            }
            return result;
        }

        public static List<MySqlTriggerInfo> GetTriggers(IDatabase db, string databaseName)
        {
            var parameters = new Dictionary<string, object> { { "schema", databaseName } };
            DataTable table = db.SelectSQL(
                "SELECT TRIGGER_NAME FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA = ?schema ORDER BY TRIGGER_NAME;",
                parameters);
            List<MySqlTriggerInfo> result = new List<MySqlTriggerInfo>();
            foreach (DataRow row in table.Rows)
            {
                string name = GetFirst(row, "TRIGGER_NAME", "Trigger", "Name");
                if (!string.IsNullOrWhiteSpace(name)) result.Add(new MySqlTriggerInfo { Name = name });
            }
            return result;
        }

        public static string NormalizeObjectCreateSql(string sql, bool removeDefiner)
        {
            if (string.IsNullOrWhiteSpace(sql)) return "";
            string normalized = sql.Trim();
            if (removeDefiner)
            {
                normalized = DefinerRegex.Replace(normalized, "");
                normalized = Regex.Replace(normalized, @"\bSQL\s+SECURITY\s+DEFINER\b", "SQL SECURITY INVOKER", RegexOptions.IgnoreCase);
            }
            return normalized;
        }

        public static string ToMySqlLiteral(object value)
        {
            if (value == null || value == DBNull.Value) return "NULL";
            byte[] bytes = value as byte[];
            if (bytes != null)
            {
                StringBuilder hex = new StringBuilder(bytes.Length * 2);
                for (int i = 0; i < bytes.Length; i++) hex.Append(bytes[i].ToString("X2"));
                return "0x" + hex;
            }
            if (value is bool) return ((bool)value) ? "1" : "0";
            if (value is DateTime)
            {
                return "'" + ((DateTime)value).ToString("yyyy-MM-dd HH:mm:ss.fffffff", System.Globalization.CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.') + "'";
            }
            IFormattable formattable = value as IFormattable;
            if (!(value is string) && !(value is char) && formattable != null)
            {
                return formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture);
            }
            return "'" + EscapeMySqlString(value.ToString()) + "'";
        }

        public static string EscapeMySqlString(string value)
        {
            if (value == null) return "";
            StringBuilder builder = new StringBuilder(value.Length + 8);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '\0': builder.Append("\\0"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    case (char)26: builder.Append("\\Z"); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\'': builder.Append("\\'"); break;
                    default: builder.Append(c); break;
                }
            }
            return builder.ToString();
        }

        public static string QuoteIdentifier(string name)
        {
            return "`" + (name ?? string.Empty).Replace("`", "``") + "`";
        }

        private static void AppendDropStatements(StringBuilder builder, List<string> tables, List<string> views, List<MySqlRoutineInfo> routines, List<MySqlTriggerInfo> triggers)
        {
            foreach (MySqlTriggerInfo trigger in triggers) builder.AppendLine("DROP TRIGGER IF EXISTS " + QuoteIdentifier(trigger.Name) + ";");
            foreach (MySqlRoutineInfo routine in routines) builder.AppendLine("DROP " + routine.Type + " IF EXISTS " + QuoteIdentifier(routine.Name) + ";");
            foreach (string view in views) builder.AppendLine("DROP VIEW IF EXISTS " + QuoteIdentifier(view) + ";");
            foreach (string table in tables) builder.AppendLine("DROP TABLE IF EXISTS " + QuoteIdentifier(table) + ";");
            if (tables.Count + views.Count + routines.Count + triggers.Count > 0) builder.AppendLine();
        }

        private static long AppendTableData(StringBuilder builder, IDatabase db, string databaseName, string tableName, int batchSize)
        {
            long total = db.CountRows(databaseName, tableName);
            long copied = 0;
            while (copied < total)
            {
                DataTable page = db.SelectTablePage(databaseName, tableName, copied, batchSize);
                if (page == null || page.Rows.Count == 0) break;
                AppendInsertBatch(builder, tableName, page);
                copied += page.Rows.Count;
            }
            if (copied > 0) builder.AppendLine();
            return copied;
        }

        private static void AppendInsertBatch(StringBuilder builder, string tableName, DataTable rows)
        {
            builder.Append("INSERT INTO ");
            builder.Append(QuoteIdentifier(tableName));
            builder.Append(" (");
            for (int i = 0; i < rows.Columns.Count; i++)
            {
                if (i > 0) builder.Append(", ");
                builder.Append(QuoteIdentifier(rows.Columns[i].ColumnName));
            }
            builder.AppendLine(") VALUES");
            for (int r = 0; r < rows.Rows.Count; r++)
            {
                builder.Append("(");
                for (int c = 0; c < rows.Columns.Count; c++)
                {
                    if (c > 0) builder.Append(", ");
                    builder.Append(ToMySqlLiteral(rows.Rows[r][c]));
                }
                builder.Append(r == rows.Rows.Count - 1 ? ");" : "),");
                builder.AppendLine();
            }
        }

        private static string BuildCreateDatabaseStatement(IDatabase db, string databaseName)
        {
            Dictionary<string, string> info = db.GetDatabaseInfo(databaseName) ?? new Dictionary<string, string>();
            string charset = info.ContainsKey("character_set") ? info["character_set"] : "";
            string collation = info.ContainsKey("collation") ? info["collation"] : "";
            StringBuilder builder = new StringBuilder();
            builder.Append("CREATE DATABASE IF NOT EXISTS ");
            builder.Append(QuoteIdentifier(databaseName));
            if (!string.IsNullOrWhiteSpace(charset)) builder.Append(" DEFAULT CHARACTER SET ").Append(charset);
            if (!string.IsNullOrWhiteSpace(collation)) builder.Append(" COLLATE ").Append(collation);
            builder.Append(";");
            return builder.ToString();
        }

        private static string GetRoutineCreateStatement(IDatabase db, string databaseName, MySqlRoutineInfo routine)
        {
            string sql = "SHOW CREATE " + routine.Type + " " + QuoteIdentifier(databaseName) + "." + QuoteIdentifier(routine.Name) + ";";
            DataTable table = db.SelectSQL(sql);
            if (table.Rows.Count == 0) return "";
            string createColumn = routine.Type.Equals("FUNCTION", StringComparison.OrdinalIgnoreCase) ? "Create Function" : "Create Procedure";
            return GetFirst(table.Rows[0], createColumn, "Create " + FirstUpper(routine.Type), "Create Statement", 2.ToString());
        }

        private static string GetTriggerCreateStatement(IDatabase db, string databaseName, string triggerName)
        {
            string sql = "SHOW CREATE TRIGGER " + QuoteIdentifier(databaseName) + "." + QuoteIdentifier(triggerName) + ";";
            DataTable table = db.SelectSQL(sql);
            if (table.Rows.Count == 0) return "";
            return GetFirst(table.Rows[0], "SQL Original Statement", "Create Trigger", "Create Statement", 2.ToString());
        }

        private static void AppendDelimitedObject(StringBuilder builder, string sql)
        {
            if (string.IsNullOrWhiteSpace(sql)) return;
            builder.AppendLine("DELIMITER ;; ");
            builder.AppendLine(TrimSqlTerminator(sql) + ";;");
            builder.AppendLine("DELIMITER ;");
            builder.AppendLine();
        }

        private static string TrimSqlTerminator(string sql)
        {
            return (sql ?? string.Empty).Trim().TrimEnd(';').TrimEnd();
        }

        private static string GetFirst(DataRow row, params string[] names)
        {
            if (row == null || row.Table == null) return "";
            foreach (string name in names)
            {
                int ordinal;
                if (int.TryParse(name, out ordinal))
                {
                    if (ordinal >= 0 && ordinal < row.Table.Columns.Count && row[ordinal] != DBNull.Value) return row[ordinal].ToString();
                    continue;
                }
                if (row.Table.Columns.Contains(name) && row[name] != DBNull.Value) return row[name].ToString();
            }
            return "";
        }

        private static string FirstUpper(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            return char.ToUpperInvariant(value[0]) + value.Substring(1).ToLowerInvariant();
        }
    }
}
