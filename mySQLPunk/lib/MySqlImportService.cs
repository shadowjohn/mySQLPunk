using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace mySQLPunk.lib
{
    public sealed class MySqlImportOptions
    {
        public bool ContinueOnError = false;
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
        public List<MySqlImportError> Errors = new List<MySqlImportError>();
    }

    public static class MySqlImportService
    {
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
            foreach (MySqlImportStatement statement in ParseStatements(reader))
            {
                if (string.IsNullOrWhiteSpace(statement.Sql)) continue;
                if (progress != null) progress("Import SQL line " + statement.StartLine + ": " + BuildPreview(statement.Sql));
                Dictionary<string, string> exec = db.ExecSQL(statement.Sql);
                if (exec != null && exec.ContainsKey("status") && string.Equals(exec["status"], "OK", StringComparison.OrdinalIgnoreCase))
                {
                    result.ExecutedStatements++;
                    continue;
                }

                result.FailedStatements++;
                string reason = DatabaseExecutionResultService.GetFailureReason(exec);
                result.Errors.Add(new MySqlImportError
                {
                    LineNumber = statement.StartLine,
                    StatementPreview = BuildPreview(statement.Sql),
                    Message = reason
                });
                if (!options.ContinueOnError)
                    throw new InvalidOperationException("MySQL import failed at line " + statement.StartLine + ": " + reason);
            }
            return result;
        }

        public static List<MySqlImportStatement> ParseStatements(TextReader reader)
        {
            if (reader == null) throw new ArgumentNullException("reader");
            List<MySqlImportStatement> result = new List<MySqlImportStatement>();
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
                        result.Add(new MySqlImportStatement { Sql = sql, StartLine = statementStartLine });
                    }
                    current.Length = 0;
                    statementStartLine = lineNumber + 1;
                }
            }

            string tail = current.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(tail))
            {
                result.Add(new MySqlImportStatement { Sql = tail, StartLine = statementStartLine });
            }
            return result;
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
