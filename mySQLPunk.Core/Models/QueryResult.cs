namespace MySqlPunk.Core.Models;

public sealed class QueryResult
{
    public IReadOnlyList<string> Columns { get; init; } = Array.Empty<string>();

    public IReadOnlyList<IReadOnlyList<object?>> Rows { get; init; } = Array.Empty<IReadOnlyList<object?>>();

    public int? RowsAffected { get; init; }

    public TimeSpan Elapsed { get; init; }

    public bool WasTruncated { get; init; }

    public bool HasResultSet => Columns.Count > 0;

    public string Summary => HasResultSet
        ? WasTruncated
            ? $"完成，顯示前 {Rows.Count:N0} 列（已截斷，{Elapsed.TotalMilliseconds:N0} ms）"
            : $"完成，共 {Rows.Count:N0} 列（{Elapsed.TotalMilliseconds:N0} ms）"
        : RowsAffected is >= 0
            ? $"完成，影響 {RowsAffected:N0} 列（{Elapsed.TotalMilliseconds:N0} ms）"
            : $"命令執行完成（{Elapsed.TotalMilliseconds:N0} ms）";
}
