using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml.Linq;

namespace mySQLPunk.lib
{
    public static partial class SchemaReportOpenXmlWriter
    {
        const int SpecificationWidth = 9638;
        static readonly int[] SpecificationFieldWidths = { 480, 2600, 2400, 450, 450, 2200, 1058 };
        static readonly string[] SpecificationHeaders = { "No.", "欄位英文", "欄位中文", "PK", "NN", "資料型態", "長度／精度" };

        static void WriteSpecificationWord(ZipArchive zip, SchemaReportDocument report, CancellationToken token, Action<int, int> progress)
        {
            var body = new XElement(W + "body",
                SpecificationParagraph(report.Title, "SpecTitle"),
                SpecificationParagraph("資料庫：" + report.DatabaseName),
                SpecificationParagraph("引擎：" + report.EngineName),
                SpecificationParagraph("作者：" + report.Author),
                SpecificationParagraph("產生時間：" + report.GeneratedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)),
                SpecificationParagraph("PK：主鍵；NN：不可為 NULL；V：是；-：否；?：未提供。長度／精度的 — 表示不適用或未提供。"),
                SpecificationParagraph("資料表清單", "SpecHeading"));
            int[] inventoryWidths = { 480, 4358, 4800 };
            var inventory = SpecificationTable(inventoryWidths);
            inventory.Add(SpecificationRow(new[] { "No.", "資料表英文名稱", "資料表中文名稱" }, inventoryWidths, true, new[] { 0 }));
            for (int i = 0; i < report.Tables.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var table = report.Tables[i];
                inventory.Add(SpecificationRow(new[] { (i + 1).ToString(CultureInfo.InvariantCulture), table.Name, table.Comment }, inventoryWidths, false, new[] { 0 }));
            }
            body.Add(inventory, SpecificationParagraph(""));
            int done = 0;
            foreach (var table in report.Tables)
            {
                token.ThrowIfCancellationRequested();
                body.Add(SpecificationParagraph((done + 1).ToString(CultureInfo.InvariantCulture) + ". " + table.Name, "SpecHeading", pageBreakBefore: true));
                int[] summaryWidths = { 1800, 7838 };
                var summary = SpecificationTable(summaryWidths);
                string primaryKey = string.Join(", ", table.Columns.Where(column => string.Equals(column.Key, "PRI", StringComparison.OrdinalIgnoreCase)).Select(column => column.Name));
                summary.Add(SpecificationSummaryRow("資料表英文名稱", table.Name, summaryWidths));
                summary.Add(SpecificationSummaryRow("資料表中文名稱", table.Comment, summaryWidths));
                summary.Add(SpecificationSummaryRow("Primary Key", primaryKey.Length == 0 ? "—" : primaryKey, summaryWidths));
                summary.Add(SpecificationSummaryRow("Index", SpecificationIndexSummary(table.Indexes, token), summaryWidths));
                body.Add(summary, SpecificationParagraph(""));

                var fields = SpecificationTable(SpecificationFieldWidths);
                fields.Add(SpecificationRow(SpecificationHeaders, SpecificationFieldWidths, true, new[] { 0, 3, 4, 6 }));
                int number = 0;
                foreach (var column in table.Columns)
                {
                    token.ThrowIfCancellationRequested();
                    string[] type = SpecificationType(column.Type);
                    fields.Add(SpecificationRow(new[] {
                        (++number).ToString(CultureInfo.InvariantCulture), column.Name, column.Comment,
                        string.Equals(column.Key, "PRI", StringComparison.OrdinalIgnoreCase) ? "V" : "-",
                        SpecificationNotNull(column.Nullable), type[0], type[1]
                    }, SpecificationFieldWidths, false, new[] { 0, 3, 4, 6 }));
                }
                body.Add(fields, SpecificationParagraph(""));
                if (progress != null) progress(++done, report.Tables.Count); else done++;
            }
            body.Add(new XElement(W + "sectPr",
                new XElement(W + "pgSz", new XAttribute(W + "w", 11906), new XAttribute(W + "h", 16838)),
                new XElement(W + "pgMar", new XAttribute(W + "top", 1134), new XAttribute(W + "right", 1134), new XAttribute(W + "bottom", 1134), new XAttribute(W + "left", 1134))));
            Part(zip, "word/document.xml", new XElement(W + "document", body));
            Part(zip, "word/styles.xml", SpecificationStyles());
            Part(zip, "word/_rels/document.xml.rels", new XElement(P + "Relationships", Relationship("rId1", "styles", "styles.xml")));
            var types = Types("word/document.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml");
            types.Add(Override("word/styles.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"));
            Part(zip, "[Content_Types].xml", types);
            Part(zip, "_rels/.rels", new XElement(P + "Relationships", Relationship("rId1", "officeDocument", "word/document.xml")));
        }

