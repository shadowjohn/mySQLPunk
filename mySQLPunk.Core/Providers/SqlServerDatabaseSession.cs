using Microsoft.Data.SqlClient;
using MySqlPunk.Core.Models;

namespace MySqlPunk.Core.Providers;

internal sealed class SqlServerDatabaseSession : AdoDatabaseSession
{
    public SqlServerDatabaseSession(ConnectionProfile profile)
        : base(profile)
    {
    }

    protected override SqlConnection CreateConnection(string? database)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = $"{Profile.Host},{Profile.Port}",
            InitialCatalog = string.IsNullOrWhiteSpace(database) ? "master" : database,
            UserID = Profile.Username,
            Password = Profile.Password,
            IntegratedSecurity = false,
            ConnectTimeout = Profile.TimeoutSeconds,
            CommandTimeout = Math.Max(1, Profile.TimeoutSeconds * 4),
            Encrypt = Profile.UseSsl,
            TrustServerCertificate = false,
            ApplicationName = "mySQLPunk"
        };

        return new SqlConnection(builder.ConnectionString);
    }

    protected override string QuoteIdentifier(string value) => $"[{value.Replace("]", "]]", StringComparison.Ordinal)}]";

    public override async Task<IReadOnlyList<string>> GetDatabasesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection("master");
        return await ReadStringsAsync(
            connection,
            "SELECT name FROM sys.databases WHERE state = 0 AND HAS_DBACCESS(name) = 1 ORDER BY name",
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
            SELECT schema_name(schema_id), name, type
            FROM sys.objects
            WHERE type IN ('U', 'V')
              AND is_ms_shipped = 0
            ORDER BY schema_name(schema_id), type, name
            """;
        command.CommandTimeout = Math.Max(1, Profile.TimeoutSeconds * 2);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var objects = new List<DatabaseObjectInfo>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var kind = string.Equals(reader.GetString(2), "V", StringComparison.OrdinalIgnoreCase)
                ? DatabaseObjectKind.View
                : DatabaseObjectKind.Table;
            objects.Add(new DatabaseObjectInfo(reader.GetString(0), reader.GetString(1), kind));
        }

        return objects;
    }

    public override string BuildSelectPreview(DatabaseObjectInfo databaseObject, int rowLimit = 200)
    {
        rowLimit = Math.Clamp(rowLimit, 1, 10_000);
        return $"SELECT TOP ({rowLimit}) * FROM {BuildQualifiedName(databaseObject)};";
    }
}
