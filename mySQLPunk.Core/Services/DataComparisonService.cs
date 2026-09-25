using System.Globalization;
using System.Text;
using MySqlPunk.Core.Models;
using MySqlPunk.Core.Providers;

namespace MySqlPunk.Core.Services;

/// <summary>
/// Row-level comparison of a table that exists on both sides. Rows are matched by primary key and compared on the
/// columns both sides share, using the table editor's canonical value text so equal values of the same type match
/// exactly. Nothing is written; the result lists the inserts／updates／deletes that would align the target.
/// </summary>
public static class DataComparisonService
{
    public const int DefaultMaximumRows = 50_000;
    private const int PageSize = 1_000;
    private const string NullMarker = "\u0000NULL";

    public static async Task<DataTableComparison> CompareTableAsync(
        IDatabaseSession sourceSession,
        string sourceDatabase,
        DatabaseObjectInfo sourceTable,
        IDatabaseSession targetSession,
        string targetDatabase,
        DatabaseObjectInfo targetTable,
        int maximumRows = DefaultMaximumRows,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceSession);
        ArgumentNullException.ThrowIfNull(targetSession);
        if (sourceSession.Profile.Provider != targetSession.Profile.Provider)
        {
            return Skipped(sourceTable, targetTable, "來源與目標是不同類型的資料庫，資料同步只支援同類型。");
        }

        var source = await LoadAllAsync(sourceSession, sourceDatabase, sourceTable, maximumRows, cancellationToken).ConfigureAwait(false);
        if (source.TooLarge)
        {
            return Skipped(sourceTable, targetTable, $"來源超過 {maximumRows:N0} 列上限。");
        }

        var target = await LoadAllAsync(targetSession, targetDatabase, targetTable, maximumRows, cancellationToken).ConfigureAwait(false);
        if (target.TooLarge)
        {
            return Skipped(sourceTable, targetTable, $"目標超過 {maximumRows:N0} 列上限。");
        }

        var sourceKeys = source.Columns.Where(column => column.IsPrimaryKey).Select(column => column.Name).ToList();
        var targetKeys = target.Columns.Where(column => column.IsPrimaryKey).Select(column => column.Name).ToList();
        if (sourceKeys.Count == 0 || targetKeys.Count == 0)
        {
            return Skipped(sourceTable, targetTable, "沒有 Primary Key，無法逐列對應。");
        }

        if (!sourceKeys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(targetKeys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
        {
            return Skipped(sourceTable, targetTable, $"兩邊 Primary Key 不同（{string.Join(", ", sourceKeys)} ↔ {string.Join(", ", targetKeys)}）。");
        }

        var warnings = new List<string>();
        var syncPairs = new List<(TableColumnInfo Source, TableColumnInfo Target)>();
        foreach (var sourceColumn in source.Columns.OrderBy(column => column.Ordinal))
        {
            var targetColumn = target.Columns.FirstOrDefault(column => column.Name.Equals(sourceColumn.Name, StringComparison.OrdinalIgnoreCase));
            if (targetColumn is null)
            {
                warnings.Add($"欄位 {sourceColumn.Name} 只在來源，不比較也不同步。");
                continue;
            }

            if (targetColumn.ValueKind == TableColumnValueKind.Unsupported || sourceColumn.ValueKind == TableColumnValueKind.Unsupported)
            {
                if (targetColumn.IsPrimaryKey)
                {
                    return Skipped(sourceTable, targetTable, $"Primary Key 欄位 {targetColumn.Name} 的型別尚不支援同步。");
                }

                warnings.Add($"欄位 {sourceColumn.Name} 的型別（{targetColumn.DataTypeName}）尚不支援寫入，不比較也不同步。");
                continue;
            }

            if (!targetColumn.IsEditable && !targetColumn.IsIdentity)
            {
                warnings.Add($"欄位 {sourceColumn.Name} 在目標是計算／產生欄位，由資料庫自行計算，不比較也不同步。");
                continue;
            }

            syncPairs.Add((sourceColumn, targetColumn));
        }

        foreach (var targetOnly in target.Columns.Where(column =>
                     source.Columns.All(item => !item.Name.Equals(column.Name, StringComparison.OrdinalIgnoreCase))))
        {
            warnings.Add(!targetOnly.IsNullable && !targetOnly.HasDefault && !targetOnly.IsGenerated
                ? $"欄位 {targetOnly.Name} 只在目標且為 NOT NULL 無預設值，新增資料列會失敗。"
                : $"欄位 {targetOnly.Name} 只在目標，新增資料列時交由資料庫預設值處理。");
        }

        var keyPairs = syncPairs.Where(pair => pair.Target.IsPrimaryKey).ToList();
        var targetByKey = new Dictionary<string, TableDataRow>(StringComparer.Ordinal);
        foreach (var row in target.Rows)
        {
            targetByKey[KeyText(keyPairs.Select(pair => pair.Target), row)] = row;
        }

        var changes = new List<DataRowChange>();
        var identical = 0;
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sourceRow in source.Rows)
        {
            var key = KeyText(keyPairs.Select(pair => pair.Source), sourceRow);
            seenKeys.Add(key);
            if (syncPairs.Any(pair => IsTooLarge(pair.Source, sourceRow.Values[pair.Source.Ordinal])))
            {
                warnings.Add($"主鍵 {DisplayKey(key)} 含超過 1 MiB 的值，略過此列。");
                continue;
            }

            if (!targetByKey.TryGetValue(key, out var targetRow))
            {
                changes.Add(new DataRowChange(
                    DataRowChangeKind.Insert,
                    key,
                    syncPairs.Select(pair => ToInput(pair.Target, pair.Source, sourceRow)).ToList(),
                    null,
                    syncPairs.Select(pair => pair.Target.Name).ToList()));
                continue;
            }

            var changed = syncPairs
                .Where(pair => !pair.Target.IsPrimaryKey &&
                               !string.Equals(
                                   Canonical(pair.Source, sourceRow.Values[pair.Source.Ordinal]),
                                   Canonical(pair.Target, targetRow.Values[pair.Target.Ordinal]),
                                   StringComparison.Ordinal))
                .ToList();
            if (changed.Count == 0)
            {
                identical++;
                continue;
            }

            if (changed.Any(pair => pair.Target.IsIdentity || !pair.Target.IsEditable))
            {
                warnings.Add($"主鍵 {DisplayKey(key)} 的差異在不可修改的欄位上，略過此列。");
                continue;
            }

            if (changed.Any(pair => IsTooLarge(pair.Target, targetRow.Values[pair.Target.Ordinal])))
            {
                warnings.Add($"主鍵 {DisplayKey(key)} 的目標值超過 1 MiB，無法安全比對原值，略過此列。");
                continue;
            }

            changes.Add(new DataRowChange(
                DataRowChangeKind.Update,
                key,
                changed.Select(pair => ToInput(pair.Target, pair.Source, sourceRow)).ToList(),
                targetRow,
                changed.Select(pair => pair.Target.Name).ToList()));
        }

        foreach (var (key, targetRow) in targetByKey)
        {
            if (!seenKeys.Contains(key))
            {
                changes.Add(new DataRowChange(DataRowChangeKind.Delete, key, Array.Empty<TableCellInput>(), targetRow, Array.Empty<string>()));
            }
        }

        return new DataTableComparison(
            sourceTable,
            targetTable,
            keyPairs.Select(pair => pair.Target.Name).ToList(),
            syncPairs.Select(pair => pair.Target.Name).ToList(),
            changes,
            identical,
            source.Rows.Count,
            target.Rows.Count,
            warnings,
            null);
    }

