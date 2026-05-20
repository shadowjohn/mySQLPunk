using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;

namespace mySQLPunk.lib
{
    public sealed class DatabaseRestoreColumnSnapshot
    {
        public string TableName { get; set; }
        public string Name { get; set; }
        public string DataType { get; set; }
        public string IsNullable { get; set; }
        public string DefaultValue { get; set; }
        public string Comment { get; set; }
        public int OrdinalPosition { get; set; }
    }

    public sealed class DatabaseRestoreSnapshot
    {
        public string DatabaseName { get; set; }
        public string ProviderName { get; set; }
        public int TableCount { get; set; }
        public int ViewCount { get; set; }
        public int FunctionCount { get; set; }
        public int EventCount { get; set; }
        public List<string> Tables { get; private set; }
        public List<string> Views { get; private set; }
        public List<string> Functions { get; private set; }
        public List<string> Events { get; private set; }
        public Dictionary<string, long> TableRowCounts { get; private set; }
        public Dictionary<string, List<DatabaseRestoreColumnSnapshot>> TableColumns { get; private set; }

        public DatabaseRestoreSnapshot()
        {
            Tables = new List<string>();
            Views = new List<string>();
            Functions = new List<string>();
            Events = new List<string>();
            TableRowCounts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            TableColumns = new Dictionary<string, List<DatabaseRestoreColumnSnapshot>>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public static class BackupRestoreDiffService
    {
        public static DatabaseRestoreSnapshot CreateSnapshot(
            string databaseName,
            string providerName,
            int tableCount,
            int viewCount,
            int functionCount,
            int eventCount)
        {
            return new DatabaseRestoreSnapshot
            {
                DatabaseName = databaseName ?? string.Empty,
                ProviderName = providerName ?? string.Empty,
                TableCount = Math.Max(0, tableCount),
                ViewCount = Math.Max(0, viewCount),
                FunctionCount = Math.Max(0, functionCount),
                EventCount = Math.Max(0, eventCount)
            };
        }

        public static DatabaseRestoreSnapshot CreateSnapshot(
            string databaseName,
            string providerName,
            IEnumerable<string> tables,
            IEnumerable<string> views,
            IEnumerable<string> functions,
            IEnumerable<string> events)
        {
            DatabaseRestoreSnapshot snapshot = new DatabaseRestoreSnapshot
            {
                DatabaseName = databaseName ?? string.Empty,
                ProviderName = providerName ?? string.Empty
            };

            snapshot.Tables.AddRange(NormalizeNames(tables));
            snapshot.Views.AddRange(NormalizeNames(views));
            snapshot.Functions.AddRange(NormalizeNames(functions));
            snapshot.Events.AddRange(NormalizeNames(events));
            snapshot.TableCount = snapshot.Tables.Count;
            snapshot.ViewCount = snapshot.Views.Count;
            snapshot.FunctionCount = snapshot.Functions.Count;
            snapshot.EventCount = snapshot.Events.Count;
            return snapshot;
        }

        public static void AddTableColumns(
            DatabaseRestoreSnapshot snapshot,
            string tableName,
            IEnumerable<DatabaseRestoreColumnSnapshot> columns)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(tableName)) return;

            List<DatabaseRestoreColumnSnapshot> normalized = NormalizeColumns(tableName, columns);
            if (normalized.Count == 0)
            {
                snapshot.TableColumns.Remove(tableName.Trim());
                return;
            }

            snapshot.TableColumns[tableName.Trim()] = normalized;
        }

