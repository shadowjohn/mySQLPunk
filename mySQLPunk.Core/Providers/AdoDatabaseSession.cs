using System.Data.Common;
using System.Diagnostics;
using MySqlPunk.Core.Models;

namespace MySqlPunk.Core.Providers;

internal abstract class AdoDatabaseSession : IDatabaseSession
{
    private const int MaximumResultRows = 10_000;

    protected AdoDatabaseSession(ConnectionProfile profile)
    {
        Profile = profile;
    }

    public ConnectionProfile Profile { get; }

    protected abstract DbConnection CreateConnection(string? database);

    protected abstract string QuoteIdentifier(string value);

    public async Task TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection(Profile.Database);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
    }

    public abstract Task<IReadOnlyList<string>> GetDatabasesAsync(
        CancellationToken cancellationToken = default);

    public abstract Task<IReadOnlyList<DatabaseObjectInfo>> GetObjectsAsync(
        string database,
        CancellationToken cancellationToken = default);

    public async Task<QueryResult> ExecuteAsync(
        string database,
        string sql,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new InvalidOperationException("請先輸入要執行的 SQL。");
        }

        var stopwatch = Stopwatch.StartNew();
        await using var connection = CreateConnection(database);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = Math.Max(1, Profile.TimeoutSeconds * 4);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var columns = new List<string>();
        for (var index = 0; index < reader.FieldCount; index++)
        {
            columns.Add(reader.GetName(index));
        }

        var rows = new List<IReadOnlyList<object?>>();
        while (rows.Count < MaximumResultRows &&
               await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var values = new object?[reader.FieldCount];
            for (var index = 0; index < reader.FieldCount; index++)
            {
                values[index] = FormatValue(reader.GetValue(index));
            }

            rows.Add(values);
        }

        var wasTruncated = reader.FieldCount > 0 &&
                           rows.Count == MaximumResultRows &&
                           await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

        stopwatch.Stop();
        return new QueryResult
        {
            Columns = columns,
            Rows = rows,
            RowsAffected = reader.RecordsAffected,
            WasTruncated = wasTruncated,
            Elapsed = stopwatch.Elapsed
        };
    }

    public string BuildSelectPreview(DatabaseObjectInfo databaseObject, int rowLimit = 200)
    {
        ArgumentNullException.ThrowIfNull(databaseObject);
        rowLimit = Math.Clamp(rowLimit, 1, 10_000);

        var qualifiedName = string.IsNullOrWhiteSpace(databaseObject.Schema)
            ? QuoteIdentifier(databaseObject.Name)
            : $"{QuoteIdentifier(databaseObject.Schema)}.{QuoteIdentifier(databaseObject.Name)}";
        return $"SELECT * FROM {qualifiedName} LIMIT {rowLimit};";
    }

    protected async Task<List<string>> ReadStringsAsync(
        DbConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = Math.Max(1, Profile.TimeoutSeconds * 2);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var values = new List<string>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    protected static object? FormatValue(object value)
    {
        return value switch
        {
            DBNull => null,
            byte[] bytes => "0x" + Convert.ToHexString(bytes),
            _ => value
        };
    }
}