    /// <summary>
    /// Orders tables so referenced (parent) tables come before the tables that reference them. Inserts／updates run
    /// in this order and deletes in reverse, so foreign keys hold at every step. Cycles keep their input order.
    /// </summary>
    public static IReadOnlyList<DatabaseObjectInfo> OrderByDependencies(
        IReadOnlyList<DatabaseObjectInfo> tables,
        IReadOnlyList<TableStructureInfo> structures)
    {
        var byName = tables.ToDictionary(table => table.Name, StringComparer.OrdinalIgnoreCase);
        var parents = tables.ToDictionary(
            table => table.Name,
            table => structures
                .Where(structure => structure.Object.Name.Equals(table.Name, StringComparison.OrdinalIgnoreCase))
                .SelectMany(structure => structure.ForeignKeys)
                .Select(foreignKey => foreignKey.ReferencedTable.Split('.').Last())
                .Where(name => byName.ContainsKey(name) && !name.Equals(table.Name, StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        var ordered = new List<DatabaseObjectInfo>();
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (ordered.Count < tables.Count)
        {
            var ready = tables.Where(table => !placed.Contains(table.Name) && parents[table.Name].All(placed.Contains)).ToList();
            if (ready.Count == 0)
            {
                ready = tables.Where(table => !placed.Contains(table.Name)).Take(1).ToList();
            }

            foreach (var table in ready)
            {
                ordered.Add(table);
                placed.Add(table.Name);
            }
        }

        return ordered;
    }

    /// <summary>Human-readable preview of the changes as literal SQL. Execution never uses this text.</summary>
    public static string BuildPreviewSql(DatabaseProviderKind provider, DataTableComparison comparison, bool includeDeletes)
    {
        var text = new StringBuilder();
        var table = QualifiedName(provider, comparison.TargetTable);
        text.AppendLine($"-- {OneLine(comparison.TargetTable.DisplayName)}：{OneLine(comparison.StatusText)}");
        foreach (var change in comparison.Changes)
        {
            switch (change.Kind)
            {
                case DataRowChangeKind.Insert:
                    text.AppendLine(
                        $"INSERT INTO {table} ({string.Join(", ", change.Values.Select(value => Quote(provider, value.ColumnName)))}) " +
                        $"VALUES ({string.Join(", ", change.Values.Select(value => Literal(provider, value)))});");
                    break;
                case DataRowChangeKind.Update:
                    text.AppendLine(
                        $"UPDATE {table} SET {string.Join(", ", change.Values.Select(value => $"{Quote(provider, value.ColumnName)} = {Literal(provider, value)}"))} " +
                        $"WHERE {KeyPredicate(provider, comparison, change)};");
                    break;
                default:
                    var delete = $"DELETE FROM {table} WHERE {KeyPredicate(provider, comparison, change)};";
                    // A commented line must stay single-line or a newline inside a key literal would escape the comment.
                    text.AppendLine(includeDeletes ? delete : "-- " + OneLine(delete));
                    break;
            }
        }

        return text.ToString();
    }

    private static string KeyPredicate(DatabaseProviderKind provider, DataTableComparison comparison, DataRowChange change)
    {
        var parts = change.KeyText.Split('\u0001');
        return string.Join(" AND ", comparison.KeyColumns.Select((name, index) =>
            $"{Quote(provider, name)} = {Literal(provider, new TableCellInput(name, TableCellInputMode.Value, index < parts.Length ? parts[index] : string.Empty))}"));
    }

    internal static string Literal(DatabaseProviderKind provider, TableCellInput value)
    {
        if (value.Mode == TableCellInputMode.Null)
        {
            return "NULL";
        }

        var text = value.Text;
        if (text.StartsWith("0x", StringComparison.Ordinal) && text.Length % 2 == 0 && text[2..].All(Uri.IsHexDigit))
        {
            return provider switch
            {
                DatabaseProviderKind.PostgreSql => $"'\\x{text[2..]}'::bytea",
                DatabaseProviderKind.Sqlite => $"X'{text[2..]}'",
                _ => text
            };
        }

        var escaped = text.Replace("'", "''", StringComparison.Ordinal);
        if (provider == DatabaseProviderKind.MySql)
        {
            escaped = escaped.Replace("\\", "\\\\", StringComparison.Ordinal);
        }

        // Newlines are kept inside the quoted literal but can never start a new statement line.
        return (provider == DatabaseProviderKind.SqlServer ? "N'" : "'") + escaped + "'";
    }

    internal static string QualifiedName(DatabaseProviderKind provider, DatabaseObjectInfo table) =>
        provider is DatabaseProviderKind.MySql or DatabaseProviderKind.Sqlite || string.IsNullOrWhiteSpace(table.Schema)
            ? Quote(provider, table.Name)
            : $"{Quote(provider, table.Schema)}.{Quote(provider, table.Name)}";

    internal static string Quote(DatabaseProviderKind provider, string identifier) => provider switch
    {
        DatabaseProviderKind.MySql => "`" + identifier.Replace("`", "``", StringComparison.Ordinal) + "`",
        DatabaseProviderKind.SqlServer => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]",
        _ => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
    };

    internal static string OneLine(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, @"[\r\n\u0085  ]+", " ");

    private static TableCellInput ToInput(TableColumnInfo target, TableColumnInfo source, TableDataRow row)
    {
        var value = row.Values[source.Ordinal];
        return value is null or DBNull
            ? new TableCellInput(target.Name, TableCellInputMode.Null, string.Empty)
            : new TableCellInput(target.Name, TableCellInputMode.Value, TableCellValueConverter.Format(source, value));
    }

    private static string Canonical(TableColumnInfo column, object? value) =>
        value is null or DBNull ? NullMarker : TableCellValueConverter.Format(column, value);

    private static string KeyText(IEnumerable<TableColumnInfo> keyColumns, TableDataRow row) =>
        string.Join('\u0001', keyColumns.Select(column => Canonical(column, row.Values[column.Ordinal])));

    private static string DisplayKey(string key) => OneLine(key.Replace('\u0001', ','));

    private static bool IsTooLarge(TableColumnInfo column, object? value) =>
        TableCellValueConverter.IsBinaryValueTooLargeToEdit(column, value) ||
        TableCellValueConverter.IsStructuredTextTooLargeToEdit(column, value);

    private static DataTableComparison Skipped(DatabaseObjectInfo source, DatabaseObjectInfo target, string reason) =>
        new(source, target, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<DataRowChange>(), 0, 0, 0, Array.Empty<string>(), reason);

    private static async Task<(IReadOnlyList<TableColumnInfo> Columns, List<TableDataRow> Rows, bool TooLarge)> LoadAllAsync(
        IDatabaseSession session,
        string database,
        DatabaseObjectInfo table,
        int maximumRows,
        CancellationToken cancellationToken)
    {
        var rows = new List<TableDataRow>();
        IReadOnlyList<TableColumnInfo> columns = Array.Empty<TableColumnInfo>();
        var offset = 0;
        while (true)
        {
            var page = await session.LoadTableDataAsync(database, table, PageSize, offset, cancellationToken).ConfigureAwait(false);
            columns = page.Columns;
            rows.AddRange(page.Rows);
            if (rows.Count > maximumRows)
            {
                return (columns, rows, true);
            }

            if (!page.HasNextPage)
            {
                return (columns, rows, false);
            }

            offset += page.Rows.Count;
        }
    }
}
