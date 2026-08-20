using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
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
        public ICollection<string> SelectedTables;
        public ICollection<string> SelectedViews;
        public ICollection<string> SelectedRoutines;
        public ICollection<string> SelectedTriggers;
        public string TargetDatabaseName;
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
            StringBuilder builder = new StringBuilder();
            MySqlExportResult result;
            using (StringWriter writer = new StringWriter(builder, System.Globalization.CultureInfo.InvariantCulture))
            {
                result = WriteExport(db, databaseName, options, writer, progress);
            }
            result.Sql = builder.ToString();
            return result;
        }

        public static MySqlExportResult WriteExportToFile(IDatabase db, string databaseName, MySqlExportOptions options, string targetPath, Action<string> progress)
        {
            if (string.IsNullOrWhiteSpace(targetPath)) throw new ArgumentException(Localization.T("Common.TargetPathRequired"), "targetPath");
            string directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory)) Directory.CreateDirectory(directory);

            using (StreamWriter writer = new StreamWriter(targetPath, false, new UTF8Encoding(false)))
            {
                return WriteExport(db, databaseName, options, writer, progress);
            }
        }

        public static MySqlExportResult WriteExport(IDatabase db, string databaseName, MySqlExportOptions options, TextWriter writer, Action<string> progress)
        {
            if (db == null) throw new ArgumentNullException("db");
            if (writer == null) throw new ArgumentNullException("writer");
            if (!string.Equals(db.ProviderName, "mysql", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("MySQL export requires a MySQL connection.");
            if (string.IsNullOrWhiteSpace(databaseName)) throw new ArgumentException("Database name is required.", "databaseName");
            options = options ?? new MySqlExportOptions();
            if (options.InsertBatchSize <= 0) options.InsertBatchSize = 1000;
            string outputDatabaseName = string.IsNullOrWhiteSpace(options.TargetDatabaseName)
                ? databaseName
                : options.TargetDatabaseName.Trim();

            MySqlExportResult result = new MySqlExportResult();
            writer.WriteLine("-- mySQLPunk MySQL Export");
            writer.WriteLine("-- Database: " + outputDatabaseName);
            writer.WriteLine("-- Created: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            writer.WriteLine("SET NAMES utf8mb4;");
            if (options.DisableForeignKeyChecks) writer.WriteLine("SET FOREIGN_KEY_CHECKS = 0;");
            if (options.IncludeCreateDatabase) writer.WriteLine(BuildCreateDatabaseStatement(db, databaseName, outputDatabaseName));
            if (options.IncludeUseDatabase) writer.WriteLine("USE " + QuoteIdentifier(outputDatabaseName) + ";");
            writer.WriteLine();

            List<string> tables = options.IncludeTables ? FilterNames(db.GetTables(databaseName), options.SelectedTables) : new List<string>();
            List<string> views = options.IncludeViews ? FilterNames(db.GetViews(databaseName), options.SelectedViews) : new List<string>();
            List<MySqlRoutineInfo> routines = options.IncludeRoutines ? FilterRoutines(GetRoutines(db, databaseName), options.SelectedRoutines) : new List<MySqlRoutineInfo>();
            List<MySqlTriggerInfo> triggers = options.IncludeTriggers ? FilterTriggers(GetTriggers(db, databaseName), options.SelectedTriggers) : new List<MySqlTriggerInfo>();

            if (options.IncludeDropStatements && options.IncludeStructure)
            {
                AppendDropStatements(writer, tables, views, routines, triggers);
            }

            if (options.IncludeStructure && options.IncludeTables)
            {
                foreach (string table in tables)
                {
                    if (progress != null) progress(Localization.Format("MySqlExport.ProgressTableStructure", table));
                    string ddl = db.GetTableCreateStatement(databaseName, table);
                    ddl = RewriteDatabaseQualifier(ddl, databaseName, outputDatabaseName);
                    if (!string.IsNullOrWhiteSpace(ddl))
                    {
                        writer.WriteLine(TrimSqlTerminator(ddl) + ";");
                        writer.WriteLine();
                    }
                    result.TableCount++;
                }
            }

            if (options.IncludeData && options.IncludeTables)
            {
                foreach (string table in tables)
                {
                    if (progress != null) progress(Localization.Format("MySqlExport.ProgressTableData", table));
                    result.RowCount += AppendTableData(writer, db, databaseName, table, options.InsertBatchSize, progress);
                }
            }

            if (options.IncludeStructure && options.IncludeViews)
            {
                foreach (string view in views)
                {
                    if (progress != null) progress(Localization.Format("MySqlExport.ProgressView", view));
                    string ddl = db.GetViewCreateStatement(databaseName, view);
                    ddl = NormalizeObjectCreateSql(ddl, options.RemoveDefiner);
                    ddl = RewriteDatabaseQualifier(ddl, databaseName, outputDatabaseName);
                    if (!string.IsNullOrWhiteSpace(ddl))
                    {
                        writer.WriteLine(TrimSqlTerminator(ddl) + ";");
                        writer.WriteLine();
                    }
                    result.ViewCount++;
                }
            }

            if (options.IncludeStructure && options.IncludeRoutines)
            {
                foreach (MySqlRoutineInfo routine in routines)
                {
                    if (progress != null) progress(Localization.Format("MySqlExport.ProgressRoutine", routine.Type, routine.Name));
                    string ddl = GetRoutineCreateStatement(db, databaseName, routine);
                    ddl = RewriteDatabaseQualifier(ddl, databaseName, outputDatabaseName);
                    AppendDelimitedObject(writer, NormalizeObjectCreateSql(ddl, options.RemoveDefiner));
                    result.RoutineCount++;
                }
            }

            if (options.IncludeStructure && options.IncludeTriggers)
            {
                foreach (MySqlTriggerInfo trigger in triggers)
                {
                    if (progress != null) progress(Localization.Format("MySqlExport.ProgressTrigger", trigger.Name));
                    string ddl = GetTriggerCreateStatement(db, databaseName, trigger.Name);
                    ddl = RewriteDatabaseQualifier(ddl, databaseName, outputDatabaseName);
                    AppendDelimitedObject(writer, NormalizeObjectCreateSql(ddl, options.RemoveDefiner));
                    result.TriggerCount++;
                }
            }

            if (options.DisableForeignKeyChecks) writer.WriteLine("SET FOREIGN_KEY_CHECKS = 1;");
            writer.Flush();
            return result;
        }

        private static List<string> FilterNames(IEnumerable<string> values, ICollection<string> selected)
        {
            IEnumerable<string> source = values ?? Enumerable.Empty<string>();
            if (selected == null) return source.Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
            HashSet<string> selection = new HashSet<string>(selected.Where(value => !string.IsNullOrWhiteSpace(value)), StringComparer.OrdinalIgnoreCase);
            return source.Where(value => selection.Contains(value)).ToList();
        }

        private static List<MySqlRoutineInfo> FilterRoutines(IEnumerable<MySqlRoutineInfo> values, ICollection<string> selected)
        {
            IEnumerable<MySqlRoutineInfo> source = values ?? Enumerable.Empty<MySqlRoutineInfo>();
            if (selected == null) return source.ToList();
            HashSet<string> selection = new HashSet<string>(selected.Where(value => !string.IsNullOrWhiteSpace(value)), StringComparer.OrdinalIgnoreCase);
            return source.Where(value => value != null && (selection.Contains(value.Name) || selection.Contains(value.Type + ":" + value.Name))).ToList();
        }

        private static List<MySqlTriggerInfo> FilterTriggers(IEnumerable<MySqlTriggerInfo> values, ICollection<string> selected)
        {
            IEnumerable<MySqlTriggerInfo> source = values ?? Enumerable.Empty<MySqlTriggerInfo>();
            if (selected == null) return source.ToList();
            HashSet<string> selection = new HashSet<string>(selected.Where(value => !string.IsNullOrWhiteSpace(value)), StringComparer.OrdinalIgnoreCase);
            return source.Where(value => value != null && selection.Contains(value.Name)).ToList();
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
            string mysqlDateTimeLiteral;
            if (TryFormatMySqlDateTime(value, out mysqlDateTimeLiteral)) return mysqlDateTimeLiteral;
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

        private static bool TryFormatMySqlDateTime(object value, out string literal)
        {
            literal = null;
            if (value == null) return false;
            Type type = value.GetType();
            if (!string.Equals(type.FullName, "MySqlConnector.MySqlDateTime", StringComparison.Ordinal)) return false;

            try
            {
                object isValid = type.GetProperty("IsValidDateTime")?.GetValue(value, null);
                if (isValid is bool && (bool)isValid)
                {
                    object dateTimeValue = type.GetMethod("GetDateTime", Type.EmptyTypes)?.Invoke(value, null);
                    if (dateTimeValue is DateTime)
                    {
                        literal = "'" + ((DateTime)dateTimeValue).ToString("yyyy-MM-dd HH:mm:ss.fffffff", System.Globalization.CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.') + "'";
                        return true;
                    }
                }

                int year = GetIntProperty(type, value, "Year");
                int month = GetIntProperty(type, value, "Month");
                int day = GetIntProperty(type, value, "Day");
                int hour = GetIntProperty(type, value, "Hour");
                int minute = GetIntProperty(type, value, "Minute");
                int second = GetIntProperty(type, value, "Second");
                int microsecond = GetIntProperty(type, value, "Microsecond");
                string formatted = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0:0000}-{1:00}-{2:00} {3:00}:{4:00}:{5:00}", year, month, day, hour, minute, second);
                if (microsecond > 0) formatted += "." + microsecond.ToString("000000", System.Globalization.CultureInfo.InvariantCulture).TrimEnd('0');
                literal = "'" + formatted + "'";
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static int GetIntProperty(Type type, object value, string propertyName)
        {
            object result = type.GetProperty(propertyName)?.GetValue(value, null);
            return result == null ? 0 : Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
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

        private static void AppendDropStatements(TextWriter writer, List<string> tables, List<string> views, List<MySqlRoutineInfo> routines, List<MySqlTriggerInfo> triggers)
        {
            foreach (MySqlTriggerInfo trigger in triggers) writer.WriteLine("DROP TRIGGER IF EXISTS " + QuoteIdentifier(trigger.Name) + ";");
            foreach (MySqlRoutineInfo routine in routines) writer.WriteLine("DROP " + routine.Type + " IF EXISTS " + QuoteIdentifier(routine.Name) + ";");
            foreach (string view in views) writer.WriteLine("DROP VIEW IF EXISTS " + QuoteIdentifier(view) + ";");
            foreach (string table in tables) writer.WriteLine("DROP TABLE IF EXISTS " + QuoteIdentifier(table) + ";");
            if (tables.Count + views.Count + routines.Count + triggers.Count > 0) writer.WriteLine();
        }

        public static string RewriteDatabaseQualifier(string sql, string sourceDatabaseName, string targetDatabaseName)
        {
            if (string.IsNullOrWhiteSpace(sql) || string.IsNullOrWhiteSpace(sourceDatabaseName) || string.IsNullOrWhiteSpace(targetDatabaseName) ||
                string.Equals(sourceDatabaseName, targetDatabaseName, StringComparison.Ordinal)) return sql ?? string.Empty;
            string source = QuoteIdentifier(sourceDatabaseName);
            string target = QuoteIdentifier(targetDatabaseName);
            return Regex.Replace(sql, Regex.Escape(source) + @"\s*\.", target + ".", RegexOptions.IgnoreCase);
        }

        private static long AppendTableData(TextWriter writer, IDatabase db, string databaseName, string tableName, int batchSize, Action<string> progress)
        {
            long total = db.CountRows(databaseName, tableName);
            long copied = 0;
            while (copied < total)
            {
                DataTable page = db.SelectTablePage(databaseName, tableName, copied, batchSize);
                if (page == null || page.Rows.Count == 0) break;
                AppendInsertBatch(writer, tableName, page);
                copied += page.Rows.Count;
                if (progress != null) progress(Localization.Format("MySqlExport.ProgressTableRows", tableName, copied, total));
            }
            if (copied > 0) writer.WriteLine();
            return copied;
        }

        private static void AppendInsertBatch(TextWriter writer, string tableName, DataTable rows)
        {
            writer.Write("INSERT INTO ");
            writer.Write(QuoteIdentifier(tableName));
            writer.Write(" (");
            for (int i = 0; i < rows.Columns.Count; i++)
            {
                if (i > 0) writer.Write(", ");
                writer.Write(QuoteIdentifier(rows.Columns[i].ColumnName));
            }
            writer.WriteLine(") VALUES");
            for (int r = 0; r < rows.Rows.Count; r++)
            {
                writer.Write("(");
                for (int c = 0; c < rows.Columns.Count; c++)
                {
                    if (c > 0) writer.Write(", ");
                    writer.Write(ToMySqlLiteral(rows.Rows[r][c]));
                }
                writer.Write(r == rows.Rows.Count - 1 ? ");" : "),");
                writer.WriteLine();
            }
        }

        private static string BuildCreateDatabaseStatement(IDatabase db, string sourceDatabaseName, string outputDatabaseName)
        {
            Dictionary<string, string> info = db.GetDatabaseInfo(sourceDatabaseName) ?? new Dictionary<string, string>();
            string charset = info.ContainsKey("character_set") ? info["character_set"] : "";
            string collation = info.ContainsKey("collation") ? info["collation"] : "";
            StringBuilder builder = new StringBuilder();
            builder.Append("CREATE DATABASE IF NOT EXISTS ");
            builder.Append(QuoteIdentifier(outputDatabaseName));
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

        private static void AppendDelimitedObject(TextWriter writer, string sql)
        {
            if (string.IsNullOrWhiteSpace(sql)) return;
            writer.WriteLine("DELIMITER ;;");
            writer.WriteLine(TrimSqlTerminator(sql) + ";;");
            writer.WriteLine("DELIMITER ;");
            writer.WriteLine();
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
