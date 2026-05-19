using System;
using System.Collections.Generic;

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

        public static string BuildSummary(DatabaseRestoreSnapshot before, DatabaseRestoreSnapshot after)
        {
            if (before == null || after == null) return string.Empty;

            List<string> lines = new List<string>
            {
                BuildLine("資料表", before.TableCount, after.TableCount),
                BuildLine("檢視", before.ViewCount, after.ViewCount),
                BuildLine("函式/程序", before.FunctionCount, after.FunctionCount),
                BuildLine("事件/Trigger", before.EventCount, after.EventCount)
            };
            return string.Join(Environment.NewLine, lines);
        }

        private static string BuildLine(string label, int before, int after)
        {
            int delta = after - before;
            string deltaText = delta == 0 ? "0" : (delta > 0 ? "+" : "") + delta.ToString();
            return label + "：" + before + " -> " + after + " (" + deltaText + ")";
        }
    }
}
