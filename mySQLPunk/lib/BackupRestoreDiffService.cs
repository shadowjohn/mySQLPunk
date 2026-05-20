using System;
using System.Collections.Generic;
using System.Linq;

namespace mySQLPunk.lib
{
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

        public DatabaseRestoreSnapshot()
        {
            Tables = new List<string>();
            Views = new List<string>();
            Functions = new List<string>();
            Events = new List<string>();
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

        private static string FormatNameList(List<string> names)
        {
            const int maxNames = 5;
            if (names.Count <= maxNames) return string.Join(", ", names);
            return string.Join(", ", names.Take(maxNames)) + " ... 等 " + names.Count + " 個";
        }
    }
}
