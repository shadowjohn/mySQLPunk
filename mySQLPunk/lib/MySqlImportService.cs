using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace mySQLPunk.lib
{
    public enum MySqlExistingObjectStrategy
    {
        ExecuteScript,
        DropAndRecreate,
        CreateIfMissing,
        SkipExisting
    }

    public sealed class MySqlImportOptions
    {
        public bool ContinueOnError = false;
        public MySqlExistingObjectStrategy ExistingObjectStrategy = MySqlExistingObjectStrategy.ExecuteScript;
    }

    public sealed class MySqlImportStatement
    {
        public string Sql;
        public int StartLine;
    }

    public sealed class MySqlImportError
    {
        public int LineNumber;
        public string StatementPreview;
        public string Message;
    }

    public sealed class MySqlImportResult
    {
        public int ExecutedStatements;
        public int FailedStatements;
        public int SkippedStatements;
        public List<MySqlImportError> Errors = new List<MySqlImportError>();
    }

    internal sealed class MySqlImportObject
    {
        public string ObjectType;
        public string DatabaseName;
        public string ObjectName;
    }

    public static class MySqlImportService
    {
        private const string IdentifierPattern = @"`(?:``|[^`])+`|[A-Za-z0-9_$]+";
        private static readonly Regex CreateObjectRegex = new Regex(
            @"^\s*CREATE\b[\s\S]*?\b(?<type>TABLE|VIEW|FUNCTION|PROCEDURE|TRIGGER)\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:(?<database>" + IdentifierPattern + @")\s*\.\s*)?(?<name>" + IdentifierPattern + @")",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex UseDatabaseRegex = new Regex(
            @"^\s*USE\s+(?<database>" + IdentifierPattern + @")\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex DropObjectRegex = new Regex(
            @"^\s*DROP\s+(?:TEMPORARY\s+)?(?:TABLE|VIEW|FUNCTION|PROCEDURE|TRIGGER)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex InsertTargetRegex = new Regex(
            @"^\s*(?:INSERT|REPLACE)\s+(?:IGNORE\s+)?INTO\s+(?:(?<database>" + IdentifierPattern + @")\s*\.\s*)?(?<name>" + IdentifierPattern + @")",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static MySqlImportResult Execute(IDatabase db, string sqlPath, MySqlImportOptions options, Action<string> progress)
        {
            if (db == null) throw new ArgumentNullException("db");
            if (!string.Equals(db.ProviderName, "mysql", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("MySQL import requires a MySQL connection.");
            if (string.IsNullOrWhiteSpace(sqlPath)) throw new ArgumentException("SQL file path is required.", "sqlPath");
            if (!File.Exists(sqlPath)) throw new FileNotFoundException("SQL file does not exist.", sqlPath);

            using (StreamReader reader = new StreamReader(sqlPath, Encoding.UTF8, true))
            {
                return Execute(db, reader, options, progress);
            }
        }

        public static MySqlImportResult Execute(IDatabase db, TextReader reader, MySqlImportOptions options, Action<string> progress)
        {
            if (db == null) throw new ArgumentNullException("db");
            if (!string.Equals(db.ProviderName, "mysql", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("MySQL import requires a MySQL connection.");
            if (reader == null) throw new ArgumentNullException("reader");
            options = options ?? new MySqlImportOptions();
            MySqlImportResult result = new MySqlImportResult();
            string currentDatabase = null;
            HashSet<string> skippedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (MySqlImportStatement statement in EnumerateStatements(reader))
            {
                if (string.IsNullOrWhiteSpace(statement.Sql)) continue;

                string useDatabase;
                if (TryParseUseDatabase(statement.Sql, out useDatabase)) currentDatabase = useDatabase;

                if ((options.ExistingObjectStrategy == MySqlExistingObjectStrategy.CreateIfMissing ||
                     options.ExistingObjectStrategy == MySqlExistingObjectStrategy.SkipExisting) &&
                    DropObjectRegex.IsMatch(statement.Sql))
                {
                    result.SkippedStatements++;
                    ReportProgress(progress, statement, Localization.T("MySqlImport.SkippedScriptDrop"));
                    continue;
                }

                MySqlImportObject createObject;
                if (TryParseCreateObject(statement.Sql, out createObject))
                {
                    if (string.IsNullOrWhiteSpace(createObject.DatabaseName))
                        createObject.DatabaseName = string.IsNullOrWhiteSpace(currentDatabase) ? GetCurrentDatabase(db) : currentDatabase;

                    bool exists = ObjectExists(db, createObject);
                    if (exists && (options.ExistingObjectStrategy == MySqlExistingObjectStrategy.CreateIfMissing ||
                                   options.ExistingObjectStrategy == MySqlExistingObjectStrategy.SkipExisting))
                    {
                        result.SkippedStatements++;
                        if (options.ExistingObjectStrategy == MySqlExistingObjectStrategy.SkipExisting &&
                            string.Equals(createObject.ObjectType, "TABLE", StringComparison.OrdinalIgnoreCase))
                        {
                            skippedTables.Add(BuildObjectKey(createObject.DatabaseName, createObject.ObjectName));
                        }
                        ReportProgress(progress, statement, Localization.Format("MySqlImport.SkippedExisting", createObject.ObjectType, createObject.ObjectName));
                        continue;
                    }

                    if (options.ExistingObjectStrategy == MySqlExistingObjectStrategy.DropAndRecreate)
                    {
                        string dropSql = BuildDropStatement(createObject);
                        if (!ExecuteStatement(db, dropSql, statement.StartLine, result, options, progress)) continue;
                    }
                }

                if (options.ExistingObjectStrategy == MySqlExistingObjectStrategy.SkipExisting)
                {
                    string insertDatabase;
                    string insertTable;
                    if (TryParseInsertTarget(statement.Sql, out insertDatabase, out insertTable))
                    {
                        if (string.IsNullOrWhiteSpace(insertDatabase)) insertDatabase = currentDatabase;
                        if (skippedTables.Contains(BuildObjectKey(insertDatabase, insertTable)))
                        {
                            result.SkippedStatements++;
                            ReportProgress(progress, statement, Localization.Format("MySqlImport.SkippedExistingData", insertTable));
                            continue;
                        }
                    }
                }

                ExecuteStatement(db, statement.Sql, statement.StartLine, result, options, progress);
            }
            return result;
        }

        private static bool ExecuteStatement(IDatabase db, string sql, int lineNumber, MySqlImportResult result, MySqlImportOptions options, Action<string> progress)
        {
            MySqlImportStatement statement = new MySqlImportStatement { Sql = sql, StartLine = lineNumber };
            ReportProgress(progress, statement, null);
            Dictionary<string, string> exec = db.ExecSQL(sql);
            if (exec != null && exec.ContainsKey("status") && string.Equals(exec["status"], "OK", StringComparison.OrdinalIgnoreCase))
            {
                result.ExecutedStatements++;
                return true;
            }

            result.FailedStatements++;
            string reason = DatabaseExecutionResultService.GetFailureReason(exec);
            result.Errors.Add(new MySqlImportError
            {
                LineNumber = lineNumber,
                StatementPreview = BuildPreview(sql),
                Message = reason
            });
            if (!options.ContinueOnError)
                throw new InvalidOperationException(Localization.Format("MySqlImport.FailedAtLine", lineNumber, reason));
            return false;
        }

        private static void ReportProgress(Action<string> progress, MySqlImportStatement statement, string detail)
        {
            if (progress == null) return;
            string preview = string.IsNullOrWhiteSpace(detail) ? BuildPreview(statement.Sql) : detail;
            progress(Localization.Format("MySqlImport.ProgressLine", statement.StartLine, preview));
        }

        private static bool TryParseUseDatabase(string sql, out string databaseName)
        {
            databaseName = string.Empty;
            Match match = UseDatabaseRegex.Match(sql ?? string.Empty);
            if (!match.Success) return false;
            databaseName = UnquoteIdentifier(match.Groups["database"].Value);
            return !string.IsNullOrWhiteSpace(databaseName);
        }

        private static bool TryParseCreateObject(string sql, out MySqlImportObject value)
        {
            value = null;
            Match match = CreateObjectRegex.Match(sql ?? string.Empty);
            if (!match.Success) return false;
            value = new MySqlImportObject
            {
                ObjectType = match.Groups["type"].Value.ToUpperInvariant(),
                DatabaseName = UnquoteIdentifier(match.Groups["database"].Value),
                ObjectName = UnquoteIdentifier(match.Groups["name"].Value)
            };
            return !string.IsNullOrWhiteSpace(value.ObjectName);
        }

        private static bool TryParseInsertTarget(string sql, out string databaseName, out string tableName)
        {
            databaseName = string.Empty;
            tableName = string.Empty;
            Match match = InsertTargetRegex.Match(sql ?? string.Empty);
            if (!match.Success) return false;
            databaseName = UnquoteIdentifier(match.Groups["database"].Value);
            tableName = UnquoteIdentifier(match.Groups["name"].Value);
            return !string.IsNullOrWhiteSpace(tableName);
        }

        private static bool ObjectExists(IDatabase db, MySqlImportObject value)
        {
            if (value == null || string.IsNullOrWhiteSpace(value.DatabaseName) || string.IsNullOrWhiteSpace(value.ObjectName)) return false;
            if (string.Equals(value.ObjectType, "TABLE", StringComparison.OrdinalIgnoreCase)) return db.TableExists(value.DatabaseName, value.ObjectName);
            if (string.Equals(value.ObjectType, "VIEW", StringComparison.OrdinalIgnoreCase)) return db.ViewExists(value.DatabaseName, value.ObjectName);

            Dictionary<string, object> parameters = new Dictionary<string, object>
            {
                { "schema", value.DatabaseName },
                { "name", value.ObjectName }
            };
            DataTable table;
            if (string.Equals(value.ObjectType, "FUNCTION", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value.ObjectType, "PROCEDURE", StringComparison.OrdinalIgnoreCase))
            {
                parameters.Add("type", value.ObjectType);
                table = db.SelectSQL(
                    "SELECT 1 FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA = ?schema AND ROUTINE_NAME = ?name AND ROUTINE_TYPE = ?type LIMIT 1;",
                    parameters);
            }
            else if (string.Equals(value.ObjectType, "TRIGGER", StringComparison.OrdinalIgnoreCase))
            {
                table = db.SelectSQL(
                    "SELECT 1 FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA = ?schema AND TRIGGER_NAME = ?name LIMIT 1;",
                    parameters);
            }
            else
            {
                return false;
            }
            return table != null && table.Rows.Count > 0;
        }

        private static string GetCurrentDatabase(IDatabase db)
        {
            DataTable table = db.SelectSQL("SELECT DATABASE() AS current_database;");
            if (table == null || table.Rows.Count == 0 || table.Columns.Count == 0 || table.Rows[0][0] == DBNull.Value) return string.Empty;
            return Convert.ToString(table.Rows[0][0]);
        }

        private static string BuildDropStatement(MySqlImportObject value)
        {
            string qualifiedName = string.IsNullOrWhiteSpace(value.DatabaseName)
                ? QuoteIdentifier(value.ObjectName)
                : QuoteIdentifier(value.DatabaseName) + "." + QuoteIdentifier(value.ObjectName);
            return "DROP " + value.ObjectType + " IF EXISTS " + qualifiedName + ";";
        }

        private static string BuildObjectKey(string databaseName, string objectName)
        {
            return (databaseName ?? string.Empty).Trim() + "." + (objectName ?? string.Empty).Trim();
        }

        private static string QuoteIdentifier(string value)
        {
            return "`" + (value ?? string.Empty).Replace("`", "``") + "`";
        }

        private static string UnquoteIdentifier(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length >= 2 && value[0] == '`' && value[value.Length - 1] == '`')
                return value.Substring(1, value.Length - 2).Replace("``", "`");
            return value;
        }

        public static List<MySqlImportStatement> ParseStatements(TextReader reader)
        {
            if (reader == null) throw new ArgumentNullException("reader");
            return new List<MySqlImportStatement>(EnumerateStatements(reader));
        }

        public static IEnumerable<MySqlImportStatement> EnumerateStatements(TextReader reader)
        {
            if (reader == null) throw new ArgumentNullException("reader");
            StringBuilder current = new StringBuilder();
            string delimiter = ";";
            string line;
            int lineNumber = 0;
            int statementStartLine = 1;
            while ((line = reader.ReadLine()) != null)
            {
                lineNumber++;
                string trimmed = line.Trim();
                if (trimmed.StartsWith("DELIMITER ", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(current.ToString()))
                {
                    delimiter = trimmed.Substring("DELIMITER ".Length).Trim();
                    if (delimiter.Length == 0) delimiter = ";";
                    statementStartLine = lineNumber + 1;
                    continue;
                }

                if (current.Length == 0) statementStartLine = lineNumber;
                current.AppendLine(line);
                if (EndsWithDelimiter(current, delimiter))
                {
                    string sql = RemoveTrailingDelimiter(current.ToString(), delimiter).Trim();
                    if (!string.IsNullOrWhiteSpace(sql))
                    {
                        yield return new MySqlImportStatement { Sql = sql, StartLine = statementStartLine };
                    }
                    current.Length = 0;
                    statementStartLine = lineNumber + 1;
                }
            }

            string tail = current.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(tail))
            {
                yield return new MySqlImportStatement { Sql = tail, StartLine = statementStartLine };
            }
        }

        public static string BuildPreview(string sql)
        {
            string value = (sql ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
            while (value.Contains("  ")) value = value.Replace("  ", " ");
            return value.Length <= 160 ? value : value.Substring(0, 157) + "...";
        }

        private static bool EndsWithDelimiter(StringBuilder builder, string delimiter)
        {
            string text = builder.ToString().TrimEnd();
            return text.EndsWith(delimiter, StringComparison.Ordinal);
        }

        private static string RemoveTrailingDelimiter(string sql, string delimiter)
        {
            string text = sql.TrimEnd();
            if (!text.EndsWith(delimiter, StringComparison.Ordinal)) return text;
            return text.Substring(0, text.Length - delimiter.Length);
        }
    }
}
