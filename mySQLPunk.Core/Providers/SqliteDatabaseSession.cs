using Microsoft.Data.Sqlite;
using MySqlPunk.Core.Models;

namespace MySqlPunk.Core.Providers;

internal sealed class SqliteDatabaseSession : AdoDatabaseSession
{
    public SqliteDatabaseSession(ConnectionProfile profile)
        : base(profile)
    {
    }

    protected override SqliteConnection CreateConnection(string? database)
    {
        var dataSource = string.IsNullOrWhiteSpace(database) ? Profile.Database : database;
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dataSource,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = Profile.TimeoutSeconds
        };
        return new SqliteConnection(builder.ConnectionString);
    }

    protected override string QuoteIdentifier(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    public override Task<IReadOnlyList<string>> GetDatabasesAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> databases = new[] { Path.GetFullPath(Profile.Database) };
        return Task.FromResult(databases);
    }

    public override async Task<IReadOnlyList<DatabaseObjectInfo>> GetObjectsAsync(
        string database,
        CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection(database);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name, type
            FROM sqlite_schema
            WHERE type IN ('table', 'view')
              AND name NOT LIKE 'sqlite_%'
            ORDER BY type, name
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var objects = new List<DatabaseObjectInfo>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var kind = string.Equals(reader.GetString(1), "view", StringComparison.OrdinalIgnoreCase)
                ? DatabaseObjectKind.View
                : DatabaseObjectKind.Table;
            objects.Add(new DatabaseObjectInfo(string.Empty, reader.GetString(0), kind));
        }

        return objects;
    }
}
