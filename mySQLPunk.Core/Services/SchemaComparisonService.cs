using System.Globalization;
using System.Net;
using System.Text;
using MySqlPunk.Core.Models;

namespace MySqlPunk.Core.Services;

public enum SchemaDifferenceKind
{
    OnlyInSource,
    OnlyInTarget,
    Changed
}

public enum SchemaDifferenceArea
{
    Object,
    Column,
    Index,
    ForeignKey
}

public sealed record SchemaDifference(
    string ObjectName,
    DatabaseObjectKind ObjectKind,
    SchemaDifferenceArea Area,
    string ItemName,
    SchemaDifferenceKind Kind,
    string SourceValue,
    string TargetValue)
{
    public string AreaText => Area switch
    {
        SchemaDifferenceArea.Object => ObjectKind == DatabaseObjectKind.View ? "檢視表" : "資料表",
        SchemaDifferenceArea.Column => "欄位",
        SchemaDifferenceArea.Index => "索引",
        _ => "外鍵"
    };

    public string KindText => Kind switch
    {
        SchemaDifferenceKind.OnlyInSource => "只在來源",
        SchemaDifferenceKind.OnlyInTarget => "只在目標",
        _ => "不同"
    };
}

public sealed record SchemaComparisonSide(string ConnectionName, string ProviderName, string Database)
{
    /// <summary>Provider of this side; required for generating a synchronization script.</summary>
    public DatabaseProviderKind? Provider { get; init; }
}

public sealed class SchemaComparisonResult
{
    public required SchemaComparisonSide Source { get; init; }

    public required SchemaComparisonSide Target { get; init; }

    public required IReadOnlyList<SchemaDifference> Differences { get; init; }

    public int IdenticalObjects { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public IReadOnlyList<DataDictionaryEntry> SourceEntries { get; init; } = Array.Empty<DataDictionaryEntry>();

    public IReadOnlyList<DataDictionaryEntry> TargetEntries { get; init; } = Array.Empty<DataDictionaryEntry>();

    public int OnlyInSourceObjects => Differences.Count(d => d.Area == SchemaDifferenceArea.Object && d.Kind == SchemaDifferenceKind.OnlyInSource);

    public int OnlyInTargetObjects => Differences.Count(d => d.Area == SchemaDifferenceArea.Object && d.Kind == SchemaDifferenceKind.OnlyInTarget);

    public int ChangedObjects => Differences
        .Where(d => d.Area != SchemaDifferenceArea.Object)
        .Select(d => d.ObjectName)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Count();

    public string Summary =>
        Differences.Count == 0
            ? $"結構一致（{IdenticalObjects} 個物件）"
            : $"{Differences.Count} 項差異：只在來源 {OnlyInSourceObjects}、只在目標 {OnlyInTargetObjects}、內容不同 {ChangedObjects} 個物件，一致 {IdenticalObjects} 個";
}

/// <summary>
/// Read-only structural diff between two databases, built from <see cref="TableStructureInfo"/> snapshots.
/// Nothing is generated or executed against either side; the result only describes differences.
/// </summary>
public static class SchemaComparisonService
{
    public static SchemaComparisonResult Compare(
        SchemaComparisonSide sourceSide,
        IReadOnlyList<DataDictionaryEntry> source,
        SchemaComparisonSide targetSide,
        IReadOnlyList<DataDictionaryEntry> target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        var differences = new List<SchemaDifference>();
        var warnings = new List<string>();
        foreach (var failed in source.Where(entry => entry.Error is not null))
        {
            warnings.Add($"來源 {failed.Object.DisplayName} 無法讀取：{failed.Error}");
        }

        foreach (var failed in target.Where(entry => entry.Error is not null))
        {
            warnings.Add($"目標 {failed.Object.DisplayName} 無法讀取：{failed.Error}");
        }

        var pairs = MatchObjects(source, target);
        var identical = 0;
        foreach (var (left, right) in pairs)
        {
            if (right is null)
            {
                differences.Add(ObjectDifference(left!, SchemaDifferenceKind.OnlyInSource));
                continue;
            }

            if (left is null)
            {
                differences.Add(ObjectDifference(right, SchemaDifferenceKind.OnlyInTarget));
                continue;
            }

            if (left.Structure is null || right.Structure is null)
            {
                // Already reported as a warning; never claim equality for an object we could not read.
                continue;
            }

            var name = left.Object.DisplayName;
            var before = differences.Count;
            if (left.Object.Kind != right.Object.Kind)
            {
                differences.Add(new SchemaDifference(name, left.Object.Kind, SchemaDifferenceArea.Object, "類型",
                    SchemaDifferenceKind.Changed, KindName(left.Object.Kind), KindName(right.Object.Kind)));
            }

            CompareColumns(name, left.Object.Kind, left.Structure, right.Structure, differences);
            if (left.Object.Kind == DatabaseObjectKind.Table && right.Object.Kind == DatabaseObjectKind.Table)
            {
                CompareIndexes(name, left.Structure, right.Structure, differences);
                CompareForeignKeys(name, left.Structure, right.Structure, differences);
            }

            if (differences.Count == before)
            {
                identical++;
            }
        }

        return new SchemaComparisonResult
        {
            Source = sourceSide,
            Target = targetSide,
            Differences = differences,
            IdenticalObjects = identical,
            Warnings = warnings,
            SourceEntries = source,
            TargetEntries = target
        };
    }

