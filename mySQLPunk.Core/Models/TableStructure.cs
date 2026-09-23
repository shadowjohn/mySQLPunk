namespace MySqlPunk.Core.Models;

/// <summary>Column as documented in the data dictionary; provider-native type text, nothing inferred.</summary>
public sealed record StructureColumnInfo(
    int Ordinal,
    string Name,
    string DataType,
    bool IsNullable,
    bool IsPrimaryKey,
    string DefaultValue,
    string Extra,
    string Collation,
    string Comment);

public sealed record StructureIndexInfo(
    string Name,
    bool IsUnique,
    bool IsPrimaryKey,
    string IndexType,
    IReadOnlyList<string> Columns,
    string Definition);

public sealed record StructureForeignKeyInfo(
    string Name,
    IReadOnlyList<string> Columns,
    string ReferencedTable,
    IReadOnlyList<string> ReferencedColumns,
    string OnUpdate,
    string OnDelete);

/// <summary>Read-only structure of one table or view, gathered through provider catalog queries only.</summary>
public sealed record TableStructureInfo(
    DatabaseObjectInfo Object,
    IReadOnlyList<StructureColumnInfo> Columns,
    IReadOnlyList<StructureIndexInfo> Indexes,
    IReadOnlyList<StructureForeignKeyInfo> ForeignKeys,
    string Comment,
    string Definition);
