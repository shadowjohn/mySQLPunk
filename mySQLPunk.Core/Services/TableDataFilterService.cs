using MySqlPunk.Core.Models;

namespace MySqlPunk.Core.Services;

public static class TableDataFilterService
{
    public static bool IsFilterable(DatabaseProviderKind provider, TableColumnInfo column)
    {
        ArgumentNullException.ThrowIfNull(column);
        return TableDataSortService.IsScalarComparable(provider, column);
    }

    public static TableColumnInfo Resolve(
        DatabaseProviderKind provider,
        IReadOnlyList<TableColumnInfo> columns,
        TableDataFilter filter)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(filter);
        if (string.IsNullOrWhiteSpace(filter.ColumnName))
        {
            throw new ArgumentException("篩選欄位不可空白。", nameof(filter));
        }

        if (!Enum.IsDefined(filter.Operator))
        {
            throw new ArgumentOutOfRangeException(nameof(filter), "不支援的 Table 篩選操作。");
        }

        var column = columns.SingleOrDefault(candidate =>
            string.Equals(candidate.Name, filter.ColumnName, StringComparison.Ordinal));
        if (column is null)
        {
            throw new ArgumentException($"找不到篩選欄位：{filter.ColumnName}", nameof(filter));
        }

        if (!IsFilterable(provider, column))
        {
            throw new InvalidOperationException(
                $"欄位 {column.Name}（{column.DataTypeName}）不支援安全篩選，請改用 scalar 欄位。");
        }

        return column;
    }
}
