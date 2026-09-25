using System.Globalization;
using System.Text;
using MySqlPunk.Core.Models;
using MySqlPunk.Core.Providers;

namespace MySqlPunk.Core.Services;

/// <summary>
/// Generates test rows for one or more tables of the same database. Tables are generated in foreign-key order;
/// foreign-key columns pick real parent rows (existing ones plus rows generated in the same run), primary-key and
/// unique columns never repeat existing or generated values, and every value is validated through the table
/// editor's parser before anything is written. Writing goes through <see cref="IDatabaseSession.ApplyDataSyncAsync"/>
/// so the whole run is a single transaction.
/// </summary>
public static class DataGeneratorService
{
    public const int MaximumRowsPerTable = 100_000;
    public const int MaximumTotalRows = 200_000;
    public const int ExistingRowLimit = 100_000;
    private const int PageSize = 1_000;
    private const int MaximumAttempts = 200;

    private static readonly string[] FirstNames =
    {
        "Alice", "Bob", "Carol", "David", "Emma", "Frank", "Grace", "Henry", "Ivy", "Jack", "Karen", "Leo",
        "Mia", "Noah", "Olivia", "Peter", "Quinn", "Ruby", "Sam", "Tina", "Uma", "Victor", "Wendy", "Yuki"
    };

    private static readonly string[] LastNames =
    {
        "Chen", "Lin", "Wang", "Smith", "Johnson", "Brown", "Garcia", "Miller", "Davis", "Lopez", "Wilson",
        "Anderson", "Taylor", "Thomas", "Moore", "Martin", "Lee", "Walker", "Hall", "Young"
    };

    private static readonly string[] Cities =
    {
        "Taipei", "Taichung", "Kaohsiung", "Tainan", "Tokyo", "Osaka", "Seoul", "Singapore", "London", "Paris",
        "Berlin", "New York", "Chicago", "Toronto", "Sydney", "Madrid"
    };

    private static readonly string[] Countries =
    {
        "Taiwan", "Japan", "Korea", "Singapore", "United Kingdom", "France", "Germany", "United States",
        "Canada", "Australia", "Spain"
    };

    private static readonly string[] Streets = { "Main", "Oak", "Maple", "Park", "Lake", "Hill", "River", "Sunset", "Cedar", "Elm" };

    private static readonly string[] Words =
    {
        "lorem", "ipsum", "dolor", "sit", "amet", "consectetur", "adipiscing", "elit", "sed", "do", "eiusmod",
        "tempor", "incididunt", "labore", "dolore", "magna", "aliqua", "enim", "minim", "veniam"
    };

    private static readonly string[] Statuses = { "active", "inactive", "pending", "archived" };

    /// <summary>Tried in order; the first one the column's editor parser accepts is used for every value.</summary>
    private static readonly string[] TemporalFormats =
    {
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss'+00:00'",
        "yyyy-MM-dd'T'HH:mm:ss'+00:00'",
        "HH:mm:ss",
        "HH:mm:ss'+00:00'",
        "yyyy-MM-dd"
    };

    private static readonly DateTime TemporalBase = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    /// <summary>Columns of a table with a description of what Auto does, for the rule editor.</summary>
    public static async Task<DataGeneratorTableInfo> DescribeTableAsync(
        IDatabaseSession session,
        string database,
        DatabaseObjectInfo table,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var snapshot = await session.LoadTableDataAsync(database, table, 1, 0, cancellationToken).ConfigureAwait(false);
        var structure = await session.GetTableStructureAsync(database, table, cancellationToken).ConfigureAwait(false);
        var uniqueSingles = UniqueSets(snapshot.Columns, structure)
            .Where(set => set.Length == 1)
            .Select(set => set[0])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var foreignKeys = structure.ForeignKeys;
        var columns = new List<DataGeneratorColumnInfo>();
        foreach (var column in snapshot.Columns.OrderBy(column => column.Ordinal))
        {
            var dataType = structure.Columns.FirstOrDefault(item => item.Name.Equals(column.Name, StringComparison.OrdinalIgnoreCase))?.DataType
                           ?? column.DataTypeName;
            var foreignKey = foreignKeys.FirstOrDefault(item => item.Columns.Contains(column.Name, StringComparer.OrdinalIgnoreCase));
            var unique = uniqueSingles.Contains(column.Name);
            string description;
            if (column.IsGenerated && !column.IsIdentity)
            {
                description = "計算欄位，由資料庫計算";
            }
            else if (IsServerNumbered(column))
            {
                description = "資料庫自動編號（被本次其他資料表參照時改寫明確的遞增值）";
            }
            else if (foreignKey is not null)
            {
                description = $"外鍵 → {foreignKey.ReferencedTable}（{string.Join(", ", foreignKey.ReferencedColumns)}），取現有或本次產生的資料列";
            }
            else
            {
                var probe = BuildAutoGenerator(column, unique, 0m, 0, out var autoDescription);
                description = probe is null
                    ? column.IsNullable || column.HasDefault ? "型別不支援自動產生，交給資料庫預設值／NULL" : "型別不支援自動產生，請指定固定值或樣式"
                    : autoDescription;
            }

            columns.Add(new DataGeneratorColumnInfo(
                column,
                dataType,
                description,
                foreignKey is not null,
                unique,
                column.IsNullable || column.HasDefault || column.IsGenerated || column.IsIdentity));
        }

        var notes = new List<string>();
        if (!snapshot.HasPrimaryKey)
        {
            notes.Add("沒有 Primary Key：只能新增資料列，唯一性只依唯一索引檢查。");
        }

        return new DataGeneratorTableInfo(table, columns, notes);
    }

