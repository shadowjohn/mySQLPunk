using MySqlPunk.Core.Models;

namespace MySqlPunk.Core.Providers;

public interface IDatabaseSession : IDisposable
{
    ConnectionProfile Profile { get; }

    Task TestConnectionAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetDatabasesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DatabaseObjectInfo>> GetObjectsAsync(
        string database,
        CancellationToken cancellationToken = default);

    Task<QueryResult> ExecuteAsync(
        string database,
        string sql,
        CancellationToken cancellationToken = default);

    /// <summary>Read-only catalog structure (columns, indexes, foreign keys, definition) of a table or view.</summary>
    Task<TableStructureInfo> GetTableStructureAsync(
        string database,
        DatabaseObjectInfo table,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs statements in order and stops at the first failure. Providers with transactional DDL (PostgreSQL,
    /// SQL Server, SQLite) wrap the batch in one transaction and roll everything back on failure; MySQL／MariaDB
    /// commit DDL implicitly, so earlier statements stay applied and the result says so.
    /// </summary>
    Task<StatementBatchResult> ExecuteBatchAsync(
        string database,
        IReadOnlyList<string> statements,
        CancellationToken cancellationToken = default);

    /// <summary>Produces the provider's execution plan for a single statement without executing it.</summary>
    Task<QueryPlanDocument> ExplainAsync(
        string database,
        string sql,
        CancellationToken cancellationToken = default);

    Task<TableDataSnapshot> LoadTableDataAsync(
        string database,
        DatabaseObjectInfo table,
        int rowLimit = 200,
        int rowOffset = 0,
        CancellationToken cancellationToken = default,
        TableDataSort? sort = null,
        TableDataFilter? filter = null);

    Task InsertTableRowAsync(
        string database,
        DatabaseObjectInfo table,
        IReadOnlyList<TableCellInput> values,
        CancellationToken cancellationToken = default);

    Task UpdateTableRowAsync(
        string database,
        DatabaseObjectInfo table,
        TableDataRow originalRow,
        IReadOnlyList<TableCellInput> changes,
        CancellationToken cancellationToken = default);

    Task DeleteTableRowAsync(
        string database,
        DatabaseObjectInfo table,
        TableDataRow originalRow,
        CancellationToken cancellationToken = default);

    string BuildSelectPreview(DatabaseObjectInfo databaseObject, int rowLimit = 200);
}
