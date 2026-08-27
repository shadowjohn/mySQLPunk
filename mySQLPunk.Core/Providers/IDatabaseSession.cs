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

    string BuildSelectPreview(DatabaseObjectInfo databaseObject, int rowLimit = 200);
}
