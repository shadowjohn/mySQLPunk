using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;

namespace mySQLPunk.lib
{
    /// <summary>
    /// 資料表設計器的外鍵與 CHECK 約束模型，以及可預覽的跨 provider DDL。
    /// 所有識別字都由這個類別重新加上引號，避免把欄位名稱直接串進 SQL。
    /// </summary>
    public sealed class TableDesignerConstraint
    {
        public string Name { get; set; }
        public string Kind { get; set; }
        public string Columns { get; set; }
        public string ReferencedTable { get; set; }
        public string ReferencedColumns { get; set; }
        public string OnDelete { get; set; }
        public string OnUpdate { get; set; }
        public string Expression { get; set; }
        public string OriginalName { get; set; }
    }

    public sealed class TableDesignerConstraintChangeSet
    {
        public List<string> DropStatements { get; } = new List<string>();
        public List<string> AddStatements { get; } = new List<string>();
        public List<string> Errors { get; } = new List<string>();
    }

    public static class TableDesignerConstraintService
    {
        public const string ForeignKeyKind = "FOREIGN KEY";
        public const string CheckKind = "CHECK";

        public static TableDesignerConstraintChangeSet BuildChanges(
            string providerName,
            string qualifiedTableName,
            IEnumerable<TableDesignerConstraint> originals,
            IEnumerable<TableDesignerConstraint> current)
        {
            TableDesignerConstraintChangeSet result = new TableDesignerConstraintChangeSet();
            string provider = NormalizeProvider(providerName);
            List<TableDesignerConstraint> oldItems = NormalizeItems(originals, qualifiedTableName, provider, result.Errors);
            List<TableDesignerConstraint> newItems = NormalizeItems(current, qualifiedTableName, provider, result.Errors);
            if (result.Errors.Count > 0) return result;

            if (provider == "sqlite" && (oldItems.Count > 0 || newItems.Count > 0))
            {
                if (!EquivalentLists(oldItems, newItems))
                {
                    result.Errors.Add("SQLite 的既有外鍵與 CHECK 約束需要重建資料表，請先在 SQL 預覽確認重建指令。");
                }
                return result;
            }

            Dictionary<string, TableDesignerConstraint> oldByName = ToNameMap(oldItems);
            Dictionary<string, TableDesignerConstraint> newByName = ToNameMap(newItems);

            foreach (KeyValuePair<string, TableDesignerConstraint> pair in oldByName)
            {
                TableDesignerConstraint replacement;
                if (!newByName.TryGetValue(pair.Key, out replacement) || !Equivalent(pair.Value, replacement))
                {
                    result.DropStatements.Add(BuildDropStatement(provider, qualifiedTableName, pair.Value));
                }
            }

            foreach (KeyValuePair<string, TableDesignerConstraint> pair in newByName)
            {
                TableDesignerConstraint previous;
                if (!oldByName.TryGetValue(pair.Key, out previous) || !Equivalent(previous, pair.Value))
                {
                    result.AddStatements.Add(BuildAddStatement(provider, qualifiedTableName, pair.Value));
                }
            }

            return result;
        }

        public static List<string> BuildCreateStatements(
            string providerName,
            string qualifiedTableName,
            IEnumerable<TableDesignerConstraint> constraints,
            List<string> errors)
        {
            List<string> targetErrors = errors ?? new List<string>();
            string provider = NormalizeProvider(providerName);
            List<TableDesignerConstraint> items = NormalizeItems(constraints, qualifiedTableName, provider, targetErrors);
            List<string> statements = new List<string>();
            if (targetErrors.Count > 0 || provider == "sqlite") return statements;
            foreach (TableDesignerConstraint item in items)
            {
                statements.Add(BuildAddStatement(provider, qualifiedTableName, item));
            }
            return statements;
        }