    /// <summary>
    /// Matches by schema + name (case-insensitive) first, then by bare name when it is unique on both sides,
    /// so dbo.orders and public.orders line up when comparing across providers.
    /// </summary>
    private static List<(DataDictionaryEntry? Left, DataDictionaryEntry? Right)> MatchObjects(
        IReadOnlyList<DataDictionaryEntry> source,
        IReadOnlyList<DataDictionaryEntry> target)
    {
        var pairs = new List<(DataDictionaryEntry?, DataDictionaryEntry?)>();
        var remainingTarget = target.ToList();
        var unmatchedSource = new List<DataDictionaryEntry>();
        foreach (var left in source)
        {
            var match = remainingTarget.FirstOrDefault(right =>
                string.Equals(right.Object.Schema, left.Object.Schema, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(right.Object.Name, left.Object.Name, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                unmatchedSource.Add(left);
                continue;
            }

            remainingTarget.Remove(match);
            pairs.Add((left, match));
        }

        foreach (var left in unmatchedSource)
        {
            var candidates = remainingTarget
                .Where(right => string.Equals(right.Object.Name, left.Object.Name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var sameNameOnSource = unmatchedSource.Count(other =>
                string.Equals(other.Object.Name, left.Object.Name, StringComparison.OrdinalIgnoreCase));
            if (candidates.Count == 1 && sameNameOnSource == 1)
            {
                remainingTarget.Remove(candidates[0]);
                pairs.Add((left, candidates[0]));
            }
            else
            {
                pairs.Add((left, null));
            }
        }

        pairs.AddRange(remainingTarget.Select(right => ((DataDictionaryEntry?)null, (DataDictionaryEntry?)right)));
        return pairs
            .OrderBy(pair => (pair.Item1 ?? pair.Item2)!.Object.Kind)
            .ThenBy(pair => (pair.Item1 ?? pair.Item2)!.Object.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static SchemaDifference ObjectDifference(DataDictionaryEntry entry, SchemaDifferenceKind kind)
    {
        var description = entry.Structure is null
            ? "（無法讀取結構）"
            : $"{entry.Structure.Columns.Count} 個欄位";
        return new SchemaDifference(
            entry.Object.DisplayName,
            entry.Object.Kind,
            SchemaDifferenceArea.Object,
            entry.Object.DisplayName,
            kind,
            kind == SchemaDifferenceKind.OnlyInSource ? description : string.Empty,
            kind == SchemaDifferenceKind.OnlyInTarget ? description : string.Empty);
    }

    private static void CompareColumns(
        string objectName,
        DatabaseObjectKind kind,
        TableStructureInfo left,
        TableStructureInfo right,
        List<SchemaDifference> differences)
    {
        var rightColumns = right.Columns.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var column in left.Columns)
        {
            if (!rightColumns.Remove(column.Name, out var other))
            {
                differences.Add(new SchemaDifference(objectName, kind, SchemaDifferenceArea.Column, column.Name,
                    SchemaDifferenceKind.OnlyInSource, DescribeColumn(column), string.Empty));
                continue;
            }

            var leftText = DescribeColumn(column);
            var rightText = DescribeColumn(other);
            if (!string.Equals(leftText, rightText, StringComparison.Ordinal))
            {
                differences.Add(new SchemaDifference(objectName, kind, SchemaDifferenceArea.Column, column.Name,
                    SchemaDifferenceKind.Changed, leftText, rightText));
            }
        }

        foreach (var column in right.Columns.Where(column => rightColumns.ContainsKey(column.Name)))
        {
            differences.Add(new SchemaDifference(objectName, kind, SchemaDifferenceArea.Column, column.Name,
                SchemaDifferenceKind.OnlyInTarget, string.Empty, DescribeColumn(column)));
        }
    }

    private static void CompareIndexes(string objectName, TableStructureInfo left, TableStructureInfo right, List<SchemaDifference> differences)
    {
        // Primary keys are matched by role, not by name, because auto-generated PK names differ per server.
        var leftIndexes = left.Indexes.ToDictionary(IndexKey, StringComparer.OrdinalIgnoreCase);
        var rightIndexes = right.Indexes.ToDictionary(IndexKey, StringComparer.OrdinalIgnoreCase);
        CompareKeyed(objectName, SchemaDifferenceArea.Index, leftIndexes, rightIndexes, DescribeIndex, differences);
    }

    private static void CompareForeignKeys(string objectName, TableStructureInfo left, TableStructureInfo right, List<SchemaDifference> differences)
    {
        // Foreign keys are matched by their column list so differently named but equivalent constraints line up.
        static string Key(StructureForeignKeyInfo foreignKey) => "(" + string.Join(", ", foreignKey.Columns).ToLowerInvariant() + ")";
        var leftKeys = GroupUnique(left.ForeignKeys, Key);
        var rightKeys = GroupUnique(right.ForeignKeys, Key);
        CompareKeyed(objectName, SchemaDifferenceArea.ForeignKey, leftKeys, rightKeys, DescribeForeignKey, differences);
    }

    private static Dictionary<string, T> GroupUnique<T>(IEnumerable<T> items, Func<T, string> key)
    {
        var result = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var baseKey = key(item);
            var candidate = baseKey;
            for (var suffix = 2; result.ContainsKey(candidate); suffix++)
            {
                candidate = $"{baseKey} #{suffix.ToString(CultureInfo.InvariantCulture)}";
            }

            result[candidate] = item;
        }

        return result;
    }

    private static void CompareKeyed<T>(
        string objectName,
        SchemaDifferenceArea area,
        Dictionary<string, T> left,
        Dictionary<string, T> right,
        Func<T, string> describe,
        List<SchemaDifference> differences)
    {
        foreach (var (key, item) in left.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!right.Remove(key, out var other))
            {
                differences.Add(new SchemaDifference(objectName, DatabaseObjectKind.Table, area, key,
                    SchemaDifferenceKind.OnlyInSource, describe(item), string.Empty));
                continue;
            }

            var leftText = describe(item);
            var rightText = describe(other);
            if (!string.Equals(leftText, rightText, StringComparison.Ordinal))
            {
                differences.Add(new SchemaDifference(objectName, DatabaseObjectKind.Table, area, key,
                    SchemaDifferenceKind.Changed, leftText, rightText));
            }
        }

        foreach (var (key, item) in right.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            differences.Add(new SchemaDifference(objectName, DatabaseObjectKind.Table, area, key,
                SchemaDifferenceKind.OnlyInTarget, string.Empty, describe(item)));
        }
    }

    private static string IndexKey(StructureIndexInfo index) => index.IsPrimaryKey ? "PRIMARY KEY" : index.Name;

    public static string DescribeColumn(StructureColumnInfo column)
    {
        var text = new StringBuilder(NormalizeType(column.DataType));
        text.Append(column.IsNullable ? " NULL" : " NOT NULL");
        if (column.IsPrimaryKey)
        {
            text.Append(" PK");
        }

        var defaultValue = NormalizeDefault(column.DefaultValue);
        if (defaultValue.Length > 0)
        {
            text.Append(" DEFAULT ").Append(defaultValue);
        }

        if (!string.IsNullOrWhiteSpace(column.Extra))
        {
            text.Append(" [").Append(column.Extra.Trim()).Append(']');
        }

        return text.ToString();
    }

    private static string DescribeIndex(StructureIndexInfo index)
    {
        var text = new StringBuilder();
        text.Append(index.IsPrimaryKey ? "PRIMARY KEY" : index.IsUnique ? "UNIQUE" : "INDEX");
        text.Append(" (").Append(string.Join(", ", index.Columns.Select(column => column.Trim()))).Append(')');
        return text.ToString();
    }

    private static string DescribeForeignKey(StructureForeignKeyInfo foreignKey) =>
        $"→ {foreignKey.ReferencedTable} ({string.Join(", ", foreignKey.ReferencedColumns)}) " +
        $"ON UPDATE {NormalizeRule(foreignKey.OnUpdate)} ON DELETE {NormalizeRule(foreignKey.OnDelete)}";

    private static string NormalizeType(string type) =>
        string.Join(' ', type.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string NormalizeDefault(string value)
    {
        var text = value.Trim();
        // SQL Server wraps defaults in parentheses: ((0)) and ('x') describe the same default as 0 and 'x'.
        while (text.Length >= 2 && text[0] == '(' && text[^1] == ')' && IsBalanced(text[1..^1]))
        {
            text = text[1..^1].Trim();
        }

        return text;
    }

    private static bool IsBalanced(string text)
    {
        var depth = 0;
        var quoted = false;
        foreach (var character in text)
        {
            if (character == '\'')
            {
                quoted = !quoted;
            }
            else if (!quoted && character == '(')
            {
                depth++;
            }
            else if (!quoted && character == ')' && --depth < 0)
            {
                return false;
            }
        }

        return depth == 0 && !quoted;
    }

    private static string NormalizeRule(string rule)
    {
        var text = rule.Trim().Replace('_', ' ').ToUpperInvariant();
        return text.Length == 0 ? "NO ACTION" : text;
    }

    private static string KindName(DatabaseObjectKind kind) => kind == DatabaseObjectKind.View ? "檢視表" : "資料表";

    public static string BuildHtml(SchemaComparisonResult result, string generatorVersion, DateTimeOffset? generatedAt = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        var timestamp = (generatedAt ?? DateTimeOffset.Now).ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
        var html = new StringBuilder();
        html.AppendLine("<!doctype html>");
        html.AppendLine("<html lang=\"zh-Hant\"><head><meta charset=\"utf-8\">");
        html.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        html.AppendLine("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">");
        html.Append("<title>").Append(H($"結構比較：{result.Source.Database} ↔ {result.Target.Database}")).AppendLine("</title>");
        html.AppendLine("<style>");
        html.AppendLine("""
            body { font-family: 'Segoe UI', 'Noto Sans TC', 'PingFang TC', sans-serif; margin: 0; color: #24292f; background: #fff; }
            .page { max-width: 1100px; margin: 0 auto; padding: 32px 40px 64px; }
            h1 { font-size: 24px; border-bottom: 3px solid #2563eb; padding-bottom: 10px; }
            h2 { font-size: 18px; color: #2563eb; margin-top: 32px; border-bottom: 1px solid #d0d7de; padding-bottom: 6px; }
            table { border-collapse: collapse; width: 100%; font-size: 13px; margin: 6px 0; }
            th, td { border: 1px solid #d0d7de; padding: 5px 9px; text-align: left; vertical-align: top; word-break: break-word; }
            th { background: #f6f8fa; white-space: nowrap; }
            .meta { color: #57606a; font-size: 13px; line-height: 1.8; }
            .src { background: #fff1f0; } .dst { background: #eefbf1; } .chg { background: #fff8e6; }
            .warn { color: #b54708; font-size: 13px; }
            code { font-family: 'Cascadia Mono', 'JetBrains Mono', Menlo, monospace; font-size: 12px; }
            """);
        html.AppendLine("</style></head><body><div class=\"page\">");
        html.AppendLine("<h1>結構比較報告</h1><p class=\"meta\">");
        html.Append("來源：").Append(H($"{result.Source.ConnectionName}（{result.Source.ProviderName}）／{result.Source.Database}")).AppendLine("<br>");
        html.Append("目標：").Append(H($"{result.Target.ConnectionName}（{result.Target.ProviderName}）／{result.Target.Database}")).AppendLine("<br>");
        html.Append("產生時間：").Append(H(timestamp)).AppendLine("<br>");
        html.Append("產生工具：mySQLPunk ").Append(H(generatorVersion)).AppendLine("（Linux／macOS 預覽版）<br>");
        html.Append("結果：").Append(H(result.Summary)).AppendLine("</p>");
        html.AppendLine("<p class=\"meta\">本報告只比對結構 metadata，不含資料列，也不產生或執行任何同步 SQL。</p>");

        if (result.Warnings.Count > 0)
        {
            html.AppendLine("<h2>無法讀取的物件</h2><ul>");
            foreach (var warning in result.Warnings)
            {
                html.Append("<li class=\"warn\">").Append(H(warning)).AppendLine("</li>");
            }

            html.AppendLine("</ul>");
        }

        if (result.Differences.Count > 0)
        {
            html.AppendLine("<h2>差異明細</h2>");
            html.AppendLine("<table><tr><th>物件</th><th>範圍</th><th>項目</th><th>狀態</th><th>來源</th><th>目標</th></tr>");
            foreach (var difference in result.Differences)
            {
                var css = difference.Kind switch
                {
                    SchemaDifferenceKind.OnlyInSource => "src",
                    SchemaDifferenceKind.OnlyInTarget => "dst",
                    _ => "chg"
                };
                html.Append("<tr class=\"").Append(css).Append("\"><td>").Append(H(difference.ObjectName)).Append("</td>");
                html.Append("<td>").Append(H(difference.AreaText)).Append("</td>");
                html.Append("<td>").Append(H(difference.ItemName)).Append("</td>");
                html.Append("<td>").Append(H(difference.KindText)).Append("</td>");
                html.Append("<td><code>").Append(H(difference.SourceValue)).Append("</code></td>");
                html.Append("<td><code>").Append(H(difference.TargetValue)).AppendLine("</code></td></tr>");
            }

            html.AppendLine("</table>");
        }

        html.AppendLine("</div></body></html>");
        return html.ToString();
    }

    private static string H(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);
}