        // Only this template uses these styles; the existing Word layouts retain their XML.
        static XElement SpecificationStyles()
        {
            var styles = new XElement(W + "styles",
                new XElement(W + "docDefaults", new XElement(W + "rPrDefault", new XElement(W + "rPr",
                    new XElement(W + "rFonts", new XAttribute(W + "ascii", "Calibri"), new XAttribute(W + "hAnsi", "Calibri"), new XAttribute(W + "eastAsia", "Microsoft JhengHei")),
                    new XElement(W + "sz", new XAttribute(W + "val", 18)), new XElement(W + "szCs", new XAttribute(W + "val", 18)))),
                    new XElement(W + "pPrDefault", new XElement(W + "pPr", SpecificationSpacing(0, 60)))));
            styles.Add(new XElement(W + "style", new XAttribute(W + "type", "paragraph"), new XAttribute(W + "default", 1), new XAttribute(W + "styleId", "SpecBody"), new XElement(W + "name", new XAttribute(W + "val", "Specification Body"))));
            foreach (string id in new[] { "SpecTitle", "SpecHeading" })
                styles.Add(new XElement(W + "style", new XAttribute(W + "type", "paragraph"), new XAttribute(W + "styleId", id),
                    new XElement(W + "name", new XAttribute(W + "val", id)), new XElement(W + "basedOn", new XAttribute(W + "val", "SpecBody")),
                    new XElement(W + "pPr", new XElement(W + "keepNext"), SpecificationSpacing(id == "SpecTitle" ? 0 : 120, id == "SpecTitle" ? 180 : 120), id == "SpecHeading" ? new XElement(W + "outlineLvl", new XAttribute(W + "val", 0)) : null),
                    new XElement(W + "rPr", new XElement(W + "b"), new XElement(W + "sz", new XAttribute(W + "val", id == "SpecTitle" ? 36 : 28)), new XElement(W + "szCs", new XAttribute(W + "val", id == "SpecTitle" ? 36 : 28)))));
            return styles;
        }

        static XElement SpecificationSpacing(int before, int after)
        {
            return new XElement(W + "spacing", new XAttribute(W + "before", before), new XAttribute(W + "after", after), new XAttribute(W + "line", 240), new XAttribute(W + "lineRule", "auto"));
        }

