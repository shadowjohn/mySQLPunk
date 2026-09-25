namespace MySqlPunk.Core.Models;

public enum DataRowChangeKind
{
    Insert,
    Update,
    Delete
}

/// <summary>
/// One row operation that would make the target match the source. Values are editor-format cell inputs so they
/// go through the same validated parse／parameter path as the table editor. Update and delete carry the full
/// target row read during comparison; applying uses it as an optimistic-concurrency predicate.
/// </summary>
public sealed record DataRowChange(
    DataRowChangeKind Kind,
    string KeyText,
    IReadOnlyList<TableCellInput> Values,
    TableDataRow? TargetOriginal,
    IReadOnlyList<string> ChangedColumns);

public sealed record DataTableComparison(
    DatabaseObjectInfo SourceTable,
    DatabaseObjectInfo TargetTable,
    IReadOnlyList<string> KeyColumns,
    IReadOnlyList<string> SyncColumns,
    IReadOnlyList<DataRowChange> Changes,
    int IdenticalRows,
    int SourceRows,
    int TargetRows,
    IReadOnlyList<string> Warnings,
    string? SkippedReason)
{
    public int Inserts => Changes.Count(change => change.Kind == DataRowChangeKind.Insert);

    public int Updates => Changes.Count(change => change.Kind == DataRowChangeKind.Update);

    public int Deletes => Changes.Count(change => change.Kind == DataRowChangeKind.Delete);

    public bool IsSkipped => SkippedReason is not null;

    public string StatusText => SkippedReason is not null
        ? $"略過：{SkippedReason}"
        : Changes.Count == 0
            ? $"一致（{IdenticalRows:N0} 列）"
            : $"只在來源 {Inserts:N0}、只在目標 {Deletes:N0}、內容不同 {Updates:N0}、一致 {IdenticalRows:N0}";
}

public sealed record DataSyncTableRequest(DatabaseObjectInfo Table, IReadOnlyList<DataRowChange> Changes);

public sealed record DataSyncResult(
    bool Succeeded,
    int Inserted,
    int Updated,
    int Deleted,
    string? FailedTable,
    string? FailedKey,
    string Message)
{
    public string Summary => Succeeded
        ? $"已在單一交易中新增 {Inserted:N0}、修改 {Updated:N0}、刪除 {Deleted:N0} 列。"
        : $"{FailedTable} 主鍵 {FailedKey} 套用失敗：{Message}；交易已回滾，目標資料沒有任何變更。";
}
