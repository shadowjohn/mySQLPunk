using System.Data;
using System.Data.Common;
using System.Diagnostics;
using MySqlPunk.Core.Models;
using MySqlPunk.Core.Services;

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

    protected abstract Task<IReadOnlyList<TableColumnInfo>> GetTableColumnsAsync(
        string database,
        DatabaseObjectInfo table,
        CancellationToken cancellationToken);

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

    public virtual string BuildSelectPreview(DatabaseObjectInfo databaseObject, int rowLimit = 200)
    {
        ArgumentNullException.ThrowIfNull(databaseObject);
        rowLimit = Math.Clamp(rowLimit, 1, 10_000);

        return $"SELECT * FROM {BuildQualifiedName(databaseObject)} LIMIT {rowLimit};";
    }

    public async Task<TableDataSnapshot> LoadTableDataAsync(
        string database,
        DatabaseObjectInfo table,
        int rowLimit = 200,
        int rowOffset = 0,
        CancellationToken cancellationToken = default)
    {
        ValidateTable(table);
        rowLimit = Math.Clamp(rowLimit, 1, 1_000);
        if (rowOffset is < 0 or > 10_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(rowOffset), "資料列起點必須介於 0 與 10,000,000。");
        }
        var columns = await GetRequiredTableColumnsAsync(database, table, cancellationToken).ConfigureAwait(false);
        if (rowOffset > 0 && columns.All(column => !column.IsPrimaryKey))
        {
            throw new InvalidOperationException("沒有 Primary Key 的資料表無法安全提供穩定分頁。");
        }

        await using var connection = CreateConnection(database);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = BuildTableDataSql(table, columns, rowLimit + 1, rowOffset);
        command.CommandTimeout = Math.Max(1, Profile.TimeoutSeconds * 2);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<TableDataRow>();
        while (rows.Count < rowLimit && await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var values = new object?[columns.Count];
            for (var index = 0; index < values.Length; index++)
            {
                var value = reader.GetValue(index);
                values[index] = value is DBNull ? null : value;
            }

            rows.Add(new TableDataRow(values));
        }

        var wasTruncated = rows.Count == rowLimit && await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new TableDataSnapshot(table, columns, rows, wasTruncated, rowOffset);
    }

    public async Task InsertTableRowAsync(
        string database,
        DatabaseObjectInfo table,
        IReadOnlyList<TableCellInput> values,
        CancellationToken cancellationToken = default)
    {
        ValidateTable(table);
        ArgumentNullException.ThrowIfNull(values);
        var columns = await GetRequiredTableColumnsAsync(database, table, cancellationToken).ConfigureAwait(false);
        var inputMap = BuildInputMap(values);

        await using var connection = CreateConnection(database);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = Math.Max(1, Profile.TimeoutSeconds * 2);

        var insertColumns = new List<TableColumnInfo>();
        foreach (var input in inputMap.Values)
        {
            var column = FindColumn(columns, input.ColumnName);
            if (input.Mode == TableCellInputMode.Default)
            {
                continue;
            }

            if (!column.IsEditable)
            {
                throw new InvalidOperationException($"「{column.Name}」是資料庫產生或不支援編輯的欄位。");
            }

            insertColumns.Add(column);
        }

        if (insertColumns.Count == 0)
        {
            command.CommandText = BuildDefaultInsertSql(table);
        }
        else
        {
            var parameterNames = new List<string>(insertColumns.Count);
            for (var index = 0; index < insertColumns.Count; index++)
            {
                var column = insertColumns[index];
                var input = inputMap[column.Name];
                var parameterName = $"@value{index}";
                AddParameter(command, parameterName, TableCellValueConverter.Parse(column, input), column);
                parameterNames.Add(parameterName);
            }

            command.CommandText =
                $"INSERT INTO {BuildQualifiedName(table)} ({string.Join(", ", insertColumns.Select(column => QuoteIdentifier(column.Name)))}) " +
                $"VALUES ({string.Join(", ", parameterNames)});";
        }

        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await CompleteSingleRowMutationAsync(transaction, affected, "新增", cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateTableRowAsync(
        string database,
        DatabaseObjectInfo table,
        TableDataRow originalRow,
        IReadOnlyList<TableCellInput> changes,
        CancellationToken cancellationToken = default)
    {
        ValidateTable(table);
        ArgumentNullException.ThrowIfNull(originalRow);
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0)
        {
            throw new InvalidOperationException("沒有需要儲存的欄位變更。");
        }

        var columns = await GetRequiredTableColumnsAsync(database, table, cancellationToken).ConfigureAwait(false);
        ValidateOriginalRow(columns, originalRow);
        RequirePrimaryKey(columns);
        var inputMap = BuildInputMap(changes);

        await using var connection = CreateConnection(database);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = Math.Max(1, Profile.TimeoutSeconds * 2);

        var assignments = new List<string>(inputMap.Count);
        var parameterIndex = 0;
        foreach (var input in inputMap.Values)
        {
            var column = FindColumn(columns, input.ColumnName);
            if (column.IsPrimaryKey || !column.IsEditable || input.Mode == TableCellInputMode.Default)
            {
                throw new InvalidOperationException($"「{column.Name}」不可在既有資料列中直接修改。");
            }

            var parameterName = $"@value{parameterIndex++}";
            AddParameter(command, parameterName, TableCellValueConverter.Parse(column, input), column);
            assignments.Add($"{QuoteIdentifier(column.Name)} = {parameterName}");
        }

        var predicate = BuildOptimisticPredicate(command, columns, originalRow);
        command.CommandText =
            $"UPDATE {BuildQualifiedName(table)} SET {string.Join(", ", assignments)} WHERE {predicate};";
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await CompleteSingleRowMutationAsync(transaction, affected, "修改", cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteTableRowAsync(
        string database,
        DatabaseObjectInfo table,
        TableDataRow originalRow,
        CancellationToken cancellationToken = default)
    {
        ValidateTable(table);
        ArgumentNullException.ThrowIfNull(originalRow);
        var columns = await GetRequiredTableColumnsAsync(database, table, cancellationToken).ConfigureAwait(false);
        ValidateOriginalRow(columns, originalRow);
        RequirePrimaryKey(columns);

        await using var connection = CreateConnection(database);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = Math.Max(1, Profile.TimeoutSeconds * 2);
        var predicate = BuildOptimisticPredicate(command, columns, originalRow);
        command.CommandText = $"DELETE FROM {BuildQualifiedName(table)} WHERE {predicate};";
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await CompleteSingleRowMutationAsync(transaction, affected, "刪除", cancellationToken).ConfigureAwait(false);
    }

    protected string BuildQualifiedName(DatabaseObjectInfo databaseObject)
    {
        ArgumentNullException.ThrowIfNull(databaseObject);
        return string.IsNullOrWhiteSpace(databaseObject.Schema)
            ? QuoteIdentifier(databaseObject.Name)
            : $"{QuoteIdentifier(databaseObject.Schema)}.{QuoteIdentifier(databaseObject.Name)}";
    }

    protected virtual string BuildTableDataSql(
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
        var offset = rowOffset == 0 ? string.Empty : $" OFFSET {rowOffset}";
        return $"SELECT {selectColumns} FROM {BuildQualifiedName(table)}{orderBy} LIMIT {fetchLimit}{offset};";
    }

    protected virtual string BuildTableDataSelectExpression(TableColumnInfo column) =>
        QuoteIdentifier(column.Name);

    protected virtual string BuildDefaultInsertSql(DatabaseObjectInfo table) =>
        $"INSERT INTO {BuildQualifiedName(table)} DEFAULT VALUES;";

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

    private async Task<IReadOnlyList<TableColumnInfo>> GetRequiredTableColumnsAsync(
        string database,
        DatabaseObjectInfo table,
        CancellationToken cancellationToken)
    {
        var columns = await GetTableColumnsAsync(database, table, cancellationToken).ConfigureAwait(false);
        if (columns.Count == 0)
        {
            throw new InvalidOperationException($"找不到「{table.DisplayName}」的欄位 metadata。");
        }

        return columns.OrderBy(column => column.Ordinal).ToList();
    }

    private static Dictionary<string, TableCellInput> BuildInputMap(IReadOnlyList<TableCellInput> inputs)
    {
        var map = new Dictionary<string, TableCellInput>(StringComparer.Ordinal);
        foreach (var input in inputs)
        {
            if (!map.TryAdd(input.ColumnName, input))
            {
                throw new InvalidOperationException($"欄位「{input.ColumnName}」重複出現。");
            }
        }

        return map;
    }

    private static TableColumnInfo FindColumn(IReadOnlyList<TableColumnInfo> columns, string name) =>
        columns.FirstOrDefault(column => string.Equals(column.Name, name, StringComparison.Ordinal)) ??
        throw new InvalidOperationException($"找不到欄位「{name}」。");

    private string BuildOptimisticPredicate(
        DbCommand command,
        IReadOnlyList<TableColumnInfo> columns,
        TableDataRow originalRow)
    {
        var predicateColumns = columns
            .Where(column =>
                column.IsPrimaryKey ||
                column.IsEditable && !TableCellValueConverter.IsBinaryValueTooLargeToEdit(
                    column,
                    originalRow.Values[column.Ordinal]) &&
                !TableCellValueConverter.IsStructuredTextTooLargeToEdit(
                    column,
                    originalRow.Values[column.Ordinal]))
            .OrderBy(column => column.Ordinal)
            .ToList();
        var predicates = new List<string>(predicateColumns.Count);
        var parameterIndex = 0;
        foreach (var column in predicateColumns)
        {
            var original = originalRow.Values[column.Ordinal];
            if (column.IsPrimaryKey && original is null)
            {
                throw new InvalidOperationException($"Primary Key「{column.Name}」不可為 NULL。");
            }

            if (original is null or DBNull)
            {
                predicates.Add($"{QuoteIdentifier(column.Name)} IS NULL");
                continue;
            }

            var parameterName = $"@original{parameterIndex++}";
            AddParameter(command, parameterName, original, column);
            predicates.Add(BuildOriginalValuePredicate(column, parameterName));
        }

        return string.Join(" AND ", predicates);
    }

    protected virtual string BuildOriginalValuePredicate(TableColumnInfo column, string parameterName) =>
        $"{QuoteIdentifier(column.Name)} = {parameterName}";

    private void AddParameter(
        DbCommand command,
        string name,
        object? value,
        TableColumnInfo column)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        ConfigureParameter(parameter, column);
        parameter.Value = PrepareParameterValue(column, value) ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    protected virtual object? PrepareParameterValue(TableColumnInfo column, object? value) => value;

    protected virtual void ConfigureParameter(DbParameter parameter, TableColumnInfo column)
    {
        parameter.DbType = column.ValueKind switch
        {
            TableColumnValueKind.Integer => DbType.Int64,
            TableColumnValueKind.UnsignedInteger => DbType.UInt64,
            TableColumnValueKind.Decimal => DbType.Decimal,
            TableColumnValueKind.FloatingPoint => DbType.Double,
            TableColumnValueKind.Boolean => DbType.Boolean,
            TableColumnValueKind.Date => DbType.Date,
            TableColumnValueKind.DateTime => DbType.DateTime,
            TableColumnValueKind.DateTimeOffset => DbType.DateTimeOffset,
            TableColumnValueKind.Time => DbType.Time,
            TableColumnValueKind.Guid => DbType.Guid,
            TableColumnValueKind.Binary => DbType.Binary,
            _ => DbType.String
        };
    }

    private static void ValidateTable(DatabaseObjectInfo table)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (table.Kind != DatabaseObjectKind.Table)
        {
            throw new InvalidOperationException("檢視表目前維持唯讀；資料寫入只允許 Table。");
        }
    }

    private static void ValidateOriginalRow(
        IReadOnlyList<TableColumnInfo> columns,
        TableDataRow originalRow)
    {
        if (originalRow.Values.Count != columns.Count)
        {
            throw new InvalidOperationException("資料列與目前 Table schema 不一致；請重新整理後再試一次。");
        }
    }

    private static void RequirePrimaryKey(IReadOnlyList<TableColumnInfo> columns)
    {
        if (!columns.Any(column => column.IsPrimaryKey))
        {
            throw new InvalidOperationException("這張 Table 沒有 Primary Key；為避免誤改多列，目前只允許新增與唯讀瀏覽。");
        }
    }

    private static async Task CompleteSingleRowMutationAsync(
        DbTransaction transaction,
        int affected,
        string operation,
        CancellationToken cancellationToken)
    {
        if (affected == 1)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        if (affected == 0)
        {
            throw new TableDataConflictException(
                $"資料列已被其他連線修改或刪除；本次{operation}未寫入，請重新整理後再試。");
        }

        throw new TableDataConflictException(
            $"安全檢查發現{operation}可能影響 {affected:N0} 列，交易已回復，未寫入任何變更。");
    }
}
