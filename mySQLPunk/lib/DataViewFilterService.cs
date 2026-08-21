using System;
using System.Data;
using System.Linq;

namespace mySQLPunk.lib
{
    public static class DataViewFilterService
    {
        public static string BuildContainsFilter(DataTable table, string keyword)
        {
            if (table == null || string.IsNullOrWhiteSpace(keyword)) return string.Empty;

            string escapedKeyword = EscapeLikeValue(keyword.Trim());
            if (escapedKeyword.Length == 0) return string.Empty;

            string[] filters = table.Columns
                .Cast<DataColumn>()
                .Where(column => !column.ColumnName.StartsWith("_", StringComparison.Ordinal)) // _ 開頭是隱藏的內部欄位
                .Select(column => "Convert(" + QuoteColumn(column.ColumnName) + ", 'System.String') LIKE '%" + escapedKeyword + "%'")
                .ToArray();

            return string.Join(" OR ", filters);
        }

        private static string QuoteColumn(string columnName)
        {
            return "[" + (columnName ?? string.Empty).Replace("\\", "\\\\").Replace("]", "\\]") + "]";
        }

        private static string EscapeLikeValue(string value)
        {
            return (value ?? string.Empty)
                .Replace("'", "''")
                .Replace("[", "[[]")
                .Replace("%", "[%]")
                .Replace("*", "[*]");
        }
    }
}