        public static void SetTableRowCount(DatabaseRestoreSnapshot snapshot, string tableName, long rowCount)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(tableName)) return;
            snapshot.TableRowCounts[tableName.Trim()] = Math.Max(0, rowCount);
        }

        public static List<DatabaseRestoreColumnSnapshot> CreateColumnSnapshots(string tableName, DataTable columns)
        {
            List<DatabaseRestoreColumnSnapshot> output = new List<DatabaseRestoreColumnSnapshot>();
            if (columns == null) return output;

            foreach (DataRow row in columns.Rows)
            {
                string name = FirstColumnValue(row, "Name", "name", "COLUMN_NAME", "column_name", "Field");
                if (string.IsNullOrWhiteSpace(name)) continue;

                output.Add(new DatabaseRestoreColumnSnapshot
                {
                    TableName = tableName ?? string.Empty,
                    Name = name,
                    DataType = FirstColumnValue(row, "DataType", "Type", "type", "DATA_TYPE", "data_type"),
                    IsNullable = FirstColumnValue(row, "IsNullable", "Nullable", "IS_NULLABLE", "nullable", "NotNull", "not_null"),
                    DefaultValue = FirstColumnValue(row, "Default", "default", "DefaultValue", "default_value", "COLUMN_DEFAULT", "column_default", "dflt_value"),
                    Comment = FirstColumnValue(row, "Comment", "comment", "description", "remarks"),
                    OrdinalPosition = ParseInt(FirstColumnValue(row, "OrdinalPosition", "ORDINAL_POSITION", "ordinal_position", "cid", "Seq"))
                });
            }

            return NormalizeColumns(tableName, output);
        }

        public static string BuildSummary(DatabaseRestoreSnapshot before, DatabaseRestoreSnapshot after)
        {
            if (before == null || after == null) return string.Empty;

            List<string> lines = new List<string>
            {
                BuildLine("資料表", before.TableCount, after.TableCount, before.Tables, after.Tables),
                BuildLine("檢視", before.ViewCount, after.ViewCount, before.Views, after.Views),
                BuildLine("函式/程序", before.FunctionCount, after.FunctionCount, before.Functions, after.Functions),
                BuildLine("事件/Trigger", before.EventCount, after.EventCount, before.Events, after.Events)
            };
            string rowCountDiff = BuildRowCountDiffSummary(before, after);
            if (!string.IsNullOrWhiteSpace(rowCountDiff))
            {
                lines.Add("資料列差異：" + rowCountDiff);
            }

            string columnDiff = BuildColumnDiffSummary(before, after);
            if (!string.IsNullOrWhiteSpace(columnDiff))
            {
                lines.Add("欄位差異：" + columnDiff);
            }
            return string.Join(Environment.NewLine, lines);
        }

        private static string BuildLine(string label, int before, int after, IEnumerable<string> beforeNames, IEnumerable<string> afterNames)
        {
            int delta = after - before;
            string deltaText = delta == 0 ? "0" : (delta > 0 ? "+" : "") + delta.ToString();
            string line = label + "：" + before + " -> " + after + " (" + deltaText + ")";
            string detail = BuildNameDiffDetail(beforeNames, afterNames);
            return string.IsNullOrWhiteSpace(detail) ? line : line + "，" + detail;
        }

        private static string BuildNameDiffDetail(IEnumerable<string> beforeNames, IEnumerable<string> afterNames)
        {
            List<string> beforeList = NormalizeNames(beforeNames);
            List<string> afterList = NormalizeNames(afterNames);
            if (beforeList.Count == 0 && afterList.Count == 0) return string.Empty;

            List<string> added = afterList.Except(beforeList, StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
            List<string> removed = beforeList.Except(afterList, StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
            List<string> parts = new List<string>();
            if (added.Count > 0) parts.Add("新增：" + FormatNameList(added));
            if (removed.Count > 0) parts.Add("移除：" + FormatNameList(removed));
            return string.Join("；", parts);
        }

        private static string BuildRowCountDiffSummary(DatabaseRestoreSnapshot before, DatabaseRestoreSnapshot after)
        {
            if ((before.TableRowCounts == null || before.TableRowCounts.Count == 0) &&
                (after.TableRowCounts == null || after.TableRowCounts.Count == 0))
            {
                return string.Empty;
            }

            List<string> tableNames = new List<string>();
            if (before.TableRowCounts != null) tableNames.AddRange(before.TableRowCounts.Keys);
            if (after.TableRowCounts != null) tableNames.AddRange(after.TableRowCounts.Keys);

            List<string> details = new List<string>();
            foreach (string tableName in NormalizeNames(tableNames))
            {
                bool hasBefore = before.TableRowCounts != null && before.TableRowCounts.ContainsKey(tableName);
                bool hasAfter = after.TableRowCounts != null && after.TableRowCounts.ContainsKey(tableName);
                long beforeCount = hasBefore ? before.TableRowCounts[tableName] : 0;
                long afterCount = hasAfter ? after.TableRowCounts[tableName] : 0;
                if (hasBefore && hasAfter && beforeCount == afterCount) continue;

                details.Add(tableName + "：" + FormatRowCountValue(hasBefore, beforeCount) + " -> " +
                    FormatRowCountValue(hasAfter, afterCount) + FormatRowCountDelta(hasBefore, hasAfter, beforeCount, afterCount));
            }

            return details.Count == 0 ? string.Empty : FormatNameList(details);
        }

        private static string FormatRowCountValue(bool hasValue, long value)
        {
            return hasValue ? value.ToString() : "未取得";
        }

        private static string FormatRowCountDelta(bool hasBefore, bool hasAfter, long before, long after)
        {
            if (!hasBefore || !hasAfter) return string.Empty;
            long delta = after - before;
            string deltaText = delta == 0 ? "0" : (delta > 0 ? "+" : "") + delta.ToString();
            return " (" + deltaText + ")";
        }

        private static string BuildColumnDiffSummary(DatabaseRestoreSnapshot before, DatabaseRestoreSnapshot after)
        {
            if ((before.TableColumns == null || before.TableColumns.Count == 0) &&
                (after.TableColumns == null || after.TableColumns.Count == 0))
            {
                return string.Empty;
            }

            List<string> details = new List<string>();
            List<string> tableNames = new List<string>();
            if (before.TableColumns != null) tableNames.AddRange(before.TableColumns.Keys);
            if (after.TableColumns != null) tableNames.AddRange(after.TableColumns.Keys);

            foreach (string tableName in NormalizeNames(tableNames))
            {
                List<DatabaseRestoreColumnSnapshot> beforeColumns = GetColumnsForTable(before, tableName);
                List<DatabaseRestoreColumnSnapshot> afterColumns = GetColumnsForTable(after, tableName);
                Dictionary<string, DatabaseRestoreColumnSnapshot> beforeMap = beforeColumns.ToDictionary(c => c.Name, c => c, StringComparer.OrdinalIgnoreCase);
                Dictionary<string, DatabaseRestoreColumnSnapshot> afterMap = afterColumns.ToDictionary(c => c.Name, c => c, StringComparer.OrdinalIgnoreCase);

                List<string> added = afterMap.Keys.Except(beforeMap.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
                List<string> removed = beforeMap.Keys.Except(afterMap.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
                foreach (string column in added) details.Add("新增 " + tableName + "." + column);
                foreach (string column in removed) details.Add("移除 " + tableName + "." + column);

                List<string> common = beforeMap.Keys.Intersect(afterMap.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
                foreach (string column in common)
                {
                    string change = BuildColumnChangeDetail(beforeMap[column], afterMap[column]);
                    if (!string.IsNullOrWhiteSpace(change)) details.Add("變更 " + tableName + "." + column + "（" + change + "）");
                }
            }

            if (details.Count == 0) return string.Empty;
            return FormatNameList(details);
        }

        private static string BuildColumnChangeDetail(DatabaseRestoreColumnSnapshot before, DatabaseRestoreColumnSnapshot after)
        {
            List<string> parts = new List<string>();
            AddChangedPart(parts, "型別", before.DataType, after.DataType);
            AddChangedPart(parts, "NULL", before.IsNullable, after.IsNullable);
            AddChangedPart(parts, "預設", before.DefaultValue, after.DefaultValue);
            AddChangedPart(parts, "註解", before.Comment, after.Comment);
            return string.Join("；", parts);
        }

        private static void AddChangedPart(List<string> parts, string label, string before, string after)
        {
            string oldValue = NormalizeText(before);
            string newValue = NormalizeText(after);
            if (string.Equals(oldValue, newValue, StringComparison.OrdinalIgnoreCase)) return;
            parts.Add(label + "：" + FormatValue(oldValue) + " -> " + FormatValue(newValue));
        }

        private static List<DatabaseRestoreColumnSnapshot> GetColumnsForTable(DatabaseRestoreSnapshot snapshot, string tableName)
        {
            List<DatabaseRestoreColumnSnapshot> columns;
            if (snapshot == null || snapshot.TableColumns == null || string.IsNullOrWhiteSpace(tableName)) return new List<DatabaseRestoreColumnSnapshot>();
            return snapshot.TableColumns.TryGetValue(tableName, out columns) ? columns : new List<DatabaseRestoreColumnSnapshot>();
        }

        private static List<string> NormalizeNames(IEnumerable<string> names)
        {
            if (names == null) return new List<string>();
            return names
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<DatabaseRestoreColumnSnapshot> NormalizeColumns(string tableName, IEnumerable<DatabaseRestoreColumnSnapshot> columns)
        {
            if (columns == null) return new List<DatabaseRestoreColumnSnapshot>();

            return columns
                .Where(column => column != null && !string.IsNullOrWhiteSpace(column.Name))
                .GroupBy(column => column.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    DatabaseRestoreColumnSnapshot column = group.OrderBy(c => c.OrdinalPosition <= 0 ? int.MaxValue : c.OrdinalPosition).First();
                    return new DatabaseRestoreColumnSnapshot
                    {
                        TableName = string.IsNullOrWhiteSpace(column.TableName) ? (tableName ?? string.Empty).Trim() : column.TableName.Trim(),
                        Name = column.Name.Trim(),
                        DataType = NormalizeText(column.DataType),
                        IsNullable = NormalizeText(column.IsNullable),
                        DefaultValue = NormalizeText(column.DefaultValue),
                        Comment = NormalizeText(column.Comment),
                        OrdinalPosition = column.OrdinalPosition
                    };
                })
                .OrderBy(column => column.OrdinalPosition <= 0 ? int.MaxValue : column.OrdinalPosition)
                .ThenBy(column => column.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string FirstColumnValue(DataRow row, params string[] names)
        {
            if (row == null || row.Table == null) return string.Empty;
            foreach (string name in names)
            {
                if (row.Table.Columns.Contains(name) && row[name] != DBNull.Value)
                {
                    return Convert.ToString(row[name]);
                }
            }

            return string.Empty;
        }

        private static int ParseInt(string value)
        {
            int output;
            return int.TryParse((value ?? string.Empty).Trim(), out output) ? output : 0;
        }

        private static string NormalizeText(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        private static string FormatValue(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "空白" : value;
        }

        private static string FormatNameList(List<string> names)
        {
            const int maxNames = 5;
            if (names.Count <= maxNames) return string.Join(", ", names);
            return string.Join(", ", names.Take(maxNames)) + " ... 等 " + names.Count + " 個";
        }
    }
}