        public static List<string> BuildInlineDefinitions(
            string providerName,
            string qualifiedTableName,
            IEnumerable<TableDesignerConstraint> constraints,
            List<string> errors)
        {
            List<string> targetErrors = errors ?? new List<string>();
            string provider = NormalizeProvider(providerName);
            List<TableDesignerConstraint> items = NormalizeItems(constraints, qualifiedTableName, provider, targetErrors);
            List<string> definitions = new List<string>();
            if (targetErrors.Count > 0) return definitions;
            foreach (TableDesignerConstraint item in items)
            {
                definitions.Add(BuildConstraintDefinition(provider, item));
            }
            return definitions;
        }

        public static List<TableDesignerConstraint> LoadExistingConstraints(IDatabase database, string databaseName, string tableName)
        {
            if (database == null || string.IsNullOrWhiteSpace(tableName)) return new List<TableDesignerConstraint>();

            string provider = NormalizeProvider(database.ProviderName);
            if (provider == "mssql") return LoadSqlServerConstraints(database, databaseName, tableName);
            if (provider == "mysql") return LoadMySqlConstraints(database, databaseName, tableName);
            return new List<TableDesignerConstraint>();
        }

        private static List<TableDesignerConstraint> LoadSqlServerConstraints(IDatabase database, string databaseName, string tableName)
        {
            SqlServerTableName target = ParseSqlServerTableName(tableName);
            string catalog = QuoteIdentifier("mssql", databaseName);
            string sql = @"
SELECT
    fk.name AS ConstraintName,
    'FOREIGN KEY' AS ConstraintKind,
    pc.name AS ColumnName,
    rc.name AS ReferencedColumnName,
    CASE WHEN rs.name = 'dbo' THEN rt.name ELSE rs.name + '.' + rt.name END AS ReferencedTable,
    fk.delete_referential_action_desc AS OnDelete,
    fk.update_referential_action_desc AS OnUpdate,
    CAST('' AS nvarchar(max)) AS Expression,
    fkc.constraint_column_id AS Ordinal
FROM " + catalog + @".sys.foreign_keys fk
INNER JOIN " + catalog + @".sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
INNER JOIN " + catalog + @".sys.tables pt ON pt.object_id = fk.parent_object_id
INNER JOIN " + catalog + @".sys.schemas ps ON ps.schema_id = pt.schema_id
INNER JOIN " + catalog + @".sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
INNER JOIN " + catalog + @".sys.tables rt ON rt.object_id = fk.referenced_object_id
INNER JOIN " + catalog + @".sys.schemas rs ON rs.schema_id = rt.schema_id
INNER JOIN " + catalog + @".sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
WHERE ps.name = @schemaName AND pt.name = @tableName
UNION ALL
SELECT
    cc.name AS ConstraintName,
    'CHECK' AS ConstraintKind,
    CAST('' AS sysname) AS ColumnName,
    CAST('' AS sysname) AS ReferencedColumnName,
    CAST('' AS nvarchar(512)) AS ReferencedTable,
    CAST('' AS nvarchar(60)) AS OnDelete,
    CAST('' AS nvarchar(60)) AS OnUpdate,
    cc.definition AS Expression,
    1 AS Ordinal
FROM " + catalog + @".sys.check_constraints cc
INNER JOIN " + catalog + @".sys.tables t ON t.object_id = cc.parent_object_id
INNER JOIN " + catalog + @".sys.schemas s ON s.schema_id = t.schema_id
WHERE s.name = @schemaName AND t.name = @tableName
ORDER BY ConstraintName, Ordinal;";

            DataTable rows = database.SelectSQL(sql, new Dictionary<string, object>
            {
                { "schemaName", target.Schema },
                { "tableName", target.Name }
            });
            return GroupMetadataRows(rows);
        }

