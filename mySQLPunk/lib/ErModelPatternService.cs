using System;
using System.Collections.Generic;
using System.Linq;

namespace mySQLPunk.lib
{
    /// <summary>資料表在維度模型或 Data Vault 2.0 模型中的角色。</summary>
    public static class ErTableRoles
    {
        public const string Fact = "Fact";
        public const string Dimension = "Dimension";
        public const string Hub = "Hub";
        public const string Link = "Link";
        public const string Satellite = "Satellite";

        public static readonly string[] All = { Fact, Dimension, Hub, Link, Satellite };

        public static string Normalize(string role)
        {
            return All.FirstOrDefault(item => string.Equals(item, (role ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase));
        }

        public static string Badge(string role)
        {
            switch (Normalize(role))
            {
                case Fact: return "FACT";
                case Dimension: return "DIM";
                case Hub: return "HUB";
                case Link: return "LNK";
                case Satellite: return "SAT";
                default: return null;
            }
        }

        /// <summary>角色的表頭顏色（沒有群組顏色時使用）。</summary>
        public static string Color(string role)
        {
            switch (Normalize(role))
            {
                case Fact: return "#f59e0b";
                case Dimension: return "#10b981";
                case Hub: return "#3b82f6";
                case Link: return "#ef4444";
                case Satellite: return "#eab308";
                default: return null;
            }
        }
    }

    public sealed class ErModelTableRole
    {
        public string Table { get; set; }
        public string Role { get; set; }
    }

    public sealed class DataVaultResult
    {
        public DataVaultResult()
        {
            Hubs = new List<string>();
            Links = new List<string>();
            Satellites = new List<string>();
            Skipped = new List<string>();
        }

        public List<string> Hubs { get; private set; }
        public List<string> Links { get; private set; }
        public List<string> Satellites { get; private set; }
        public List<string> Skipped { get; private set; }
        public IEnumerable<string> AllTables { get { return Hubs.Concat(Links).Concat(Satellites); } }
    }

    /// <summary>
    /// 維度模型與 Data Vault 2.0：依結構推測角色，並把關聯式資料表轉成 Hub（業務鍵＋雜湊鍵）、
    /// Satellite（描述屬性＋hash diff，依載入時間保留歷史）與 Link（每個外鍵一個）。
    /// 只修改模型；要建立到資料庫請用「同步模型到資料庫」審核執行。
    /// </summary>
    public static class ErModelPatternService
    {
        public static string RoleOf(ErModelDocument document, string table)
        {
            if (document == null || document.Roles == null) return null;
            ErModelTableRole role = document.Roles.FirstOrDefault(item => string.Equals(item.Table, table, StringComparison.OrdinalIgnoreCase));
            return role == null ? null : ErTableRoles.Normalize(role.Role);
        }

        public static void SetRole(ErModelDocument document, string table, string role)
        {
            document.Roles = document.Roles ?? new List<ErModelTableRole>();
            document.Roles.RemoveAll(item => string.Equals(item.Table, table, StringComparison.OrdinalIgnoreCase));
            string normalized = ErTableRoles.Normalize(role);
            if (normalized != null) document.Roles.Add(new ErModelTableRole { Table = table, Role = normalized });
        }

        /// <summary>
        /// 推測角色。Data Vault：名稱前綴 hub_／h_、lnk_／link_／l_、sat_／s_。維度：名稱前綴 fact_／fct_、dim_／d_；
        /// 否則有兩個以上外鍵且有數值欄位的表視為事實表，被事實表參照的表視為維度表。
        /// </summary>
        public static Dictionary<string, string> SuggestRoles(SchemaModelSnapshot snapshot)
        {
            Dictionary<string, string> roles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (SchemaTableModel table in snapshot.Tables)
            {
                string name = table.Name.ToLowerInvariant();
                int dot = name.LastIndexOf('.');
                if (dot >= 0) name = name.Substring(dot + 1);
                if (name.StartsWith("hub_") || name.StartsWith("h_")) roles[table.Name] = ErTableRoles.Hub;
                else if (name.StartsWith("lnk_") || name.StartsWith("link_") || name.StartsWith("l_")) roles[table.Name] = ErTableRoles.Link;
                else if (name.StartsWith("sat_") || name.StartsWith("s_")) roles[table.Name] = ErTableRoles.Satellite;
                else if (name.StartsWith("fact_") || name.StartsWith("fct_") || name.StartsWith("f_")) roles[table.Name] = ErTableRoles.Fact;
                else if (name.StartsWith("dim_") || name.StartsWith("d_")) roles[table.Name] = ErTableRoles.Dimension;
            }
            if (roles.Count > 0) return roles;

            foreach (SchemaTableModel table in snapshot.Tables)
            {
                int foreignKeys = snapshot.Relationships.Where(item => string.Equals(item.FromTable, table.Name, StringComparison.OrdinalIgnoreCase))
                    .Select(item => item.Name + "|" + item.ToTable).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                HashSet<string> keyColumns = new HashSet<string>(snapshot.Relationships.Where(item => string.Equals(item.FromTable, table.Name, StringComparison.OrdinalIgnoreCase)).Select(item => item.FromColumn), StringComparer.OrdinalIgnoreCase);
                bool measures = table.Columns.Any(column => !column.IsPrimaryKey && !keyColumns.Contains(column.Name) && IsNumeric(column.DataType));
                if (foreignKeys >= 2 && measures) roles[table.Name] = ErTableRoles.Fact;
            }
            foreach (SchemaRelationshipModel relationship in snapshot.Relationships)
            {
                string from;
                if (roles.TryGetValue(relationship.FromTable, out from) && from == ErTableRoles.Fact && !roles.ContainsKey(relationship.ToTable))
                {
                    roles[relationship.ToTable] = ErTableRoles.Dimension;
                }
            }
            return roles;
        }

