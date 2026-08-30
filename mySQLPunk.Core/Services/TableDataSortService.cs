using MySqlPunk.Core.Models;

namespace MySqlPunk.Core.Services;

public static class TableDataSortService
{
    public static bool IsSortable(DatabaseProviderKind provider, TableColumnInfo column)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (column.IsPrimaryKey)
        {
            return true;
        }

        if (provider == DatabaseProviderKind.SqlServer &&
            column.ValueKind == TableColumnValueKind.String &&
            GetStorageBaseType(column) is "text" or "ntext")
        {
            return false;
        }

        return column.ValueKind is
            TableColumnValueKind.String or
            TableColumnValueKind.Integer or
            TableColumnValueKind.UnsignedInteger or
            TableColumnValueKind.SqliteNumeric or
            TableColumnValueKind.SqliteTemporal or
            TableColumnValueKind.SqliteGuid or
            TableColumnValueKind.ExactDecimal or
            TableColumnValueKind.PostgreSqlMoney or
            TableColumnValueKind.SqlServerMoney or
            TableColumnValueKind.SinglePrecisionFloatingPoint or
            TableColumnValueKind.DoublePrecisionFloatingPoint or
            TableColumnValueKind.Boolean or
            TableColumnValueKind.Date or
            TableColumnValueKind.PostgreSqlDate or
            TableColumnValueKind.DateTime or
            TableColumnValueKind.DateTimeOffset or
            TableColumnValueKind.Time or
            TableColumnValueKind.MySqlTemporal or
            TableColumnValueKind.MySqlTime or
            TableColumnValueKind.MySqlYear or
            TableColumnValueKind.PostgreSqlTemporal or
            TableColumnValueKind.TimeWithTimeZone or
            TableColumnValueKind.Interval or
            TableColumnValueKind.LogSequenceNumber or
            TableColumnValueKind.Guid or
            TableColumnValueKind.NetworkAddress or
            TableColumnValueKind.BitString or
            TableColumnValueKind.SqlServerTemporal;
    }

    public static TableColumnInfo Resolve(
        DatabaseProviderKind provider,
        IReadOnlyList<TableColumnInfo> columns,
        TableDataSort sort)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(sort);
        if (string.IsNullOrWhiteSpace(sort.ColumnName))
        {
            throw new ArgumentException("排序欄位不可空白。", nameof(sort));
        }

        var column = columns.SingleOrDefault(candidate =>
            string.Equals(candidate.Name, sort.ColumnName, StringComparison.Ordinal));
        if (column is null)
        {
            throw new ArgumentException($"找不到排序欄位：{sort.ColumnName}", nameof(sort));
        }

        if (!IsSortable(provider, column))
        {
            throw new InvalidOperationException(
                $"欄位 {column.Name}（{column.DataTypeName}）不支援安全排序，請改用 scalar 或 Primary Key 欄位。");
        }

        return column;
    }

    private static string GetStorageBaseType(TableColumnInfo column)
    {
        var storageType = column.StorageDataTypeName.Trim();
        var definitionStart = storageType.IndexOf('(');
        return (definitionStart < 0 ? storageType : storageType[..definitionStart]).ToLowerInvariant();
    }
}
