using System;
using System.Data;

namespace mySQLPunk.lib
{
    public static class DataViewSortService
    {
        public static string BuildSortExpression(DataTable table, string columnName, bool descending)
        {
            if (table == null || string.IsNullOrWhiteSpace(columnName) || !table.Columns.Contains(columnName)) return string.Empty;
            return "[" + columnName.Replace("]", "]]") + "] " + (descending ? "DESC" : "ASC");
        }
    }
}
