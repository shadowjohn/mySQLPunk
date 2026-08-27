using MySqlPunk.Core.Models;
using Npgsql;

namespace MySqlPunk.Core.Providers;

internal sealed class PostgreSqlDatabaseSession : AdoDatabaseSession
{
    public PostgreSqlDatabaseSession(ConnectionProfile profile)
        : base(profile)
    {
    }

    protected override NpgsqlConnection CreateConnection(string? database)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = Profile.Host,
            Port = Profile.Port,
            Username = Profile.Username,
            Password = Profile.Password,
            Database = string.IsNullOrWhiteSpace(database) ? "postgres" : database,
            Timeout = Profile.TimeoutSeconds,
            CommandTimeout = Math.Max(1, Profile.TimeoutSeconds * 4),
            SslMode = Profile.UseSsl ? SslMode.Prefer : SslMode.Disable
        };

        return new NpgsqlConnection(builder.ConnectionString);
    }

    protected override string QuoteIdentifier(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    public override async Task<IReadOnlyList<string>> GetDatabasesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection(Profile.Database);
        return await ReadStringsAsync(
            connection,
            "SELECT datname FROM pg_database WHERE datallowconn AND NOT datistemplate ORDER BY datname",
            cancellationToken).ConfigureAwait(false);
    }

    public override async Task<IReadOnlyList<DatabaseObjectInfo>> GetObjectsAsync(
        string database,
        CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection(database);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT table_schema, table_name, table_type
            FROM information_schema.tables
            WHERE table_schema NOT IN ('pg_catalog', 'information_schema')
            ORDER BY table_schema, table_type, table_name
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var objects = new List<DatabaseObjectInfo>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var kind = string.Equals(reader.GetString(2), "VIEW", StringComparison.OrdinalIgnoreCase)
                ? DatabaseObjectKind.View
                : DatabaseObjectKind.Table;
            objects.Add(new DatabaseObjectInfo(reader.GetString(0), reader.GetString(1), kind));
        }

        return objects;
    }
}
