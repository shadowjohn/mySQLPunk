using MySqlPunk.Core.Models;

namespace MySqlPunk.Core.Providers;

public interface IDatabaseSession
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

    Task<TableDataSnapshot> LoadTableDataAsync(
        string database,
        DatabaseObjectInfo table,
        int rowLimit = 200,
        int rowOffset = 0,
        CancellationToken cancellationToken = default);

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
