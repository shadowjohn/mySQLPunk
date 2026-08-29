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

    protected override void ConfigureParameter(
        System.Data.Common.DbParameter parameter,
        TableColumnInfo column)
    {
        base.ConfigureParameter(parameter, column);
        if (column.ValueKind == TableColumnValueKind.Xml && parameter is SqlParameter sqlParameter)
        {
            sqlParameter.SqlDbType = System.Data.SqlDbType.Xml;
        }
    }

    protected override string BuildOriginalValuePredicate(TableColumnInfo column, string parameterName)
    {
        if (column.ValueKind == TableColumnValueKind.Xml)
        {
            return $"CONVERT(varbinary(max), CONVERT(nvarchar(max), {QuoteIdentifier(column.Name)})) = " +
                   $"CONVERT(varbinary(max), CONVERT(nvarchar(max), {parameterName}))";
        }

        return base.BuildOriginalValuePredicate(column, parameterName);
    }

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

    protected override async Task<IReadOnlyList<TableColumnInfo>> GetTableColumnsAsync(
        string database,
        DatabaseObjectInfo table,
        CancellationToken cancellationToken)
    {
        var schema = string.IsNullOrWhiteSpace(table.Schema) ? "dbo" : table.Schema;
        await using var connection = CreateConnection(database);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.name,
                   ty.name,
                   c.is_nullable,
                   CASE WHEN EXISTS (
                       SELECT 1
                       FROM sys.indexes i
                       JOIN sys.index_columns ic
                         ON ic.object_id = i.object_id
                        AND ic.index_id = i.index_id
                       WHERE i.object_id = c.object_id
                         AND i.is_primary_key = 1
                         AND ic.column_id = c.column_id
                   ) THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS is_primary_key,
                   c.is_identity,
                   c.is_computed,
                   c.default_object_id
            FROM sys.columns c
            JOIN sys.tables t ON t.object_id = c.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            WHERE s.name = @schema
              AND t.name = @table
            ORDER BY c.column_id
            """;
        command.Parameters.AddWithValue("@schema", schema);
        command.Parameters.AddWithValue("@table", table.Name);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var columns = new List<TableColumnInfo>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var dataType = reader.GetString(1);
            var generated = reader.GetBoolean(4) || reader.GetBoolean(5) ||
                            dataType.Equals("timestamp", StringComparison.OrdinalIgnoreCase) ||
                            dataType.Equals("rowversion", StringComparison.OrdinalIgnoreCase);
            columns.Add(new TableColumnInfo(
                columns.Count,
                reader.GetString(0),
                dataType,
                reader.GetBoolean(2),
                reader.GetBoolean(3),
                generated,
                reader.GetInt32(6) != 0,
                MapValueKind(dataType)));
        }

        return columns;
    }

    protected override string BuildTableDataSql(
        DatabaseObjectInfo table,
        IReadOnlyList<TableColumnInfo> columns,
        int fetchLimit,
        int rowOffset)
    {
        var selectColumns = string.Join(", ", columns.Select(BuildTableDataSelectExpression));
        var primaryKey = columns.Where(column => column.IsPrimaryKey).OrderBy(column => column.Ordinal).ToList();
        var orderBy = primaryKey.Count == 0
            ? string.Empty
            : $" ORDER BY {string.Join(", ", primaryKey.Select(column => QuoteIdentifier(column.Name)))}";
        if (rowOffset == 0)
        {
            return $"SELECT TOP ({fetchLimit}) {selectColumns} FROM {BuildQualifiedName(table)}{orderBy};";
        }

        return $"SELECT {selectColumns} FROM {BuildQualifiedName(table)}{orderBy} " +
               $"OFFSET {rowOffset} ROWS FETCH NEXT {fetchLimit} ROWS ONLY;";
    }

    private static TableColumnValueKind MapValueKind(string dataType) => dataType.ToLowerInvariant() switch
    {
        "tinyint" or "smallint" or "int" or "bigint" => TableColumnValueKind.Integer,
        "decimal" or "numeric" or "money" or "smallmoney" => TableColumnValueKind.Decimal,
        "float" or "real" => TableColumnValueKind.FloatingPoint,
        "bit" => TableColumnValueKind.Boolean,
        "date" => TableColumnValueKind.Date,
        "datetime" or "datetime2" or "smalldatetime" => TableColumnValueKind.DateTime,
        "datetimeoffset" => TableColumnValueKind.DateTimeOffset,
        "time" => TableColumnValueKind.Time,
        "uniqueidentifier" => TableColumnValueKind.Guid,
        "xml" => TableColumnValueKind.Xml,
        "binary" or "varbinary" => TableColumnValueKind.Binary,
        "char" or "varchar" or "nchar" or "nvarchar" => TableColumnValueKind.String,
        _ => TableColumnValueKind.Unsupported
    };
}