        private static List<TableDesignerConstraint> LoadMySqlConstraints(IDatabase database, string databaseName, string tableName)
        {
            const string sql = @"
SELECT
    tc.CONSTRAINT_NAME AS ConstraintName,
    tc.CONSTRAINT_TYPE AS ConstraintKind,
    kcu.COLUMN_NAME AS ColumnName,
    kcu.REFERENCED_COLUMN_NAME AS ReferencedColumnName,
    kcu.REFERENCED_TABLE_NAME AS ReferencedTable,
    COALESCE(rc.DELETE_RULE, '') AS OnDelete,
    COALESCE(rc.UPDATE_RULE, '') AS OnUpdate,
    COALESCE(cc.CHECK_CLAUSE, '') AS Expression,
    COALESCE(kcu.ORDINAL_POSITION, 1) AS Ordinal
FROM information_schema.TABLE_CONSTRAINTS tc
LEFT JOIN information_schema.KEY_COLUMN_USAGE kcu
    ON kcu.CONSTRAINT_SCHEMA = tc.CONSTRAINT_SCHEMA
    AND kcu.TABLE_NAME = tc.TABLE_NAME
    AND kcu.CONSTRAINT_NAME = tc.CONSTRAINT_NAME
LEFT JOIN information_schema.REFERENTIAL_CONSTRAINTS rc
    ON rc.CONSTRAINT_SCHEMA = tc.CONSTRAINT_SCHEMA
    AND rc.TABLE_NAME = tc.TABLE_NAME
    AND rc.CONSTRAINT_NAME = tc.CONSTRAINT_NAME
LEFT JOIN information_schema.CHECK_CONSTRAINTS cc
    ON cc.CONSTRAINT_SCHEMA = tc.CONSTRAINT_SCHEMA
    AND cc.CONSTRAINT_NAME = tc.CONSTRAINT_NAME
WHERE tc.CONSTRAINT_SCHEMA = @databaseName
    AND tc.TABLE_NAME = @tableName
    AND tc.CONSTRAINT_TYPE IN ('FOREIGN KEY', 'CHECK')
ORDER BY tc.CONSTRAINT_NAME, kcu.ORDINAL_POSITION;";
            DataTable rows = database.SelectSQL(sql, new Dictionary<string, object>
            {
                { "databaseName", databaseName },
                { "tableName", tableName }
            });
            return GroupMetadataRows(rows);
        }

        private static List<TableDesignerConstraint> GroupMetadataRows(DataTable rows)
        {
            Dictionary<string, TableDesignerConstraint> output = new Dictionary<string, TableDesignerConstraint>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<string>> columns = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<string>> referenceColumns = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (rows == null) return new List<TableDesignerConstraint>();

            foreach (DataRow row in rows.Rows)
            {
                string name = ReadRow(row, "ConstraintName").Trim();
                string kind = NormalizeKind(ReadRow(row, "ConstraintKind"));
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(kind)) continue;

                TableDesignerConstraint item;
                if (!output.TryGetValue(name, out item))
                {
                    item = new TableDesignerConstraint
                    {
                        Name = name,
                        OriginalName = name,
                        Kind = kind,
                        ReferencedTable = ReadRow(row, "ReferencedTable"),
                        OnDelete = NormalizeAction(ReadRow(row, "OnDelete")),
                        OnUpdate = NormalizeAction(ReadRow(row, "OnUpdate")),
                        Expression = ReadRow(row, "Expression")
                    };
                    output.Add(name, item);
                    columns.Add(name, new List<string>());
                    referenceColumns.Add(name, new List<string>());
                }

                string column = ReadRow(row, "ColumnName").Trim();
                if (!string.IsNullOrWhiteSpace(column)) columns[name].Add(column);
                string referencedColumn = ReadRow(row, "ReferencedColumnName").Trim();
                if (!string.IsNullOrWhiteSpace(referencedColumn)) referenceColumns[name].Add(referencedColumn);
            }

