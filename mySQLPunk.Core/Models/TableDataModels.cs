namespace MySqlPunk.Core.Models;

public enum TableColumnValueKind
{
    String,
    Integer,
    UnsignedInteger,
    SqliteNumeric,
    ExactDecimal,
    PostgreSqlMoney,
    SqlServerMoney,
    SinglePrecisionFloatingPoint,
    DoublePrecisionFloatingPoint,
    Boolean,
    Date,
    PostgreSqlDate,
    DateTime,
    DateTimeOffset,
    Time,
    MySqlTemporal,
    MySqlTime,
    MySqlYear,
    PostgreSqlTemporal,
    TimeWithTimeZone,
    Interval,
    LogSequenceNumber,
    FullTextVector,
    FullTextQuery,
    PostgreSqlRange,
    PostgreSqlArray,
    PostgreSqlGeometric,
    PostgreSqlServerValidatedText,
    SqlServerTemporal,
    SqlServerHierarchyId,
    SqlServerVariant,
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
    public string StorageDataTypeName { get; init; } = DataTypeName;

    public int? MonetaryScale { get; init; }

    public long? IntegerMinimum { get; init; }

    public ulong? IntegerMaximum { get; init; }

    public int? RequiredBinaryLength { get; init; }

    public int? MaximumStringLengthInBytes { get; init; }

    public string? StorageCollationName { get; init; }

    public bool TrailingSpacesAreNotRoundTrippable { get; init; }

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

public sealed record SqliteNumericValue(string Text);

public sealed record FloatingPointValue(object Value, string Text);

public sealed record PostgreSqlMoneyValue(string Text);

public sealed record SqlServerMoneyValue(decimal Value, string Text);

public sealed record ExactDecimalDefinition(int? Precision, int? Scale, bool IsUnsigned);

public sealed record SqlServerVariantValue(
    string BaseTypeName,
    object Value,
    string CanonicalText,
    int? Size = null,
    byte? Precision = null,
    byte? Scale = null,
    int? LocaleId = null,
    int? ComparisonStyle = null,
    string? CollationName = null);

public sealed class TableDataConflictException : Exception
{
    public TableDataConflictException(string message)
        : base(message)
    {
    }
}
