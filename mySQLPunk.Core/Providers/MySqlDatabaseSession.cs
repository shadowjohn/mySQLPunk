using MySqlConnector;
using MySqlPunk.Core.Models;

namespace MySqlPunk.Core.Providers;

internal sealed class MySqlDatabaseSession : AdoDatabaseSession
{
    public MySqlDatabaseSession(ConnectionProfile profile)
        : base(profile)
    {
    }

    protected override MySqlConnection CreateConnection(string? database)
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server = Profile.Host,
            Port = (uint)Profile.Port,
            UserID = Profile.Username,
            Password = Profile.Password,
            ConnectionTimeout = (uint)Profile.TimeoutSeconds,
            DefaultCommandTimeout = (uint)Math.Max(1, Profile.TimeoutSeconds * 4),
            SslMode = Profile.UseSsl ? MySqlSslMode.Preferred : MySqlSslMode.None,
            AllowUserVariables = false
        };

        if (!string.IsNullOrWhiteSpace(database))
        {
            builder.Database = database;
        }

        return new MySqlConnection(builder.ConnectionString);
    }

    protected override string QuoteIdentifier(string value) => $"`{value.Replace("`", "``")}`";

    public override async Task<IReadOnlyList<string>> GetDatabasesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection(null);
        return await ReadStringsAsync(
            connection,
            "SELECT SCHEMA_NAME FROM information_schema.SCHEMATA ORDER BY SCHEMA_NAME",
            cancellationToken).ConfigureAwait(false);
    }

    public override async Task<IReadOnlyList<DatabaseObjectInfo>> GetObjectsAsync(
        string database,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(database))
        {
            return Array.Empty<DatabaseObjectInfo>();
        }

        await using var connection = CreateConnection(database);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE
            FROM information_schema.TABLES
            WHERE TABLE_SCHEMA = @database
            ORDER BY TABLE_TYPE, TABLE_NAME
            """;
        command.Parameters.AddWithValue("@database", database);
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
