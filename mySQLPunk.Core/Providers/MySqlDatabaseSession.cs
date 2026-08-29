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

    protected override void ConfigureParameter(
        System.Data.Common.DbParameter parameter,
        TableColumnInfo column)
    {
        base.ConfigureParameter(parameter, column);
        if (column.ValueKind == TableColumnValueKind.Json && parameter is MySqlParameter mySqlParameter)
        {
            mySqlParameter.MySqlDbType = MySqlDbType.JSON;
        }
        else if (column.ValueKind == TableColumnValueKind.UnsignedInteger &&
                 column.DataTypeName.StartsWith("bit(", StringComparison.OrdinalIgnoreCase) &&
                 parameter is MySqlParameter bitParameter)
        {
            bitParameter.MySqlDbType = MySqlDbType.Bit;
        }
    }

    protected override string BuildOriginalValuePredicate(TableColumnInfo column, string parameterName)
    {
        if (column.ValueKind == TableColumnValueKind.Json)
        {
            return $"BINARY CAST({QuoteIdentifier(column.Name)} AS CHAR) = BINARY CAST({parameterName} AS CHAR)";
        }

        return base.BuildOriginalValuePredicate(column, parameterName);
    }

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

    protected override async Task<IReadOnlyList<TableColumnInfo>> GetTableColumnsAsync(
        string database,
        DatabaseObjectInfo table,
        CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection(database);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COLUMN_NAME, COLUMN_TYPE, DATA_TYPE, IS_NULLABLE, COLUMN_KEY, EXTRA, GENERATION_EXPRESSION, COLUMN_DEFAULT
            FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = @database
              AND TABLE_NAME = @table
            ORDER BY ORDINAL_POSITION
            """;
        command.Parameters.AddWithValue("@database", database);
        command.Parameters.AddWithValue("@table", table.Name);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var columns = new List<TableColumnInfo>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var columnType = reader.GetString(1);
            var dataType = reader.GetString(2);
            var extra = reader.IsDBNull(5) ? string.Empty : reader.GetString(5);
            var generationExpression = reader.IsDBNull(6) ? string.Empty : reader.GetString(6);
            var generated = extra.Contains("auto_increment", StringComparison.OrdinalIgnoreCase) ||
                            extra.Contains("virtual generated", StringComparison.OrdinalIgnoreCase) ||
                            extra.Contains("stored generated", StringComparison.OrdinalIgnoreCase) ||
                            !string.IsNullOrWhiteSpace(generationExpression);
            columns.Add(new TableColumnInfo(
                columns.Count,
                reader.GetString(0),
                columnType,
                string.Equals(reader.GetString(3), "YES", StringComparison.OrdinalIgnoreCase),
                string.Equals(reader.GetString(4), "PRI", StringComparison.OrdinalIgnoreCase),
                generated,
                !reader.IsDBNull(7),
                MapValueKind(dataType, columnType)));
        }

        return columns;
    }

    protected override string BuildDefaultInsertSql(DatabaseObjectInfo table) =>
        $"INSERT INTO {BuildQualifiedName(table)} () VALUES ();";

    private static TableColumnValueKind MapValueKind(string dataType, string columnType)
    {
        var normalizedDataType = dataType.ToLowerInvariant();
        var normalizedColumnType = columnType.ToLowerInvariant();
        if (normalizedDataType == "tinyint" && normalizedColumnType.StartsWith("tinyint(1)", StringComparison.Ordinal))
        {
            return TableColumnValueKind.Boolean;
        }

        if (normalizedDataType is "tinyint" or "smallint" or "mediumint" or "int" or "integer" or "bigint")
        {
            return normalizedColumnType.Contains("unsigned", StringComparison.Ordinal)
                ? TableColumnValueKind.UnsignedInteger
                : TableColumnValueKind.Integer;
        }

        return normalizedDataType switch
        {
            "year" => TableColumnValueKind.UnsignedInteger,
            "decimal" or "numeric" => TableColumnValueKind.Decimal,
            "float" or "double" or "real" => TableColumnValueKind.FloatingPoint,
            "bool" or "boolean" => TableColumnValueKind.Boolean,
            "bit" => TableColumnValueKind.UnsignedInteger,
            "date" => TableColumnValueKind.Date,
            "datetime" or "timestamp" => TableColumnValueKind.DateTime,
            "time" => TableColumnValueKind.Time,
            "json" => TableColumnValueKind.Json,
            "binary" or "varbinary" or "tinyblob" or "blob" or "mediumblob" or "longblob" =>
                TableColumnValueKind.Binary,
            "char" or "varchar" or "tinytext" or "text" or "mediumtext" or "longtext" or "enum" or "set" =>
                TableColumnValueKind.String,
            _ => TableColumnValueKind.Unsupported
        };
    }
}
