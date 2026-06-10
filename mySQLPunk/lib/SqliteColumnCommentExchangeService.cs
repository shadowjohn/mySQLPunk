using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace mySQLPunk.lib
{
    public sealed class SqliteColumnCommentExportResult
    {
        public int TableCount { get; set; }
        public int CommentCount { get; set; }
    }

    public sealed class SqliteColumnCommentImportPlan
    {
        public Dictionary<string, Dictionary<string, string>> Tables { get; set; }
        public List<string> Statements { get; private set; }
        public int TableCount { get; set; }
        public int CommentCount { get; set; }

        public SqliteColumnCommentImportPlan()
        {
            Tables = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            Statements = new List<string>();
        }
    }

    public sealed class SqliteColumnCommentImportReviewEntry
    {
        public string TableName { get; set; }
        public string ColumnName { get; set; }
        public string CurrentComment { get; set; }
        public string ImportedComment { get; set; }
        public string Status { get; set; }
    }

    public sealed class SqliteColumnCommentImportReviewReport
    {
        public int FormatVersion { get; set; }
        public string ReviewedAtUtc { get; set; }
        public string DatabaseName { get; set; }
        public string SourcePath { get; set; }
        public int TableCount { get; set; }
        public int CommentCount { get; set; }
        public int Added { get; set; }
        public int Updated { get; set; }
        public int Removed { get; set; }
        public int Unchanged { get; set; }
        public List<SqliteColumnCommentImportReviewEntry> Entries { get; private set; }

        public SqliteColumnCommentImportReviewReport()
        {
            FormatVersion = 1;
            ReviewedAtUtc = DateTime.UtcNow.ToString("o");
            Entries = new List<SqliteColumnCommentImportReviewEntry>();
        }
    }

    public sealed class SqliteColumnCommentCliResult
    {
        public bool Handled { get; set; }
        public int ExitCode { get; set; }
        public string Message { get; set; }
    }

    public static class SqliteColumnCommentCliService
    {
        private const string ExportCommand = "--sqlite-comments-export";
        private const string ImportCommand = "--sqlite-comments-import";

        public static SqliteColumnCommentCliResult TryRun(string[] args)
        {
            if (args == null || args.Length == 0 || !IsCommand(args[0]))
            {
                return new SqliteColumnCommentCliResult { Handled = false, ExitCode = 0, Message = "" };
            }

            try
            {
                return RunCommand(args);
            }
            catch (Exception ex)
            {
                return new SqliteColumnCommentCliResult
                {
                    Handled = true,
                    ExitCode = 1,
                    Message = ex.Message
                };
            }
        }

        private static SqliteColumnCommentCliResult RunCommand(string[] args)
        {
            string command = args[0];
            Dictionary<string, string> options = ParseOptions(args);
            string databasePath = RequireOption(options, "--database");
            string tableName = GetOption(options, "--table");

            using (my_sqlite db = new my_sqlite())
            {
                db.SetConn(BuildSqliteConnectionString(databasePath));
                db.Open();

                if (string.Equals(command, ExportCommand, StringComparison.OrdinalIgnoreCase))
                {
                    string outputPath = RequireOption(options, "--output");
                    SqliteColumnCommentExportResult result =
                        SqliteColumnCommentExchangeService.WriteExportFile(db, "main", tableName, outputPath);
                    return new SqliteColumnCommentCliResult
                    {
                        Handled = true,
                        ExitCode = 0,
                        Message = "Exported " + result.TableCount + " tables and " + result.CommentCount + " comments."
                    };
                }

                string inputPath = RequireOption(options, "--input");
                SqliteColumnCommentImportPlan plan =
                    SqliteColumnCommentExchangeService.ImportFromFile(db, inputPath, tableName);
                return new SqliteColumnCommentCliResult
                {
                    Handled = true,
                    ExitCode = 0,
                    Message = "Imported " + plan.TableCount + " tables and " + plan.CommentCount + " comments."
                };
            }
        }

        private static Dictionary<string, string> ParseOptions(string[] args)
        {
            Dictionary<string, string> options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < args.Length; i++)
            {
                string key = args[i] ?? string.Empty;
                if (!key.StartsWith("--", StringComparison.Ordinal)) continue;
                if (i + 1 >= args.Length || (args[i + 1] ?? string.Empty).StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException("Missing value for " + key + ".");
                }
                options[key] = args[++i];
            }
            return options;
        }

        private static string RequireOption(Dictionary<string, string> options, string key)
        {
            string value = GetOption(options, key);
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Missing required option " + key + ".");
            return value;
        }

        private static string GetOption(Dictionary<string, string> options, string key)
        {
            string value;
            return options != null && options.TryGetValue(key, out value) ? value : "";
        }

        private static bool IsCommand(string value)
        {
            return string.Equals(value, ExportCommand, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, ImportCommand, StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildSqliteConnectionString(string databasePath)
        {
            if (string.IsNullOrWhiteSpace(databasePath)) throw new ArgumentException("SQLite database path is required.");
            System.Data.SQLite.SQLiteConnectionStringBuilder builder = new System.Data.SQLite.SQLiteConnectionStringBuilder
            {
                DataSource = databasePath,
                Version = 3
            };
            return builder.ConnectionString;
        }
    }

    public static class SqliteColumnCommentExchangeService
    {
        public const int FormatVersion = 1;

        public static SqliteColumnCommentExportResult WriteExportFile(IDatabase db, string databaseName, string tableName, string targetPath)
        {
            if (string.IsNullOrWhiteSpace(targetPath)) throw new ArgumentException("targetPath");

            string dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            SqliteColumnCommentExportResult result;
            if (IsXlsxPath(targetPath))
            {
                WriteExportXlsxFile(db, databaseName, tableName, targetPath, out result);
                return result;
            }

            string content = IsCsvPath(targetPath)
                ? BuildExportCsv(db, databaseName, tableName, out result)
                : (IsYamlPath(targetPath)
                    ? BuildExportYaml(db, databaseName, tableName, out result)
                    : BuildExportJson(db, databaseName, tableName, out result));
            File.WriteAllText(targetPath, content, new UTF8Encoding(false));
            return result;
        }

        public static string BuildExportJson(IDatabase db, string databaseName, string tableName, out SqliteColumnCommentExportResult result)
        {
            EnsureSqlite(db);

            result = new SqliteColumnCommentExportResult();
            List<string> tableNames = new List<string>();
            if (!string.IsNullOrWhiteSpace(tableName))
            {
                tableNames.Add(tableName);
            }
            else
            {
                tableNames.AddRange(db.GetTables(databaseName) ?? new List<string>());
            }

            JArray tables = new JArray();
            foreach (string currentTable in tableNames)
            {
                if (string.IsNullOrWhiteSpace(currentTable)) continue;

                Dictionary<string, string> comments = ReadColumnComments(db, databaseName, currentTable);
                if (comments.Count == 0) continue;

                JObject columns = new JObject();
                foreach (var item in comments)
                {
                    columns[item.Key] = item.Value;
                    result.CommentCount++;
                }

                tables.Add(new JObject
                {
                    ["name"] = currentTable,
                    ["columns"] = columns
                });
                result.TableCount++;
            }

            JObject root = new JObject
            {
                ["version"] = FormatVersion,
                ["source"] = "mySQLPunk SQLite column comments",
                ["provider"] = "sqlite",
                ["database"] = databaseName ?? "",
                ["exportedAtUtc"] = DateTime.UtcNow.ToString("o"),
                ["tableCount"] = result.TableCount,
                ["commentCount"] = result.CommentCount,
                ["tables"] = tables
            };
            return root.ToString(Formatting.Indented);
        }

        public static string BuildExportCsv(IDatabase db, string databaseName, string tableName, out SqliteColumnCommentExportResult result)
        {
            EnsureSqlite(db);

            result = new SqliteColumnCommentExportResult();
            List<string> tableNames = new List<string>();
            if (!string.IsNullOrWhiteSpace(tableName))
            {
                tableNames.Add(tableName);
            }
            else
            {
                tableNames.AddRange(db.GetTables(databaseName) ?? new List<string>());
            }

            StringBuilder builder = new StringBuilder();
            builder.AppendLine("table,column,comment");
            foreach (string currentTable in tableNames)
            {
                if (string.IsNullOrWhiteSpace(currentTable)) continue;
                Dictionary<string, string> comments = ReadColumnComments(db, databaseName, currentTable);
                if (comments.Count == 0) continue;

                result.TableCount++;
                foreach (var item in comments)
                {
                    builder.Append(CsvField(currentTable));
                    builder.Append(",");
                    builder.Append(CsvField(item.Key));
                    builder.Append(",");
                    builder.AppendLine(CsvField(item.Value));
                    result.CommentCount++;
                }
            }
            return builder.ToString();
        }

        public static string BuildExportYaml(IDatabase db, string databaseName, string tableName, out SqliteColumnCommentExportResult result)
        {
            EnsureSqlite(db);

            result = new SqliteColumnCommentExportResult();
            List<string> tableNames = new List<string>();
            if (!string.IsNullOrWhiteSpace(tableName))
            {
                tableNames.Add(tableName);
            }
            else
            {
                tableNames.AddRange(db.GetTables(databaseName) ?? new List<string>());
            }

            StringBuilder builder = new StringBuilder();
            builder.AppendLine("version: " + FormatVersion);
            builder.AppendLine("provider: sqlite");
            builder.AppendLine("comments:");
            foreach (string currentTable in tableNames)
            {
                if (string.IsNullOrWhiteSpace(currentTable)) continue;
                Dictionary<string, string> comments = ReadColumnComments(db, databaseName, currentTable);
                if (comments.Count == 0) continue;

                result.TableCount++;
                foreach (var item in comments)
                {
                    builder.AppendLine("- table: " + YamlScalar(currentTable));
                    builder.AppendLine("  column: " + YamlScalar(item.Key));
                    builder.AppendLine("  comment: " + YamlScalar(item.Value));
                    result.CommentCount++;
                }
            }
            return builder.ToString();
        }

        public static SqliteColumnCommentImportPlan BuildImportPlanFromFile(string sourcePath, string tableNameFilter = null)
        {
            if (string.IsNullOrWhiteSpace(sourcePath)) throw new ArgumentException("sourcePath");
            if (IsXlsxPath(sourcePath)) return BuildImportPlanFromXlsx(sourcePath, tableNameFilter);

            string text = File.ReadAllText(sourcePath, Encoding.UTF8);
            return IsCsvPath(sourcePath)
                ? BuildImportPlanFromCsv(text, tableNameFilter)
                : (IsYamlPath(sourcePath)
                    ? BuildImportPlanFromYaml(text, tableNameFilter)
                    : BuildImportPlan(text, tableNameFilter));
        }

        public static SqliteColumnCommentImportPlan BuildImportPlan(string json, string tableNameFilter = null)
        {
            Dictionary<string, Dictionary<string, string>> tables = ParseExchangeJson(json, tableNameFilter);
            return BuildImportPlanFromTables(tables);
        }

        public static SqliteColumnCommentImportPlan BuildImportPlanFromCsv(string csv, string tableNameFilter = null)
        {
            Dictionary<string, Dictionary<string, string>> tables = ParseExchangeCsv(csv, tableNameFilter);
            return BuildImportPlanFromTables(tables);
        }

        public static SqliteColumnCommentImportPlan BuildImportPlanFromYaml(string yaml, string tableNameFilter = null)
        {
            Dictionary<string, Dictionary<string, string>> tables = ParseExchangeYaml(yaml, tableNameFilter);
            return BuildImportPlanFromTables(tables);
        }

        public static SqliteColumnCommentImportPlan BuildImportPlanFromXlsx(string sourcePath, string tableNameFilter = null)
        {
            Dictionary<string, Dictionary<string, string>> tables = ParseExchangeXlsx(sourcePath, tableNameFilter);
            return BuildImportPlanFromTables(tables);
        }

        private static SqliteColumnCommentImportPlan BuildImportPlanFromTables(Dictionary<string, Dictionary<string, string>> tables)
        {
            SqliteColumnCommentImportPlan plan = new SqliteColumnCommentImportPlan();
            plan.Tables = tables;
            plan.TableCount = tables.Count;
            plan.Statements.Add(BuildEnsureSidecarTableSql());

            foreach (var table in tables)
            {
                plan.Statements.Add("DELETE FROM " + QuoteSqliteIdentifier(my_sqlite.ColumnCommentTableName) +
                                    " WHERE table_name = '" + EscapeSqlLiteral(table.Key) + "';");
                foreach (var column in table.Value)
                {
                    plan.Statements.Add("INSERT OR REPLACE INTO " + QuoteSqliteIdentifier(my_sqlite.ColumnCommentTableName) +
                                        " (table_name, column_name, comment) VALUES (" +
                                        "'" + EscapeSqlLiteral(table.Key) + "', " +
                                        "'" + EscapeSqlLiteral(column.Key) + "', " +
                                        "'" + EscapeSqlLiteral(column.Value) + "');");
                    plan.CommentCount++;
                }
            }

            return plan;
        }

        public static SqliteColumnCommentImportPlan ImportFromFile(IDatabase db, string sourcePath, string tableNameFilter = null)
        {
            EnsureSqlite(db);
            SqliteColumnCommentImportPlan plan = BuildImportPlanFromFile(sourcePath, tableNameFilter);
            foreach (string statement in plan.Statements)
            {
                db.ExecSQL(statement);
            }
            return plan;
        }

        public static SqliteColumnCommentImportReviewReport BuildImportReviewReport(
            IDatabase db,
            string databaseName,
            SqliteColumnCommentImportPlan plan)
        {
            EnsureSqlite(db);
            if (plan == null) throw new ArgumentNullException(nameof(plan));

            SqliteColumnCommentImportReviewReport report = new SqliteColumnCommentImportReviewReport
            {
                DatabaseName = databaseName ?? string.Empty,
                TableCount = plan.TableCount,
                CommentCount = plan.CommentCount
            };

            foreach (KeyValuePair<string, Dictionary<string, string>> table in plan.Tables)
            {
                Dictionary<string, string> current = ReadColumnComments(db, databaseName, table.Key);
                Dictionary<string, string> imported = table.Value ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (KeyValuePair<string, string> column in imported)
                {
                    string currentComment;
                    bool hasCurrent = current.TryGetValue(column.Key, out currentComment);
                    string status = !hasCurrent
                        ? "added"
                        : (string.Equals(currentComment ?? string.Empty, column.Value ?? string.Empty, StringComparison.Ordinal)
                            ? "unchanged"
                            : "updated");
                    AddImportReviewEntry(report, table.Key, column.Key, hasCurrent ? currentComment : string.Empty, column.Value, status);
                }

                foreach (KeyValuePair<string, string> column in current)
                {
                    if (imported.ContainsKey(column.Key)) continue;
                    AddImportReviewEntry(report, table.Key, column.Key, column.Value, string.Empty, "removed");
                }
            }

            report.Entries.Sort((left, right) =>
            {
                int tableCompare = string.Compare(left.TableName, right.TableName, StringComparison.OrdinalIgnoreCase);
                return tableCompare != 0
                    ? tableCompare
                    : string.Compare(left.ColumnName, right.ColumnName, StringComparison.OrdinalIgnoreCase);
            });
            return report;
        }

        public static string BuildImportReviewSummary(SqliteColumnCommentImportReviewReport report)
        {
            if (report == null) return string.Empty;
            return "審核摘要：新增 " + report.Added +
                   "、更新 " + report.Updated +
                   "、移除 " + report.Removed +
                   "、不變 " + report.Unchanged;
        }

        public static string WriteImportReviewReport(SqliteColumnCommentImportReviewReport report, string reportDirectory)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));
            if (string.IsNullOrWhiteSpace(reportDirectory)) throw new ArgumentException("Report directory is required.", nameof(reportDirectory));

            Directory.CreateDirectory(reportDirectory);
            string fileName = "sqlite-column-comments-review_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + ".json";
            string path = Path.Combine(reportDirectory, fileName);
            File.WriteAllText(path, JsonConvert.SerializeObject(report, Formatting.Indented), new UTF8Encoding(false));
            return path;
        }

        private static void AddImportReviewEntry(
            SqliteColumnCommentImportReviewReport report,
            string tableName,
            string columnName,
            string currentComment,
            string importedComment,
            string status)
        {
            report.Entries.Add(new SqliteColumnCommentImportReviewEntry
            {
                TableName = tableName ?? string.Empty,
                ColumnName = columnName ?? string.Empty,
                CurrentComment = currentComment ?? string.Empty,
                ImportedComment = importedComment ?? string.Empty,
                Status = status ?? string.Empty
            });

            if (string.Equals(status, "added", StringComparison.OrdinalIgnoreCase)) report.Added++;
            else if (string.Equals(status, "updated", StringComparison.OrdinalIgnoreCase)) report.Updated++;
            else if (string.Equals(status, "removed", StringComparison.OrdinalIgnoreCase)) report.Removed++;
            else if (string.Equals(status, "unchanged", StringComparison.OrdinalIgnoreCase)) report.Unchanged++;
        }

        public static Dictionary<string, Dictionary<string, string>> ParseExchangeJson(string json, string tableNameFilter = null)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new InvalidOperationException("SQLite column comment exchange file is empty.");

            Dictionary<string, Dictionary<string, string>> tables =
                new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            JToken rootToken = JToken.Parse(json);
            JArray rootArray = rootToken as JArray;
            if (rootArray != null)
            {
                AddParsedCommentRows(tables, rootArray, tableNameFilter);
                if (tables.Count == 0) throw new InvalidOperationException("SQLite column comment exchange file has no usable comments.");
                return tables;
            }

            JObject root = rootToken as JObject;
            if (root == null) throw new InvalidOperationException("SQLite column comment exchange file is not a supported JSON object.");

            JArray commentArray = root["comments"] as JArray;
            if (commentArray != null)
            {
                AddParsedCommentRows(tables, commentArray, tableNameFilter);
            }

            JArray tableArray = root["tables"] as JArray;
            if (tableArray != null)
            {
                foreach (JObject tableObject in tableArray)
                {
                    string tableName = tableObject.Value<string>("name");
                    JArray columnArray = tableObject["columns"] as JArray;
                    if (columnArray != null)
                    {
                        AddParsedCommentRows(tables, columnArray, tableNameFilter, tableName);
                    }
                    else
                    {
                        AddParsedTable(tables, tableName, tableObject["columns"] as JObject, tableNameFilter);
                    }
                }
            }
            else if (commentArray == null)
            {
                foreach (JProperty property in root.Properties())
                {
                    if (IsReservedRootProperty(property.Name)) continue;
                    AddParsedTable(tables, property.Name, property.Value as JObject, tableNameFilter);
                }
            }

            if (tables.Count == 0) throw new InvalidOperationException("SQLite column comment exchange file has no usable comments.");
            return tables;
        }

        public static Dictionary<string, Dictionary<string, string>> ParseExchangeCsv(string csv, string tableNameFilter = null)
        {
            if (string.IsNullOrWhiteSpace(csv)) throw new InvalidOperationException("SQLite column comment CSV is empty.");

            List<List<string>> rows = ParseCsvRows(csv);
            return ParseExchangeRows(rows, "CSV", tableNameFilter);
        }

        private static Dictionary<string, Dictionary<string, string>> ParseExchangeRows(
            List<List<string>> rows,
            string sourceName,
            string tableNameFilter)
        {
            if (rows == null || rows.Count == 0) throw new InvalidOperationException("SQLite column comment " + sourceName + " has no rows.");
            Dictionary<string, int> header = BuildCsvHeaderMap(rows[0]);
            int tableIndex = FindCsvIndex(header, "table", "tablename", "table_name", "object", "objectname", "object_name", "entity", "entityname");
            int columnIndex = FindCsvIndex(header, "column", "columnname", "column_name", "field", "fieldname", "field_name", "attribute", "attributename", "name");
            int commentIndex = FindCsvIndex(header, "comment", "commenttext", "comment_text", "description", "column_description", "columndescription", "remarks", "remark", "memo", "note");
            if (tableIndex < 0 || columnIndex < 0 || commentIndex < 0)
            {
                throw new InvalidOperationException("SQLite column comment " + sourceName + " requires table, column and comment headers.");
            }

            Dictionary<string, Dictionary<string, string>> tables =
                new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < rows.Count; i++)
            {
                List<string> row = rows[i];
                string tableName = CsvValue(row, tableIndex);
                string columnName = CsvValue(row, columnIndex);
                string comment = CsvValue(row, commentIndex);
                if (string.IsNullOrWhiteSpace(tableName) ||
                    string.IsNullOrWhiteSpace(columnName) ||
                    string.IsNullOrWhiteSpace(comment))
                {
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(tableNameFilter) &&
                    !string.Equals(tableName, tableNameFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Dictionary<string, string> columns;
                if (!tables.TryGetValue(tableName.Trim(), out columns))
                {
                    columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    tables[tableName.Trim()] = columns;
                }
                columns[columnName.Trim()] = comment.Trim();
            }

            if (tables.Count == 0) throw new InvalidOperationException("SQLite column comment " + sourceName + " has no usable comments.");
            return tables;
        }

        public static Dictionary<string, Dictionary<string, string>> ParseExchangeYaml(string yaml, string tableNameFilter = null)
        {
            if (string.IsNullOrWhiteSpace(yaml)) throw new InvalidOperationException("SQLite column comment YAML is empty.");

            Dictionary<string, Dictionary<string, string>> tables =
                new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> current = null;
            string[] lines = yaml.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                if (string.Equals(line, "comments:", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("version:", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("provider:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (line.StartsWith("- ", StringComparison.Ordinal))
                {
                    AddYamlCommentRow(tables, current, tableNameFilter);
                    current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    line = line.Substring(2).Trim();
                }

                int colon = line.IndexOf(':');
                if (colon < 0 || current == null) continue;
                string key = NormalizeCsvHeader(line.Substring(0, colon));
                string value = ParseYamlScalar(line.Substring(colon + 1).Trim());
                if (key == "table" || key == "tablename" || key == "table_name" ||
                    key == "object" || key == "objectname" || key == "object_name" ||
                    key == "entity" || key == "entityname")
                {
                    current["table"] = value;
                }
                else if (key == "column" || key == "columnname" || key == "column_name" ||
                         key == "field" || key == "fieldname" || key == "field_name" ||
                         key == "attribute" || key == "attributename" || key == "name")
                {
                    current["column"] = value;
                }
                else if (key == "comment" || key == "commenttext" || key == "comment_text" ||
                         key == "description" || key == "column_description" || key == "columndescription" ||
                         key == "remarks" || key == "remark" || key == "memo" || key == "note")
                {
                    current["comment"] = value;
                }
            }
            AddYamlCommentRow(tables, current, tableNameFilter);

            if (tables.Count == 0) throw new InvalidOperationException("SQLite column comment YAML has no usable comments.");
            return tables;
        }

        public static Dictionary<string, Dictionary<string, string>> ParseExchangeXlsx(string sourcePath, string tableNameFilter = null)
        {
            if (string.IsNullOrWhiteSpace(sourcePath)) throw new ArgumentException("sourcePath");
            if (!File.Exists(sourcePath)) throw new FileNotFoundException(Localization.Format("Designer.SqliteColumnCommentXlsxFileMissing", sourcePath ?? string.Empty), sourcePath);

            List<List<string>> rows = ReadXlsxRows(sourcePath);
            return ParseExchangeRows(rows, "XLSX", tableNameFilter);
        }

        public static string BuildEnsureSidecarTableSql()
        {
            return "CREATE TABLE IF NOT EXISTS " + QuoteSqliteIdentifier(my_sqlite.ColumnCommentTableName) + " (" +
                   "table_name TEXT NOT NULL, " +
                   "column_name TEXT NOT NULL, " +
                   "comment TEXT NOT NULL, " +
                   "PRIMARY KEY (table_name, column_name));";
        }

        private static Dictionary<string, string> ReadColumnComments(IDatabase db, string databaseName, string tableName)
        {
            DataTable columns = db.GetColumns(databaseName, tableName);
            Dictionary<string, string> comments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (columns == null) return comments;

            foreach (DataRow row in columns.Rows)
            {
                string columnName = FirstColumnValue(row, "name", "Name", "COLUMN_NAME", "column_name", "Field");
                string comment = FirstColumnValue(row, "Comment", "comment");
                if (string.IsNullOrWhiteSpace(columnName) || string.IsNullOrWhiteSpace(comment)) continue;
                comments[columnName] = comment;
            }
            return comments;
        }

        private static void AddParsedTable(
            Dictionary<string, Dictionary<string, string>> tables,
            string tableName,
            JObject columns,
            string tableNameFilter)
        {
            if (string.IsNullOrWhiteSpace(tableName) || columns == null) return;
            if (!string.IsNullOrWhiteSpace(tableNameFilter) &&
                !string.Equals(tableName, tableNameFilter, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Dictionary<string, string> comments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (JProperty column in columns.Properties())
            {
                string columnName = column.Name;
                string comment = column.Value.Type == JTokenType.Null ? "" : column.Value.ToString();
                if (string.IsNullOrWhiteSpace(columnName) || string.IsNullOrWhiteSpace(comment)) continue;
                comments[columnName] = comment.Trim();
            }

            if (comments.Count > 0) tables[tableName.Trim()] = comments;
        }

        private static void AddParsedCommentRows(
            Dictionary<string, Dictionary<string, string>> tables,
            JArray rows,
            string tableNameFilter,
            string fallbackTableName = null)
        {
            if (rows == null) return;
            foreach (JObject row in rows)
            {
                if (row == null) continue;
                string tableName = FirstJsonValue(row, "table", "tableName", "table_name", "TABLE_NAME", "object", "objectName", "object_name", "entity", "entityName");
                if (string.IsNullOrWhiteSpace(tableName)) tableName = fallbackTableName;
                string columnName = FirstJsonValue(row, "column", "columnName", "column_name", "COLUMN_NAME", "field", "fieldName", "field_name", "attribute", "attributeName", "name", "Name");
                string comment = FirstJsonValue(row, "comment", "Comment", "commentText", "comment_text", "description", "Description", "columnDescription", "column_description", "remarks", "Remarks", "remark", "memo", "note");

                if (string.IsNullOrWhiteSpace(tableName) ||
                    string.IsNullOrWhiteSpace(columnName) ||
                    string.IsNullOrWhiteSpace(comment))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(tableNameFilter) &&
                    !string.Equals(tableName, tableNameFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Dictionary<string, string> columns;
                if (!tables.TryGetValue(tableName.Trim(), out columns))
                {
                    columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    tables[tableName.Trim()] = columns;
                }
                columns[columnName.Trim()] = comment.Trim();
            }
        }

        private static string FirstJsonValue(JObject row, params string[] names)
        {
            if (row == null) return "";
            foreach (string name in names)
            {
                JToken value;
                if (row.TryGetValue(name, StringComparison.OrdinalIgnoreCase, out value) &&
                    value != null &&
                    value.Type != JTokenType.Null)
                {
                    return value.ToString();
                }
            }
            return "";
        }

        private static bool IsReservedRootProperty(string name)
        {
            return string.Equals(name, "version", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "source", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "provider", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "database", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "exportedAtUtc", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "tableCount", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "commentCount", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "comments", StringComparison.OrdinalIgnoreCase);
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

        private static bool IsCsvPath(string path)
        {
            return string.Equals(Path.GetExtension(path), ".csv", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsYamlPath(string path)
        {
            string extension = Path.GetExtension(path);
            return string.Equals(extension, ".yaml", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".yml", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsXlsxPath(string path)
        {
            return string.Equals(Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase);
        }

        private static void AddYamlCommentRow(
            Dictionary<string, Dictionary<string, string>> tables,
            Dictionary<string, string> row,
            string tableNameFilter)
        {
            if (row == null) return;
            string tableName = row.ContainsKey("table") ? row["table"] : "";
            string columnName = row.ContainsKey("column") ? row["column"] : "";
            string comment = row.ContainsKey("comment") ? row["comment"] : "";
            if (string.IsNullOrWhiteSpace(tableName) ||
                string.IsNullOrWhiteSpace(columnName) ||
                string.IsNullOrWhiteSpace(comment))
            {
                return;
            }
            if (!string.IsNullOrWhiteSpace(tableNameFilter) &&
                !string.Equals(tableName, tableNameFilter, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Dictionary<string, string> columns;
            if (!tables.TryGetValue(tableName.Trim(), out columns))
            {
                columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                tables[tableName.Trim()] = columns;
            }
            columns[columnName.Trim()] = comment.Trim();
        }

        private static string YamlScalar(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static string ParseYamlScalar(string value)
        {
            string text = value ?? string.Empty;
            if (text.Length >= 2 && text[0] == '"' && text[text.Length - 1] == '"')
            {
                text = text.Substring(1, text.Length - 2);
                StringBuilder builder = new StringBuilder();
                bool escaped = false;
                foreach (char ch in text)
                {
                    if (escaped)
                    {
                        builder.Append(ch);
                        escaped = false;
                    }
                    else if (ch == '\\')
                    {
                        escaped = true;
                    }
                    else
                    {
                        builder.Append(ch);
                    }
                }
                if (escaped) builder.Append('\\');
                return builder.ToString();
            }
            if (text.Length >= 2 && text[0] == '\'' && text[text.Length - 1] == '\'')
            {
                return text.Substring(1, text.Length - 2).Replace("''", "'");
            }
            return text;
        }

        private static string CsvField(string value)
        {
            string text = value ?? string.Empty;
            if (text.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0) return text;
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }

        private static List<List<string>> ParseCsvRows(string csv)
        {
            List<List<string>> rows = new List<List<string>>();
            List<string> row = new List<string>();
            StringBuilder field = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < csv.Length; i++)
            {
                char ch = csv[i];
                if (inQuotes)
                {
                    if (ch == '"')
                    {
                        if (i + 1 < csv.Length && csv[i + 1] == '"')
                        {
                            field.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        field.Append(ch);
                    }
                    continue;
                }

                if (ch == '"')
                {
                    inQuotes = true;
                }
                else if (ch == ',')
                {
                    row.Add(field.ToString());
                    field.Length = 0;
                }
                else if (ch == '\r' || ch == '\n')
                {
                    if (ch == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n') i++;
                    row.Add(field.ToString());
                    field.Length = 0;
                    if (row.Count > 1 || !string.IsNullOrWhiteSpace(row[0])) rows.Add(row);
                    row = new List<string>();
                }
                else
                {
                    field.Append(ch);
                }
            }

            if (field.Length > 0 || row.Count > 0)
            {
                row.Add(field.ToString());
                if (row.Count > 1 || !string.IsNullOrWhiteSpace(row[0])) rows.Add(row);
            }
            return rows;
        }

        private static Dictionary<string, int> BuildCsvHeaderMap(List<string> headerRow)
        {
            Dictionary<string, int> map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (headerRow == null) return map;
            for (int i = 0; i < headerRow.Count; i++)
            {
                string name = NormalizeCsvHeader(headerRow[i]);
                if (!string.IsNullOrWhiteSpace(name) && !map.ContainsKey(name)) map[name] = i;
            }
            return map;
        }

        private static int FindCsvIndex(Dictionary<string, int> header, params string[] names)
        {
            foreach (string name in names)
            {
                int index;
                if (header.TryGetValue(NormalizeCsvHeader(name), out index)) return index;
            }
            return -1;
        }

        private static string NormalizeCsvHeader(string value)
        {
            return (value ?? string.Empty).Trim().Replace("-", "").Replace("_", "").Replace(" ", "").ToLowerInvariant();
        }

        private static string CsvValue(List<string> row, int index)
        {
            return row != null && index >= 0 && index < row.Count ? row[index] : "";
        }

        private static void WriteExportXlsxFile(IDatabase db, string databaseName, string tableName, string targetPath, out SqliteColumnCommentExportResult result)
        {
            EnsureSqlite(db);
            result = new SqliteColumnCommentExportResult();
            if (File.Exists(targetPath)) File.Delete(targetPath);

            List<List<string>> rows = BuildExportRows(db, databaseName, tableName, result);
            using (ZipArchive archive = ZipFile.Open(targetPath, ZipArchiveMode.Create))
            {
                AddZipEntry(archive, "[Content_Types].xml", BuildXlsxContentTypes());
                AddZipEntry(archive, "_rels/.rels", BuildXlsxRootRelationships());
                AddZipEntry(archive, "xl/workbook.xml", BuildXlsxWorkbook());
                AddZipEntry(archive, "xl/_rels/workbook.xml.rels", BuildXlsxWorkbookRelationships());
                AddZipEntry(archive, "xl/worksheets/sheet1.xml", BuildXlsxSheet(rows));
                AddZipEntry(archive, "xl/styles.xml", BuildXlsxStyles());
            }
        }

        private static List<List<string>> BuildExportRows(IDatabase db, string databaseName, string tableName, SqliteColumnCommentExportResult result)
        {
            List<List<string>> rows = new List<List<string>>();
            rows.Add(new List<string> { "provider", "database", "table", "column", "type", "not_null", "default_value", "comment" });

            List<string> tableNames = new List<string>();
            if (!string.IsNullOrWhiteSpace(tableName))
            {
                tableNames.Add(tableName);
            }
            else
            {
                tableNames.AddRange(db.GetTables(databaseName) ?? new List<string>());
            }

            foreach (string currentTable in tableNames)
            {
                if (string.IsNullOrWhiteSpace(currentTable)) continue;
                DataTable columns = db.GetColumns(databaseName, currentTable);
                if (columns == null) continue;

                bool countedTable = false;
                foreach (DataRow row in columns.Rows)
                {
                    string columnName = FirstColumnValue(row, "name", "Name", "COLUMN_NAME", "column_name", "Field");
                    string comment = FirstColumnValue(row, "Comment", "comment", "description", "remarks");
                    if (string.IsNullOrWhiteSpace(columnName) || string.IsNullOrWhiteSpace(comment)) continue;

                    if (!countedTable)
                    {
                        result.TableCount++;
                        countedTable = true;
                    }

                    rows.Add(new List<string>
                    {
                        "sqlite",
                        databaseName ?? string.Empty,
                        currentTable,
                        columnName,
                        FirstColumnValue(row, "Type", "type", "DATA_TYPE", "data_type"),
                        FirstColumnValue(row, "NotNull", "not_null", "IS_NULLABLE", "Nullable"),
                        FirstColumnValue(row, "Default", "default", "DEFAULT", "COLUMN_DEFAULT", "dflt_value"),
                        comment
                    });
                    result.CommentCount++;
                }
            }

            return rows;
        }

        private static List<List<string>> ReadXlsxRows(string sourcePath)
        {
            using (ZipArchive archive = ZipFile.OpenRead(sourcePath))
            {
                List<string> sharedStrings = LoadSharedStrings(archive);
                ZipArchiveEntry sheet = archive.GetEntry("xl/worksheets/sheet1.xml");
                if (sheet == null) throw new InvalidOperationException("SQLite column comment XLSX has no first worksheet.");

                XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                XDocument document;
                using (Stream stream = sheet.Open())
                {
                    document = XDocument.Load(stream);
                }

                List<List<string>> rows = new List<List<string>>();
                foreach (XElement rowElement in document.Descendants(ns + "row"))
                {
                    List<string> row = new List<string>();
                    foreach (XElement cell in rowElement.Elements(ns + "c"))
                    {
                        int columnIndex = GetXlsxColumnIndex((string)cell.Attribute("r"));
                        if (columnIndex <= 0) columnIndex = row.Count + 1;
                        while (row.Count < columnIndex) row.Add("");
                        row[columnIndex - 1] = GetXlsxCellText(cell, sharedStrings, ns);
                    }

                    if (HasAnyValue(row)) rows.Add(row);
                }

                return rows;
            }
        }

        private static List<string> LoadSharedStrings(ZipArchive archive)
        {
            List<string> sharedStrings = new List<string>();
            ZipArchiveEntry entry = archive.GetEntry("xl/sharedStrings.xml");
            if (entry == null) return sharedStrings;

            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            XDocument document;
            using (Stream stream = entry.Open())
            {
                document = XDocument.Load(stream);
            }

            foreach (XElement item in document.Descendants(ns + "si"))
            {
                StringBuilder builder = new StringBuilder();
                foreach (XElement text in item.Descendants(ns + "t"))
                {
                    builder.Append(text.Value);
                }
                sharedStrings.Add(builder.ToString());
            }

            return sharedStrings;
        }

        private static string GetXlsxCellText(XElement cell, List<string> sharedStrings, XNamespace ns)
        {
            string cellType = ((string)cell.Attribute("t") ?? "").Trim();
            if (string.Equals(cellType, "inlineStr", StringComparison.OrdinalIgnoreCase))
            {
                StringBuilder builder = new StringBuilder();
                foreach (XElement text in cell.Descendants(ns + "t"))
                {
                    builder.Append(text.Value);
                }
                return builder.ToString();
            }

            XElement valueElement = cell.Element(ns + "v");
            string rawValue = valueElement == null ? "" : valueElement.Value;
            if (string.Equals(cellType, "s", StringComparison.OrdinalIgnoreCase))
            {
                int sharedIndex;
                if (int.TryParse(rawValue, out sharedIndex) &&
                    sharedIndex >= 0 &&
                    sharedIndex < sharedStrings.Count)
                {
                    return sharedStrings[sharedIndex];
                }
                return "";
            }

            return rawValue;
        }

        private static bool HasAnyValue(List<string> row)
        {
            if (row == null) return false;
            foreach (string value in row)
            {
                if (!string.IsNullOrWhiteSpace(value)) return true;
            }
            return false;
        }

        private static int GetXlsxColumnIndex(string cellReference)
        {
            if (string.IsNullOrWhiteSpace(cellReference)) return -1;
            int index = 0;
            foreach (char ch in cellReference)
            {
                if (ch >= 'A' && ch <= 'Z')
                {
                    index = index * 26 + (ch - 'A' + 1);
                }
                else if (ch >= 'a' && ch <= 'z')
                {
                    index = index * 26 + (ch - 'a' + 1);
                }
                else
                {
                    break;
                }
            }
            return index;
        }

        private static void AddZipEntry(ZipArchive archive, string path, string content)
        {
            ZipArchiveEntry entry = archive.CreateEntry(path, CompressionLevel.Optimal);
            using (Stream stream = entry.Open())
            using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(content);
            }
        }

        private static string BuildXlsxSheet(List<List<string>> rows)
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            builder.AppendLine("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");

            for (int r = 0; r < rows.Count; r++)
            {
                int rowIndex = r + 1;
                builder.Append("<row r=\"").Append(rowIndex).Append("\">");
                List<string> row = rows[r];
                for (int c = 0; c < row.Count; c++)
                {
                    AppendInlineStringCell(builder, rowIndex, c + 1, row[c]);
                }
                builder.AppendLine("</row>");
            }

            builder.AppendLine("</sheetData></worksheet>");
            return builder.ToString();
        }

        private static void AppendInlineStringCell(StringBuilder builder, int rowIndex, int columnIndex, string value)
        {
            builder.Append("<c r=\"")
                .Append(GetExcelColumnName(columnIndex))
                .Append(rowIndex)
                .Append("\" t=\"inlineStr\"><is><t");
            if (!string.IsNullOrEmpty(value) &&
                (value.StartsWith(" ") || value.EndsWith(" ") || value.Contains("\n") || value.Contains("\r") || value.Contains("\t")))
            {
                builder.Append(" xml:space=\"preserve\"");
            }
            builder.Append(">")
                .Append(XmlEscape(value))
                .Append("</t></is></c>");
        }

        private static string BuildXlsxContentTypes()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                   "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                   "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                   "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                   "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                   "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                   "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
                   "</Types>";
        }

        private static string BuildXlsxRootRelationships()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                   "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                   "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                   "</Relationships>";
        }

        private static string BuildXlsxWorkbook()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                   "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                   "<sheets><sheet name=\"Comments\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>";
        }

        private static string BuildXlsxWorkbookRelationships()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                   "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                   "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
                   "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
                   "</Relationships>";
        }

        private static string BuildXlsxStyles()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                   "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                   "<fonts count=\"1\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
                   "<fills count=\"1\"><fill><patternFill patternType=\"none\"/></fill></fills>" +
                   "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
                   "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
                   "<cellXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/></cellXfs>" +
                   "</styleSheet>";
        }

        private static string XmlEscape(string value)
        {
            return System.Security.SecurityElement.Escape(value ?? string.Empty) ?? string.Empty;
        }

        private static string GetExcelColumnName(int columnNumber)
        {
            string columnName = string.Empty;
            while (columnNumber > 0)
            {
                int modulo = (columnNumber - 1) % 26;
                columnName = Convert.ToChar('A' + modulo) + columnName;
                columnNumber = (columnNumber - modulo) / 26;
            }
            return columnName;
        }

        private static void EnsureSqlite(IDatabase db)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (!string.Equals(db.ProviderName, "sqlite", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException("SQLite column comment exchange only supports SQLite connections.");
            }
        }

        private static string QuoteSqliteIdentifier(string name)
        {
            return "\"" + (name ?? string.Empty).Replace("\"", "\"\"") + "\"";
        }

        private static string EscapeSqlLiteral(string value)
        {
            return (value ?? string.Empty).Replace("'", "''");
        }
    }
}
