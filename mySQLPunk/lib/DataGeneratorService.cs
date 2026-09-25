using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace mySQLPunk.lib
{
    /// <summary>
    /// Windows 版資料產生器的 provider 包裝層：以既有 IDatabase 讀取欄位型別、自動編號／計算欄位、主鍵與唯一索引、
    /// 外鍵與既有資料列，交給 DataGeneratorCore 產生，寫入時沿用資料同步的單一交易路徑。
    /// </summary>
    public static class DataGeneratorService
    {
        private const int PageSize = 1000;

        public static bool IsSupported(string providerName, out string reason)
        {
            string provider = SchemaSyncScriptService.NormalizeProvider(providerName);
            if (provider == "mysql" || provider == "postgresql" || provider == "mssql" || provider == "sqlite")
            {
                reason = string.Empty;
                return true;
            }
            reason = Localization.Format("DataSync.UnsupportedProvider", provider);
            return false;
        }

        /// <summary>讀取一張資料表的產生資訊；includeRows 為 false 時不讀資料列（只供規則編輯畫面使用）。</summary>
        public static DataGeneratorTable LoadTable(IDatabase database, string databaseName, SchemaModelSnapshot snapshot, string tableName, bool includeRows)
        {
            SchemaTableModel model = snapshot.Tables.FirstOrDefault(item => string.Equals(item.Name, tableName, StringComparison.OrdinalIgnoreCase));
            if (model == null) return null;
            string provider = SchemaSyncScriptService.NormalizeProvider(database.ProviderName);
            DataTable catalog = database.GetColumns(databaseName, model.Name) ?? new DataTable();
            DataSyncService.ThrowIfQueryFailed(catalog);
            HashSet<string> computed = new HashSet<string>(DataSyncService.GetComputedColumns(database, databaseName, model.Name), StringComparer.OrdinalIgnoreCase);
            HashSet<string> identity = new HashSet<string>(DataSyncService.GetIdentityColumns(database, databaseName, model.Name), StringComparer.OrdinalIgnoreCase);
            List<string> primaryKey = model.Columns.Where(column => column.IsPrimaryKey).OrderBy(column => column.Ordinal).Select(column => column.Name).ToList();

            DataGeneratorTable table = new DataGeneratorTable { Name = model.Name };
            foreach (SchemaColumnModel column in model.Columns.OrderBy(item => item.Ordinal))
            {
                DataRow row = catalog.Rows.Cast<DataRow>().FirstOrDefault(item =>
                    string.Equals(Read(item, "Field", "COLUMN_NAME", "column_name", "name", "Name"), column.Name, StringComparison.OrdinalIgnoreCase));
                string extra = row == null ? string.Empty : Read(row, "Extra");
                string defaultValue = row == null ? string.Empty : Read(row, "Default", "COLUMN_DEFAULT", "column_default", "dflt_value");
                bool hasDefault = row != null && HasValue(row, "Default", "COLUMN_DEFAULT", "column_default", "dflt_value");
                DataGeneratorColumn generated = new DataGeneratorColumn
                {
                    Name = column.Name,
                    Ordinal = column.Ordinal,
                    IsNullable = column.IsNullable,
                    IsPrimaryKey = column.IsPrimaryKey,
                    HasDefault = hasDefault,
                    IsComputed = computed.Contains(column.Name) || extra.IndexOf("GENERATED", StringComparison.OrdinalIgnoreCase) >= 0,
                    TemporalAsText = provider == "postgresql" || provider == "sqlite",
                    BooleanAsInteger = provider == "sqlite"
                };
                generated.IsAutoNumber = !generated.IsComputed && (
                    identity.Contains(column.Name) ||
                    extra.IndexOf("auto_increment", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    defaultValue.StartsWith("nextval(", StringComparison.OrdinalIgnoreCase) ||
                    row != null && string.Equals(Read(row, "IsIdentity"), "YES", StringComparison.OrdinalIgnoreCase) ||
                    provider == "sqlite" && primaryKey.Count == 1 && column.IsPrimaryKey &&
                    string.Equals((row == null ? column.DataType : Read(row, "type")).Trim(), "INTEGER", StringComparison.OrdinalIgnoreCase));
                Classify(provider, row, column, generated);
                table.Columns.Add(generated);
            }

            if (primaryKey.Count > 0) table.UniqueSets.Add(primaryKey.ToArray());
            foreach (string[] set in LoadUniqueSets(database, databaseName, provider, model.Name))
            {
                if (set.Length == 0 || set.Any(name => table.Columns.All(column => !string.Equals(column.Name, name, StringComparison.OrdinalIgnoreCase)))) continue;
                if (table.UniqueSets.Any(existing => existing.Length == set.Length &&
                                                     existing.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                                                         .SequenceEqual(set.OrderBy(name => name, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))) continue;
                table.UniqueSets.Add(set);
            }

            foreach (IGrouping<string, SchemaRelationshipModel> group in snapshot.Relationships
                         .Where(item => string.Equals(item.FromTable, model.Name, StringComparison.OrdinalIgnoreCase))
                         .GroupBy(item => item.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            {
                List<SchemaRelationshipModel> parts = group.OrderBy(item => item.Ordinal).ToList();
                SchemaTableModel parent = snapshot.Tables.FirstOrDefault(item => string.Equals(item.Name, parts[0].ToTable, StringComparison.OrdinalIgnoreCase));
                List<string> parentColumns = parts.Select(item => item.ToColumn).ToList();
                if (parent != null && parentColumns.Any(string.IsNullOrWhiteSpace))
                {
                    // SQLite 的 REFERENCES parent 省略欄位時指向父表主鍵。
                    parentColumns = parent.Columns.Where(item => item.IsPrimaryKey).OrderBy(item => item.Ordinal).Select(item => item.Name).ToList();
                }
                table.ForeignKeys.Add(new DataGeneratorForeignKey
                {
                    Name = group.Key,
                    Columns = parts.Select(item => item.FromColumn).ToList(),
                    ParentTable = parent != null ? parent.Name : parts[0].ToTable,
                    ParentColumns = parentColumns
                });
            }

            if (includeRows)
            {
                bool truncated;
                table.ExistingRows = LoadExisting(database, databaseName, model.Name, out truncated);
                table.ExistingTruncated = truncated;
            }
            return table;
        }

        public static DataGenerationResult Generate(IDatabase database, string databaseName, SchemaModelSnapshot snapshot, IList<DataGeneratorPlan> plans, int? seed)
        {
            List<string> ordered = DataSyncService.OrderByDependencies(plans.Select(plan => plan.TableName).ToList(), snapshot);
            List<DataGeneratorPlan> orderedPlans = ordered
                .Select(name => plans.First(plan => string.Equals(plan.TableName, name, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            return DataGeneratorCore.Generate(orderedPlans, name => LoadTable(database, databaseName, snapshot, name, true), seed);
        }

        public static string BuildPreview(IDatabase database, DataGenerationResult result, int maximumRowsPerTable)
        {
            StringBuilder text = new StringBuilder();
            foreach (DataTableComparison table in result.Tables)
            {
                int shown = Math.Min(maximumRowsPerTable, table.Changes.Count);
                DataTableComparison head = new DataTableComparison { TableName = table.TableName };
                head.KeyColumns.AddRange(table.KeyColumns);
                head.Changes.AddRange(table.Changes.Take(shown));
                string body = DataSyncService.BuildPreview(database, head, false);
                int firstLine = body.IndexOf('\n');
                text.AppendLine("-- " + SchemaSyncScriptService.SingleLine(shown < table.Changes.Count
                    ? Localization.Format("DataGen.PreviewTablePartial", table.TableName, table.Changes.Count, shown)
                    : Localization.Format("DataGen.PreviewTable", table.TableName, table.Changes.Count)));
                text.Append(firstLine < 0 ? string.Empty : body.Substring(firstLine + 1));
                text.AppendLine();
            }
            return text.ToString();
        }

        /// <summary>所有資料表在單一交易中寫入；任何一列失敗就整批回滾。</summary>
        public static DataSyncResult Apply(IDatabase database, string databaseName, DataGenerationResult result)
        {
            if (result == null || !result.Succeeded) throw new InvalidOperationException(Localization.T("DataGen.Error.NotGenerated"));
            return DataSyncService.Apply(database, databaseName, result.Tables, false);
        }

        private static void Classify(string provider, DataRow row, SchemaColumnModel model, DataGeneratorColumn column)
        {
            string type;
            int? length = null;
            if (provider == "mssql" && row != null)
            {
                type = Read(row, "DATA_TYPE").ToLowerInvariant();
                int characters = ReadInt(row, "CHARACTER_MAXIMUM_LENGTH");
                if (characters > 0) length = characters;
                int precision = ReadInt(row, "NUMERIC_PRECISION");
                int scale = ReadInt(row, "NUMERIC_SCALE");
                if ((type == "decimal" || type == "numeric") && precision > 0)
                {
                    column.Precision = precision;
                    column.Scale = scale;
                }
                column.TypeText = type + (characters > 0 ? "(" + characters.ToString(CultureInfo.InvariantCulture) + ")" : characters == -1 ? "(max)" : string.Empty) +
                                  (column.Precision.HasValue ? "(" + precision.ToString(CultureInfo.InvariantCulture) + "," + scale.ToString(CultureInfo.InvariantCulture) + ")" : string.Empty);
            }
            else
            {
                string raw = row == null ? model.DataType : provider == "mysql" ? Read(row, "Type") : provider == "postgresql" ? Read(row, "ProviderType", "data_type") : Read(row, "type");
                if (string.IsNullOrWhiteSpace(raw)) raw = model.DataType ?? string.Empty;
                column.TypeText = raw;
                type = raw.Trim().ToLowerInvariant();
            }

            Match parens = Regex.Match(type, @"\((\d+)(?:\s*,\s*(\d+))?\)");
            if (parens.Success && !length.HasValue)
            {
                length = int.Parse(parens.Groups[1].Value, CultureInfo.InvariantCulture);
                if (parens.Groups[2].Success)
                {
                    column.Precision = length;
                    column.Scale = int.Parse(parens.Groups[2].Value, CultureInfo.InvariantCulture);
                }
            }

            bool unsigned = type.Contains("unsigned");
            string baseType = Regex.Replace(type, @"\(.*?\)", string.Empty).Replace("unsigned", string.Empty).Replace("zerofill", string.Empty).Trim();

            if (provider == "sqlite")
            {
                ClassifySqlite(type, length, column);
                return;
            }

            if (provider == "mysql" && (type.StartsWith("enum(", StringComparison.Ordinal) || type.StartsWith("set(", StringComparison.Ordinal)))
            {
                column.Kind = GeneratedValueKind.String;
                column.IsSet = type.StartsWith("set(", StringComparison.Ordinal);
                foreach (Match member in Regex.Matches(column.TypeText, @"'((?:[^']|'')*)'"))
                {
                    column.EnumValues.Add(member.Groups[1].Value.Replace("''", "'"));
                }
                return;
            }

            if (provider == "mysql" && (type == "tinyint(1)" || type == "bit(1)") || baseType == "bool" || baseType == "boolean" || provider == "mssql" && baseType == "bit")
            {
                column.Kind = GeneratedValueKind.Boolean;
                return;
            }

            long minimum, maximum;
            if (IntegerRange(provider, baseType, unsigned, out minimum, out maximum))
            {
                column.Kind = GeneratedValueKind.Integer;
                column.IntegerMinimum = minimum;
                column.IntegerMaximum = maximum;
                return;
            }

            switch (baseType)
            {
                case "year":
                    column.Kind = GeneratedValueKind.Year;
                    column.IntegerMinimum = 1901;
                    column.IntegerMaximum = 2155;
                    return;
                case "decimal":
                case "numeric":
                case "dec":
                case "fixed":
                case "float":
                case "double":
                case "real":
                case "double precision":
                case "float4":
                case "float8":
                    column.Kind = GeneratedValueKind.Decimal;
                    return;
                case "money":
                case "smallmoney":
                    column.Kind = GeneratedValueKind.Decimal;
                    column.Scale = 2;
                    return;
                case "date":
                    column.Kind = GeneratedValueKind.Date;
                    return;
                case "datetime":
                case "datetime2":
                case "smalldatetime":
                case "timestamp without time zone":
                    column.Kind = GeneratedValueKind.DateTime;
                    return;
                case "timestamp":
                    // SQL Server 的 timestamp 是 rowversion，由資料庫產生。
                    column.Kind = provider == "mssql" ? GeneratedValueKind.Unsupported : GeneratedValueKind.DateTime;
                    return;
                case "timestamp with time zone":
                case "datetimeoffset":
                    column.Kind = GeneratedValueKind.DateTimeOffset;
                    return;
                case "time":
                case "time without time zone":
                    column.Kind = GeneratedValueKind.Time;
                    return;
                case "uuid":
                case "uniqueidentifier":
                    column.Kind = GeneratedValueKind.Guid;
                    if (provider == "mysql") column.TemporalAsText = true;
                    return;
                case "char":
                case "varchar":
                case "nchar":
                case "nvarchar":
                case "text":
                case "ntext":
                case "tinytext":
                case "mediumtext":
                case "longtext":
                case "character varying":
                case "character":
                case "bpchar":
                case "citext":
                    column.Kind = GeneratedValueKind.String;
                    column.MaxLength = length;
                    return;
                case "json":
                case "jsonb":
                    column.Kind = GeneratedValueKind.Json;
                    return;
                case "xml":
                    column.Kind = GeneratedValueKind.Xml;
                    return;
                case "binary":
                    column.Kind = GeneratedValueKind.Binary;
                    column.FixedBinaryLength = length ?? 1;
                    return;
                case "varbinary":
                case "blob":
                case "tinyblob":
                case "mediumblob":
                case "longblob":
                case "bytea":
                case "image":
                    column.Kind = GeneratedValueKind.Binary;
                    column.MaxLength = length;
                    return;
                case "inet":
                case "cidr":
                case "inet4":
                    column.Kind = GeneratedValueKind.NetworkAddress;
                    return;
                default:
                    column.Kind = GeneratedValueKind.Unsupported;
                    return;
            }
        }

        /// <summary>SQLite 依型別名稱的親和性規則分類。</summary>
        private static void ClassifySqlite(string type, int? length, DataGeneratorColumn column)
        {
            string upper = type.ToUpperInvariant();
            if (upper.Contains("INT"))
            {
                column.Kind = GeneratedValueKind.Integer;
                column.IntegerMinimum = long.MinValue;
                column.IntegerMaximum = long.MaxValue;
            }
            else if (upper.Contains("BOOL"))
            {
                column.Kind = GeneratedValueKind.Boolean;
            }
            else if (upper.Contains("DATETIME") || upper.Contains("TIMESTAMP"))
            {
                column.Kind = GeneratedValueKind.DateTime;
            }
            else if (upper.Contains("DATE"))
            {
                column.Kind = GeneratedValueKind.Date;
            }
            else if (upper.Contains("TIME"))
            {
                column.Kind = GeneratedValueKind.Time;
            }
            else if (upper.Contains("GUID") || upper.Contains("UUID"))
            {
                column.Kind = GeneratedValueKind.Guid;
            }
            else if (upper.Contains("CHAR") || upper.Contains("CLOB") || upper.Contains("TEXT") || upper.Length == 0)
            {
                column.Kind = GeneratedValueKind.String;
                column.MaxLength = upper.Length == 0 ? null : length;
            }
            else if (upper.Contains("BLOB"))
            {
                column.Kind = GeneratedValueKind.Binary;
            }
            else if (upper.Contains("JSON"))
            {
                column.Kind = GeneratedValueKind.Json;
            }
            else
            {
                column.Kind = GeneratedValueKind.Decimal;
            }
        }

        private static bool IntegerRange(string provider, string baseType, bool unsigned, out long minimum, out long maximum)
        {
            switch (baseType)
            {
                case "tinyint":
                    if (provider == "mssql" || unsigned) { minimum = 0; maximum = 255; }
                    else { minimum = -128; maximum = 127; }
                    return true;
                case "smallint":
                case "int2":
                case "smallserial":
                    if (unsigned) { minimum = 0; maximum = 65535; }
                    else { minimum = short.MinValue; maximum = short.MaxValue; }
                    return true;
                case "mediumint":
                    if (unsigned) { minimum = 0; maximum = 16777215; }
                    else { minimum = -8388608; maximum = 8388607; }
                    return true;
                case "int":
                case "integer":
                case "int4":
                case "serial":
                    if (unsigned) { minimum = 0; maximum = uint.MaxValue; }
                    else { minimum = int.MinValue; maximum = int.MaxValue; }
                    return true;
                case "bigint":
                case "int8":
                case "bigserial":
                    minimum = unsigned ? 0 : long.MinValue;
                    maximum = long.MaxValue;
                    return true;
                default:
                    minimum = 0;
                    maximum = 0;
                    return false;
            }
        }

        private static IEnumerable<string[]> LoadUniqueSets(IDatabase database, string databaseName, string provider, string tableName)
        {
            string schema, table;
            DataSyncService.SplitName(provider, tableName, out schema, out table);
            DataTable rows;
            switch (provider)
            {
                case "mysql":
                    rows = database.SelectSQL(
                        "SELECT INDEX_NAME AS index_name, COLUMN_NAME AS column_name FROM information_schema.STATISTICS " +
                        "WHERE TABLE_SCHEMA = ?db AND TABLE_NAME = ?tableName AND NON_UNIQUE = 0 AND COLUMN_NAME IS NOT NULL ORDER BY INDEX_NAME, SEQ_IN_INDEX",
                        new Dictionary<string, object> { { "db", databaseName }, { "tableName", table } });
                    break;
                case "postgresql":
                    rows = database.SelectSQL(
                        "SELECT i.relname AS index_name, a.attname AS column_name FROM pg_index x " +
                        "JOIN pg_class t ON t.oid = x.indrelid JOIN pg_namespace n ON n.oid = t.relnamespace JOIN pg_class i ON i.oid = x.indexrelid " +
                        "JOIN LATERAL unnest(x.indkey::int2[]) WITH ORDINALITY AS k(attnum, ord) ON true " +
                        "JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.attnum " +
                        "WHERE x.indisunique AND x.indpred IS NULL AND NOT (0 = ANY (x.indkey::int2[])) AND n.nspname = :schema AND t.relname = :tableName " +
                        "ORDER BY i.relname, k.ord",
                        new Dictionary<string, object> { { "schema", schema }, { "tableName", table } });
                    break;
                case "mssql":
                    string prefix = "[" + (databaseName ?? string.Empty).Replace("]", "]]") + "]";
                    rows = database.SelectSQL(
                        "SELECT i.name AS index_name, c.name AS column_name FROM " + prefix + ".sys.indexes i " +
                        "JOIN " + prefix + ".sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id " +
                        "JOIN " + prefix + ".sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id " +
                        "WHERE i.object_id = OBJECT_ID(@name) AND i.is_unique = 1 AND i.has_filter = 0 AND ic.is_included_column = 0 ORDER BY i.name, ic.key_ordinal",
                        new Dictionary<string, object> { { "name", prefix + ".[" + schema.Replace("]", "]]") + "].[" + table.Replace("]", "]]") + "]" } });
                    break;
                case "sqlite":
                    rows = database.SelectSQL(
                        "SELECT il.name AS index_name, ii.name AS column_name FROM pragma_index_list(@tableName) il " +
                        "JOIN pragma_index_info(il.name) ii WHERE il.\"unique\" = 1 AND il.partial = 0 ORDER BY il.name, ii.seqno",
                        new Dictionary<string, object> { { "tableName", table } });
                    break;
                default:
                    return Enumerable.Empty<string[]>();
            }
            DataSyncService.ThrowIfQueryFailed(rows);
            return rows.Rows.Cast<DataRow>()
                .GroupBy(row => System.Convert.ToString(row["index_name"], CultureInfo.InvariantCulture), StringComparer.Ordinal)
                .Where(group => group.All(row => !(row["column_name"] is DBNull)))
                .Select(group => group.Select(row => System.Convert.ToString(row["column_name"], CultureInfo.InvariantCulture)).ToArray())
                .ToList();
        }

        private static DataTable LoadExisting(IDatabase database, string databaseName, string tableName, out bool truncated)
        {
            DataTable all = null;
            long offset = 0;
            truncated = false;
            while (true)
            {
                DataTable page = database.SelectTablePage(databaseName, tableName, offset, PageSize);
                DataSyncService.ThrowIfQueryFailed(page);
                if (all == null) all = page.Clone();
                foreach (DataRow row in page.Rows) all.ImportRow(row);
                if (page.Rows.Count < PageSize) return all;
                if (all.Rows.Count >= DataGeneratorCore.ExistingRowLimit)
                {
                    truncated = true;
                    return all;
                }
                offset += page.Rows.Count;
            }
        }

        private static string Read(DataRow row, params string[] names)
        {
            foreach (string name in names)
            {
                if (row.Table.Columns.Contains(name) && !(row[name] is DBNull)) return System.Convert.ToString(row[name], CultureInfo.InvariantCulture) ?? string.Empty;
            }
            return string.Empty;
        }

        private static bool HasValue(DataRow row, params string[] names)
        {
            return names.Any(name => row.Table.Columns.Contains(name) && !(row[name] is DBNull) &&
                                     !string.Equals(System.Convert.ToString(row[name], CultureInfo.InvariantCulture), "NULL", StringComparison.OrdinalIgnoreCase));
        }

        private static int ReadInt(DataRow row, string name)
        {
            int value;
            return row.Table.Columns.Contains(name) && !(row[name] is DBNull) &&
                   int.TryParse(System.Convert.ToString(row[name], CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                ? value
                : 0;
        }
    }
}
