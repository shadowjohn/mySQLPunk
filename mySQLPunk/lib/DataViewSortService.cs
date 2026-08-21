using System;
using System.Data;

namespace mySQLPunk.lib
{
    public static class DataViewSortService
    {
        public static string BuildSortExpression(DataTable table, string columnName, bool descending)
        {
            if (table == null || string.IsNullOrWhiteSpace(columnName) || !table.Columns.Contains(columnName)) return string.Empty;
            // DataView 運算式的跳脫是反斜線形式（與 DataViewFilterService 一致）；]] 不是合法跳脫
            return "[" + columnName.Replace("\\", "\\\\").Replace("]", "\\]") + "] " + (descending ? "DESC" : "ASC");
        }
    }
}
