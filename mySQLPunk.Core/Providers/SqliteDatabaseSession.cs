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

    protected override void ConfigureParameter(
        System.Data.Common.DbParameter parameter,
        TableColumnInfo column)
    {
        base.ConfigureParameter(parameter, column);
        if (column.ValueKind == TableColumnValueKind.SqliteNumeric && parameter is SqliteParameter sqliteParameter)
        {
            sqliteParameter.SqliteType = SqliteType.Text;
        }
    }

    protected override string BuildTableDataSelectExpression(TableColumnInfo column)
    {
        var quotedName = QuoteIdentifier(column.Name);
        return column.ValueKind == TableColumnValueKind.SqliteNumeric
            ? $"CASE typeof({quotedName}) " +
              $"WHEN 'real' THEN printf('%.15g', {quotedName}) " +
              $"ELSE CAST({quotedName} AS TEXT) END AS {quotedName}"
            : base.BuildTableDataSelectExpression(column);
    }

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

    protected override async Task<IReadOnlyList<TableColumnInfo>> GetTableColumnsAsync(
        string database,
        DatabaseObjectInfo table,
        CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection(database);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var schemaCommand = connection.CreateCommand();
        schemaCommand.CommandText = "SELECT sql FROM sqlite_schema WHERE type = 'table' AND name = @table;";
        schemaCommand.Parameters.AddWithValue("@table", table.Name);
        var createSql = Convert.ToString(
            await schemaCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) ?? string.Empty;
        var withoutRowId = createSql.Contains("WITHOUT ROWID", StringComparison.OrdinalIgnoreCase);

        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_xinfo({QuoteIdentifier(table.Name)});";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var entries = new List<(
            string Name,
            string DataType,
            bool IsNullable,
            int PrimaryKeyPosition,
            bool IsHidden,
            bool HasDefault)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var dataType = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
            var primaryKeyPosition = checked((int)reader.GetInt64(5));
            entries.Add((
                reader.GetString(1),
                dataType,
                reader.GetInt64(3) == 0 && primaryKeyPosition == 0,
                primaryKeyPosition,
                reader.FieldCount > 6 && reader.GetInt64(6) != 0,
                !reader.IsDBNull(4)));
        }

        var primaryKeyCount = entries.Count(entry => entry.PrimaryKeyPosition > 0);
        return entries.Select((entry, ordinal) =>
        {
            var isPrimaryKey = entry.PrimaryKeyPosition > 0;
            var isRowIdAlias = !withoutRowId &&
                               primaryKeyCount == 1 &&
                               isPrimaryKey &&
                               string.Equals(entry.DataType.Trim(), "INTEGER", StringComparison.OrdinalIgnoreCase);
            return new TableColumnInfo(
                ordinal,
                entry.Name,
                string.IsNullOrWhiteSpace(entry.DataType) ? "BLOB" : entry.DataType,
                entry.IsNullable,
                isPrimaryKey,
                entry.IsHidden || isRowIdAlias,
                entry.HasDefault,
                MapValueKind(entry.DataType));
        }).ToList();
    }

    private static TableColumnValueKind MapValueKind(string dataType)
    {
        var normalized = dataType.Trim().ToUpperInvariant();
        if (normalized.Contains("INT", StringComparison.Ordinal))
        {
            return TableColumnValueKind.Integer;
        }

        if (normalized.Contains("CHAR", StringComparison.Ordinal) ||
            normalized.Contains("CLOB", StringComparison.Ordinal) ||
            normalized.Contains("TEXT", StringComparison.Ordinal))
        {
            return TableColumnValueKind.String;
        }

        if (normalized.Contains("BLOB", StringComparison.Ordinal) || normalized.Length == 0)
        {
            return TableColumnValueKind.Binary;
        }

        if (normalized.Contains("JSON", StringComparison.Ordinal))
        {
            return TableColumnValueKind.Json;
        }

        if (normalized.Contains("XML", StringComparison.Ordinal))
        {
            return TableColumnValueKind.Xml;
        }

        if (normalized.Contains("REAL", StringComparison.Ordinal) ||
            normalized.Contains("FLOA", StringComparison.Ordinal) ||
            normalized.Contains("DOUB", StringComparison.Ordinal))
        {
            return TableColumnValueKind.DoublePrecisionFloatingPoint;
        }

        if (normalized.Contains("BOOL", StringComparison.Ordinal))
        {
            return TableColumnValueKind.Boolean;
        }

        if (normalized.Contains("DATE", StringComparison.Ordinal) ||
            normalized.Contains("TIME", StringComparison.Ordinal))
        {
            return normalized.Contains("TIME", StringComparison.Ordinal)
                ? TableColumnValueKind.DateTime
                : TableColumnValueKind.Date;
        }

        return TableColumnValueKind.SqliteNumeric;
    }
}