        static XElement SpecificationParagraph(string text, string style = null, bool center = false, bool bold = false, bool cell = false, bool pageBreakBefore = false)
        {
            var properties = new XElement(W + "pPr");
            if (style != null) properties.Add(new XElement(W + "pStyle", new XAttribute(W + "val", style)));
            if (pageBreakBefore) properties.Add(new XElement(W + "pageBreakBefore"));
            if (cell) properties.Add(SpecificationSpacing(0, 0));
            if (center) properties.Add(new XElement(W + "jc", new XAttribute(W + "val", "center")));
            var run = new XElement(W + "r", bold ? new XElement(W + "rPr", new XElement(W + "b")) : null);
            // Word does not render literal XML newlines as line breaks. Preserve their visual
            // meaning (and tabs) without trimming comments, identifiers, or blank lines.
            foreach (string segment in Regex.Split(Text(text), "(\\r\\n|\\r|\\n|\\t)"))
            {
                if (segment == "\r\n" || segment == "\r" || segment == "\n") run.Add(new XElement(W + "br"));
                else if (segment == "\t") run.Add(new XElement(W + "tab"));
                else run.Add(new XElement(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), segment));
            }
            return new XElement(W + "p", properties, run);
        }

        static XElement SpecificationTable(int[] widths)
        {
            var borders = new XElement(W + "tblBorders");
            foreach (string edge in new[] { "top", "left", "bottom", "right", "insideH", "insideV" })
                borders.Add(new XElement(W + edge, new XAttribute(W + "val", "single"), new XAttribute(W + "sz", 4), new XAttribute(W + "color", "000000")));
            return new XElement(W + "tbl", new XElement(W + "tblPr",
                new XElement(W + "tblW", new XAttribute(W + "w", SpecificationWidth), new XAttribute(W + "type", "dxa")), borders,
                new XElement(W + "tblLayout", new XAttribute(W + "type", "fixed"))),
                new XElement(W + "tblGrid", widths.Select(width => new XElement(W + "gridCol", new XAttribute(W + "w", width)))));
        }

        static XElement SpecificationRow(string[] values, int[] widths, bool header, int[] centered)
        {
            var row = new XElement(W + "tr", header ? new XElement(W + "trPr", new XElement(W + "tblHeader")) : null);
            for (int i = 0; i < values.Length; i++) row.Add(SpecificationCell(values[i], widths[i], header, centered.Contains(i)));
            return row;
        }

        static XElement SpecificationSummaryRow(string label, string value, int[] widths)
        {
            return new XElement(W + "tr", SpecificationCell(label, widths[0], true, false), SpecificationCell(value, widths[1], false, false));
        }

        static XElement SpecificationCell(string value, int width, bool header, bool center)
        {
            return new XElement(W + "tc", new XElement(W + "tcPr",
                new XElement(W + "tcW", new XAttribute(W + "w", width), new XAttribute(W + "type", "dxa")),
                new XElement(W + "shd", new XAttribute(W + "val", "clear"), new XAttribute(W + "fill", header ? "D9E1F2" : "FFFFFF")),
                new XElement(W + "tcMar", new XElement(W + "top", new XAttribute(W + "w", 60), new XAttribute(W + "type", "dxa")), new XElement(W + "left", new XAttribute(W + "w", 75), new XAttribute(W + "type", "dxa")), new XElement(W + "bottom", new XAttribute(W + "w", 60), new XAttribute(W + "type", "dxa")), new XElement(W + "right", new XAttribute(W + "w", 75), new XAttribute(W + "type", "dxa"))),
                new XElement(W + "vAlign", new XAttribute(W + "val", "center"))), SpecificationParagraph(value, center: center, bold: header, cell: true));
        }

        static string SpecificationNotNull(string nullable)
        {
            switch ((nullable ?? "").Trim().ToUpperInvariant())
            {
                case "NO": case "N": return "V";
                case "YES": case "Y": return "-";
                default: return "?";
            }
        }

        static string[] SpecificationType(string value)
        {
            string type = Text(value);
            const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
            // Integer display widths, datetime precision, enum/set members, arrays and
            // unfamiliar provider types stay intact rather than acquiring a false length.
            Match match = Regex.Match(type, @"\A(?<type>\s*(?:char|varchar|nchar|nvarchar|varchar2|nvarchar2|character(?:\s+varying)?|binary|varbinary)\s*)\((?<size>\s*(?:[0-9]+(?:\s+(?:char|byte))?|max)\s*)\)(?<suffix>\s*)\z", options);
            if (!match.Success)
                match = Regex.Match(type, @"\A(?<type>\s*(?:decimal|dec|numeric|number)\s*)\((?<size>\s*[0-9]+(?:\s*,\s*-?[0-9]+)?\s*)\)(?<suffix>(?:\s+(?:unsigned|zerofill))*\s*)\z", options);
            return match.Success ? new[] { match.Groups["type"].Value + match.Groups["suffix"].Value, match.Groups["size"].Value } : new[] { type, "—" };
        }

        static string SpecificationIndexValue(DataRow row, params string[] names)
        {
            foreach (string name in names)
                if (row.Table.Columns.Contains(name) && row[name] != DBNull.Value)
                    return Convert.ToString(row[name], CultureInfo.InvariantCulture);
            return "";
        }

        static string SpecificationIndexSummary(DataTable indexes, CancellationToken token)
        {
            if (indexes == null) return "未提供索引資訊";
            if (indexes.Rows.Count == 0) return "—";
            var groups = new List<KeyValuePair<string, List<DataRow>>>();
            var positions = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (DataRow row in indexes.Rows)
            {
                token.ThrowIfCancellationRequested();
                string name = SpecificationIndexValue(row, "Key_name", "INDEX_NAME", "IndexName");
                int position;
                if (name.Length == 0 || !positions.TryGetValue(name, out position))
                {
                    position = groups.Count;
                    groups.Add(new KeyValuePair<string, List<DataRow>>(name, new List<DataRow>()));
                    if (name.Length > 0) positions.Add(name, position);
                }
                groups[position].Value.Add(row);
            }
            var lines = new List<string>();
            foreach (var group in groups)
            {
                token.ThrowIfCancellationRequested();
                // Metadata providers normally order these rows. Only override row order
                // when every member supplies an unambiguous positive sequence number.
                var sequence = new List<KeyValuePair<int, DataRow>>();
                var used = new HashSet<int>();
                foreach (DataRow row in group.Value)
                {
                    int ordinal;
                    if (!int.TryParse(SpecificationIndexValue(row, "Seq_in_index", "COLUMN_POSITION", "OrdinalPosition"), NumberStyles.Integer, CultureInfo.InvariantCulture, out ordinal) || ordinal < 1 || !used.Add(ordinal)) break;
                    sequence.Add(new KeyValuePair<int, DataRow>(ordinal, row));
                }
                IEnumerable<DataRow> rows = sequence.Count == group.Value.Count ? sequence.OrderBy(pair => pair.Key).Select(pair => pair.Value) : group.Value;
                var members = new List<string>();
                foreach (DataRow row in rows)
                {
                    token.ThrowIfCancellationRequested();
                    string member = SpecificationIndexValue(row, "Column_name", "COLUMN_NAME", "ColumnName");
                    if (member.Length == 0) member = SpecificationIndexValue(row, "Expression", "COLUMN_EXPRESSION", "ColumnExpression");
                    if (member.Length == 0) member = "欄位資訊未提供";
                    string prefix = SpecificationIndexValue(row, "Sub_part", "PREFIX_LENGTH");
                    if (prefix.Length > 0) member += " (" + prefix + ")";
                    if (string.Equals(SpecificationIndexValue(row, "Collation"), "D", StringComparison.OrdinalIgnoreCase)) member += " DESC";
                    members.Add(member);
                }
                lines.Add((group.Key.Length == 0 ? "索引名稱未提供" : group.Key) + " (" + string.Join(", ", members) + ")");
            }
            return string.Join("\n", lines);
        }
    }
}