    public static async Task<DataGenerationResult> GenerateAsync(
        IDatabaseSession session,
        string database,
        IReadOnlyList<DataGeneratorTablePlan> plans,
        int? seed = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(plans);
        if (plans.Count == 0)
        {
            return Failed("請至少選擇一個資料表。");
        }

        foreach (var plan in plans)
        {
            if (plan.RowCount is < 1 or > MaximumRowsPerTable)
            {
                return Failed($"{plan.Table.DisplayName} 的筆數必須介於 1 與 {MaximumRowsPerTable:N0}。");
            }
        }

        if (plans.Sum(plan => (long)plan.RowCount) > MaximumTotalRows)
        {
            return Failed($"一次最多產生 {MaximumTotalRows:N0} 列。");
        }

        if (plans.Select(plan => plan.Table.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plans.Count)
        {
            return Failed("同一個資料表只能出現一次。");
        }

        var random = seed is { } fixedSeed ? new Random(fixedSeed) : new Random();
        var warnings = new List<string>();
        var objects = await session.GetObjectsAsync(database, cancellationToken).ConfigureAwait(false);
        var states = new Dictionary<string, TableState>(StringComparer.OrdinalIgnoreCase);
        foreach (var plan in plans)
        {
            states[plan.Table.DisplayName] = await LoadStateAsync(session, database, plan.Table, cancellationToken).ConfigureAwait(false);
        }

        // Register every foreign key of the planned tables before generating, so parent rows generated earlier in the
        // run are collected into the pools their children pick from.
        var links = new Dictionary<string, List<ForeignKeyLink>>(StringComparer.OrdinalIgnoreCase);
        foreach (var plan in plans)
        {
            var state = states[plan.Table.DisplayName];
            var list = new List<ForeignKeyLink>();
            links[plan.Table.DisplayName] = list;
            foreach (var foreignKey in state.Structure.ForeignKeys)
            {
                if (foreignKey.Columns.Count == 0 ||
                    foreignKey.Columns.Count != foreignKey.ReferencedColumns.Count ||
                    foreignKey.Columns.Any(name => FindColumn(state.Columns, name) is null))
                {
                    warnings.Add($"{plan.Table.DisplayName} 的外鍵 {foreignKey.Name} 欄位無法對應，依一般欄位產生，資料庫可能拒絕。");
                    continue;
                }

                var parentObject = ResolveTable(objects, plan.Table, foreignKey.ReferencedTable);
                if (parentObject is null)
                {
                    return Failed($"找不到 {plan.Table.DisplayName} 外鍵 {foreignKey.Name} 參照的資料表 {foreignKey.ReferencedTable}。");
                }

                if (!states.TryGetValue(parentObject.DisplayName, out var parent))
                {
                    parent = await LoadStateAsync(session, database, parentObject, cancellationToken).ConfigureAwait(false);
                    states[parentObject.DisplayName] = parent;
                }

                var parentColumns = foreignKey.ReferencedColumns.Select(name => FindColumn(parent.Columns, name)).ToList();
                if (parentColumns.Any(column => column is null))
                {
                    return Failed($"{parentObject.DisplayName} 沒有外鍵 {foreignKey.Name} 參照的欄位。");
                }

                list.Add(new ForeignKeyLink(foreignKey, parent, parent.RegisterPool(parentColumns!)));
            }
        }

        foreach (var state in states.Values)
        {
            if (state.ExistingTruncated)
            {
                warnings.Add($"{state.Table.DisplayName} 超過 {ExistingRowLimit:N0} 列（或沒有主鍵無法分頁），只以已讀取的資料列檢查唯一性與挑選參照；若仍衝突，資料庫會拒絕並整批回滾。");
            }
        }

        var referenced = links.Values.SelectMany(list => list)
            .SelectMany(link => link.Pool.Columns.Select(column => (link.Parent.Table.DisplayName, column.Name)))
            .ToHashSet();
        var ordered = DataComparisonService.OrderByDependencies(
            plans.Select(plan => plan.Table).ToList(),
            plans.Select(plan => states[plan.Table.DisplayName].Structure).ToList());
        var generated = new List<DataGeneratedTable>();
        foreach (var table in ordered)
        {
            var plan = plans.First(item => item.Table.DisplayName.Equals(table.DisplayName, StringComparison.OrdinalIgnoreCase));
            try
            {
                generated.Add(GenerateTable(plan, states[table.DisplayName], links[table.DisplayName], referenced, random, warnings, cancellationToken));
            }
            catch (GenerationException exception)
            {
                return new DataGenerationResult(Array.Empty<DataGeneratedTable>(), warnings, $"{table.DisplayName}：{exception.Message}");
            }
        }

        return new DataGenerationResult(generated, warnings, null);
    }

    /// <summary>Writes every generated row in one transaction; any failure rolls the whole run back.</summary>
    public static Task<DataSyncResult> ApplyAsync(
        IDatabaseSession session,
        string database,
        DataGenerationResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException("產生失敗的結果不可寫入。");
        }

        return session.ApplyDataSyncAsync(
            database,
            result.Tables.Where(table => table.Rows.Count > 0).Select(table => new DataSyncTableRequest(table.Table, table.Rows)).ToList(),
            cancellationToken);
    }

    /// <summary>Human-readable INSERT preview; execution never uses this text.</summary>
    public static string BuildPreviewSql(DatabaseProviderKind provider, DataGenerationResult result, int maximumRowsPerTable = 100)
    {
        var text = new StringBuilder();
        foreach (var table in result.Tables)
        {
            var shown = Math.Min(maximumRowsPerTable, table.Rows.Count);
            text.AppendLine(DataComparisonService.OneLine(
                $"-- {table.Table.DisplayName}：將新增 {table.Rows.Count:N0} 列" +
                (shown < table.Rows.Count ? $"（以下只列出前 {shown:N0} 列）" : string.Empty)));
            var name = DataComparisonService.QualifiedName(provider, table.Table);
            foreach (var row in table.Rows.Take(shown))
            {
                text.AppendLine(
                    $"INSERT INTO {name} ({string.Join(", ", row.Values.Select(value => DataComparisonService.Quote(provider, value.ColumnName)))}) " +
                    $"VALUES ({string.Join(", ", row.Values.Select(value => DataComparisonService.Literal(provider, value)))});");
            }

            text.AppendLine();
        }

        return text.ToString();
    }

