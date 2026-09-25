using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MySqlPunk.Core.Models;

namespace MySqlPunk.Core.Services;

public sealed record SchemaSyncScript(
    DatabaseProviderKind Provider,
    string Text,
    IReadOnlyList<string> Statements,
    IReadOnlyList<string> ManualItems,
    IReadOnlyList<string> DestructiveItems)
{
    public string Summary =>
        $"{Statements.Count} 個可執行語句、{DestructiveItems.Count} 個破壞性變更（已註解）、{ManualItems.Count} 個需手動處理";
}

/// <summary>
/// Turns a same-provider <see cref="SchemaComparisonResult"/> into a preview script that would make the target
/// look like the source. It is text only: nothing is executed. Additive changes become statements; anything that
/// drops objects or columns is emitted as a comment so data can never be lost by running the script as-is, and
/// changes the provider cannot express safely are listed for manual handling.
/// </summary>
public static class SchemaSyncScriptService
{
    public static bool CanGenerate(SchemaComparisonResult result, out string reason)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Source.Provider is not { } source || result.Target.Provider is not { } target)
        {
            reason = "比較結果缺少資料庫類型，無法產生同步 SQL。";
            return false;
        }

        if (source != target)
        {
            reason = "來源與目標是不同類型的資料庫；型別與語法無法可靠轉換，只提供差異報告。";
            return false;
        }

        if (result.Differences.Count == 0)
        {
            reason = "結構一致，沒有需要同步的變更。";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public static SchemaSyncScript Generate(SchemaComparisonResult result, DateTimeOffset? generatedAt = null)
    {
        if (!CanGenerate(result, out var reason))
        {
            throw new InvalidOperationException(reason);
        }

        var provider = result.Target.Provider!.Value;
        var builder = new ScriptBuilder(provider, result);
        builder.Build();

        var text = new StringBuilder();
        var timestamp = (generatedAt ?? DateTimeOffset.Now).ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
        text.AppendLine("-- mySQLPunk 結構同步預覽（不會自動執行）");
        text.AppendLine($"-- 產生時間：{timestamp}");
        text.AppendLine($"-- 來源：{OneLine(result.Source.ConnectionName)}／{OneLine(result.Source.Database)}");
        text.AppendLine($"-- 目標：{OneLine(result.Target.ConnectionName)}／{OneLine(result.Target.Database)}（請在「目標」資料庫執行）");
        text.AppendLine("-- 會刪除物件或欄位的變更一律以註解呈現；請先備份並逐項確認後再手動取消註解。");
        text.AppendLine();
        foreach (var section in builder.Sections.Where(section => section.Lines.Count > 0))
        {
            text.AppendLine($"-- ===== {section.Title} =====");
            foreach (var line in section.Lines)
            {
                text.AppendLine(line);
            }

            text.AppendLine();
        }

        return new SchemaSyncScript(provider, text.ToString().TrimEnd() + Environment.NewLine, builder.Statements, builder.Manual, builder.Destructive);
    }

    private static string OneLine(string value) => Regex.Replace(value, @"[\r\n\u0085\u2028\u2029]+", " ");

    private sealed class ScriptBuilder
    {
        private readonly DatabaseProviderKind _provider;
        private readonly SchemaComparisonResult _result;
        private readonly Section _createTables = new("建立資料表");
        private readonly Section _addColumns = new("新增欄位");
        private readonly Section _alterColumns = new("修改欄位");
        private readonly Section _indexes = new("索引");
        private readonly Section _foreignKeys = new("外鍵");
        private readonly Section _views = new("檢視表");
        private readonly Section _manual = new("需手動處理");
        private readonly Section _destructive = new("破壞性變更（已註解，預設不執行）");

        public ScriptBuilder(DatabaseProviderKind provider, SchemaComparisonResult result)
        {
            _provider = provider;
            _result = result;
        }

        public List<string> Statements { get; } = new();

        public List<string> Manual { get; } = new();

        public List<string> Destructive { get; } = new();

        public IEnumerable<Section> Sections => new[] { _createTables, _addColumns, _alterColumns, _indexes, _foreignKeys, _views, _manual, _destructive };

        public void Build()
        {
            foreach (var group in _result.Differences.GroupBy(difference => difference.ObjectName, StringComparer.OrdinalIgnoreCase))
            {
                var differences = group.ToList();
                var objectDifference = differences.FirstOrDefault(difference => difference.Area == SchemaDifferenceArea.Object);
                if (objectDifference is { Kind: SchemaDifferenceKind.OnlyInSource })
                {
                    CreateObject(FindSource(group.Key));
                    continue;
                }

                if (objectDifference is { Kind: SchemaDifferenceKind.OnlyInTarget })
                {
                    var entry = FindTarget(group.Key);
                    var keyword = entry.Object.Kind == DatabaseObjectKind.View ? "VIEW" : "TABLE";
                    AddDestructive($"DROP {keyword} {TargetName(entry.Object)}", $"刪除只在目標的{(entry.Object.Kind == DatabaseObjectKind.View ? "檢視表" : "資料表")} {entry.Object.DisplayName}");
                    continue;
                }

                if (objectDifference is { Kind: SchemaDifferenceKind.Changed })
                {
                    AddManual($"{group.Key}：來源與目標的物件類型不同（{objectDifference.SourceValue} ↔ {objectDifference.TargetValue}），請手動重建。");
                    continue;
                }

                var source = FindSource(group.Key);
                var target = FindMatchingTarget(source);
                if (source.Structure is null || target?.Structure is null)
                {
                    continue;
                }

                if (source.Object.Kind == DatabaseObjectKind.View)
                {
                    RecreateView(source, target);
                    continue;
                }

                foreach (var difference in differences)
                {
                    switch (difference.Area)
                    {
                        case SchemaDifferenceArea.Column:
                            HandleColumn(difference, source, target);
                            break;
                        case SchemaDifferenceArea.Index:
                            HandleIndex(difference, source, target);
                            break;
                        case SchemaDifferenceArea.ForeignKey:
                            HandleForeignKey(difference, source, target);
                            break;
                    }
                }
            }
        }

        private void CreateObject(DataDictionaryEntry source)
        {
            if (source.Structure is null)
            {
                AddManual($"{source.Object.DisplayName}：來源結構無法讀取，無法產生建立語法。");
                return;
            }

            if (source.Object.Kind == DatabaseObjectKind.View)
            {
                AddView(source, _views);
                return;
            }

            var structure = source.Structure;
            if (_provider == DatabaseProviderKind.Sqlite &&
                structure.Definition.TrimStart().StartsWith("CREATE TABLE", StringComparison.OrdinalIgnoreCase))
            {
                // sqlite_schema.sql is exact; inline REFERENCES are not checked at creation time.
                AddStatement(_createTables, structure.Definition.Trim().TrimEnd(';'));
                foreach (var index in structure.Indexes.Where(index => !index.IsPrimaryKey && !IsConstraintBacked(index) &&
                                                                       index.Definition.Length > 0))
                {
                    CreateIndex(source.Object, index);
                }

                return;
            }

            if (_provider == DatabaseProviderKind.MySql &&
                structure.Definition.TrimStart().StartsWith("CREATE TABLE", StringComparison.OrdinalIgnoreCase))
            {
                // SHOW CREATE TABLE is exact. Foreign keys are moved to the foreign-key section so referenced
                // tables created later in the script exist first, and AUTO_INCREMENT=N is dropped so the target
                // keeps its own counter.
                var createLines = structure.Definition.Trim().TrimEnd(';').Split('\n').Select(line => line.TrimEnd('\r')).ToList();
                createLines.RemoveAll(line => line.TrimStart().StartsWith("CONSTRAINT ", StringComparison.Ordinal) &&
                                        line.Contains(" FOREIGN KEY ", StringComparison.Ordinal));
                var closing = createLines.FindLastIndex(line => line.StartsWith(")", StringComparison.Ordinal));
                if (closing > 0)
                {
                    createLines[closing - 1] = createLines[closing - 1].TrimEnd(',');
                    createLines[closing] = Regex.Replace(createLines[closing], @"\s*AUTO_INCREMENT=\d+", string.Empty);
                }

                AddStatement(_createTables, string.Join("\n", createLines));
                foreach (var foreignKey in structure.ForeignKeys)
                {
                    AddForeignKey(source.Object, foreignKey);
                }

                return;
            }

            var lines = new List<string>();
            foreach (var column in structure.Columns)
            {
                var definition = ColumnDefinition(structure, column, out var problem);
                if (definition is null)
                {
                    AddManual($"{source.Object.DisplayName}.{column.Name}：{problem}");
                    return;
                }

                lines.Add("    " + definition);
            }

            var primary = structure.Indexes.FirstOrDefault(index => index.IsPrimaryKey);
            if (primary is not null && primary.Columns.Count > 0)
            {
                lines.Add($"    PRIMARY KEY ({string.Join(", ", primary.Columns.Select(IndexColumn))})");
            }

            AddStatement(_createTables, $"CREATE TABLE {TargetName(source.Object)} (\n{string.Join(",\n", lines)}\n)");
            foreach (var index in structure.Indexes.Where(index => !index.IsPrimaryKey))
            {
                CreateIndex(source.Object, index);
            }

            foreach (var foreignKey in structure.ForeignKeys)
            {
                AddForeignKey(source.Object, foreignKey);
            }
        }

        private void HandleColumn(SchemaDifference difference, DataDictionaryEntry source, DataDictionaryEntry target)
        {
            var table = TargetName(target.Object);
            switch (difference.Kind)
            {
                case SchemaDifferenceKind.OnlyInTarget:
                    if (_provider == DatabaseProviderKind.Sqlite)
                    {
                        AddDestructive($"ALTER TABLE {table} DROP COLUMN {Q(difference.ItemName)}", $"刪除只在目標的欄位 {target.Object.DisplayName}.{difference.ItemName}（SQLite 3.35+，且欄位不可被索引或約束引用）");
                    }
                    else
                    {
                        AddDestructive($"ALTER TABLE {table} DROP COLUMN {Q(difference.ItemName)}", $"刪除只在目標的欄位 {target.Object.DisplayName}.{difference.ItemName}");
                    }

                    return;
                case SchemaDifferenceKind.OnlyInSource:
                {
                    var column = source.Structure!.Columns.First(item => item.Name.Equals(difference.ItemName, StringComparison.OrdinalIgnoreCase));
                    if (column.IsPrimaryKey)
                    {
                        AddManual($"{source.Object.DisplayName}.{column.Name}：新增主鍵欄位需要重建主鍵，請手動處理。");
                        return;
                    }

                    var definition = ColumnDefinition(source.Structure, column, out var problem);
                    if (definition is null)
                    {
                        AddManual($"{source.Object.DisplayName}.{column.Name}：{problem}");
                        return;
                    }

                    if (!column.IsNullable && NormalizeDefault(column.DefaultValue).Length == 0 && column.Extra.Length == 0)
                    {
                        AddManual($"{source.Object.DisplayName}.{column.Name}：NOT NULL 且沒有預設值，既有資料列無法直接加入；已產生語句但請先確認目標沒有資料或補上預設值。");
                    }

                    AddStatement(_addColumns, _provider switch
                    {
                        DatabaseProviderKind.SqlServer => $"ALTER TABLE {table} ADD {definition}",
                        _ => $"ALTER TABLE {table} ADD COLUMN {definition}"
                    });
                    return;
                }

                default:
                    AlterColumn(source, target, difference.ItemName);
                    return;
            }
        }

        private void AlterColumn(DataDictionaryEntry source, DataDictionaryEntry target, string columnName)
        {
            var wanted = source.Structure!.Columns.First(item => item.Name.Equals(columnName, StringComparison.OrdinalIgnoreCase));
            var current = target.Structure!.Columns.First(item => item.Name.Equals(columnName, StringComparison.OrdinalIgnoreCase));
            var name = $"{source.Object.DisplayName}.{wanted.Name}";
            var table = TargetName(target.Object);
            if (wanted.IsPrimaryKey != current.IsPrimaryKey)
            {
                AddManual($"{name}：主鍵成員不同，請手動調整主鍵。");
            }

            if (!string.Equals(wanted.Extra.Trim(), current.Extra.Trim(), StringComparison.OrdinalIgnoreCase) &&
                _provider != DatabaseProviderKind.MySql)
            {
                AddManual($"{name}：identity／generated／computed 設定不同（{Describe(current.Extra)} → {Describe(wanted.Extra)}），請手動重建欄位。");
            }

            var typeChanged = !SameType(wanted.DataType, current.DataType);
            var nullChanged = wanted.IsNullable != current.IsNullable;
            var defaultChanged = !string.Equals(NormalizeDefault(wanted.DefaultValue), NormalizeDefault(current.DefaultValue), StringComparison.Ordinal);
            switch (_provider)
            {
                case DatabaseProviderKind.MySql:
                {
                    var definition = ColumnDefinition(source.Structure, wanted, out var problem);
                    if (definition is null)
                    {
                        AddManual($"{name}：{problem}");
                        return;
                    }

                    if (typeChanged)
                    {
                        AddManual($"{name}：型別 {current.DataType} → {wanted.DataType} 可能截斷既有資料，執行前請先確認。");
                    }

                    AddStatement(_alterColumns, $"ALTER TABLE {table} MODIFY COLUMN {definition}");
                    return;
                }

                case DatabaseProviderKind.PostgreSql:
                    if (typeChanged)
                    {
                        AddManual($"{name}：型別 {current.DataType} → {wanted.DataType}；若無法隱式轉換需補 USING 子句。");
                        AddStatement(_alterColumns, $"ALTER TABLE {table} ALTER COLUMN {Q(wanted.Name)} TYPE {wanted.DataType}");
                    }

                    if (nullChanged)
                    {
                        AddStatement(_alterColumns, $"ALTER TABLE {table} ALTER COLUMN {Q(wanted.Name)} {(wanted.IsNullable ? "DROP NOT NULL" : "SET NOT NULL")}");
                    }

                    if (defaultChanged && !wanted.Extra.Contains("GENERATED", StringComparison.OrdinalIgnoreCase))
                    {
                        AddStatement(_alterColumns, NormalizeDefault(wanted.DefaultValue).Length == 0
                            ? $"ALTER TABLE {table} ALTER COLUMN {Q(wanted.Name)} DROP DEFAULT"
                            : $"ALTER TABLE {table} ALTER COLUMN {Q(wanted.Name)} SET DEFAULT {wanted.DefaultValue}");
                    }

                    return;
                case DatabaseProviderKind.SqlServer:
                    if (typeChanged || nullChanged)
                    {
                        if (typeChanged)
                        {
                            AddManual($"{name}：型別 {current.DataType} → {wanted.DataType} 可能截斷既有資料；若欄位被索引或約束引用需先移除。");
                        }

                        var collation = wanted.Collation.Length > 0 ? $" COLLATE {wanted.Collation}" : string.Empty;
                        AddStatement(_alterColumns, $"ALTER TABLE {table} ALTER COLUMN {Q(wanted.Name)} {wanted.DataType}{collation} {(wanted.IsNullable ? "NULL" : "NOT NULL")}");
                    }

                    if (defaultChanged)
                    {
                        AddManual($"{name}：預設值 {Describe(current.DefaultValue)} → {Describe(wanted.DefaultValue)}；SQL Server 預設值是具名約束，請先刪除舊的 DEFAULT 約束再新增。");
                    }

                    return;
                default:
                    AddManual($"{name}：SQLite 不支援修改欄位定義（{DescribeColumnPair(current, wanted)}），需以新表複製資料後重建。");
                    return;
            }
        }

        private void HandleIndex(SchemaDifference difference, DataDictionaryEntry source, DataDictionaryEntry target)
        {
            if (difference.ItemName == "PRIMARY KEY")
            {
                AddManual($"{source.Object.DisplayName}：主鍵不同（{Describe(difference.TargetValue)} → {Describe(difference.SourceValue)}），請手動重建主鍵。");
                return;
            }

            var wanted = source.Structure!.Indexes.FirstOrDefault(index => index.Name.Equals(difference.ItemName, StringComparison.OrdinalIgnoreCase));
            var current = target.Structure!.Indexes.FirstOrDefault(index => index.Name.Equals(difference.ItemName, StringComparison.OrdinalIgnoreCase));
            switch (difference.Kind)
            {
                case SchemaDifferenceKind.OnlyInTarget when current is not null:
                    AddDestructive(DropIndex(target.Object, current), $"刪除只在目標的索引 {target.Object.DisplayName}.{current.Name}");
                    return;
                case SchemaDifferenceKind.OnlyInSource when wanted is not null:
                    CreateIndex(source.Object, wanted);
                    return;
                case SchemaDifferenceKind.Changed when wanted is not null && current is not null:
                    if (IsConstraintBacked(current) || IsConstraintBacked(wanted))
                    {
                        AddManual($"{source.Object.DisplayName}.{wanted.Name}：由 UNIQUE 約束產生的索引不同，請手動調整約束。");
                        return;
                    }

                    // Rebuilding an index loses no data, so the drop is emitted as a real statement.
                    AddStatement(_indexes, DropIndex(target.Object, current));
                    CreateIndex(source.Object, wanted);
                    return;
            }
        }

        private void HandleForeignKey(SchemaDifference difference, DataDictionaryEntry source, DataDictionaryEntry target)
        {
            var key = difference.ItemName;
            var wanted = FindForeignKey(source.Structure!, key);
            var current = FindForeignKey(target.Structure!, key);
            if (_provider == DatabaseProviderKind.Sqlite)
            {
                AddManual($"{source.Object.DisplayName} {key}：SQLite 不支援新增或修改外鍵，需重建資料表。");
                return;
            }

            switch (difference.Kind)
            {
                case SchemaDifferenceKind.OnlyInTarget when current is not null:
                    AddDestructive(DropForeignKey(target.Object, current), $"刪除只在目標的外鍵 {target.Object.DisplayName}.{current.Name}");
                    return;
                case SchemaDifferenceKind.OnlyInSource when wanted is not null:
                    AddForeignKey(source.Object, wanted);
                    return;
                case SchemaDifferenceKind.Changed when wanted is not null && current is not null:
                    // Dropping a constraint removes no rows; re-adding it validates existing data.
                    AddStatement(_foreignKeys, DropForeignKey(target.Object, current));
                    AddForeignKey(source.Object, wanted);
                    return;
            }
        }

        private void RecreateView(DataDictionaryEntry source, DataDictionaryEntry target)
        {
            // Views hold no data, so replacing one is safe; drop first because column lists changed.
            AddStatement(_views, $"DROP VIEW {TargetName(target.Object)}");
            AddView(source, _views);
        }

        private void AddView(DataDictionaryEntry source, Section section)
        {
            var definition = source.Structure!.Definition.Trim().TrimEnd(';');
            if (definition.Length == 0)
            {
                AddManual($"{source.Object.DisplayName}：來源沒有提供檢視表定義，請手動建立。");
                return;
            }

            if (definition.StartsWith("CREATE", StringComparison.OrdinalIgnoreCase))
            {
                // SQL Server sql_modules／SQLite sqlite_schema already hold the full statement.
                AddStatement(section, definition);
                return;
            }

            if (_provider == DatabaseProviderKind.MySql)
            {
                // information_schema.VIEWS qualifies tables with the source database; drop it so the view binds
                // to the target database.
                definition = definition.Replace(Q(_result.Source.Database) + ".", string.Empty, StringComparison.Ordinal);
            }

            AddStatement(section, $"CREATE VIEW {TargetName(source.Object)} AS\n{definition}");
        }

        private void CreateIndex(DatabaseObjectInfo table, StructureIndexInfo index)
        {
            if (IsConstraintBacked(index))
            {
                AddManual($"{table.DisplayName}.{index.Name}：由 UNIQUE 約束產生的索引，請以 ALTER TABLE ... ADD UNIQUE 手動建立。");
                return;
            }

            if (_provider == DatabaseProviderKind.MySql && index.Columns.Any(column => column.StartsWith("(expression)", StringComparison.Ordinal)))
            {
                AddManual($"{table.DisplayName}.{index.Name}：函式索引的運算式無法從 information_schema 取得，請手動建立。");
                return;
            }

            switch (_provider)
            {
                case DatabaseProviderKind.PostgreSql or DatabaseProviderKind.Sqlite
                    when index.Definition.TrimStart().StartsWith("CREATE", StringComparison.OrdinalIgnoreCase):
                    AddStatement(_indexes, index.Definition.Trim().TrimEnd(';'));
                    return;
                case DatabaseProviderKind.MySql:
                {
                    var kind = index.IndexType.ToUpperInvariant() switch
                    {
                        "FULLTEXT" => "FULLTEXT ",
                        "SPATIAL" => "SPATIAL ",
                        _ => index.IsUnique ? "UNIQUE " : string.Empty
                    };
                    AddStatement(_indexes, $"CREATE {kind}INDEX {Q(index.Name)} ON {TargetName(table)} ({string.Join(", ", index.Columns.Select(IndexColumn))})");
                    return;
                }

                case DatabaseProviderKind.SqlServer:
                {
                    var clustered = index.IndexType.StartsWith("CLUSTERED", StringComparison.OrdinalIgnoreCase) ? "CLUSTERED " : "NONCLUSTERED ";
                    var include = index.Definition.StartsWith("INCLUDE (", StringComparison.Ordinal)
                        ? " INCLUDE (" + string.Join(", ", index.Definition[9..^1].Split(", ").Select(Q)) + ")"
                        : string.Empty;
                    AddStatement(_indexes, $"CREATE {(index.IsUnique ? "UNIQUE " : string.Empty)}{clustered}INDEX {Q(index.Name)} ON {TargetName(table)} ({string.Join(", ", index.Columns.Select(IndexColumn))}){include}");
                    return;
                }

                default:
                    AddManual($"{table.DisplayName}.{index.Name}：來源沒有索引定義，請手動建立。");
                    return;
            }
        }

        private void AddForeignKey(DatabaseObjectInfo table, StructureForeignKeyInfo foreignKey)
        {
            if (_provider == DatabaseProviderKind.Sqlite)
            {
                AddManual($"{table.DisplayName}.{foreignKey.Name}：SQLite 不支援新增外鍵，需重建資料表。");
                return;
            }

            var referenced = foreignKey.ReferencedTable.Contains('.')
                ? string.Join(".", foreignKey.ReferencedTable.Split('.', 2).Select(Q))
                : _provider == DatabaseProviderKind.MySql
                    ? Q(foreignKey.ReferencedTable)
                    : $"{Q(SchemaOf(table))}.{Q(foreignKey.ReferencedTable)}";
            var constraint = foreignKey.Name.Length == 0 ? string.Empty : $"CONSTRAINT {Q(foreignKey.Name)} ";
            AddStatement(_foreignKeys,
                $"ALTER TABLE {TargetName(table)} ADD {constraint}FOREIGN KEY ({string.Join(", ", foreignKey.Columns.Select(Q))}) " +
                $"REFERENCES {referenced} ({string.Join(", ", foreignKey.ReferencedColumns.Select(Q))}) " +
                $"ON UPDATE {Rule(foreignKey.OnUpdate)} ON DELETE {Rule(foreignKey.OnDelete)}");
        }

        private string DropIndex(DatabaseObjectInfo table, StructureIndexInfo index) => _provider switch
        {
            DatabaseProviderKind.MySql or DatabaseProviderKind.SqlServer => $"DROP INDEX {Q(index.Name)} ON {TargetName(table)}",
            DatabaseProviderKind.PostgreSql => $"DROP INDEX {Q(SchemaOf(table))}.{Q(index.Name)}",
            _ => $"DROP INDEX {Q(index.Name)}"
        };

        private string DropForeignKey(DatabaseObjectInfo table, StructureForeignKeyInfo foreignKey) => _provider == DatabaseProviderKind.MySql
            ? $"ALTER TABLE {TargetName(table)} DROP FOREIGN KEY {Q(foreignKey.Name)}"
            : $"ALTER TABLE {TargetName(table)} DROP CONSTRAINT {Q(foreignKey.Name)}";

        private string? ColumnDefinition(TableStructureInfo structure, StructureColumnInfo column, out string problem)
        {
            problem = string.Empty;
            if (_provider == DatabaseProviderKind.MySql)
            {
                // SHOW CREATE TABLE holds the exact column clause (charset, collation, generated expression, ...).
                var prefix = "  " + Q(column.Name) + " ";
                var line = structure.Definition.Split('\n').Select(item => item.TrimEnd('\r'))
                    .FirstOrDefault(item => item.StartsWith(prefix, StringComparison.Ordinal));
                if (line is not null)
                {
                    return line.Trim().TrimEnd(',');
                }

                problem = "找不到 SHOW CREATE TABLE 內的欄位定義。";
                return null;
            }

            var extra = column.Extra.Trim();
            if (_provider == DatabaseProviderKind.SqlServer && extra.StartsWith("COMPUTED AS ", StringComparison.Ordinal))
            {
                return $"{Q(column.Name)} AS {extra[12..]}";
            }

            if (_provider == DatabaseProviderKind.Sqlite && extra.StartsWith("GENERATED", StringComparison.Ordinal))
            {
                problem = "SQLite generated 欄位的運算式無法從 PRAGMA 取得，請手動建立。";
                return null;
            }

            var text = new StringBuilder();
            text.Append(Q(column.Name)).Append(' ').Append(column.DataType.Length == 0 ? "TEXT" : column.DataType);
            if (column.Collation.Length > 0)
            {
                text.Append(" COLLATE ").Append(_provider == DatabaseProviderKind.PostgreSql ? Q(column.Collation) : column.Collation);
            }

            if (_provider == DatabaseProviderKind.SqlServer && extra == "IDENTITY")
            {
                text.Append(" IDENTITY(1,1)");
            }
            else if (_provider == DatabaseProviderKind.PostgreSql && extra.StartsWith("GENERATED", StringComparison.Ordinal))
            {
                text.Append(' ').Append(extra);
            }

            text.Append(column.IsNullable ? " NULL" : " NOT NULL");
            if (column.DefaultValue.Trim().Length > 0 && !extra.StartsWith("GENERATED ALWAYS AS (", StringComparison.Ordinal))
            {
                text.Append(" DEFAULT ").Append(column.DefaultValue.Trim());
            }

            return text.ToString();
        }

        private string IndexColumn(string column)
        {
            var trimmed = column.Trim();
            var descending = trimmed.EndsWith(" DESC", StringComparison.OrdinalIgnoreCase);
            var name = descending ? trimmed[..^5] : trimmed;
            var prefix = Regex.Match(name, @"^(?<name>.+)\((?<length>\d+)\)$");
            var quoted = prefix.Success
                ? $"{Q(prefix.Groups["name"].Value)}({prefix.Groups["length"].Value})"
                : IsPlainIdentifier(name) ? Q(name) : name;
            return quoted + (descending ? " DESC" : string.Empty);
        }

        private static bool IsPlainIdentifier(string value) => Regex.IsMatch(value, "^[A-Za-z_][A-Za-z0-9_$]*$");

        private static bool IsConstraintBacked(StructureIndexInfo index) =>
            index.IndexType.StartsWith("UNIQUE constraint", StringComparison.Ordinal) ||
            (index.Definition.Length == 0 && index.Name.StartsWith("sqlite_autoindex_", StringComparison.Ordinal));

        private DataDictionaryEntry FindSource(string displayName) =>
            _result.SourceEntries.First(entry => entry.Object.DisplayName.Equals(displayName, StringComparison.OrdinalIgnoreCase));

        private DataDictionaryEntry FindTarget(string displayName) =>
            _result.TargetEntries.First(entry => entry.Object.DisplayName.Equals(displayName, StringComparison.OrdinalIgnoreCase));

        private DataDictionaryEntry? FindMatchingTarget(DataDictionaryEntry source)
        {
            var exact = _result.TargetEntries.FirstOrDefault(entry =>
                entry.Object.Schema.Equals(source.Object.Schema, StringComparison.OrdinalIgnoreCase) &&
                entry.Object.Name.Equals(source.Object.Name, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }

            var byName = _result.TargetEntries.Where(entry => entry.Object.Name.Equals(source.Object.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            return byName.Count == 1 ? byName[0] : null;
        }

        private static StructureForeignKeyInfo? FindForeignKey(TableStructureInfo structure, string key)
        {
            var baseKey = key.Split(" #", 2)[0];
            var ordinal = key.Contains(" #", StringComparison.Ordinal) ? int.Parse(key.Split(" #", 2)[1], CultureInfo.InvariantCulture) - 1 : 0;
            return structure.ForeignKeys
                .Where(foreignKey => ("(" + string.Join(", ", foreignKey.Columns).ToLowerInvariant() + ")").Equals(baseKey, StringComparison.OrdinalIgnoreCase))
                .Skip(ordinal)
                .FirstOrDefault();
        }

        private string SchemaOf(DatabaseObjectInfo table) => table.Schema.Length > 0
            ? table.Schema
            : _provider == DatabaseProviderKind.SqlServer ? "dbo" : "public";

        private string TargetName(DatabaseObjectInfo table) => _provider switch
        {
            DatabaseProviderKind.MySql or DatabaseProviderKind.Sqlite => Q(table.Name),
            _ => $"{Q(SchemaOf(table))}.{Q(table.Name)}"
        };

        private string Q(string identifier) => _provider switch
        {
            DatabaseProviderKind.MySql => "`" + identifier.Replace("`", "``", StringComparison.Ordinal) + "`",
            DatabaseProviderKind.SqlServer => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]",
            _ => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
        };

        private static string Rule(string rule)
        {
            var text = rule.Trim().Replace('_', ' ').ToUpperInvariant();
            return text is "" ? "NO ACTION" : text;
        }

        private static bool SameType(string left, string right) =>
            string.Equals(
                string.Join(' ', left.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)),
                string.Join(' ', right.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)),
                StringComparison.Ordinal);

        private static string NormalizeDefault(string value)
        {
            var text = value.Trim();
            while (text.Length >= 2 && text[0] == '(' && text[^1] == ')')
            {
                text = text[1..^1].Trim();
            }

            return text;
        }

        private static string Describe(string value) => value.Trim().Length == 0 ? "（無）" : value.Trim();

        private static string DescribeColumnPair(StructureColumnInfo current, StructureColumnInfo wanted) =>
            $"{SchemaComparisonService.DescribeColumn(current)} → {SchemaComparisonService.DescribeColumn(wanted)}";

        private void AddStatement(Section section, string statement)
        {
            Statements.Add(statement);
            section.Lines.Add(statement + ";");
            if (_provider == DatabaseProviderKind.SqlServer)
            {
                // CREATE VIEW must start its own batch in SSMS／sqlcmd.
                section.Lines.Add("GO");
            }
        }

        private void AddDestructive(string statement, string description)
        {
            // Everything written after "-- " must stay on one line, or a crafted object name could escape the comment.
            description = SingleLine(description);
            Destructive.Add(description);
            _destructive.Lines.Add($"-- {description}");
            foreach (var line in statement.Split('\n'))
            {
                _destructive.Lines.Add("-- " + line.TrimEnd('\r') + (line == statement.Split('\n')[^1] ? ";" : string.Empty));
            }
        }

        private void AddManual(string description)
        {
            description = SingleLine(description);
            Manual.Add(description);
            _manual.Lines.Add("-- " + description);
        }

        private static string SingleLine(string value) =>
            Regex.Replace(value, @"[\r\n\u0085\u2028\u2029]+", " ");
    }

    private sealed record Section(string Title)
    {
        public List<string> Lines { get; } = new();
    }
}
