using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Text;
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

    public static class SqliteColumnCommentExchangeService
    {
        public const int FormatVersion = 1;

        public static SqliteColumnCommentExportResult WriteExportFile(IDatabase db, string databaseName, string tableName, string targetPath)
        {
            if (string.IsNullOrWhiteSpace(targetPath)) throw new ArgumentException("targetPath");

            string dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            SqliteColumnCommentExportResult result;
            string json = BuildExportJson(db, databaseName, tableName, out result);
            File.WriteAllText(targetPath, json, new UTF8Encoding(false));
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

        public static SqliteColumnCommentImportPlan BuildImportPlanFromFile(string sourcePath, string tableNameFilter = null)
        {
            if (string.IsNullOrWhiteSpace(sourcePath)) throw new ArgumentException("sourcePath");
            return BuildImportPlan(File.ReadAllText(sourcePath, Encoding.UTF8), tableNameFilter);
        }

        public static SqliteColumnCommentImportPlan BuildImportPlan(string json, string tableNameFilter = null)
        {
            Dictionary<string, Dictionary<string, string>> tables = ParseExchangeJson(json, tableNameFilter);
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

        public static Dictionary<string, Dictionary<string, string>> ParseExchangeJson(string json, string tableNameFilter = null)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new InvalidOperationException("SQLite column comment exchange file is empty.");

            JObject root = JObject.Parse(json);
            Dictionary<string, Dictionary<string, string>> tables =
                new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            JArray tableArray = root["tables"] as JArray;
            if (tableArray != null)
            {
                foreach (JObject tableObject in tableArray)
                {
                    string tableName = tableObject.Value<string>("name");
                    AddParsedTable(tables, tableName, tableObject["columns"] as JObject, tableNameFilter);
                }
            }
            else
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

        private static bool IsReservedRootProperty(string name)
        {
            return string.Equals(name, "version", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "source", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "provider", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "database", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "exportedAtUtc", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "tableCount", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "commentCount", StringComparison.OrdinalIgnoreCase);
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
