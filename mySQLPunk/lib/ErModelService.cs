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

    public sealed class ErModelDiagram
    {
        public ErModelDiagram()
        {
            Tables = new List<ErModelTablePlacement>();
        }

        public string Name { get; set; }
        public List<ErModelTablePlacement> Tables { get; set; }

        public ErModelTablePlacement Find(string table)
        {
            return Tables.FirstOrDefault(item => string.Equals(item.Table, table, StringComparison.OrdinalIgnoreCase));
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
                float middle = (startX + endX) / 2f;
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