    private static DataGeneratedTable GenerateTable(
        DataGeneratorTablePlan plan,
        TableState state,
        IReadOnlyList<ForeignKeyLink> links,
        HashSet<(string Table, string Column)> referenced,
        Random random,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        DataGeneratorRule RuleFor(TableColumnInfo column) =>
            plan.Rules.FirstOrDefault(pair => pair.Key.Equals(column.Name, StringComparison.OrdinalIgnoreCase)).Value ?? DataGeneratorRule.Auto;

        foreach (var name in plan.Rules.Keys)
        {
            if (FindColumn(state.Columns, name) is null)
            {
                throw new GenerationException($"規則指定的欄位 {name} 不存在。");
            }
        }

        var uniqueSets = UniqueSets(state.Columns, state.Structure);
        var uniqueSingles = uniqueSets.Where(set => set.Length == 1).Select(set => set[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Foreign keys whose columns are all on Auto pick whole parent tuples; explicit rules win over the link.
        var activeLinks = links.Where(link => link.ForeignKey.Columns.All(name => RuleFor(FindColumn(state.Columns, name)!).Kind == DataGeneratorRuleKind.Auto)).ToList();
        var linkedColumns = activeLinks.SelectMany(link => link.ForeignKey.Columns).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var generators = new List<ColumnGenerator>();
        foreach (var column in state.Columns.OrderBy(column => column.Ordinal))
        {
            var rule = RuleFor(column);
            if (rule.NullPercent is < 0 or > 100)
            {
                throw new GenerationException($"欄位 {column.Name} 的 NULL 比例必須介於 0 與 100。");
            }

            if (column.IsGenerated && !column.IsIdentity)
            {
                if (rule.Kind is not (DataGeneratorRuleKind.Auto or DataGeneratorRuleKind.DatabaseDefault))
                {
                    throw new GenerationException($"欄位 {column.Name} 是計算欄位，無法指定值。");
                }

                continue;
            }

            if (linkedColumns.Contains(column.Name))
            {
                continue;
            }

            if (rule.Kind == DataGeneratorRuleKind.DatabaseDefault ||
                rule.Kind == DataGeneratorRuleKind.Auto && IsServerNumbered(column) && !referenced.Contains((state.Table.DisplayName, column.Name)))
            {
                if (!column.IsNullable && !column.HasDefault && !column.IsIdentity)
                {
                    throw new GenerationException($"欄位 {column.Name} 為 NOT NULL 且沒有預設值，不能交給資料庫預設。");
                }

                continue;
            }

            var unique = uniqueSingles.Contains(column.Name);
            Func<Random, string?>? next;
            if (rule.Kind == DataGeneratorRuleKind.Auto)
            {
                next = BuildAutoGenerator(column, unique, state.MaximumNumber(column), state.ExistingRowCount, out _);
                if (next is null)
                {
                    if (column.IsNullable || column.HasDefault)
                    {
                        continue;
                    }

                    throw new GenerationException($"欄位 {column.Name}（{column.DataTypeName}）的型別無法自動產生，請指定固定值、清單或樣式。");
                }
            }
            else
            {
                if (rule.Kind == DataGeneratorRuleKind.Null && !column.IsNullable)
                {
                    throw new GenerationException($"欄位 {column.Name} 為 NOT NULL，不能使用 NULL 規則。");
                }

                next = BuildRuleGenerator(column, rule);
            }

            var nullPercent = column.IsNullable && !unique && !column.IsPrimaryKey ? rule.NullPercent : 0;
            generators.Add(new ColumnGenerator(column, next, nullPercent));
        }

        foreach (var link in activeLinks)
        {
            if (link.Pool.Tuples.Count == 0 && link.Parent != state &&
                !link.ForeignKey.Columns.All(name => FindColumn(state.Columns, name)!.IsNullable))
            {
                throw new GenerationException($"外鍵 {link.ForeignKey.Name} 參照的 {link.Parent.Table.DisplayName} 沒有任何資料列，且本次產生時也無法取得其鍵值（請一併產生該資料表，或讓其鍵值不要交給資料庫預設）。");
            }
        }

        var written = generators.Select(generator => generator.Column)
            .Concat(activeLinks.SelectMany(link => link.ForeignKey.Columns.Select(name => FindColumn(state.Columns, name)!)))
            .ToList();
        var checkedSets = uniqueSets
            .Where(set => set.All(name => written.Any(column => column.Name.Equals(name, StringComparison.OrdinalIgnoreCase))))
            .Select(set => (Set: set, Seen: state.ExistingTuples(set)))
            .ToList();
        var keyColumns = state.Columns.Where(column => column.IsPrimaryKey).Select(column => column.Name).ToList();
        var nullLinkWarned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<DataRowChange>(plan.RowCount);
        for (var rowNumber = 1; rowNumber <= plan.RowCount; rowNumber++)
        {
            if (rowNumber % 1_000 == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            Dictionary<string, (TableColumnInfo Column, string? Text, string Canonical)>? values = null;
            List<string>? keys = null;
            for (var attempt = 0; attempt < MaximumAttempts && keys is null; attempt++)
            {
                values = new Dictionary<string, (TableColumnInfo, string?, string)>(StringComparer.OrdinalIgnoreCase);
                foreach (var link in activeLinks)
                {
                    var tuple = link.Pool.Tuples.Count == 0 ? null : link.Pool.Tuples[random.Next(link.Pool.Tuples.Count)];
                    var nullPercent = link.ForeignKey.Columns.Max(name => RuleFor(FindColumn(state.Columns, name)!).NullPercent);
                    var allNullable = link.ForeignKey.Columns.All(name => FindColumn(state.Columns, name)!.IsNullable);
                    if (tuple is null || allNullable && nullPercent > 0 && random.Next(100) < nullPercent)
                    {
                        if (tuple is null && !allNullable)
                        {
                            throw new GenerationException($"第 {rowNumber:N0} 列：外鍵 {link.ForeignKey.Name} 參照的資料表還沒有資料列，且欄位不允許 NULL。");
                        }

                        if (tuple is null && link.Parent != state && nullLinkWarned.Add(link.ForeignKey.Name))
                        {
                            warnings.Add($"{state.Table.DisplayName} 外鍵 {link.ForeignKey.Name} 參照的 {link.Parent.Table.DisplayName} 沒有資料列，外鍵欄位填入 NULL。");
                        }

                        foreach (var name in link.ForeignKey.Columns)
                        {
                            var column = FindColumn(state.Columns, name)!;
                            values[column.Name] = (column, null, NullMarker);
                        }

                        continue;
                    }

                    for (var index = 0; index < link.ForeignKey.Columns.Count; index++)
                    {
                        var column = FindColumn(state.Columns, link.ForeignKey.Columns[index])!;
                        values[column.Name] = Validate(column, tuple[index], rowNumber);
                    }
                }

                foreach (var generator in generators)
                {
                    var text = generator.NullPercent > 0 && random.Next(100) < generator.NullPercent
                        ? null
                        : generator.Next(random);
                    values[generator.Column.Name] = Validate(generator.Column, text, rowNumber);
                }

                var candidateKeys = new List<string>();
                var collides = false;
                foreach (var (set, seen) in checkedSets)
                {
                    var parts = set.Select(name => values[FindColumn(state.Columns, name)!.Name].Canonical).ToList();
                    if (parts.Contains(NullMarker))
                    {
                        candidateKeys.Add(string.Empty);
                        continue;
                    }

                    var key = string.Join('\u0001', parts);
                    if (seen.Contains(key))
                    {
                        collides = true;
                        break;
                    }

                    candidateKeys.Add(key);
                }

                if (!collides)
                {
                    keys = candidateKeys;
                }
            }

            if (keys is null || values is null)
            {
                throw new GenerationException(
                    $"第 {rowNumber:N0} 列在 {MaximumAttempts} 次嘗試內仍與既有或已產生的資料重複（唯一約束：" +
                    string.Join("；", checkedSets.Select(item => string.Join(", ", item.Set))) + "）；請減少筆數，或改用序列／樣式規則。");
            }

            for (var index = 0; index < checkedSets.Count; index++)
            {
                if (keys[index].Length > 0)
                {
                    checkedSets[index].Seen.Add(keys[index]);
                }
            }

            var inputs = state.Columns
                .Where(column => values.ContainsKey(column.Name))
                .OrderBy(column => column.Ordinal)
                .Select(column => values[column.Name].Text is { } text
                    ? new TableCellInput(column.Name, TableCellInputMode.Value, text)
                    : new TableCellInput(column.Name, TableCellInputMode.Null, string.Empty))
                .ToList();
            var keyText = keyColumns.Count > 0 && keyColumns.All(values.ContainsKey)
                ? string.Join('\u0001', keyColumns.Select(name => values[name].Text ?? "NULL"))
                : $"#{rowNumber}";
            rows.Add(new DataRowChange(DataRowChangeKind.Insert, keyText, inputs, null, inputs.Select(input => input.ColumnName).ToList()));
            state.AddGeneratedRow(values.ToDictionary(pair => pair.Key, pair => pair.Value.Text, StringComparer.OrdinalIgnoreCase));
        }

        return new DataGeneratedTable(
            state.Table,
            rows.Count == 0 ? Array.Empty<string>() : rows[0].Values.Select(value => value.ColumnName).ToList(),
            rows);
    }

    private static (TableColumnInfo Column, string? Text, string Canonical) Validate(TableColumnInfo column, string? text, int rowNumber)
    {
        if (text is null)
        {
            if (!column.IsNullable)
            {
                throw new GenerationException($"第 {rowNumber:N0} 列：欄位 {column.Name} 不允許 NULL。");
            }

            return (column, null, NullMarker);
        }

        try
        {
            var parsed = TableCellValueConverter.ParseForDataSync(column, new TableCellInput(column.Name, TableCellInputMode.Value, text));
            return (column, text, Canonical(column, parsed));
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or InvalidOperationException or ArgumentException)
        {
            throw new GenerationException($"第 {rowNumber:N0} 列：欄位 {column.Name} 的值「{DataComparisonService.OneLine(Shorten(text))}」無效：{exception.Message}");
        }
    }

    private const string NullMarker = "\u0000NULL";

    /// <summary>Case-insensitive on purpose: MySQL／SQL Server default collations treat such strings as duplicates.</summary>
    private static string Canonical(TableColumnInfo column, object? value) =>
        value is null or DBNull ? NullMarker : TableCellValueConverter.Format(column, value).ToUpperInvariant();

    private static string Shorten(string text) => text.Length <= 80 ? text : text[..80] + "…";

    private static bool IsServerNumbered(TableColumnInfo column) =>
        column.IsIdentity ||
        column.IsPrimaryKey && column.HasDefault && column.ValueKind is TableColumnValueKind.Integer or TableColumnValueKind.UnsignedInteger;

    private static List<string[]> UniqueSets(IReadOnlyList<TableColumnInfo> columns, TableStructureInfo structure)
    {
        var sets = new List<string[]>();
        var primaryKey = columns.Where(column => column.IsPrimaryKey).OrderBy(column => column.Ordinal).Select(column => column.Name).ToArray();
        if (primaryKey.Length > 0)
        {
            sets.Add(primaryKey);
        }

        foreach (var index in structure.Indexes.Where(index => index.IsUnique || index.IsPrimaryKey))
        {
            if (index.Columns.Count == 0 || index.Columns.Any(name => FindColumn(columns, name) is null))
            {
                continue;
            }

            var set = index.Columns.Select(name => FindColumn(columns, name)!.Name).ToArray();
            if (!sets.Any(existing => existing.Length == set.Length &&
                                      existing.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                                          .SequenceEqual(set.OrderBy(name => name, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)))
            {
                sets.Add(set);
            }
        }

        return sets;
    }

    private static TableColumnInfo? FindColumn(IReadOnlyList<TableColumnInfo> columns, string name) =>
        columns.FirstOrDefault(column => column.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static DatabaseObjectInfo? ResolveTable(IReadOnlyList<DatabaseObjectInfo> objects, DatabaseObjectInfo child, string reference)
    {
        var tables = objects.Where(item => item.Kind == DatabaseObjectKind.Table).ToList();
        var parts = reference.Split('.');
        var name = parts[^1].Trim('"', '[', ']', '`');
        var schema = parts.Length > 1 ? parts[^2].Trim('"', '[', ']', '`') : null;
        var candidates = tables.Where(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (schema is not null && candidates.Any(item => item.Schema.Equals(schema, StringComparison.OrdinalIgnoreCase)))
        {
            return candidates.First(item => item.Schema.Equals(schema, StringComparison.OrdinalIgnoreCase));
        }

        return candidates.FirstOrDefault(item => item.Schema.Equals(child.Schema, StringComparison.OrdinalIgnoreCase)) ?? candidates.FirstOrDefault();
    }

    private static async Task<TableState> LoadStateAsync(
        IDatabaseSession session,
        string database,
        DatabaseObjectInfo table,
        CancellationToken cancellationToken)
    {
        var structure = await session.GetTableStructureAsync(database, table, cancellationToken).ConfigureAwait(false);
        var rows = new List<TableDataRow>();
        IReadOnlyList<TableColumnInfo> columns = Array.Empty<TableColumnInfo>();
        var truncated = false;
        var offset = 0;
        while (true)
        {
            var page = await session.LoadTableDataAsync(database, table, PageSize, offset, cancellationToken).ConfigureAwait(false);
            columns = page.Columns;
            rows.AddRange(page.Rows);
            if (!page.HasNextPage)
            {
                truncated = page.WasTruncated;
                break;
            }

            if (rows.Count >= ExistingRowLimit)
            {
                truncated = true;
                break;
            }

            offset += page.Rows.Count;
        }

        return new TableState(table, columns, structure, rows, truncated);
    }

    // ---------------------------------------------------------------- Auto generators

    /// <param name="counterStart">Existing row count; numbered text continues after it so a rerun does not start by
    /// colliding with the rows an earlier run wrote.</param>
    internal static Func<Random, string?>? BuildAutoGenerator(TableColumnInfo column, bool unique, decimal existingMaximum, int counterStart, out string description)
    {
        var name = new string(column.Name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        switch (column.ValueKind)
        {
            case TableColumnValueKind.Integer:
            case TableColumnValueKind.UnsignedInteger:
            case TableColumnValueKind.SqliteNumeric when IsIntegerTypeName(column) || column.IsPrimaryKey:
            {
                var minimum = column.IntegerMinimum ?? (column.ValueKind == TableColumnValueKind.UnsignedInteger ? 0 : long.MinValue);
                var maximum = column.IntegerMaximum is { } upper ? (long)Math.Min(upper, long.MaxValue) : long.MaxValue;
                if (unique)
                {
                    var nextValue = (long)Math.Max(Math.Max(existingMaximum, 0m) + 1m, minimum);
                    description = "不重複的遞增整數（接在現有最大值之後）";
                    return _ =>
                    {
                        if (nextValue > maximum)
                        {
                            throw new GenerationException($"欄位 {column.Name} 的遞增值超出型別上限 {maximum:N0}。");
                        }

                        return (nextValue++).ToString(CultureInfo.InvariantCulture);
                    };
                }

                var (low, high, label) = name switch
                {
                    _ when name.Contains("age") => (18L, 80L, "年齡 18–80"),
                    _ when name.Contains("year") => (1990L, 2030L, "年份 1990–2030"),
                    _ when name.Contains("qty") || name.Contains("quantity") || name.Contains("count") || name.Contains("stock") => (0L, 100L, "數量 0–100"),
                    _ when name.Contains("status") || name.Contains("level") || name.Contains("type") || name.Contains("flag") => (0L, 5L, "代碼 0–5"),
                    _ => (1L, 1000L, "隨機整數 1–1000")
                };
                low = Math.Max(low, minimum);
                high = Math.Min(high, maximum);
                if (low > high)
                {
                    low = Math.Max(0, minimum);
                    high = Math.Min(maximum, Math.Max(low, 1));
                }

                description = label;
                return random => random.NextInt64(low, high + 1).ToString(CultureInfo.InvariantCulture);
            }

            case TableColumnValueKind.Boolean:
                description = "隨機 true／false";
                return random => random.Next(2) == 0 ? "false" : "true";

            case TableColumnValueKind.ExactDecimal:
            case TableColumnValueKind.PostgreSqlMoney:
            case TableColumnValueKind.SqlServerMoney:
            case TableColumnValueKind.SinglePrecisionFloatingPoint:
            case TableColumnValueKind.DoublePrecisionFloatingPoint:
            case TableColumnValueKind.SqliteNumeric:
            {
                var scale = 2;
                decimal maximum = name.Contains("price") || name.Contains("amount") || name.Contains("cost") || name.Contains("total") || name.Contains("salary")
                    ? 9_999m
                    : 1_000m;
                if (column.ValueKind == TableColumnValueKind.ExactDecimal)
                {
                    var definition = TableCellValueConverter.GetExactDecimalDefinition(column);
                    scale = Math.Min(definition.Scale ?? 2, 2);
                    if (definition.Precision is { } precision)
                    {
                        var integerDigits = precision - (definition.Scale ?? 0);
                        maximum = integerDigits <= 0 ? 0m : Math.Min(maximum, Pow10(Math.Min(integerDigits, 18)) - 1m);
                    }
                }
                else if (column.ValueKind == TableColumnValueKind.PostgreSqlMoney)
                {
                    scale = Math.Min(TableCellValueConverter.GetPostgreSqlMoneyScale(column), 2);
                }

                var format = "F" + scale.ToString(CultureInfo.InvariantCulture);
                if (unique)
                {
                    var nextValue = decimal.Floor(Math.Max(existingMaximum, 0m)) + 1m;
                    description = "不重複的遞增數值（接在現有最大值之後）";
                    return _ => (nextValue++).ToString(format, CultureInfo.InvariantCulture);
                }

                description = maximum >= 9_999m ? $"隨機金額 0–{maximum:N0}" : $"隨機數值 0–{maximum:N0}";
                var steps = (long)(maximum * Pow10(scale));
                return random => (random.NextInt64(0, steps + 1) / Pow10(scale)).ToString(format, CultureInfo.InvariantCulture);
            }

            case TableColumnValueKind.Date:
            case TableColumnValueKind.PostgreSqlDate:
            case TableColumnValueKind.DateTime:
            case TableColumnValueKind.DateTimeOffset:
            case TableColumnValueKind.Time:
            case TableColumnValueKind.MySqlTemporal:
            case TableColumnValueKind.MySqlTime:
            case TableColumnValueKind.PostgreSqlTemporal:
            case TableColumnValueKind.SqlServerTemporal:
            case TableColumnValueKind.SqliteTemporal:
            case TableColumnValueKind.TimeWithTimeZone:
            {
                var format = ProbeTemporalFormat(column);
                if (format is null)
                {
                    description = string.Empty;
                    return null;
                }

                var hasDate = format.Contains("yyyy", StringComparison.Ordinal);
                var hasTime = format.Contains("HH", StringComparison.Ordinal);
                if (unique)
                {
                    var step = (long)counterStart;
                    description = hasDate && !hasTime ? "不重複的遞增日期" : "不重複的遞增時間";
                    return _ =>
                    {
                        var value = hasDate && !hasTime ? TemporalBase.AddDays(step) : hasDate ? TemporalBase.AddMinutes(step) : TemporalBase.AddSeconds(step % 86_400);
                        if (!hasDate && step >= 86_400)
                        {
                            throw new GenerationException($"欄位 {column.Name} 是純時間，最多只有 86,400 個不重複值。");
                        }

                        step++;
                        return value.ToString(format, CultureInfo.InvariantCulture);
                    };
                }

                var birth = name.Contains("birth") || name.Contains("dob");
                var start = birth ? new DateTime(1960, 1, 1) : TemporalBase;
                var days = birth ? 16_000 : 2_190;
                description = birth ? "隨機生日 1960–2003" : hasDate ? "隨機日期時間 2020–2025" : "隨機時間";
                return random =>
                {
                    var value = start.AddDays(random.Next(days));
                    if (hasTime)
                    {
                        value = value.AddMinutes(random.Next(24 * 60));
                    }

                    return value.ToString(format, CultureInfo.InvariantCulture);
                };
            }

            case TableColumnValueKind.MySqlYear:
                if (unique)
                {
                    var year = Math.Max(1901, (int)Math.Min(existingMaximum, 2154m) + 1);
                    description = "不重複的遞增年份";
                    return _ => year > 2155
                        ? throw new GenerationException($"欄位 {column.Name} 的 YEAR 值已用盡。")
                        : (year++).ToString(CultureInfo.InvariantCulture);
                }

                description = "年份 1990–2030";
                return random => random.Next(1990, 2031).ToString(CultureInfo.InvariantCulture);

            case TableColumnValueKind.Guid:
            case TableColumnValueKind.SqliteGuid:
                description = "隨機 UUID";
                return random => NewGuid(random).ToString("D");

            case TableColumnValueKind.String:
                return BuildStringGenerator(column, name, unique, counterStart, out description);

            case TableColumnValueKind.Json:
            {
                var counter = 1;
                description = "範例 JSON 物件";
                return _ =>
                {
                    var id = counter++;
                    return "{\"id\":" + id.ToString(CultureInfo.InvariantCulture) + ",\"label\":\"sample " + id.ToString(CultureInfo.InvariantCulture) + "\"}";
                };
            }

            case TableColumnValueKind.Xml:
            {
                var counter = 1;
                description = "範例 XML 元素";
                return _ => "<item id=\"" + (counter++).ToString(CultureInfo.InvariantCulture) + "\" />";
            }

            case TableColumnValueKind.Binary:
            {
                var length = column.RequiredBinaryLength ?? Math.Clamp(column.MaximumStringLengthInBytes ?? 8, 1, 8);
                var counter = (long)counterStart;
                description = $"隨機位元組（{length} bytes）";
                return random =>
                {
                    var bytes = new byte[length];
                    random.NextBytes(bytes);
                    if (unique)
                    {
                        var value = counter++;
                        for (var index = length - 1; index >= 0 && index >= length - 8; index--)
                        {
                            bytes[index] = (byte)(value & 0xFF);
                            value >>= 8;
                        }
                    }

                    return "0x" + Convert.ToHexString(bytes);
                };
            }

            case TableColumnValueKind.NetworkAddress:
            {
                if (!Accepts(column, "192.0.2.1"))
                {
                    description = string.Empty;
                    return null;
                }

                var counter = counterStart;
                description = "IPv4 位址 10.x.x.x";
                return random =>
                {
                    var value = unique ? counter++ : random.Next(1 << 24);
                    return $"10.{(value >> 16) & 0xFF}.{(value >> 8) & 0xFF}.{value & 0xFF}";
                };
            }

            default:
                description = string.Empty;
                return null;
        }
    }

    private static Func<Random, string?>? BuildStringGenerator(TableColumnInfo column, string name, bool unique, int counterStart, out string description)
    {
        if (column.AllowedStringValues is { Count: > 0 } allowed)
        {
            description = "從 ENUM 成員隨機挑選";
            return random => allowed[random.Next(allowed.Count)];
        }

        if (column.StringSetMembers is { Count: > 0 } members)
        {
            description = "從 SET 成員隨機挑選";
            return random => members[random.Next(members.Count)];
        }

        var maximumLength = column.MaximumStringLengthInCharacters ??
                            (column.MaximumStringLengthInBytes is { } bytes ? Math.Max(1, bytes / 4) : (int?)null);
        Func<Random, int, string> produce;
        if (name.Contains("email") || name.Contains("mail"))
        {
            description = "Email（user{n}@example.com）";
            produce = (_, n) => $"user{n}@example.com";
        }
        else if (name.Contains("firstname") || name.Contains("givenname"))
        {
            description = "名字";
            produce = (random, _) => FirstNames[random.Next(FirstNames.Length)];
        }
        else if (name.Contains("lastname") || name.Contains("surname") || name.Contains("familyname"))
        {
            description = "姓氏";
            produce = (random, _) => LastNames[random.Next(LastNames.Length)];
        }
        else if (name.Contains("username") || name.Contains("login") || name.Contains("account"))
        {
            description = "使用者名稱";
            produce = (random, n) => $"{FirstNames[random.Next(FirstNames.Length)].ToLowerInvariant()}{n}";
        }
        else if (name.Contains("phone") || name.Contains("mobile") || name.Contains("tel"))
        {
            description = "電話號碼";
            produce = (random, _) => "09" + random.NextInt64(0, 100_000_000).ToString("D8", CultureInfo.InvariantCulture);
        }
        else if (name.Contains("city"))
        {
            description = "城市";
            produce = (random, _) => Cities[random.Next(Cities.Length)];
        }
        else if (name.Contains("country"))
        {
            description = "國家";
            produce = (random, _) => Countries[random.Next(Countries.Length)];
        }
        else if (name.Contains("address") || name.Contains("street"))
        {
            description = "地址";
            produce = (random, _) => $"{random.Next(1, 999)} {Streets[random.Next(Streets.Length)]} St.";
        }
        else if (name.Contains("url") || name.Contains("website") || name.Contains("link"))
        {
            description = "網址";
            produce = (_, n) => $"https://example.com/item/{n}";
        }
        else if (name.Contains("name"))
        {
            description = "姓名";
            produce = (random, _) => $"{FirstNames[random.Next(FirstNames.Length)]} {LastNames[random.Next(LastNames.Length)]}";
        }
        else if (name.Contains("status") || name.Contains("state"))
        {
            description = "狀態文字";
            produce = (random, _) => Statuses[random.Next(Statuses.Length)];
        }
        else if (name.Contains("description") || name.Contains("comment") || name.Contains("note") ||
                 name.Contains("remark") || name.Contains("content") || name.Contains("body") || name.Contains("text"))
        {
            description = "隨機句子";
            produce = (random, _) =>
            {
                var words = Enumerable.Range(0, random.Next(4, 12)).Select(_ => Words[random.Next(Words.Length)]).ToArray();
                words[0] = char.ToUpperInvariant(words[0][0]) + words[0][1..];
                return string.Join(' ', words) + ".";
            };
        }
        else if (name.Contains("title") || name.Contains("subject"))
        {
            description = "標題";
            produce = (_, n) => $"Sample title {n}";
        }
        else if (name.Contains("code") || name.Contains("sku") || name.Contains("no") && name.Length <= 8)
        {
            description = "代碼";
            produce = (_, n) => $"C{n:D6}";
        }
        else
        {
            description = $"文字（{column.Name}_{{n}}）";
            produce = (_, n) => $"{column.Name}_{n}";
        }

        if (unique)
        {
            description += "，不重複";
        }

        var counter = counterStart + 1;
        return random =>
        {
            var n = counter++;
            var text = produce(random, n);
            if (unique && !text.Contains(n.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            {
                text += "-" + n.ToString(CultureInfo.InvariantCulture);
            }

            text = text.TrimEnd();
            if (maximumLength is { } limit && text.Length > limit)
            {
                if (!unique)
                {
                    return text[..limit].TrimEnd();
                }

                // Keep the distinguishing number: shorten the prefix, then fall back to the number alone.
                var suffix = n.ToString(CultureInfo.InvariantCulture);
                if (suffix.Length > limit)
                {
                    throw new GenerationException($"欄位 {column.Name} 最多 {limit} 個字元，無法再產生不重複的值。");
                }

                text = text[..(limit - suffix.Length)] + suffix;
            }

            return text;
        };
    }

    // ---------------------------------------------------------------- Explicit rules

    internal static Func<Random, string?> BuildRuleGenerator(TableColumnInfo column, DataGeneratorRule rule)
    {
        var text = rule.Text ?? string.Empty;
        switch (rule.Kind)
        {
            case DataGeneratorRuleKind.Null:
                return _ => null;
            case DataGeneratorRuleKind.Fixed:
                return _ => text;
            case DataGeneratorRuleKind.Dictionary:
            {
                var dictionary = rule.Dictionary ?? throw new GenerationException($"欄位 {column.Name} 使用的字典「{text}」不存在或無法讀取。");
                return random => dictionary.Pick(random);
            }

            case DataGeneratorRuleKind.List:
            {
                var values = text.Split('|').Select(value => value.Trim()).Where(value => value.Length > 0).ToArray();
                if (values.Length == 0)
                {
                    throw new GenerationException($"欄位 {column.Name} 的清單至少要有一個值（以 | 分隔）。");
                }

                return random => values[random.Next(values.Length)];
            }

            case DataGeneratorRuleKind.Sequence:
            {
                var parts = text.Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length is < 1 or > 2 || parts[0].Length == 0)
                {
                    throw new GenerationException($"欄位 {column.Name} 的序列格式應為「起始值[,間隔]」，例如 1000,10 或 2024-01-01,1。");
                }

                if (!TryParseNumber(parts.Length > 1 ? parts[1] : "1", out var step, out var stepScale))
                {
                    throw new GenerationException($"欄位 {column.Name} 的序列間隔必須是數字。");
                }

                if (TryParseNumber(parts[0], out var start, out var startScale))
                {
                    var format = "F" + Math.Max(startScale, stepScale).ToString(CultureInfo.InvariantCulture);
                    var current = start;
                    return _ =>
                    {
                        var value = current;
                        current += step;
                        return value.ToString(format, CultureInfo.InvariantCulture);
                    };
                }

                if (TryParseDate(parts[0], out var date, out var dateFormat))
                {
                    var index = 0m;
                    return _ => date.AddDays((double)(step * index++)).ToString(dateFormat, CultureInfo.InvariantCulture);
                }

                throw new GenerationException($"欄位 {column.Name} 的序列起始值必須是數字或 yyyy-MM-dd[ HH:mm:ss] 日期。");
            }

            case DataGeneratorRuleKind.Range:
            {
                var parts = text.Split("..", StringSplitOptions.TrimEntries);
                if (parts.Length != 2)
                {
                    throw new GenerationException($"欄位 {column.Name} 的範圍格式應為「最小值..最大值」，例如 1..100 或 2024-01-01..2024-12-31。");
                }

                if (TryParseNumber(parts[0], out var low, out var lowScale) && TryParseNumber(parts[1], out var high, out var highScale))
                {
                    if (low > high)
                    {
                        throw new GenerationException($"欄位 {column.Name} 的範圍最小值大於最大值。");
                    }

                    var scale = Math.Min(Math.Max(lowScale, highScale), 6);
                    var factor = Pow10(scale);
                    var steps = (long)Math.Min((high - low) * factor, long.MaxValue - 1);
                    var format = "F" + scale.ToString(CultureInfo.InvariantCulture);
                    return random => (low + random.NextInt64(0, steps + 1) / factor).ToString(format, CultureInfo.InvariantCulture);
                }

                if (TryParseDate(parts[0], out var from, out var fromFormat) && TryParseDate(parts[1], out var to, out var toFormat))
                {
                    if (from > to)
                    {
                        throw new GenerationException($"欄位 {column.Name} 的日期範圍起點晚於終點。");
                    }

                    var format = fromFormat.Length >= toFormat.Length ? fromFormat : toFormat;
                    var dateOnly = !format.Contains("HH", StringComparison.Ordinal);
                    var span = dateOnly ? (long)(to - from).TotalDays : (long)(to - from).TotalMinutes;
                    return random =>
                    {
                        var offset = random.NextInt64(0, span + 1);
                        return (dateOnly ? from.AddDays(offset) : from.AddMinutes(offset)).ToString(format, CultureInfo.InvariantCulture);
                    };
                }

                throw new GenerationException($"欄位 {column.Name} 的範圍兩端必須同為數字或 yyyy-MM-dd[ HH:mm:ss] 日期。");
            }

            case DataGeneratorRuleKind.Pattern:
            {
                var template = ParsePattern(column, text);
                var counter = 1;
                return random =>
                {
                    var n = counter++;
                    var builder = new StringBuilder();
                    foreach (var part in template)
                    {
                        builder.Append(part(random, n));
                    }

                    return builder.ToString();
                };
            }

            default:
                throw new GenerationException($"欄位 {column.Name} 的規則不支援。");
        }
    }

    private static List<Func<Random, int, string>> ParsePattern(TableColumnInfo column, string text)
    {
        var parts = new List<Func<Random, int, string>>();
        var literal = new StringBuilder();
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '{')
            {
                literal.Append(text[index]);
                continue;
            }

            var end = text.IndexOf('}', index);
            if (end < 0)
            {
                throw new GenerationException($"欄位 {column.Name} 的樣式有未閉合的 {{。");
            }

            if (literal.Length > 0)
            {
                var fixedText = literal.ToString();
                parts.Add((_, _) => fixedText);
                literal.Clear();
            }

            var token = text[(index + 1)..end];
            index = end;
            var colon = token.IndexOf(':');
            var tokenName = (colon < 0 ? token : token[..colon]).Trim().ToLowerInvariant();
            var argument = colon < 0 ? string.Empty : token[(colon + 1)..].Trim();
            switch (tokenName)
            {
                case "n" when argument.Length == 0:
                    parts.Add((_, n) => n.ToString(CultureInfo.InvariantCulture));
                    break;
                case "uuid" when argument.Length == 0:
                    parts.Add((random, _) => NewGuid(random).ToString("D"));
                    break;
                case "digits" or "letters" when int.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count is >= 1 and <= 64:
                    var alphabet = tokenName == "digits" ? "0123456789" : "abcdefghijklmnopqrstuvwxyz";
                    parts.Add((random, _) => new string(Enumerable.Range(0, count).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray()));
                    break;
                case "int":
                    var bounds = argument.Split('-', 2);
                    if (bounds.Length == 2 &&
                        long.TryParse(bounds[0], NumberStyles.None, CultureInfo.InvariantCulture, out var low) &&
                        long.TryParse(bounds[1], NumberStyles.None, CultureInfo.InvariantCulture, out var high) &&
                        low <= high && high < long.MaxValue)
                    {
                        parts.Add((random, _) => random.NextInt64(low, high + 1).ToString(CultureInfo.InvariantCulture));
                        break;
                    }

                    throw new GenerationException($"欄位 {column.Name} 的 {{int:最小-最大}} 格式不正確。");
                default:
                    throw new GenerationException($"欄位 {column.Name} 的樣式含不支援的標記 {{{token}}}；可用 {{n}}、{{int:1-100}}、{{digits:4}}、{{letters:5}}、{{uuid}}。");
            }
        }

        if (literal.Length > 0)
        {
            var fixedText = literal.ToString();
            parts.Add((_, _) => fixedText);
        }

        return parts;
    }

    // ---------------------------------------------------------------- Helpers

    private static string? ProbeTemporalFormat(TableColumnInfo column)
    {
        var sample = new DateTime(2024, 1, 2, 3, 4, 0);
        return TemporalFormats.FirstOrDefault(format => Accepts(column, sample.ToString(format, CultureInfo.InvariantCulture)));
    }

    private static bool Accepts(TableColumnInfo column, string text)
    {
        try
        {
            TableCellValueConverter.ParseForDataSync(column, new TableCellInput(column.Name, TableCellInputMode.Value, text));
            return true;
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    private static bool IsIntegerTypeName(TableColumnInfo column) =>
        column.StorageDataTypeName.Contains("INT", StringComparison.OrdinalIgnoreCase);

    private static decimal Pow10(int exponent)
    {
        var value = 1m;
        for (var index = 0; index < exponent; index++)
        {
            value *= 10m;
        }

        return value;
    }

    private static bool TryParseNumber(string text, out decimal value, out int scale)
    {
        scale = 0;
        if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value))
        {
            return false;
        }

        var dot = text.IndexOf('.');
        scale = dot < 0 ? 0 : text.Length - dot - 1;
        return true;
    }

    private static bool TryParseDate(string text, out DateTime value, out string format)
    {
        foreach (var candidate in new[] { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd" })
        {
            if (DateTime.TryParseExact(text, candidate, CultureInfo.InvariantCulture, DateTimeStyles.None, out value))
            {
                format = candidate;
                return true;
            }
        }

        value = default;
        format = string.Empty;
        return false;
    }

    private static Guid NewGuid(Random random)
    {
        var bytes = new byte[16];
        random.NextBytes(bytes);
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x40);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }

    private static DataGenerationResult Failed(string message) =>
        new(Array.Empty<DataGeneratedTable>(), Array.Empty<string>(), message);

    private sealed record ColumnGenerator(TableColumnInfo Column, Func<Random, string?> Next, int NullPercent);

    private sealed record ForeignKeyLink(StructureForeignKeyInfo ForeignKey, TableState Parent, TuplePool Pool);

    private sealed class TuplePool(IReadOnlyList<TableColumnInfo> columns)
    {
        public IReadOnlyList<TableColumnInfo> Columns { get; } = columns;

        public List<string?[]> Tuples { get; } = new();
    }

    private sealed class TableState(
        DatabaseObjectInfo table,
        IReadOnlyList<TableColumnInfo> columns,
        TableStructureInfo structure,
        List<TableDataRow> existingRows,
        bool existingTruncated)
    {
        private readonly List<TuplePool> _pools = new();

        public DatabaseObjectInfo Table { get; } = table;

        public IReadOnlyList<TableColumnInfo> Columns { get; } = columns;

        public TableStructureInfo Structure { get; } = structure;

        public bool ExistingTruncated { get; } = existingTruncated;

        public int ExistingRowCount => existingRows.Count;

        public TuplePool RegisterPool(IReadOnlyList<TableColumnInfo> poolColumns)
        {
            var existing = _pools.FirstOrDefault(pool => pool.Columns.Select(column => column.Name)
                .SequenceEqual(poolColumns.Select(column => column.Name), StringComparer.OrdinalIgnoreCase));
            if (existing is not null)
            {
                return existing;
            }

            var created = new TuplePool(poolColumns);
            foreach (var row in existingRows)
            {
                var tuple = poolColumns.Select(column => row.Values[column.Ordinal] is null or DBNull
                    ? null
                    : TableCellValueConverter.Format(column, row.Values[column.Ordinal])).ToArray();
                if (tuple.All(value => value is not null))
                {
                    created.Tuples.Add(tuple);
                }
            }

            _pools.Add(created);
            return created;
        }

        /// <summary>Generated rows become pickable parents; rows whose key the database assigns are not.</summary>
        public void AddGeneratedRow(IReadOnlyDictionary<string, string?> values)
        {
            foreach (var pool in _pools)
            {
                var tuple = new string?[pool.Columns.Count];
                var complete = true;
                for (var index = 0; index < pool.Columns.Count; index++)
                {
                    if (!values.TryGetValue(pool.Columns[index].Name, out var value) || value is null)
                    {
                        complete = false;
                        break;
                    }

                    tuple[index] = value;
                }

                if (complete)
                {
                    pool.Tuples.Add(tuple);
                }
            }
        }

        public HashSet<string> ExistingTuples(string[] set)
        {
            var setColumns = set.Select(name => FindColumn(Columns, name)!).ToList();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in existingRows)
            {
                var parts = setColumns.Select(column => Canonical(column, row.Values[column.Ordinal])).ToList();
                if (!parts.Contains(NullMarker))
                {
                    seen.Add(string.Join('\u0001', parts));
                }
            }

            return seen;
        }

        public decimal MaximumNumber(TableColumnInfo column)
        {
            var maximum = 0m;
            foreach (var row in existingRows)
            {
                var value = row.Values[column.Ordinal];
                if (value is null or DBNull)
                {
                    continue;
                }

                if (decimal.TryParse(TableCellValueConverter.Format(column, value), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
                    number > maximum)
                {
                    maximum = number;
                }
            }

            return maximum;
        }
    }

    private sealed class GenerationException(string message) : Exception(message);
}