            foreach (KeyValuePair<string, TableDesignerConstraint> pair in output)
            {
                pair.Value.Columns = string.Join(", ", columns[pair.Key].ToArray());
                pair.Value.ReferencedColumns = string.Join(", ", referenceColumns[pair.Key].ToArray());
            }
            return output.Values.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static List<TableDesignerConstraint> NormalizeItems(
            IEnumerable<TableDesignerConstraint> source,
            string qualifiedTableName,
            string provider,
            List<string> errors)
        {
            List<TableDesignerConstraint> result = new List<TableDesignerConstraint>();
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (TableDesignerConstraint raw in source ?? Enumerable.Empty<TableDesignerConstraint>())
            {
                if (raw == null || IsBlank(raw)) continue;
                TableDesignerConstraint item = new TableDesignerConstraint
                {
                    Name = (raw.Name ?? string.Empty).Trim(),
                    OriginalName = (raw.OriginalName ?? string.Empty).Trim(),
                    Kind = NormalizeKind(raw.Kind),
                    Columns = (raw.Columns ?? string.Empty).Trim(),
                    ReferencedTable = (raw.ReferencedTable ?? string.Empty).Trim(),
                    ReferencedColumns = (raw.ReferencedColumns ?? string.Empty).Trim(),
                    OnDelete = NormalizeAction(raw.OnDelete),
                    OnUpdate = NormalizeAction(raw.OnUpdate),
                    Expression = (raw.Expression ?? string.Empty).Trim()
                };
                if (string.IsNullOrWhiteSpace(item.Kind))
                {
                    errors.Add("約束類型只支援 FOREIGN KEY 或 CHECK。");
                    continue;
                }
                if (string.IsNullOrWhiteSpace(item.Name))
                {
                    item.Name = BuildDefaultName(item, qualifiedTableName, provider);
                }
                if (!IsSafeIdentifier(item.Name))
                {
                    errors.Add("約束名稱包含不支援的字元：" + item.Name);
                    continue;
                }
                if (!names.Add(item.Name))
                {
                    errors.Add("約束名稱不可重複：" + item.Name);
                    continue;
                }
                if (item.Kind == ForeignKeyKind)
                {
                    List<string> columns = ParseIdentifierList(item.Columns);
                    List<string> referenceColumns = ParseIdentifierList(item.ReferencedColumns);
                    if (columns.Count == 0 || referenceColumns.Count == 0 || columns.Count != referenceColumns.Count || string.IsNullOrWhiteSpace(item.ReferencedTable))
                    {
                        errors.Add("外鍵「" + item.Name + "」必須填寫數量相同的欄位與參照欄位，以及參照資料表。");
                        continue;
                    }
                    if (!IsSafeTableName(item.ReferencedTable))
                    {
                        errors.Add("外鍵「" + item.Name + "」的參照資料表格式不正確。");
                        continue;
                    }
                    item.Columns = string.Join(", ", columns.ToArray());
                    item.ReferencedColumns = string.Join(", ", referenceColumns.ToArray());
                }
                else if (!IsSafeCheckExpression(item.Expression))
                {
                    errors.Add("CHECK 約束「" + item.Name + "」必須填寫條件，且條件不可包含分號或 SQL 註解。");
                    continue;
                }
                result.Add(item);
            }
            return result;
        }

        private static Dictionary<string, TableDesignerConstraint> ToNameMap(IEnumerable<TableDesignerConstraint> items)
        {
            Dictionary<string, TableDesignerConstraint> result = new Dictionary<string, TableDesignerConstraint>(StringComparer.OrdinalIgnoreCase);
            foreach (TableDesignerConstraint item in items)
            {
                string key = string.IsNullOrWhiteSpace(item.OriginalName) ? item.Name : item.OriginalName;
                result[key] = item;
            }
            return result;
        }

        private static bool EquivalentLists(List<TableDesignerConstraint> left, List<TableDesignerConstraint> right)
        {
            if (left.Count != right.Count) return false;
            Dictionary<string, TableDesignerConstraint> leftMap = ToNameMap(left);
            Dictionary<string, TableDesignerConstraint> rightMap = ToNameMap(right);
            foreach (KeyValuePair<string, TableDesignerConstraint> pair in leftMap)
            {
                TableDesignerConstraint match;
                if (!rightMap.TryGetValue(pair.Key, out match) || !Equivalent(pair.Value, match)) return false;
            }
            return true;
        }

