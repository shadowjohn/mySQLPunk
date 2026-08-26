using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace mySQLPunk.lib
{
    public enum SchemaDifferenceKind
    {
        TableMissingInTarget,
        TableOnlyInTarget,
        ColumnMissingInTarget,
        ColumnOnlyInTarget,
        ColumnTypeChanged,
        ColumnNullabilityChanged,
        ColumnPrimaryKeyChanged,
        RelationshipMissingInTarget,
        RelationshipOnlyInTarget,
        MetadataWarning
    }

    public sealed class SchemaDifference
    {
        public SchemaDifferenceKind Kind { get; set; }
        public string ObjectName { get; set; }
        public string DetailName { get; set; }
        public string SourceValue { get; set; }
        public string TargetValue { get; set; }
    }

    public sealed class SchemaComparisonResult
    {
        public SchemaComparisonResult(SchemaModelSnapshot source, SchemaModelSnapshot target)
        {
            Source = source;
            Target = target;
            Differences = new List<SchemaDifference>();
        }

        public SchemaModelSnapshot Source { get; private set; }
        public SchemaModelSnapshot Target { get; private set; }
        public List<SchemaDifference> Differences { get; private set; }

        public int SourceOnlyCount
        {
            get
            {
                return Differences.Count(item =>
                    item.Kind == SchemaDifferenceKind.TableMissingInTarget ||
                    item.Kind == SchemaDifferenceKind.ColumnMissingInTarget ||
                    item.Kind == SchemaDifferenceKind.RelationshipMissingInTarget);
            }
        }

        public int TargetOnlyCount
        {
            get
            {
                return Differences.Count(item =>
                    item.Kind == SchemaDifferenceKind.TableOnlyInTarget ||
                    item.Kind == SchemaDifferenceKind.ColumnOnlyInTarget ||
                    item.Kind == SchemaDifferenceKind.RelationshipOnlyInTarget);
            }
        }

        public int ChangedCount
        {
            get
            {
                return Differences.Count(item =>
                    item.Kind == SchemaDifferenceKind.ColumnTypeChanged ||
                    item.Kind == SchemaDifferenceKind.ColumnNullabilityChanged ||
                    item.Kind == SchemaDifferenceKind.ColumnPrimaryKeyChanged);
            }
        }

        public int WarningCount
        {
            get { return Differences.Count(item => item.Kind == SchemaDifferenceKind.MetadataWarning); }
        }
    }

    /// <summary>
    /// 比較兩份唯讀 schema snapshot。這個服務只整理差異，不會建立或執行 DDL。
    /// </summary>
    public static class SchemaComparisonService
    {
        private static readonly Dictionary<string, string> TypeAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "integer", "int" },
            { "int4", "int" },
            { "int8", "bigint" },
            { "int2", "smallint" },
            { "boolean", "bool" },
            { "character varying", "varchar" },
            { "decimal", "numeric" },
            { "double precision", "double" }
        };

        public static SchemaComparisonResult Compare(SchemaModelSnapshot source, SchemaModelSnapshot target)
        {
            if (source == null) throw new ArgumentNullException("source");
            if (target == null) throw new ArgumentNullException("target");

            SchemaComparisonResult result = new SchemaComparisonResult(source, target);
            Dictionary<string, SchemaTableModel> sourceTables = BuildTableMap(source.Tables);
            Dictionary<string, SchemaTableModel> targetTables = BuildTableMap(target.Tables);

            foreach (string tableName in sourceTables.Keys.Union(targetTables.Keys, StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            {
                SchemaTableModel sourceTable;
                SchemaTableModel targetTable;
                bool hasSource = sourceTables.TryGetValue(tableName, out sourceTable);
                bool hasTarget = targetTables.TryGetValue(tableName, out targetTable);
                if (!hasTarget)
                {
                    result.Differences.Add(NewDifference(SchemaDifferenceKind.TableMissingInTarget, sourceTable.Name, string.Empty,
                        Localization.T("SchemaComparison.Present"), Localization.T("SchemaComparison.Missing")));
                    continue;
                }
                if (!hasSource)
                {
                    result.Differences.Add(NewDifference(SchemaDifferenceKind.TableOnlyInTarget, targetTable.Name, string.Empty,
                        Localization.T("SchemaComparison.Missing"), Localization.T("SchemaComparison.Present")));
                    continue;
                }

                CompareColumns(sourceTable, targetTable, result.Differences);
            }

            CompareRelationships(source.Relationships, target.Relationships, result.Differences);
            AppendWarnings(source, true, result.Differences);
            AppendWarnings(target, false, result.Differences);

            result.Differences.Sort(CompareDifferences);
            return result;
        }

        public static string GetKindDisplayName(SchemaDifferenceKind kind)
        {
            return Localization.T("SchemaComparison.Kind." + kind);
        }

        public static string BuildHtml(SchemaComparisonResult result, string applicationVersion)
        {
            if (result == null) throw new ArgumentNullException("result");

            string sourceName = BuildSnapshotName(result.Source);
            string targetName = BuildSnapshotName(result.Target);
            StringBuilder html = new StringBuilder();
            html.AppendLine("<!doctype html>");
            html.AppendLine("<html lang=\"zh-Hant\"><head><meta charset=\"utf-8\">");
            html.Append("<title>").Append(Encode(Localization.T("SchemaComparison.ReportTitle"))).AppendLine("</title>");
            html.AppendLine("<style>body{font-family:Segoe UI,Microsoft JhengHei,sans-serif;margin:32px;color:#202124}h1{margin-bottom:8px}.meta{color:#5f6368;margin-bottom:20px}.summary{display:flex;gap:12px;flex-wrap:wrap;margin:18px 0}.card{background:#f4f6f8;border-radius:8px;padding:10px 14px}table{border-collapse:collapse;width:100%;font-size:14px}th,td{border:1px solid #dfe3e8;padding:8px;text-align:left;vertical-align:top}th{background:#eef1f4}tr:nth-child(even){background:#fafbfc}.empty{padding:18px;background:#eef8f0;border-radius:8px}</style></head><body>");
            html.Append("<h1>").Append(Encode(Localization.T("SchemaComparison.ReportTitle"))).AppendLine("</h1>");
            html.Append("<div class=\"meta\">").Append(Encode(sourceName)).Append(" &rarr; ").Append(Encode(targetName));
            if (!string.IsNullOrWhiteSpace(applicationVersion)) html.Append(" · mySQLPunk ").Append(Encode(applicationVersion));
            html.AppendLine("</div>");
            html.AppendLine("<div class=\"summary\">");
            AppendSummaryCard(html, "SchemaComparison.SourceOnly", result.SourceOnlyCount);
            AppendSummaryCard(html, "SchemaComparison.TargetOnly", result.TargetOnlyCount);
            AppendSummaryCard(html, "SchemaComparison.Changed", result.ChangedCount);
            AppendSummaryCard(html, "SchemaComparison.Warnings", result.WarningCount);
            html.AppendLine("</div>");

            if (result.Differences.Count == 0)
            {
                html.Append("<div class=\"empty\">").Append(Encode(Localization.T("SchemaComparison.NoDifferences"))).AppendLine("</div>");
            }
            else
            {
                html.Append("<table><thead><tr><th>").Append(Encode(Localization.T("SchemaComparison.Category")))
                    .Append("</th><th>").Append(Encode(Localization.T("SchemaComparison.Object")))
                    .Append("</th><th>").Append(Encode(Localization.T("SchemaComparison.Detail")))
                    .Append("</th><th>").Append(Encode(Localization.T("SchemaComparison.Source")))
                    .Append("</th><th>").Append(Encode(Localization.T("SchemaComparison.Target")))
                    .AppendLine("</th></tr></thead><tbody>");
                foreach (SchemaDifference difference in result.Differences)
                {
                    html.Append("<tr><td>").Append(Encode(GetKindDisplayName(difference.Kind)))
                        .Append("</td><td>").Append(Encode(difference.ObjectName))
                        .Append("</td><td>").Append(Encode(difference.DetailName))
                        .Append("</td><td>").Append(Encode(difference.SourceValue))
                        .Append("</td><td>").Append(Encode(difference.TargetValue))
                        .AppendLine("</td></tr>");
                }
                html.AppendLine("</tbody></table>");
            }
            html.AppendLine("</body></html>");
            return html.ToString();
        }

        internal static string NormalizeDataType(string dataType)
        {
            string value = Regex.Replace((dataType ?? string.Empty).Trim().ToLowerInvariant(), @"\s+", " ");
            value = Regex.Replace(value, @"\s*([(),])\s*", "$1");
            if (value.Length == 0) return value;

            foreach (KeyValuePair<string, string> alias in TypeAliases.OrderByDescending(item => item.Key.Length))
            {
                if (string.Equals(value, alias.Key, StringComparison.OrdinalIgnoreCase)) return alias.Value;
                if (value.StartsWith(alias.Key + "(", StringComparison.OrdinalIgnoreCase))
                    return alias.Value + value.Substring(alias.Key.Length);
            }
            return value;
        }

        private static Dictionary<string, SchemaTableModel> BuildTableMap(IEnumerable<SchemaTableModel> tables)
        {
            return (tables ?? Enumerable.Empty<SchemaTableModel>())
                .Where(table => table != null && !string.IsNullOrWhiteSpace(table.Name))
                .GroupBy(table => table.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        }

        private static void CompareColumns(SchemaTableModel sourceTable, SchemaTableModel targetTable, ICollection<SchemaDifference> differences)
        {
            Dictionary<string, SchemaColumnModel> sourceColumns = BuildColumnMap(sourceTable.Columns);
            Dictionary<string, SchemaColumnModel> targetColumns = BuildColumnMap(targetTable.Columns);
            foreach (string columnName in sourceColumns.Keys.Union(targetColumns.Keys, StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            {
                SchemaColumnModel sourceColumn;
                SchemaColumnModel targetColumn;
                bool hasSource = sourceColumns.TryGetValue(columnName, out sourceColumn);
                bool hasTarget = targetColumns.TryGetValue(columnName, out targetColumn);
                if (!hasTarget)
                {
                    differences.Add(NewDifference(SchemaDifferenceKind.ColumnMissingInTarget, sourceTable.Name, sourceColumn.Name,
                        DescribeColumn(sourceColumn), Localization.T("SchemaComparison.Missing")));
                    continue;
                }
                if (!hasSource)
                {
                    differences.Add(NewDifference(SchemaDifferenceKind.ColumnOnlyInTarget, targetTable.Name, targetColumn.Name,
                        Localization.T("SchemaComparison.Missing"), DescribeColumn(targetColumn)));
                    continue;
                }

                if (!string.Equals(NormalizeDataType(sourceColumn.DataType), NormalizeDataType(targetColumn.DataType), StringComparison.Ordinal))
                {
                    differences.Add(NewDifference(SchemaDifferenceKind.ColumnTypeChanged, sourceTable.Name, sourceColumn.Name,
                        sourceColumn.DataType, targetColumn.DataType));
                }
                if (sourceColumn.IsNullable != targetColumn.IsNullable)
                {
                    differences.Add(NewDifference(SchemaDifferenceKind.ColumnNullabilityChanged, sourceTable.Name, sourceColumn.Name,
                        DescribeNullable(sourceColumn.IsNullable), DescribeNullable(targetColumn.IsNullable)));
                }
                if (sourceColumn.IsPrimaryKey != targetColumn.IsPrimaryKey)
                {
                    differences.Add(NewDifference(SchemaDifferenceKind.ColumnPrimaryKeyChanged, sourceTable.Name, sourceColumn.Name,
                        DescribePrimaryKey(sourceColumn.IsPrimaryKey), DescribePrimaryKey(targetColumn.IsPrimaryKey)));
                }
            }
        }

        private static Dictionary<string, SchemaColumnModel> BuildColumnMap(IEnumerable<SchemaColumnModel> columns)
        {
            return (columns ?? Enumerable.Empty<SchemaColumnModel>())
                .Where(column => column != null && !string.IsNullOrWhiteSpace(column.Name))
                .GroupBy(column => column.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        }

        private static void CompareRelationships(
            IEnumerable<SchemaRelationshipModel> sourceRelationships,
            IEnumerable<SchemaRelationshipModel> targetRelationships,
            ICollection<SchemaDifference> differences)
        {
            Dictionary<string, SchemaRelationshipModel> sourceMap = BuildRelationshipMap(sourceRelationships);
            Dictionary<string, SchemaRelationshipModel> targetMap = BuildRelationshipMap(targetRelationships);
            foreach (string key in sourceMap.Keys.Union(targetMap.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
            {
                SchemaRelationshipModel sourceRelationship;
                SchemaRelationshipModel targetRelationship;
                bool hasSource = sourceMap.TryGetValue(key, out sourceRelationship);
                bool hasTarget = targetMap.TryGetValue(key, out targetRelationship);
                if (!hasTarget)
                {
                    differences.Add(NewDifference(SchemaDifferenceKind.RelationshipMissingInTarget,
                        sourceRelationship.FromTable, BuildRelationshipDetail(sourceRelationship),
                        Localization.T("SchemaComparison.Present"), Localization.T("SchemaComparison.Missing")));
                }
                else if (!hasSource)
                {
                    differences.Add(NewDifference(SchemaDifferenceKind.RelationshipOnlyInTarget,
                        targetRelationship.FromTable, BuildRelationshipDetail(targetRelationship),
                        Localization.T("SchemaComparison.Missing"), Localization.T("SchemaComparison.Present")));
                }
            }
        }

        private static Dictionary<string, SchemaRelationshipModel> BuildRelationshipMap(IEnumerable<SchemaRelationshipModel> relationships)
        {
            return (relationships ?? Enumerable.Empty<SchemaRelationshipModel>())
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.FromTable) && !string.IsNullOrWhiteSpace(item.FromColumn) &&
                               !string.IsNullOrWhiteSpace(item.ToTable) && !string.IsNullOrWhiteSpace(item.ToColumn))
                .GroupBy(BuildRelationshipKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        }

        private static string BuildRelationshipKey(SchemaRelationshipModel item)
        {
            return string.Join("|", new[] { item.FromTable, item.FromColumn, item.ToTable, item.ToColumn });
        }

        private static string BuildRelationshipDetail(SchemaRelationshipModel item)
        {
            return item.FromColumn + " → " + item.ToTable + "." + item.ToColumn;
        }

        private static void AppendWarnings(SchemaModelSnapshot snapshot, bool source, ICollection<SchemaDifference> differences)
        {
            foreach (string warning in (snapshot.Warnings ?? new List<string>()).Where(item => !string.IsNullOrWhiteSpace(item)))
            {
                differences.Add(NewDifference(SchemaDifferenceKind.MetadataWarning, snapshot.DatabaseName, string.Empty,
                    source ? warning.Trim() : string.Empty,
                    source ? string.Empty : warning.Trim()));
            }
        }

        private static SchemaDifference NewDifference(SchemaDifferenceKind kind, string objectName, string detailName, string sourceValue, string targetValue)
        {
            return new SchemaDifference
            {
                Kind = kind,
                ObjectName = objectName ?? string.Empty,
                DetailName = detailName ?? string.Empty,
                SourceValue = sourceValue ?? string.Empty,
                TargetValue = targetValue ?? string.Empty
            };
        }

        private static string DescribeColumn(SchemaColumnModel column)
        {
            return (column.DataType ?? string.Empty) + ", " + DescribeNullable(column.IsNullable) +
                   (column.IsPrimaryKey ? ", " + Localization.T("SchemaComparison.PrimaryKey") : string.Empty);
        }

        private static string DescribeNullable(bool nullable)
        {
            return Localization.T(nullable ? "SchemaComparison.Nullable" : "SchemaComparison.NotNullable");
        }

        private static string DescribePrimaryKey(bool primaryKey)
        {
            return Localization.T(primaryKey ? "SchemaComparison.PrimaryKey" : "SchemaComparison.NotPrimaryKey");
        }

        private static int CompareDifferences(SchemaDifference left, SchemaDifference right)
        {
            int order = left.Kind.CompareTo(right.Kind);
            if (order != 0) return order;
            order = StringComparer.OrdinalIgnoreCase.Compare(left.ObjectName, right.ObjectName);
            if (order != 0) return order;
            order = StringComparer.OrdinalIgnoreCase.Compare(left.DetailName, right.DetailName);
            if (order != 0) return order;
            order = StringComparer.OrdinalIgnoreCase.Compare(left.SourceValue, right.SourceValue);
            return order != 0 ? order : StringComparer.OrdinalIgnoreCase.Compare(left.TargetValue, right.TargetValue);
        }

        private static string BuildSnapshotName(SchemaModelSnapshot snapshot)
        {
            string name = snapshot == null ? string.Empty : snapshot.DatabaseName;
            string provider = snapshot == null ? string.Empty : snapshot.ProviderName;
            return string.IsNullOrWhiteSpace(provider) ? name : name + " (" + provider + ")";
        }

        private static void AppendSummaryCard(StringBuilder html, string key, int count)
        {
            html.Append("<div class=\"card\"><strong>").Append(Encode(Localization.T(key)))
                .Append("</strong><br>").Append(count).AppendLine("</div>");
        }

        private static string Encode(string value)
        {
            return WebUtility.HtmlEncode(value ?? string.Empty);
        }
    }
}
