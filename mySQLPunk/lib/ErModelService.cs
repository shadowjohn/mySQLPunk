using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace mySQLPunk.lib
{
    public sealed class ErModelGroup
    {
        public string Name { get; set; }
        /// <summary>#RRGGBB。</summary>
        public string Color { get; set; } = "#2563eb";
        public bool Visible { get; set; } = true;
        public bool Locked { get; set; }
    }

    public sealed class ErModelTablePlacement
    {
        public string Table { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        /// <summary>所屬群組名稱；null 代表不屬於任何群組。</summary>
        public string Group { get; set; }
    }

    /// <summary>手動調整的連接線：垂直段落放在指定的 X（邏輯座標）。</summary>
    public sealed class ErModelRoute
    {
        /// <summary>來源表|來源欄|目標表|目標欄。</summary>
        public string Key { get; set; }
        public int X { get; set; }
    }

    public sealed class ErModelDiagram
    {
        public ErModelDiagram()
        {
            Tables = new List<ErModelTablePlacement>();
            Routes = new List<ErModelRoute>();
        }

        public string Name { get; set; }
        public List<ErModelTablePlacement> Tables { get; set; }
        public List<ErModelRoute> Routes { get; set; }

        public ErModelTablePlacement Find(string table)
        {
            return Tables.FirstOrDefault(item => string.Equals(item.Table, table, StringComparison.OrdinalIgnoreCase));
        }

        public ErModelRoute FindRoute(string key)
        {
            return Routes == null ? null : Routes.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));
        }
    }

    public sealed class ErModelColumn
    {
        public string Name { get; set; }
        public string DataType { get; set; }
        public bool Nullable { get; set; } = true;
        public bool PrimaryKey { get; set; }
    }

    public sealed class ErModelTable
    {
        public ErModelTable()
        {
            Columns = new List<ErModelColumn>();
        }

        public string Name { get; set; }
        public List<ErModelColumn> Columns { get; set; }
    }

    public sealed class ErModelRelationship
    {
        public string Name { get; set; }
        public string FromTable { get; set; }
        public string FromColumn { get; set; }
        public string ToTable { get; set; }
        public string ToColumn { get; set; }
    }

    /// <summary>
    /// 模型內保存的結構（模型優先模式）：可以離線編輯，再與資料庫雙向比較／同步。
    /// 型別只接受一般型別語法，避免模型檔夾帶額外 SQL。
    /// </summary>
    public sealed class ErModelSchema
    {
        public ErModelSchema()
        {
            Tables = new List<ErModelTable>();
            Relationships = new List<ErModelRelationship>();
            Routines = new List<ErModelRoutine>();
        }

        public string Provider { get; set; }
        public List<ErModelTable> Tables { get; set; }
        public List<ErModelRelationship> Relationships { get; set; }
        /// <summary>函式與預存程序（完整 CREATE 定義）。</summary>
        public List<ErModelRoutine> Routines { get; set; }

        public ErModelTable Find(string name)
        {
            return Tables.FirstOrDefault(table => string.Equals(table.Name, name, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// 模型檔（.punkmodel）：保存同一個資料庫的多張圖表、每張表的位置與群組（顏色、顯示、鎖定）。
    /// 欄位與關聯一律從資料庫即時讀取，模型檔只保存版面，不含資料或連線資訊以外的祕密。
    /// </summary>
    public sealed class ErModelDocument
    {
        public ErModelDocument()
        {
            Version = 1;
            Groups = new List<ErModelGroup>();
            Diagrams = new List<ErModelDiagram>();
        }

        public int Version { get; set; }
        public string Database { get; set; }
        public List<ErModelGroup> Groups { get; set; }
        public List<ErModelDiagram> Diagrams { get; set; }
        /// <summary>模型內的結構；null 代表圖表直接顯示資料庫目前的結構。</summary>
        public ErModelSchema Schema { get; set; }

        public ErModelGroup FindGroup(string name)
        {
            return string.IsNullOrEmpty(name) ? null : Groups.FirstOrDefault(group => string.Equals(group.Name, name, StringComparison.OrdinalIgnoreCase));
        }
    }

    public static class ErModelService
    {
        public const int CardWidth = 300;
        public const int HeaderHeight = 38;
        public const int RowHeight = 24;
        public const int MaximumVisibleColumns = 16;
        public const int HorizontalGap = 110;
        public const int VerticalGap = 50;
        public const int Margin = 48;
        public const int MaximumCoordinate = 200000;

        public static string RouteKey(SchemaRelationshipModel relationship)
        {
            return relationship.FromTable + "|" + relationship.FromColumn + "|" + relationship.ToTable + "|" + relationship.ToColumn;
        }

        public static int CardHeight(int columnCount)
        {
            int visible = Math.Min(MaximumVisibleColumns, columnCount);
            int extra = columnCount > MaximumVisibleColumns ? 1 : 0;
            return HeaderHeight + Math.Max(1, visible + extra) * RowHeight + 8;
        }

        /// <summary>以目前資料庫全部資料表建立只有一張圖的預設模型，版面依外鍵分層排列。</summary>
        public static ErModelDocument CreateDefault(SchemaModelSnapshot snapshot, string diagramName)
        {
            ErModelDocument document = new ErModelDocument { Database = snapshot.DatabaseName };
            ErModelDiagram diagram = new ErModelDiagram { Name = diagramName };
            diagram.Tables.AddRange(snapshot.Tables.Select(table => new ErModelTablePlacement { Table = table.Name }));
            document.Diagrams.Add(diagram);
            ApplyLayeredLayout(diagram, snapshot);
            return document;
        }

        /// <summary>
        /// 依外鍵分層：被參照的父表在左、子表往右，每層內依父表位置的平均值排序以減少交叉；
        /// 循環參照以深度優先時遇到的反向邊忽略。沒有任何關聯的表排在最後一欄。
        /// </summary>
        public static void ApplyLayeredLayout(ErModelDiagram diagram, SchemaModelSnapshot snapshot)
        {
            // 自動排列會重新擺放資料表，手動調整的連接線一併清除。
            if (diagram.Routes != null) diagram.Routes.Clear();
            List<string> names = diagram.Tables.Select(item => item.Table).ToList();
            HashSet<string> present = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, HashSet<string>> parents = names.ToDictionary(name => name, name => new HashSet<string>(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
            foreach (SchemaRelationshipModel relation in snapshot.Relationships)
            {
                if (!present.Contains(relation.FromTable) || !present.Contains(relation.ToTable) ||
                    string.Equals(relation.FromTable, relation.ToTable, StringComparison.OrdinalIgnoreCase)) continue;
                parents[relation.FromTable].Add(relation.ToTable);
            }

            Dictionary<string, int> layer = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Func<string, int> depth = null;
            depth = name =>
            {
                int known;
                if (layer.TryGetValue(name, out known)) return known;
                if (!visiting.Add(name)) return 0;
                int value = parents[name].Count == 0 ? 0 : parents[name].Max(parent => depth(parent) + 1);
                visiting.Remove(name);
                layer[name] = value;
                return value;
            };
            foreach (string name in names) depth(name);

            HashSet<string> related = new HashSet<string>(parents.Where(pair => pair.Value.Count > 0).SelectMany(pair => pair.Value.Concat(new[] { pair.Key })), StringComparer.OrdinalIgnoreCase);
            int lastLayer = layer.Values.DefaultIfEmpty(0).Max();
            int isolatedLayer = related.Count == 0 ? 0 : lastLayer + 1;
            Dictionary<int, List<string>> columns = new Dictionary<int, List<string>>();
            foreach (string name in names)
            {
                int column = related.Contains(name) ? layer[name] : isolatedLayer;
                List<string> list;
                if (!columns.TryGetValue(column, out list)) columns[column] = list = new List<string>();
                list.Add(name);
            }

            Dictionary<string, SchemaTableModel> tables = snapshot.Tables.ToDictionary(table => table.Name, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, float> centers = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            foreach (int column in columns.Keys.OrderBy(key => key))
            {
                List<string> ordered = columns[column]
                    .Select((name, index) => new
                    {
                        Name = name,
                        Weight = parents[name].Where(centers.ContainsKey).Select(parent => centers[parent]).DefaultIfEmpty(float.MaxValue).Average(),
                        Index = index
                    })
                    .OrderBy(item => item.Weight).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(item => item.Name)
                    .ToList();
                int y = Margin;
                foreach (string name in ordered)
                {
                    SchemaTableModel table;
                    int height = CardHeight(tables.TryGetValue(name, out table) ? table.Columns.Count : 0);
                    ErModelTablePlacement placement = diagram.Find(name);
                    placement.X = Margin + column * (CardWidth + HorizontalGap);
                    placement.Y = y;
                    centers[name] = y + height / 2f;
                    y += height + VerticalGap;
                }
            }
        }

        public static void Validate(ErModelDocument document)
        {
            if (document == null) throw new InvalidOperationException(Localization.T("ErModel.Error.Empty"));
            if (document.Version != 1) throw new InvalidOperationException(Localization.Format("ErModel.Error.Version", document.Version));
            document.Groups = document.Groups ?? new List<ErModelGroup>();
            document.Diagrams = document.Diagrams ?? new List<ErModelDiagram>();
            if (document.Diagrams.Count == 0) throw new InvalidOperationException(Localization.T("ErModel.Error.NoDiagram"));
            foreach (ErModelGroup group in document.Groups)
            {
                group.Name = (group.Name ?? string.Empty).Trim();
                if (group.Name.Length == 0 || group.Name.Length > 60) throw new InvalidOperationException(Localization.T("ErModel.Error.GroupName"));
                if (!Regex.IsMatch(group.Color ?? string.Empty, "^#[0-9A-Fa-f]{6}$")) throw new InvalidOperationException(Localization.Format("ErModel.Error.Color", group.Name));
            }
            if (document.Groups.Select(group => group.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != document.Groups.Count)
            {
                throw new InvalidOperationException(Localization.T("ErModel.Error.DuplicateGroup"));
            }
            foreach (ErModelDiagram diagram in document.Diagrams)
            {
                diagram.Name = (diagram.Name ?? string.Empty).Trim();
                if (diagram.Name.Length == 0 || diagram.Name.Length > 60) throw new InvalidOperationException(Localization.T("ErModel.Error.DiagramName"));
                diagram.Tables = (diagram.Tables ?? new List<ErModelTablePlacement>())
                    .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Table))
                    .GroupBy(item => item.Table, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();
                diagram.Routes = (diagram.Routes ?? new List<ErModelRoute>())
                    .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Key) && item.Key.Length <= 1200)
                    .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase).Select(group => group.Last()).ToList();
                foreach (ErModelRoute route in diagram.Routes) route.X = Math.Max(0, Math.Min(MaximumCoordinate, route.X));
                foreach (ErModelTablePlacement placement in diagram.Tables)
                {
                    placement.X = Math.Max(0, Math.Min(MaximumCoordinate, placement.X));
                    placement.Y = Math.Max(0, Math.Min(MaximumCoordinate, placement.Y));
                    if (document.FindGroup(placement.Group) == null) placement.Group = null;
                }
            }
            if (document.Diagrams.Select(diagram => diagram.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != document.Diagrams.Count)
            {
                throw new InvalidOperationException(Localization.T("ErModel.Error.DuplicateDiagram"));
            }
            if (document.Schema != null) ValidateSchema(document.Schema);
        }

        public const int MaximumSchemaTables = 2000;
        public const int MaximumSchemaColumns = 1000;
        private static readonly Regex DataTypePattern = new Regex(
            @"^[A-Za-z_][A-Za-z0-9_]*( [A-Za-z_][A-Za-z0-9_]*)*( ?\( *(\d+( [A-Za-z]+)?|[A-Za-z_][A-Za-z0-9_]*|'([^';\\]|''){0,64}') *(, *(\d+( [A-Za-z]+)?|[A-Za-z_][A-Za-z0-9_]*|'([^';\\]|''){0,64}') *)*\))?( [A-Za-z_][A-Za-z0-9_]*)*(\[\])?\z",
            RegexOptions.CultureInvariant);

        /// <summary>
        /// 型別只允許「名稱 (參數) 修飾字」的一般語法，例如 varchar(20)、numeric(10, 2)、int unsigned、enum('a','b')、text[]；
        /// 空白代表未指定（SQLite 允許），產生 DDL 時使用 provider 預設型別。
        /// </summary>
        public static bool IsSafeDataType(string dataType)
        {
            string value = (dataType ?? string.Empty).Trim();
            if (value.Length == 0) return true;
            return value.Length <= 200 && value.IndexOf("--", StringComparison.Ordinal) < 0 && DataTypePattern.IsMatch(value);
        }

        public static void ValidateSchema(ErModelSchema schema)
        {
            schema.Provider = (schema.Provider ?? string.Empty).Trim();
            if (schema.Provider.Length == 0) throw new InvalidOperationException(Localization.T("ErModel.Error.SchemaProvider"));
            schema.Tables = schema.Tables ?? new List<ErModelTable>();
            schema.Relationships = schema.Relationships ?? new List<ErModelRelationship>();
            schema.Routines = schema.Routines ?? new List<ErModelRoutine>();
            RoutineModelService.Validate(schema.Routines);
            if (schema.Tables.Count > MaximumSchemaTables) throw new InvalidOperationException(Localization.Format("ErModel.Error.TooManyTables", MaximumSchemaTables));
            HashSet<string> tableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ErModelTable table in schema.Tables)
            {
                if (table == null) throw new InvalidOperationException(Localization.T("ErModel.Error.TableName"));
                table.Name = ValidateName(table.Name, "ErModel.Error.TableName");
                if (!tableNames.Add(table.Name)) throw new InvalidOperationException(Localization.Format("ErModel.Error.DuplicateTable", table.Name));
                table.Columns = table.Columns ?? new List<ErModelColumn>();
                if (table.Columns.Count == 0) throw new InvalidOperationException(Localization.Format("ErModel.Error.NoColumns", table.Name));
                if (table.Columns.Count > MaximumSchemaColumns) throw new InvalidOperationException(Localization.Format("ErModel.Error.TooManyColumns", table.Name, MaximumSchemaColumns));
                HashSet<string> columnNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (ErModelColumn column in table.Columns)
                {
                    if (column == null) throw new InvalidOperationException(Localization.Format("ErModel.Error.ColumnName", table.Name));
                    column.Name = ValidateName(column.Name, "ErModel.Error.ColumnName", table.Name);
                    if (!columnNames.Add(column.Name)) throw new InvalidOperationException(Localization.Format("ErModel.Error.DuplicateColumn", table.Name, column.Name));
                    column.DataType = (column.DataType ?? string.Empty).Trim();
                    if (!IsSafeDataType(column.DataType)) throw new InvalidOperationException(Localization.Format("ErModel.Error.DataType", table.Name, column.Name, column.DataType));
                }
            }
            foreach (ErModelRelationship relationship in schema.Relationships)
            {
                if (relationship == null) throw new InvalidOperationException(Localization.T("ErModel.Error.Relationship"));
                relationship.Name = string.IsNullOrWhiteSpace(relationship.Name) ? null : ValidateName(relationship.Name, "ErModel.Error.Relationship");
                ErModelTable from = schema.Find(relationship.FromTable);
                ErModelTable to = schema.Find(relationship.ToTable);
                ErModelColumn fromColumn = from == null ? null : from.Columns.FirstOrDefault(column => string.Equals(column.Name, relationship.FromColumn, StringComparison.OrdinalIgnoreCase));
                ErModelColumn toColumn = to == null ? null : to.Columns.FirstOrDefault(column => string.Equals(column.Name, relationship.ToColumn, StringComparison.OrdinalIgnoreCase));
                if (fromColumn == null || toColumn == null)
                {
                    throw new InvalidOperationException(Localization.Format("ErModel.Error.RelationshipTarget",
                        relationship.FromTable + "." + relationship.FromColumn, relationship.ToTable + "." + relationship.ToColumn));
                }
                relationship.FromTable = from.Name;
                relationship.FromColumn = fromColumn.Name;
                relationship.ToTable = to.Name;
                relationship.ToColumn = toColumn.Name;
            }
        }

        private static string ValidateName(string name, string errorKey, params object[] context)
        {
            string value = (name ?? string.Empty).Trim();
            if (value.Length == 0 || value.Length > 128 || value.Any(char.IsControl))
            {
                throw new InvalidOperationException(context.Length == 0 ? Localization.T(errorKey) : Localization.Format(errorKey, context));
            }
            return value;
        }

        /// <summary>把資料庫結構快照轉成模型內的結構。</summary>
        public static ErModelSchema CaptureSchema(SchemaModelSnapshot snapshot)
        {
            ErModelSchema schema = new ErModelSchema { Provider = snapshot.ProviderName };
            foreach (SchemaTableModel table in snapshot.Tables)
            {
                ErModelTable copy = new ErModelTable { Name = table.Name };
                copy.Columns.AddRange(table.Columns.OrderBy(column => column.Ordinal).Select(column => new ErModelColumn
                {
                    Name = column.Name,
                    DataType = column.DataType,
                    Nullable = column.IsNullable,
                    PrimaryKey = column.IsPrimaryKey
                }));
                schema.Tables.Add(copy);
            }
            schema.Relationships.AddRange(snapshot.Relationships.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Ordinal).Select(item => new ErModelRelationship
            {
                Name = item.Name,
                FromTable = item.FromTable,
                FromColumn = item.FromColumn,
                ToTable = item.ToTable,
                ToColumn = item.ToColumn
            }));
            return schema;
        }

        /// <summary>模型結構轉成比較／繪圖用的快照；同名外鍵的多個欄位依順序編號成複合外鍵。</summary>
        public static SchemaModelSnapshot ToSnapshot(ErModelSchema schema, string databaseName)
        {
            SchemaModelSnapshot snapshot = new SchemaModelSnapshot { DatabaseName = databaseName, ProviderName = schema.Provider };
            foreach (ErModelTable table in schema.Tables)
            {
                SchemaTableModel copy = new SchemaTableModel { Name = table.Name };
                int ordinal = 0;
                foreach (ErModelColumn column in table.Columns)
                {
                    copy.Columns.Add(new SchemaColumnModel
                    {
                        Name = column.Name,
                        DataType = column.DataType,
                        IsNullable = column.Nullable,
                        IsPrimaryKey = column.PrimaryKey,
                        Ordinal = ++ordinal
                    });
                }
                snapshot.Tables.Add(copy);
            }
            Dictionary<string, int> ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int unnamed = 0;
            foreach (ErModelRelationship relationship in schema.Relationships)
            {
                string name = relationship.Name ?? DefaultRelationshipName(relationship, ++unnamed);
                int next;
                ordinals.TryGetValue(name, out next);
                ordinals[name] = ++next;
                snapshot.Relationships.Add(new SchemaRelationshipModel
                {
                    Name = name,
                    FromTable = relationship.FromTable,
                    FromColumn = relationship.FromColumn,
                    ToTable = relationship.ToTable,
                    ToColumn = relationship.ToColumn,
                    Ordinal = next
                });
            }
            return snapshot;
        }

        private static string DefaultRelationshipName(ErModelRelationship relationship, int index)
        {
            string name = "fk_" + relationship.FromTable + "_" + relationship.ToTable;
            name = new string(name.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
            if (name.Length > 60) name = name.Substring(0, 60);
            return name + (index > 1 ? "_" + index.ToString(CultureInfo.InvariantCulture) : string.Empty);
        }

        /// <summary>
        /// 新增或取代模型中的資料表（originalName 為 null 代表新增）。改名時同步更新外鍵與所有圖表的位置；
        /// 移除的欄位連帶移除相關外鍵。回傳被移除的外鍵數。
        /// </summary>
        public static int ReplaceTable(ErModelDocument document, string originalName, ErModelTable table, IList<ErModelRelationship> outgoing)
        {
            ErModelSchema schema = document.Schema;
            if (schema == null) throw new InvalidOperationException(Localization.T("ErModel.Error.NoSchema"));
            ErModelSchema candidate = new ErModelSchema
            {
                Provider = schema.Provider,
                Tables = schema.Tables.Select(item => item).ToList(),
                Relationships = new List<ErModelRelationship>(),
                Routines = schema.Routines
            };
            int index = originalName == null ? -1 : candidate.Tables.FindIndex(item => string.Equals(item.Name, originalName, StringComparison.OrdinalIgnoreCase));
            if (originalName != null && index < 0) throw new InvalidOperationException(Localization.Format("ErModel.Error.UnknownTable", originalName));
            if (index >= 0) candidate.Tables[index] = table;
            else candidate.Tables.Add(table);

            string oldName = originalName ?? table.Name;
            int dropped = 0;
            foreach (ErModelRelationship relationship in schema.Relationships)
            {
                if (string.Equals(relationship.FromTable, oldName, StringComparison.OrdinalIgnoreCase) && originalName != null) continue;
                ErModelRelationship copy = new ErModelRelationship
                {
                    Name = relationship.Name,
                    FromTable = relationship.FromTable,
                    FromColumn = relationship.FromColumn,
                    ToTable = string.Equals(relationship.ToTable, oldName, StringComparison.OrdinalIgnoreCase) && originalName != null ? table.Name : relationship.ToTable,
                    ToColumn = relationship.ToColumn
                };
                if (!ColumnExists(candidate, copy.ToTable, copy.ToColumn))
                {
                    dropped++;
                    continue;
                }
                candidate.Relationships.Add(copy);
            }
            foreach (ErModelRelationship relationship in outgoing ?? new List<ErModelRelationship>())
            {
                relationship.FromTable = table.Name;
                candidate.Relationships.Add(relationship);
            }
            ValidateSchema(candidate);
            document.Schema = candidate;
            if (originalName != null && !string.Equals(originalName, table.Name, StringComparison.Ordinal))
            {
                foreach (ErModelDiagram diagram in document.Diagrams)
                {
                    foreach (ErModelTablePlacement placement in diagram.Tables.Where(item => string.Equals(item.Table, originalName, StringComparison.OrdinalIgnoreCase)))
                    {
                        placement.Table = table.Name;
                    }
                }
            }
            return dropped;
        }

        /// <summary>從模型刪除資料表，連帶刪除相關外鍵與所有圖表中的位置。</summary>
        public static void DropTable(ErModelDocument document, string name)
        {
            ErModelSchema schema = document.Schema;
            if (schema == null) throw new InvalidOperationException(Localization.T("ErModel.Error.NoSchema"));
            if (schema.Tables.RemoveAll(table => string.Equals(table.Name, name, StringComparison.OrdinalIgnoreCase)) == 0)
            {
                throw new InvalidOperationException(Localization.Format("ErModel.Error.UnknownTable", name));
            }
            schema.Relationships.RemoveAll(item => string.Equals(item.FromTable, name, StringComparison.OrdinalIgnoreCase) || string.Equals(item.ToTable, name, StringComparison.OrdinalIgnoreCase));
            foreach (ErModelDiagram diagram in document.Diagrams)
            {
                diagram.Tables.RemoveAll(item => string.Equals(item.Table, name, StringComparison.OrdinalIgnoreCase));
            }
        }

        private static bool ColumnExists(ErModelSchema schema, string table, string column)
        {
            ErModelTable match = schema.Find(table);
            return match != null && match.Columns.Any(item => string.Equals(item.Name, column, StringComparison.OrdinalIgnoreCase));
        }

        public static void Save(ErModelDocument document, string path)
        {
            Validate(document);
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonConvert.SerializeObject(document, Formatting.Indented), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }

        public static ErModelDocument Load(string path)
        {
            FileInfo info = new FileInfo(path);
            if (info.Length > 16 * 1024 * 1024) throw new InvalidOperationException(Localization.T("ErModel.Error.TooLarge"));
            ErModelDocument document;
            try
            {
                document = JsonConvert.DeserializeObject<ErModelDocument>(File.ReadAllText(path, Encoding.UTF8));
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException(Localization.Format("ErModel.Error.Json", exception.Message), exception);
            }
            Validate(document);
            return document;
        }

        /// <summary>
        /// 向量輸出：只含目前可見群組的表格與兩端都可見的關聯；所有名稱都經 XML 跳脫，不含腳本。
        /// </summary>
        public static string BuildSvg(SchemaModelSnapshot snapshot, ErModelDocument document, ErModelDiagram diagram)
        {
            Dictionary<string, SchemaTableModel> tables = snapshot.Tables.ToDictionary(table => table.Name, StringComparer.OrdinalIgnoreCase);
            List<KeyValuePair<ErModelTablePlacement, SchemaTableModel>> cards = diagram.Tables
                .Where(item => tables.ContainsKey(item.Table) && IsVisible(document, item))
                .Select(item => new KeyValuePair<ErModelTablePlacement, SchemaTableModel>(item, tables[item.Table]))
                .ToList();
            int width = cards.Count == 0 ? 200 : cards.Max(card => card.Key.X + CardWidth) + Margin;
            int height = cards.Count == 0 ? 100 : cards.Max(card => card.Key.Y + CardHeight(card.Value.Columns.Count)) + Margin;
            Func<string, string> x = value => WebUtility.HtmlEncode(value ?? string.Empty);
            Func<float, string> n = value => value.ToString("0.#", CultureInfo.InvariantCulture);
            StringBuilder svg = new StringBuilder();
            svg.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            svg.AppendLine("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"" + width + "\" height=\"" + height + "\" viewBox=\"0 0 " + width + " " + height + "\" font-family=\"Segoe UI, Microsoft JhengHei, sans-serif\" font-size=\"12\">");
            svg.AppendLine("<rect width=\"100%\" height=\"100%\" fill=\"#ffffff\"/>");
            Dictionary<string, KeyValuePair<ErModelTablePlacement, SchemaTableModel>> byName = cards.ToDictionary(card => card.Key.Table, StringComparer.OrdinalIgnoreCase);
            foreach (SchemaRelationshipModel relation in snapshot.Relationships)
            {
                KeyValuePair<ErModelTablePlacement, SchemaTableModel> from, to;
                if (!byName.TryGetValue(relation.FromTable, out from) || !byName.TryGetValue(relation.ToTable, out to)) continue;
                float startY = AnchorY(from, relation.FromColumn);
                float endY = AnchorY(to, relation.ToColumn);
                bool leftToRight = from.Key.X <= to.Key.X;
                float startX = leftToRight ? from.Key.X + CardWidth : from.Key.X;
                float endX = leftToRight ? to.Key.X : to.Key.X + CardWidth;
                ErModelRoute route = diagram.FindRoute(RouteKey(relation));
                float middle = route != null ? route.X : (startX + endX) / 2f;
                svg.AppendLine("<polyline fill=\"none\" stroke=\"#2563eb\" stroke-width=\"1.6\" points=\"" +
                               n(startX) + "," + n(startY) + " " + n(middle) + "," + n(startY) + " " + n(middle) + "," + n(endY) + " " + n(endX) + "," + n(endY) + "\"/>");
            }
            foreach (KeyValuePair<ErModelTablePlacement, SchemaTableModel> card in cards)
            {
                ErModelGroup group = document.FindGroup(card.Key.Group);
                string header = group == null ? "#dbeafe" : Tint(group.Color);
                int cardHeight = CardHeight(card.Value.Columns.Count);
                svg.AppendLine("<g>");
                svg.AppendLine("<rect x=\"" + card.Key.X + "\" y=\"" + card.Key.Y + "\" width=\"" + CardWidth + "\" height=\"" + cardHeight + "\" fill=\"#ffffff\" stroke=\"#94a3b8\"/>");
                svg.AppendLine("<rect x=\"" + card.Key.X + "\" y=\"" + card.Key.Y + "\" width=\"" + CardWidth + "\" height=\"" + HeaderHeight + "\" fill=\"" + header + "\" stroke=\"#94a3b8\"/>");
                svg.AppendLine("<text x=\"" + (card.Key.X + 12) + "\" y=\"" + (card.Key.Y + 24) + "\" font-weight=\"bold\">" + x(card.Value.Name) + "</text>");
                int visible = Math.Min(MaximumVisibleColumns, card.Value.Columns.Count);
                for (int index = 0; index < visible; index++)
                {
                    SchemaColumnModel column = card.Value.Columns[index];
                    int top = card.Key.Y + HeaderHeight + index * RowHeight;
                    svg.AppendLine("<text x=\"" + (card.Key.X + 12) + "\" y=\"" + (top + 16) + "\">" + (column.IsPrimaryKey ? "<tspan fill=\"#2563eb\" font-weight=\"bold\">PK </tspan>" : string.Empty) + x(column.Name) + "</text>");
                    svg.AppendLine("<text x=\"" + (card.Key.X + CardWidth - 10) + "\" y=\"" + (top + 16) + "\" text-anchor=\"end\" fill=\"#64748b\">" + x((column.DataType ?? string.Empty) + (column.IsNullable ? " ?" : string.Empty)) + "</text>");
                }
                if (card.Value.Columns.Count > MaximumVisibleColumns)
                {
                    int top = card.Key.Y + HeaderHeight + visible * RowHeight;
                    svg.AppendLine("<text x=\"" + (card.Key.X + 12) + "\" y=\"" + (top + 16) + "\" fill=\"#64748b\">" + x(Localization.Format("ErDiagram.MoreColumns", card.Value.Columns.Count - MaximumVisibleColumns)) + "</text>");
                }
                svg.AppendLine("</g>");
            }
            svg.AppendLine("</svg>");
            return svg.ToString();
        }

        public static bool IsVisible(ErModelDocument document, ErModelTablePlacement placement)
        {
            ErModelGroup group = document.FindGroup(placement.Group);
            return group == null || group.Visible;
        }

        public static bool IsLocked(ErModelDocument document, ErModelTablePlacement placement)
        {
            ErModelGroup group = document.FindGroup(placement.Group);
            return group != null && group.Locked;
        }

        /// <summary>群組色的淡化版本（與白色 3:1 混合），用在表頭背景。</summary>
        public static string Tint(string color)
        {
            Color value = ColorTranslator.FromHtml(color);
            Func<int, int> mix = channel => (channel + 255 * 3) / 4;
            return "#" + mix(value.R).ToString("x2", CultureInfo.InvariantCulture) + mix(value.G).ToString("x2", CultureInfo.InvariantCulture) + mix(value.B).ToString("x2", CultureInfo.InvariantCulture);
        }

        private static float AnchorY(KeyValuePair<ErModelTablePlacement, SchemaTableModel> card, string column)
        {
            int index = card.Value.Columns.FindIndex(item => string.Equals(item.Name, column, StringComparison.OrdinalIgnoreCase));
            if (index < 0) index = 0;
            index = Math.Min(index, MaximumVisibleColumns);
            return card.Key.Y + HeaderHeight + index * RowHeight + RowHeight / 2f;
        }
    }
}