        private static bool Equivalent(TableDesignerConstraint left, TableDesignerConstraint right)
        {
            return string.Equals(NormalizeKind(left.Kind), NormalizeKind(right.Kind), StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(NormalizeComparable(left.Columns), NormalizeComparable(right.Columns), StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(NormalizeComparable(left.ReferencedTable), NormalizeComparable(right.ReferencedTable), StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(NormalizeComparable(left.ReferencedColumns), NormalizeComparable(right.ReferencedColumns), StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(NormalizeAction(left.OnDelete), NormalizeAction(right.OnDelete), StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(NormalizeAction(left.OnUpdate), NormalizeAction(right.OnUpdate), StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(NormalizeComparable(left.Expression), NormalizeComparable(right.Expression), StringComparison.Ordinal);
        }

        private static string BuildAddStatement(string provider, string qualifiedTableName, TableDesignerConstraint item)
        {
            return "ALTER TABLE " + qualifiedTableName + " ADD " + BuildConstraintDefinition(provider, item) + ";";
        }

        private static string BuildDropStatement(string provider, string qualifiedTableName, TableDesignerConstraint item)
        {
            if (provider == "mysql" && item.Kind == ForeignKeyKind)
            {
                return "ALTER TABLE " + qualifiedTableName + " DROP FOREIGN KEY " + QuoteIdentifier(provider, item.Name) + ";";
            }
            if (provider == "mysql" && item.Kind == CheckKind)
            {
                return "ALTER TABLE " + qualifiedTableName + " DROP CHECK " + QuoteIdentifier(provider, item.Name) + ";";
            }
            return "ALTER TABLE " + qualifiedTableName + " DROP CONSTRAINT " + QuoteIdentifier(provider, item.Name) + ";";
        }

        private static string BuildConstraintDefinition(string provider, TableDesignerConstraint item)
        {
            string prefix = "CONSTRAINT " + QuoteIdentifier(provider, item.Name) + " ";
            if (item.Kind == CheckKind)
            {
                return prefix + "CHECK (" + item.Expression + ")";
            }

            string columnList = string.Join(", ", ParseIdentifierList(item.Columns).Select(value => QuoteIdentifier(provider, value)).ToArray());
            string referenceTable = QuoteTableName(provider, item.ReferencedTable);
            string referenceColumns = string.Join(", ", ParseIdentifierList(item.ReferencedColumns).Select(value => QuoteIdentifier(provider, value)).ToArray());
            string onDelete = BuildActionClause("DELETE", item.OnDelete, provider);
            string onUpdate = BuildActionClause("UPDATE", item.OnUpdate, provider);
            return prefix + "FOREIGN KEY (" + columnList + ") REFERENCES " + referenceTable + " (" + referenceColumns + ")" + onDelete + onUpdate;
        }

        private static string BuildActionClause(string actionKind, string action, string provider)
        {
            if (string.IsNullOrWhiteSpace(action) || action == "NO ACTION") return "";
            if (provider == "oracle" && actionKind == "UPDATE") return "";
            return " ON " + actionKind + " " + action;
        }

        private static string BuildDefaultName(TableDesignerConstraint item, string qualifiedTableName, string provider)
        {
            string tablePart = LastIdentifierPart(qualifiedTableName);
            string suffix = item.Kind == CheckKind ? "rule" : FirstIdentifierPart(item.Columns);
            string prefix = item.Kind == CheckKind ? "CK_" : "FK_";
            string name = SanitizeIdentifier(prefix + tablePart + "_" + suffix);
            int maxLength = provider == "oracle" ? 30 : (provider == "mssql" ? 128 : 64);
            return name.Length <= maxLength ? name : name.Substring(0, maxLength);
        }

        private static string NormalizeKind(string value)
        {
            string normalized = (value ?? string.Empty).Trim().ToUpperInvariant();
            if (normalized == "FOREIGN KEY" || normalized == "FOREIGN_KEY" || normalized == "FK") return ForeignKeyKind;
            if (normalized == "CHECK" || normalized == "CHECK CONSTRAINT") return CheckKind;
            return string.Empty;
        }

        private static string NormalizeAction(string value)
        {
            string normalized = (value ?? string.Empty).Trim().Replace("_", " ").ToUpperInvariant();
            if (normalized == "NO ACTION" || normalized == "CASCADE" || normalized == "SET NULL" || normalized == "SET DEFAULT" || normalized == "RESTRICT") return normalized;
            return string.Empty;
        }

        private static string NormalizeProvider(string providerName)
        {
            return (providerName ?? string.Empty).Trim().ToLowerInvariant();
        }

        private static List<string> ParseIdentifierList(string value)
        {
            List<string> result = new List<string>();
            foreach (string part in (value ?? string.Empty).Split(','))
            {
                string item = part.Trim();
                if (string.IsNullOrWhiteSpace(item) || !IsSafeIdentifier(item)) return new List<string>();
                result.Add(item);
            }
            return result;
        }

        private static bool IsSafeIdentifier(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            foreach (char ch in value)
            {
                if (!(char.IsLetterOrDigit(ch) || ch == '_' || ch == '$' || ch == '#')) return false;
            }
            return true;
        }

        private static bool IsSafeTableName(string value)
        {
            string[] parts = (value ?? string.Empty).Split('.');
            return parts.Length > 0 && parts.Length <= 3 && parts.All(part => IsSafeIdentifier(part.Trim()));
        }

        private static bool IsSafeCheckExpression(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            return value.IndexOf(';') < 0 && value.IndexOf("--", StringComparison.Ordinal) < 0 && value.IndexOf("/*", StringComparison.Ordinal) < 0;
        }

        private static bool IsBlank(TableDesignerConstraint item)
        {
            return string.IsNullOrWhiteSpace(item.Name) && string.IsNullOrWhiteSpace(item.Kind) &&
                   string.IsNullOrWhiteSpace(item.Columns) && string.IsNullOrWhiteSpace(item.ReferencedTable) &&
                   string.IsNullOrWhiteSpace(item.ReferencedColumns) && string.IsNullOrWhiteSpace(item.Expression);
        }

        private static string QuoteTableName(string provider, string value)
        {
            return string.Join(".", value.Split('.').Select(part => QuoteIdentifier(provider, part.Trim())).ToArray());
        }

        private static string QuoteIdentifier(string provider, string value)
        {
            string name = value ?? string.Empty;
            if (provider == "mysql") return "`" + name.Replace("`", "``") + "`";
            if (provider == "mssql") return "[" + name.Replace("]", "]]" ) + "]";
            return "\"" + name.Replace("\"", "\"\"") + "\"";
        }

        private static string NormalizeComparable(string value)
        {
            return string.Join(" ", (value ?? string.Empty).Trim().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        }

        private static string FirstIdentifierPart(string value)
        {
            List<string> parts = ParseIdentifierList(value);
            return parts.Count == 0 ? "column" : parts[0];
        }

        private static string LastIdentifierPart(string value)
        {
            string[] parts = (value ?? string.Empty).Replace("[", "").Replace("]", "").Replace("`", "").Replace("\"", "").Split('.');
            return parts.Length == 0 ? "table" : parts[parts.Length - 1].Trim();
        }

        private static string SanitizeIdentifier(string value)
        {
            string cleaned = new string((value ?? string.Empty).Select(ch => char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_').ToArray());
            return string.IsNullOrWhiteSpace(cleaned.Trim('_')) ? "constraint" : cleaned;
        }

        private static string ReadRow(DataRow row, string columnName)
        {
            return row != null && row.Table.Columns.Contains(columnName) && row[columnName] != DBNull.Value ? row[columnName].ToString() : string.Empty;
        }

        private struct SqlServerTableName
        {
            public string Schema;
            public string Name;
        }

        private static SqlServerTableName ParseSqlServerTableName(string value)
        {
            string[] parts = (value ?? string.Empty).Split('.');
            if (parts.Length >= 2)
            {
                return new SqlServerTableName { Schema = parts[parts.Length - 2].Trim(), Name = parts[parts.Length - 1].Trim() };
            }
            return new SqlServerTableName { Schema = "dbo", Name = (value ?? string.Empty).Trim() };
        }
    }
}
