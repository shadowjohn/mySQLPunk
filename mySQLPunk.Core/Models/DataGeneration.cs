namespace MySqlPunk.Core.Models;

public enum DataGeneratorRuleKind
{
    /// <summary>Inferred from type, name, constraints and foreign keys.</summary>
    Auto,

    /// <summary>Column is left out of the INSERT so the database default／identity applies.</summary>
    DatabaseDefault,
    Null,
    Fixed,

    /// <summary>Text "start[,step]"; numbers or yyyy-MM-dd dates (step in days).</summary>
    Sequence,

    /// <summary>Text "min..max"; numbers or dates, picked at random.</summary>
    Range,

    /// <summary>Values separated by "|", picked at random.</summary>
    List,

    /// <summary>Template with {n}, {int:a-b}, {digits:k}, {letters:k}, {uuid}.</summary>
    Pattern,

    /// <summary>Text is a dictionary name; values are picked by weight from <see cref="DataGeneratorRule.Dictionary"/>.</summary>
    Dictionary
}

public sealed record DataGeneratorRule(DataGeneratorRuleKind Kind, string Text = "", int NullPercent = 0)
{
    public static DataGeneratorRule Auto { get; } = new(DataGeneratorRuleKind.Auto);

    /// <summary>Dictionary 規則在產生前解析好的字典；null 代表找不到。</summary>
    public Services.DataGeneratorDictionary? Dictionary { get; init; }
}

public sealed record DataGeneratorTablePlan(
    DatabaseObjectInfo Table,
    int RowCount,
    IReadOnlyDictionary<string, DataGeneratorRule> Rules);

/// <summary>What the generator will do with one column when its rule is Auto; shown before generating.</summary>
public sealed record DataGeneratorColumnInfo(
    TableColumnInfo Column,
    string DataType,
    string AutoDescription,
    bool IsForeignKey,
    bool IsUnique,
    bool CanOmit);

public sealed record DataGeneratorTableInfo(
    DatabaseObjectInfo Table,
    IReadOnlyList<DataGeneratorColumnInfo> Columns,
    IReadOnlyList<string> Notes);

public sealed record DataGeneratedTable(
    DatabaseObjectInfo Table,
    IReadOnlyList<string> Columns,
    IReadOnlyList<DataRowChange> Rows);

public sealed record DataGenerationResult(
    IReadOnlyList<DataGeneratedTable> Tables,
    IReadOnlyList<string> Warnings,
    string? Error)
{
    public bool Succeeded => Error is null;

    public int TotalRows => Tables.Sum(table => table.Rows.Count);
}