        /// <summary>把指定的關聯式資料表轉成 Data Vault 2.0 結構並加入模型（已存在的同名表會略過）。</summary>
        public static DataVaultResult AddDataVault(ErModelDocument document, IEnumerable<string> tableNames, string diagramName)
        {
            ErModelSchema schema = document.Schema;
            if (schema == null) throw new InvalidOperationException(Localization.T("ErModel.Error.NoSchema"));
            string provider = SchemaSyncScriptService.NormalizeProvider(schema.Provider);
            string hashType = provider == "sqlite" ? "TEXT" : "char(32)";
            string timeType = provider == "mysql" ? "datetime(6)" : provider == "postgresql" ? "timestamp" : provider == "mssql" ? "datetime2" : "TEXT";
            string sourceType = provider == "sqlite" ? "TEXT" : "varchar(100)";
            HashSet<string> selected = new HashSet<string>(tableNames ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            List<ErModelTable> sources = schema.Tables.Where(table => selected.Contains(table.Name)).ToList();
            if (sources.Count == 0) throw new InvalidOperationException(Localization.T("ErModel.DataVault.NothingSelected"));

            DataVaultResult result = new DataVaultResult();
            List<ErModelTable> added = new List<ErModelTable>();
            List<ErModelRelationship> relationships = new List<ErModelRelationship>();
            Func<string, bool> exists = name => schema.Find(name) != null || added.Any(table => string.Equals(table.Name, name, StringComparison.OrdinalIgnoreCase));
            Dictionary<string, string> hubOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (ErModelTable source in sources)
            {
                string baseName = BaseName(source.Name);
                List<ErModelColumn> keys = source.Columns.Where(column => column.PrimaryKey).ToList();
                if (keys.Count == 0)
                {
                    result.Skipped.Add(Localization.Format("ErModel.DataVault.NoKey", source.Name));
                    continue;
                }
                string hubName = "hub_" + baseName;
                string hashKey = "hk_" + baseName;
                hubOf[source.Name] = hubName;
                if (!exists(hubName))
                {
                    ErModelTable hub = new ErModelTable { Name = hubName };
                    hub.Columns.Add(new ErModelColumn { Name = hashKey, DataType = hashType, PrimaryKey = true, Nullable = false });
                    foreach (ErModelColumn key in keys) hub.Columns.Add(new ErModelColumn { Name = key.Name, DataType = key.DataType, Nullable = false });
                    hub.Columns.Add(new ErModelColumn { Name = "load_dts", DataType = timeType, Nullable = false });
                    hub.Columns.Add(new ErModelColumn { Name = "record_source", DataType = sourceType, Nullable = false });
                    added.Add(hub);
                    result.Hubs.Add(hubName);
                }

                List<ErModelColumn> attributes = source.Columns.Where(column => !column.PrimaryKey && !IsForeignKeyColumn(schema, source.Name, column.Name)).ToList();
                string satelliteName = "sat_" + baseName;
                if (attributes.Count > 0 && !exists(satelliteName))
                {
                    ErModelTable satellite = new ErModelTable { Name = satelliteName };
                    satellite.Columns.Add(new ErModelColumn { Name = hashKey, DataType = hashType, PrimaryKey = true, Nullable = false });
                    satellite.Columns.Add(new ErModelColumn { Name = "load_dts", DataType = timeType, PrimaryKey = true, Nullable = false });
                    satellite.Columns.Add(new ErModelColumn { Name = "hash_diff", DataType = hashType, Nullable = false });
                    satellite.Columns.Add(new ErModelColumn { Name = "record_source", DataType = sourceType, Nullable = false });
                    foreach (ErModelColumn attribute in attributes)
                    {
                        if (satellite.Columns.Any(column => string.Equals(column.Name, attribute.Name, StringComparison.OrdinalIgnoreCase))) continue;
                        satellite.Columns.Add(new ErModelColumn { Name = attribute.Name, DataType = attribute.DataType, Nullable = true });
                    }
                    added.Add(satellite);
                    relationships.Add(new ErModelRelationship { Name = "fk_" + Trim(satelliteName + "_" + hubName), FromTable = satelliteName, FromColumn = hashKey, ToTable = hubName, ToColumn = hashKey });
                    result.Satellites.Add(satelliteName);
                }
            }

            // 每個外鍵（兩端都有 Hub）產生一個 Link，連接兩個 Hub 的雜湊鍵。
            foreach (IGrouping<string, ErModelRelationship> foreignKey in schema.Relationships
                         .Where(item => hubOf.ContainsKey(item.FromTable) && hubOf.ContainsKey(item.ToTable))
                         .GroupBy(item => item.FromTable + "|" + (item.Name ?? item.FromColumn) + "|" + item.ToTable, StringComparer.OrdinalIgnoreCase))
            {
                ErModelRelationship first = foreignKey.First();
                string fromHub = hubOf[first.FromTable];
                string toHub = hubOf[first.ToTable];
                string fromKey = "hk_" + BaseName(first.FromTable);
                string toKey = "hk_" + BaseName(first.ToTable);
                string linkName = "lnk_" + BaseName(first.FromTable) + "_" + BaseName(first.ToTable);
                if (string.Equals(fromKey, toKey, StringComparison.OrdinalIgnoreCase)) toKey = "hk_" + BaseName(first.ToTable) + "_parent";
                if (exists(linkName)) continue;
                ErModelTable link = new ErModelTable { Name = linkName };
                link.Columns.Add(new ErModelColumn { Name = "hk_" + BaseName(linkName), DataType = hashType, PrimaryKey = true, Nullable = false });
                link.Columns.Add(new ErModelColumn { Name = fromKey, DataType = hashType, Nullable = false });
                link.Columns.Add(new ErModelColumn { Name = toKey, DataType = hashType, Nullable = false });
                link.Columns.Add(new ErModelColumn { Name = "load_dts", DataType = timeType, Nullable = false });
                link.Columns.Add(new ErModelColumn { Name = "record_source", DataType = sourceType, Nullable = false });
                added.Add(link);
                relationships.Add(new ErModelRelationship { Name = "fk_" + Trim(linkName + "_" + fromHub), FromTable = linkName, FromColumn = fromKey, ToTable = fromHub, ToColumn = "hk_" + BaseName(first.FromTable) });
                relationships.Add(new ErModelRelationship { Name = "fk_" + Trim(linkName + "_" + toHub + "_2"), FromTable = linkName, FromColumn = toKey, ToTable = toHub, ToColumn = "hk_" + BaseName(first.ToTable) });
                result.Links.Add(linkName);
            }

            if (added.Count == 0) return result;
            ErModelSchema candidateSchema = new ErModelSchema
            {
                Provider = schema.Provider,
                Tables = schema.Tables.Concat(added).ToList(),
                Relationships = schema.Relationships.Concat(relationships).ToList(),
                Routines = schema.Routines
            };
            ErModelService.ValidateSchema(candidateSchema);
            document.Schema = candidateSchema;
            foreach (string name in result.Hubs) SetRole(document, name, ErTableRoles.Hub);
            foreach (string name in result.Links) SetRole(document, name, ErTableRoles.Link);
            foreach (string name in result.Satellites) SetRole(document, name, ErTableRoles.Satellite);

            string title = string.IsNullOrWhiteSpace(diagramName) ? "Data Vault" : diagramName.Trim();
            string unique = title;
            int number = 2;
            while (document.Diagrams.Any(item => string.Equals(item.Name, unique, StringComparison.OrdinalIgnoreCase))) unique = title + " " + number++;
            ErModelDiagram diagram = new ErModelDiagram { Name = unique };
            diagram.Tables.AddRange(result.AllTables.Select(name => new ErModelTablePlacement { Table = name }));
            document.Diagrams.Add(diagram);
            ErModelService.ApplyLayeredLayout(diagram, ErModelService.ToSnapshot(candidateSchema, document.Database));
            return result;
        }

        private static bool IsForeignKeyColumn(ErModelSchema schema, string table, string column)
        {
            return schema.Relationships.Any(item => string.Equals(item.FromTable, table, StringComparison.OrdinalIgnoreCase) && string.Equals(item.FromColumn, column, StringComparison.OrdinalIgnoreCase));
        }

        private static string BaseName(string table)
        {
            string name = table ?? string.Empty;
            int dot = name.LastIndexOf('.');
            if (dot >= 0) name = name.Substring(dot + 1);
            return new string(name.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
        }

        private static string Trim(string name)
        {
            return name.Length <= 55 ? name : name.Substring(0, 55);
        }

        private static bool IsNumeric(string dataType)
        {
            string type = (dataType ?? string.Empty).ToLowerInvariant();
            return new[] { "int", "decimal", "numeric", "number", "real", "double", "float", "money" }.Any(type.Contains);
        }
    }
}
