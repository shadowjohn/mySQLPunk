using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace mySQLPunk.lib
{
    public sealed class SchemaSyncScript
    {
        public SchemaSyncScript(string provider, string text, List<string> statements, List<string> manualItems, List<string> destructiveItems)
        {
            Provider = provider;
            Text = text;
            Statements = statements;
            ManualItems = manualItems;
            DestructiveItems = destructiveItems;
        }

        public string Provider { get; private set; }
        public string Text { get; private set; }
        public List<string> Statements { get; private set; }
        public List<string> ManualItems { get; private set; }
        public List<string> DestructiveItems { get; private set; }

        public string Summary
        {
            get { return Localization.Format("SchemaSync.Summary", Statements.Count, DestructiveItems.Count, ManualItems.Count); }
        }
    }

    /// <summary>
    /// 由同類型資料庫的結構比較結果產生「讓目標跟上來源」的 SQL 預覽。只產生文字、不執行；
    /// 會刪除資料表或欄位的變更一律輸出成註解，無法安全表達的變更列為手動處理。
    /// Windows 版快照只含欄位型別、NULL、主鍵與外鍵關係，所以不處理預設值、索引與外鍵規則。
    /// </summary>
    public static class SchemaSyncScriptService
    {
        private static readonly Regex LineBreaks = new Regex("[\\r\\n\\u0085\\u2028\\u2029]+", RegexOptions.Compiled);

        public static bool CanGenerate(SchemaComparisonResult result, out string reason)
        {
            if (result == null) throw new ArgumentNullException("result");
            string sourceProvider = NormalizeProvider(result.Source == null ? null : result.Source.ProviderName);
            string targetProvider = NormalizeProvider(result.Target == null ? null : result.Target.ProviderName);
            if (!string.Equals(sourceProvider, targetProvider, StringComparison.Ordinal))
            {
                reason = Localization.T("SchemaSync.DifferentProviders");
                return false;
            }

            if (!IsSupported(targetProvider))
            {
                reason = Localization.Format("SchemaSync.UnsupportedProvider", targetProvider);
                return false;
            }

            if (!result.Differences.Any(item => item.Kind != SchemaDifferenceKind.MetadataWarning))
            {
                reason = Localization.T("SchemaSync.NoChanges");
                return false;
            }

            reason = string.Empty;
            return true;
        }

        public static SchemaSyncScript Generate(SchemaComparisonResult result, DateTime? generatedAt = null)
        {
            string reason;
            if (!CanGenerate(result, out reason)) throw new InvalidOperationException(reason);

            Builder builder = new Builder(NormalizeProvider(result.Target.ProviderName), result);
            builder.Build();

            StringBuilder text = new StringBuilder();
            text.AppendLine("-- " + SingleLine(Localization.T("SchemaSync.Header")));
            text.AppendLine("-- " + SingleLine(Localization.Format("SchemaSync.GeneratedAt",
                (generatedAt ?? DateTime.Now).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))));
            text.AppendLine("-- " + SingleLine(Localization.Format("SchemaSync.SourceLine", result.Source.DatabaseName, result.Source.ProviderName)));
            text.AppendLine("-- " + SingleLine(Localization.Format("SchemaSync.TargetLine", result.Target.DatabaseName, result.Target.ProviderName)));
            text.AppendLine("-- " + SingleLine(Localization.T("SchemaSync.DestructiveNotice")));
            text.AppendLine("-- " + SingleLine(Localization.T("SchemaSync.ScopeNotice")));
            text.AppendLine();
            foreach (KeyValuePair<string, List<string>> section in builder.Sections)
            {
                if (section.Value.Count == 0) continue;
                text.AppendLine("-- ===== " + SingleLine(Localization.T(section.Key)) + " =====");
                foreach (string line in section.Value) text.AppendLine(line);
                text.AppendLine();
            }

            return new SchemaSyncScript(builder.Provider, text.ToString().TrimEnd() + Environment.NewLine,
                builder.Statements, builder.Manual, builder.Destructive);
        }

        public static string NormalizeProvider(string provider)
        {
            string value = (provider ?? string.Empty).Trim().ToLowerInvariant();
            if (value == "mariadb") return "mysql";
            if (value == "sqlserver" || value == "sql server" || value == "mssql") return "mssql";
            if (value == "postgres" || value == "npgsql" || value == "pgsql") return "postgresql";
            if (value == "system.data.sqlite") return "sqlite";
            return value;
        }

        private static bool IsSupported(string provider)
        {
            return provider == "mysql" || provider == "postgresql" || provider == "mssql" || provider == "sqlite";
        }

        internal static string SingleLine(string value)
        {
            return LineBreaks.Replace(value ?? string.Empty, " ");
        }

        private sealed class Builder
        {
            private readonly SchemaComparisonResult result;
            private readonly List<string> createTables = new List<string>();
            private readonly List<string> addColumns = new List<string>();
            private readonly List<string> alterColumns = new List<string>();
            private readonly List<string> foreignKeys = new List<string>();
            private readonly List<string> manual = new List<string>();
            private readonly List<string> destructive = new List<string>();
            private readonly HashSet<string> alteredColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public Builder(string provider, SchemaComparisonResult result)
            {
                Provider = provider;
                this.result = result;
                Statements = new List<string>();
                Manual = new List<string>();
                Destructive = new List<string>();
            }

            public string Provider { get; private set; }
            public List<string> Statements { get; private set; }
            public List<string> Manual { get; private set; }
            public List<string> Destructive { get; private set; }

            public IEnumerable<KeyValuePair<string, List<string>>> Sections
            {
                get
                {
                    yield return new KeyValuePair<string, List<string>>("SchemaSync.Section.CreateTables", createTables);
                    yield return new KeyValuePair<string, List<string>>("SchemaSync.Section.AddColumns", addColumns);
                    yield return new KeyValuePair<string, List<string>>("SchemaSync.Section.AlterColumns", alterColumns);
                    yield return new KeyValuePair<string, List<string>>("SchemaSync.Section.ForeignKeys", foreignKeys);
                    yield return new KeyValuePair<string, List<string>>("SchemaSync.Section.Manual", manual);
                    yield return new KeyValuePair<string, List<string>>("SchemaSync.Section.Destructive", destructive);
                }
            }

            public void Build()
            {
                Dictionary<string, SchemaTableModel> sourceTables = Map(result.Source.Tables);
                Dictionary<string, SchemaTableModel> targetTables = Map(result.Target.Tables);
                HashSet<string> createdTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (SchemaDifference difference in result.Differences)
                {
                    SchemaTableModel sourceTable;
                    SchemaTableModel targetTable;
                    sourceTables.TryGetValue(difference.ObjectName ?? string.Empty, out sourceTable);
                    targetTables.TryGetValue(difference.ObjectName ?? string.Empty, out targetTable);
                    switch (difference.Kind)
                    {
                        case SchemaDifferenceKind.TableMissingInTarget:
                            if (sourceTable != null && createdTables.Add(sourceTable.Name)) CreateTable(sourceTable);
                            break;
                        case SchemaDifferenceKind.TableOnlyInTarget:
                            if (targetTable != null)
                            {
                                AddDestructive("DROP TABLE " + TableName(targetTable.Name),
                                    Localization.Format("SchemaSync.DropTable", targetTable.Name));
                            }
                            break;
                        case SchemaDifferenceKind.ColumnMissingInTarget:
                            AddColumn(sourceTable, FindColumn(sourceTable, difference.DetailName));
                            break;
                        case SchemaDifferenceKind.ColumnOnlyInTarget:
                            if (targetTable != null)
                            {
                                AddDestructive("ALTER TABLE " + TableName(targetTable.Name) + " DROP COLUMN " + Q(difference.DetailName),
                                    Localization.Format("SchemaSync.DropColumn", targetTable.Name, difference.DetailName));
                            }
                            break;
                        case SchemaDifferenceKind.ColumnTypeChanged:
                        case SchemaDifferenceKind.ColumnNullabilityChanged:
                            AlterColumn(difference.Kind, sourceTable, targetTable, difference.DetailName);
                            break;
                        case SchemaDifferenceKind.ColumnPrimaryKeyChanged:
                            AddManual(Localization.Format("SchemaSync.PrimaryKeyChanged", difference.ObjectName, difference.DetailName));
                            break;
                    }
                }

                AddRelationships();
            }

            private void CreateTable(SchemaTableModel table)
            {
                List<SchemaColumnModel> columns = table.Columns.OrderBy(column => column.Ordinal).ToList();
                if (columns.Count == 0)
                {
                    AddManual(Localization.Format("SchemaSync.NoColumns", table.Name));
                    return;
                }

                List<string> lines = columns.Select(column => "    " + ColumnDefinition(column)).ToList();
                List<string> primary = columns.Where(column => column.IsPrimaryKey).Select(column => Q(column.Name)).ToList();
                if (primary.Count > 0) lines.Add("    PRIMARY KEY (" + string.Join(", ", primary) + ")");
                AddStatement(createTables, "CREATE TABLE " + TableName(table.Name) + " (\n" + string.Join(",\n", lines) + "\n)");
            }

            private void AddColumn(SchemaTableModel table, SchemaColumnModel column)
            {
                if (table == null || column == null) return;
                if (column.IsPrimaryKey)
                {
                    AddManual(Localization.Format("SchemaSync.PrimaryKeyColumn", table.Name, column.Name));
                    return;
                }

                if (!column.IsNullable)
                {
                    AddManual(Localization.Format("SchemaSync.NotNullWithoutDefault", table.Name, column.Name));
                }

                string keyword = Provider == "mssql" ? " ADD " : " ADD COLUMN ";
                AddStatement(addColumns, "ALTER TABLE " + TableName(table.Name) + keyword + ColumnDefinition(column));
            }

            private void AlterColumn(SchemaDifferenceKind kind, SchemaTableModel sourceTable, SchemaTableModel targetTable, string columnName)
            {
                SchemaColumnModel wanted = FindColumn(sourceTable, columnName);
                SchemaColumnModel current = FindColumn(targetTable, columnName);
                if (wanted == null || current == null) return;
                string table = TableName(targetTable.Name);
                string qualified = sourceTable.Name + "." + wanted.Name;
                // 型別與 NULL 差異是兩筆 difference；SQL Server／MySQL／SQLite 一條語句（或一個手動項目）就同時涵蓋兩者。
                if (Provider != "postgresql" && !alteredColumns.Add(qualified)) return;
                switch (Provider)
                {
                    case "postgresql":
                        if (kind == SchemaDifferenceKind.ColumnTypeChanged)
                        {
                            AddManual(Localization.Format("SchemaSync.TypeChangeUsing", qualified, current.DataType, wanted.DataType));
                            AddStatement(alterColumns, "ALTER TABLE " + table + " ALTER COLUMN " + Q(wanted.Name) + " TYPE " + wanted.DataType);
                        }
                        else
                        {
                            AddStatement(alterColumns, "ALTER TABLE " + table + " ALTER COLUMN " + Q(wanted.Name) +
                                (wanted.IsNullable ? " DROP NOT NULL" : " SET NOT NULL"));
                        }
                        break;
                    case "mssql":
                        if (kind == SchemaDifferenceKind.ColumnTypeChanged)
                        {
                            AddManual(Localization.Format("SchemaSync.TypeChangeTruncate", qualified, current.DataType, wanted.DataType));
                        }
                        AddStatement(alterColumns, "ALTER TABLE " + table + " ALTER COLUMN " + Q(wanted.Name) + " " + wanted.DataType +
                            (wanted.IsNullable ? " NULL" : " NOT NULL"));
                        break;
                    case "mysql":
                        // MODIFY 需要完整欄位定義；快照沒有預設值、AUTO_INCREMENT、字元集與註解，直接執行會把它們清掉。
                        AddManual(Localization.Format("SchemaSync.MySqlModify", qualified,
                            "ALTER TABLE " + table + " MODIFY COLUMN " + ColumnDefinition(wanted)));
                        break;
                    default:
                        AddManual(Localization.Format("SchemaSync.SqliteAlter", qualified));
                        break;
                }
            }

            private void AddRelationships()
            {
                Dictionary<string, SchemaTableModel> targetTables = Map(result.Target.Tables);
                HashSet<string> targetKeys = new HashSet<string>(result.Target.Relationships.Select(RelationshipKey), StringComparer.OrdinalIgnoreCase);
                HashSet<string> sourceKeys = new HashSet<string>(result.Source.Relationships.Select(RelationshipKey), StringComparer.OrdinalIgnoreCase);

                foreach (IGrouping<string, SchemaRelationshipModel> group in result.Source.Relationships
                    .Where(item => !targetKeys.Contains(RelationshipKey(item)))
                    .GroupBy(item => (item.FromTable ?? string.Empty) + "\u0001" + (item.Name ?? string.Empty), StringComparer.OrdinalIgnoreCase)
                    .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
                {
                    List<SchemaRelationshipModel> parts = group.OrderBy(item => item.Ordinal).ToList();
                    SchemaRelationshipModel first = parts[0];
                    if (Provider == "sqlite")
                    {
                        AddManual(Localization.Format("SchemaSync.SqliteForeignKey", first.FromTable, first.Name));
                        continue;
                    }

                    string constraint = string.IsNullOrWhiteSpace(first.Name) ? string.Empty : "CONSTRAINT " + Q(first.Name) + " ";
                    AddStatement(foreignKeys, "ALTER TABLE " + TableName(first.FromTable) + " ADD " + constraint + "FOREIGN KEY (" +
                        string.Join(", ", parts.Select(item => Q(item.FromColumn))) + ") REFERENCES " + TableName(first.ToTable) + " (" +
                        string.Join(", ", parts.Select(item => Q(item.ToColumn))) + ")");
                }

                foreach (IGrouping<string, SchemaRelationshipModel> group in result.Target.Relationships
                    .Where(item => !sourceKeys.Contains(RelationshipKey(item)) && targetTables.ContainsKey(item.FromTable ?? string.Empty))
                    .GroupBy(item => (item.FromTable ?? string.Empty) + "\u0001" + (item.Name ?? string.Empty), StringComparer.OrdinalIgnoreCase)
                    .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
                {
                    SchemaRelationshipModel first = group.First();
                    if (Provider == "sqlite" || string.IsNullOrWhiteSpace(first.Name)) continue;
                    string statement = Provider == "mysql"
                        ? "ALTER TABLE " + TableName(first.FromTable) + " DROP FOREIGN KEY " + Q(first.Name)
                        : "ALTER TABLE " + TableName(first.FromTable) + " DROP CONSTRAINT " + Q(first.Name);
                    AddDestructive(statement, Localization.Format("SchemaSync.DropForeignKey", first.FromTable, first.Name));
                }
            }

            private string ColumnDefinition(SchemaColumnModel column)
            {
                string type = string.IsNullOrWhiteSpace(column.DataType) ? (Provider == "sqlite" ? "TEXT" : "varchar(255)") : column.DataType.Trim();
                return Q(column.Name) + " " + type + (column.IsNullable ? " NULL" : " NOT NULL");
            }

            private string TableName(string name)
            {
                string value = name ?? string.Empty;
                int dot = value.IndexOf('.');
                if ((Provider == "postgresql" || Provider == "mssql") && dot > 0 && dot < value.Length - 1)
                {
                    return Q(value.Substring(0, dot)) + "." + Q(value.Substring(dot + 1));
                }

                return Q(value);
            }

            private string Q(string identifier)
            {
                string value = identifier ?? string.Empty;
                if (Provider == "mysql") return "`" + value.Replace("`", "``") + "`";
                if (Provider == "mssql") return "[" + value.Replace("]", "]]") + "]";
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            }

            private void AddStatement(List<string> section, string statement)
            {
                Statements.Add(statement);
                section.Add(statement + ";");
                if (Provider == "mssql") section.Add("GO");
            }

            private void AddDestructive(string statement, string description)
            {
                description = SingleLine(description);
                Destructive.Add(description);
                destructive.Add("-- " + description);
                string[] lines = statement.Split('\n');
                for (int index = 0; index < lines.Length; index++)
                {
                    destructive.Add("-- " + lines[index].TrimEnd('\r') + (index == lines.Length - 1 ? ";" : string.Empty));
                }
            }

            private void AddManual(string description)
            {
                description = SingleLine(description);
                Manual.Add(description);
                manual.Add("-- " + description);
            }

            private static Dictionary<string, SchemaTableModel> Map(IEnumerable<SchemaTableModel> tables)
            {
                Dictionary<string, SchemaTableModel> map = new Dictionary<string, SchemaTableModel>(StringComparer.OrdinalIgnoreCase);
                foreach (SchemaTableModel table in tables ?? Enumerable.Empty<SchemaTableModel>())
                {
                    if (table != null && !string.IsNullOrWhiteSpace(table.Name) && !map.ContainsKey(table.Name)) map[table.Name] = table;
                }
                return map;
            }

            private static SchemaColumnModel FindColumn(SchemaTableModel table, string name)
            {
                if (table == null) return null;
                return table.Columns.FirstOrDefault(column => string.Equals(column.Name, name, StringComparison.OrdinalIgnoreCase));
            }

            private static string RelationshipKey(SchemaRelationshipModel item)
            {
                return ((item.FromTable ?? string.Empty) + "|" + (item.FromColumn ?? string.Empty) + "|" +
                        (item.ToTable ?? string.Empty) + "|" + (item.ToColumn ?? string.Empty)).ToLowerInvariant();
            }
        }
    }
}
