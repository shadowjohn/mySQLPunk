using System.Net.NetworkInformation;
using MySqlPunk.Core.Models;
using Npgsql;
using NpgsqlTypes;

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

    protected override void ConfigureParameter(
        System.Data.Common.DbParameter parameter,
        TableColumnInfo column)
    {
        base.ConfigureParameter(parameter, column);
        if (column.ValueKind == TableColumnValueKind.Json && parameter is NpgsqlParameter npgsqlParameter)
        {
            npgsqlParameter.NpgsqlDbType = string.Equals(
                column.DataTypeName,
                "jsonb",
                StringComparison.OrdinalIgnoreCase)
                ? NpgsqlDbType.Jsonb
                : NpgsqlDbType.Json;
        }
        else if (column.ValueKind == TableColumnValueKind.Xml && parameter is NpgsqlParameter xmlParameter)
        {
            xmlParameter.NpgsqlDbType = NpgsqlDbType.Xml;
        }
        else if (column.ValueKind == TableColumnValueKind.NetworkAddress && parameter is NpgsqlParameter networkParameter)
        {
            networkParameter.NpgsqlDbType = column.DataTypeName.ToLowerInvariant() switch
            {
                "inet" => NpgsqlDbType.Inet,
                "cidr" => NpgsqlDbType.Cidr,
                "macaddr" => NpgsqlDbType.MacAddr,
                "macaddr8" => NpgsqlDbType.MacAddr8,
                _ => throw new InvalidOperationException($"不支援的 PostgreSQL 網路位址型別：{column.DataTypeName}")
            };
        }
    }

    protected override object? PrepareParameterValue(TableColumnInfo column, object? value)
    {
        if (column.ValueKind != TableColumnValueKind.NetworkAddress || value is not string text)
        {
            return base.PrepareParameterValue(column, value);
        }

        return column.DataTypeName.ToLowerInvariant() switch
        {
            "inet" => new NpgsqlInet(text),
            "cidr" => new NpgsqlCidr(text),
            "macaddr" or "macaddr8" => new PhysicalAddress(
                Convert.FromHexString(text.Replace(":", string.Empty, StringComparison.Ordinal))),
            _ => throw new InvalidOperationException($"不支援的 PostgreSQL 網路位址型別：{column.DataTypeName}")
        };
    }

    protected override string BuildOriginalValuePredicate(TableColumnInfo column, string parameterName)
    {
        if (column.ValueKind == TableColumnValueKind.Json &&
            string.Equals(column.DataTypeName, "json", StringComparison.OrdinalIgnoreCase))
        {
            return $"{QuoteIdentifier(column.Name)}::jsonb = CAST({parameterName} AS jsonb)";
        }

        if (column.ValueKind == TableColumnValueKind.Xml)
        {
            return $"CAST({QuoteIdentifier(column.Name)} AS text) = CAST({parameterName} AS text)";
        }

        return base.BuildOriginalValuePredicate(column, parameterName);
    }

    protected override string BuildTableDataSelectExpression(TableColumnInfo column)
    {
        var quotedName = QuoteIdentifier(column.Name);
        return column.ValueKind == TableColumnValueKind.NetworkAddress
            ? $"CAST({quotedName} AS text) AS {quotedName}"
            : base.BuildTableDataSelectExpression(column);
    }

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

    protected override async Task<IReadOnlyList<TableColumnInfo>> GetTableColumnsAsync(
        string database,
        DatabaseObjectInfo table,
        CancellationToken cancellationToken)
    {
        var schema = string.IsNullOrWhiteSpace(table.Schema) ? "public" : table.Schema;
        await using var connection = CreateConnection(database);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.column_name,
                   CASE WHEN c.data_type = 'USER-DEFINED' THEN c.udt_name ELSE c.data_type END AS display_type,
                   c.data_type,
                   c.udt_name,
                   c.is_nullable,
                   EXISTS (
                       SELECT 1
                       FROM information_schema.table_constraints tc
                       JOIN information_schema.key_column_usage kcu
                         ON kcu.constraint_catalog = tc.constraint_catalog
                        AND kcu.constraint_schema = tc.constraint_schema
                        AND kcu.constraint_name = tc.constraint_name
                       WHERE tc.constraint_type = 'PRIMARY KEY'
                         AND tc.table_schema = c.table_schema
                         AND tc.table_name = c.table_name
                         AND kcu.column_name = c.column_name
                   ) AS is_primary_key,
                   c.is_identity,
                   c.is_generated,
                   c.column_default
            FROM information_schema.columns c
            WHERE c.table_schema = @schema
              AND c.table_name = @table
            ORDER BY c.ordinal_position
            """;
        command.Parameters.AddWithValue("@schema", schema);
        command.Parameters.AddWithValue("@table", table.Name);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var columns = new List<TableColumnInfo>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var dataType = reader.GetString(2);
            var userDefinedType = reader.GetString(3);
            var generated = string.Equals(reader.GetString(6), "YES", StringComparison.OrdinalIgnoreCase) ||
                            !string.Equals(reader.GetString(7), "NEVER", StringComparison.OrdinalIgnoreCase);
            columns.Add(new TableColumnInfo(
                columns.Count,
                reader.GetString(0),
                reader.GetString(1),
                string.Equals(reader.GetString(4), "YES", StringComparison.OrdinalIgnoreCase),
                reader.GetBoolean(5),
                generated,
                !reader.IsDBNull(8),
                MapValueKind(dataType, userDefinedType)));
        }

        return columns;
    }

    private static TableColumnValueKind MapValueKind(string dataType, string userDefinedType) =>
        dataType.ToLowerInvariant() switch
        {
            "smallint" or "integer" or "bigint" => TableColumnValueKind.Integer,
            "numeric" or "decimal" or "money" => TableColumnValueKind.Decimal,
            "real" or "double precision" => TableColumnValueKind.FloatingPoint,
            "boolean" => TableColumnValueKind.Boolean,
            "date" => TableColumnValueKind.Date,
            "timestamp without time zone" => TableColumnValueKind.DateTime,
            "timestamp with time zone" => TableColumnValueKind.DateTimeOffset,
            "time without time zone" => TableColumnValueKind.Time,
            "uuid" => TableColumnValueKind.Guid,
            "json" or "jsonb" => TableColumnValueKind.Json,
            "xml" => TableColumnValueKind.Xml,
            "inet" or "cidr" or "macaddr" or "macaddr8" => TableColumnValueKind.NetworkAddress,
            "bytea" => TableColumnValueKind.Binary,
            "character" or "character varying" or "text" => TableColumnValueKind.String,
            "user-defined" when string.Equals(userDefinedType, "citext", StringComparison.OrdinalIgnoreCase) =>
                TableColumnValueKind.String,
            _ => TableColumnValueKind.Unsupported
        };
}
