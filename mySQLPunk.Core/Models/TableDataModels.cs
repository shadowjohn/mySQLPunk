namespace MySqlPunk.Core.Models;

public enum TableColumnValueKind
{
    String,
    Integer,
    UnsignedInteger,
    Decimal,
    ExactDecimal,
    FloatingPoint,
    Boolean,
    Date,
    DateTime,
    DateTimeOffset,
    Time,
    MySqlTime,
    MySqlYear,
    TimeWithTimeZone,
    Interval,
    LogSequenceNumber,
    FullTextVector,
    FullTextQuery,
    PostgreSqlRange,
    PostgreSqlArray,
    PostgreSqlGeometric,
    PostgreSqlServerValidatedText,
    SqlServerHierarchyId,
    Spatial,
    Guid,
    Json,
    Xml,
    NetworkAddress,
    BitString,
    Binary,
    Unsupported
}

public sealed record TableColumnInfo(
    int Ordinal,
    string Name,
    string DataTypeName,
    bool IsNullable,
    bool IsPrimaryKey,
    bool IsGenerated,
    bool HasDefault,
    TableColumnValueKind ValueKind)
{
    public bool IsEditable => !IsGenerated && ValueKind is not TableColumnValueKind.Unsupported;

    public string DisplayName => IsPrimaryKey ? $"{Name} · PK" : Name;
}

public sealed record TableDataRow(IReadOnlyList<object?> Values);

public sealed record TableDataSnapshot(
    DatabaseObjectInfo Table,
    IReadOnlyList<TableColumnInfo> Columns,
    IReadOnlyList<TableDataRow> Rows,
    bool WasTruncated,
    int RowOffset = 0)
{
    public bool HasPrimaryKey => Columns.Any(column => column.IsPrimaryKey);

    public bool HasPreviousPage => HasPrimaryKey && RowOffset > 0;

    public bool HasNextPage => HasPrimaryKey && WasTruncated;
}

public enum TableCellInputMode
{
    Value,
    Null,
    Default
}

public sealed record TableCellInput(
    string ColumnName,
    TableCellInputMode Mode,
    string Text);

public sealed record IntervalComponents(int Months, int Days, long Microseconds);

public sealed record ExactDecimalValue(string Text);

public sealed record ExactDecimalDefinition(int? Precision, int? Scale, bool IsUnsigned);

public sealed class TableDataConflictException : Exception
{
    public TableDataConflictException(string message)
        : base(message)
    {
    }
}
