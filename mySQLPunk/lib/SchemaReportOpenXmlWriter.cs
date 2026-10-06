using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using System.Xml.Linq;

namespace mySQLPunk.lib
{
    public static partial class SchemaReportOpenXmlWriter
    {
        static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        static readonly XNamespace P = "http://schemas.openxmlformats.org/package/2006/relationships";
        static readonly XNamespace C = "http://schemas.openxmlformats.org/package/2006/content-types";
        static readonly string[] Headers = { "欄位 / Column", "型別 / Type", "NULL", "鍵 / Key", "預設值 / Default", "註解 / Comment", "其他 / Extra" };

        public static void Write(string path, SchemaReportDocument report, ReportTemplate template, CancellationToken token, Action<int, int> progress)
        {
            if (report == null || template == null) throw new ArgumentNullException();
            bool specification = template.Id == "docx.schema-specification";
            bool docx = specification || template.Id == "docx.classic-dictionary" || template.Id == "docx.project-handover";
            bool xlsx = template.Id == "xlsx.table-sheets" || template.Id == "xlsx.column-inventory";
            if (!docx && !xlsx) throw new ArgumentException("Unsupported Office report template.");
            token.ThrowIfCancellationRequested();
            using (var file = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                if (specification) WriteSpecificationWord(zip, report, token, progress);
                else if (docx) WriteWord(zip, report, template, token, progress);
                else WriteExcel(zip, report, template, token, progress);
                token.ThrowIfCancellationRequested();
            }
        }

        // Invalid XML controls cannot round-trip: fail explicitly instead of changing metadata.
        static string Text(string value) { value = value ?? ""; XmlConvert.VerifyXmlChars(value); return value; }
        static void Part(ZipArchive zip, string name, XElement root)
        {
            using (var stream = zip.CreateEntry(name).Open())
            using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false, NewLineHandling = NewLineHandling.Entitize }))
                new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), root).Save(writer);
        }
        static XElement Relationship(string id, string type, string target) { return new XElement(P + "Relationship", new XAttribute("Id", id), new XAttribute("Type", R.NamespaceName + "/" + type), new XAttribute("Target", target)); }
        static XElement Types(string mainPath, string mainType)
        {
            return new XElement(C + "Types", new XElement(C + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")), new XElement(C + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")), Override(mainPath, mainType));
        }
        static XElement Override(string path, string type) { return new XElement(C + "Override", new XAttribute("PartName", "/" + path), new XAttribute("ContentType", type)); }
        static string[] Values(SchemaReportColumn c) { return new[] { c.Name, c.Type, c.Nullable, c.Key, c.DefaultValue, c.Comment, c.Extra }; }
        static XElement Paragraph(string text, string style = null)
        {
            return new XElement(W + "p", style == null ? null : new XElement(W + "pPr", new XElement(W + "pStyle", new XAttribute(W + "val", style))), new XElement(W + "r", new XElement(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), Text(text))));
        }
        static XElement WordRow(string[] values, bool header)
        {
            return new XElement(W + "tr", header ? new XElement(W + "trPr", new XElement(W + "tblHeader")) : null,
                values.Select(v => new XElement(W + "tc", new XElement(W + "tcPr", new XElement(W + "tcW", new XAttribute(W + "w", 2199), new XAttribute(W + "type", "dxa")), new XElement(W + "shd", new XAttribute(W + "val", "clear"), new XAttribute(W + "fill", header ? "F4A340" : "FFFFFF")), new XElement(W + "tcMar", new XElement(W + "left", new XAttribute(W + "w", 75), new XAttribute(W + "type", "dxa"))), new XElement(W + "vAlign", new XAttribute(W + "val", "center"))), Paragraph(v))));
        }
        static void WriteWord(ZipArchive zip, SchemaReportDocument report, ReportTemplate template, CancellationToken token, Action<int, int> progress)
        {
            var body = new XElement(W + "body", Paragraph(report.Title, "Title"), Paragraph("資料庫 / Database: " + report.DatabaseName), Paragraph("引擎 / Engine: " + report.EngineName), Paragraph("作者 / Author: " + report.Author), Paragraph("產生時間 / Generated: " + report.GeneratedAt.ToString("yyyy-MM-dd HH:mm")));
            if (template.Id == "docx.project-handover")
            {
                body.Add(new XElement(W + "p", new XElement(W + "r", new XElement(W + "br", new XAttribute(W + "type", "page")))));
                body.Add(Paragraph("目錄 / Contents", "Heading1"));
                // Static navigation is immediately readable; Word can update the TOC field.
                body.Add(new XElement(W + "p", new XElement(W + "fldSimple", new XAttribute(W + "instr", "TOC \\o \"1-2\" \\h \\z \\u"), new XElement(W + "r", new XElement(W + "t", "更新目錄 / Update table of contents in Word")))));
                foreach (var table in report.Tables) { token.ThrowIfCancellationRequested(); body.Add(Paragraph(table.Name)); }
                body.Add(new XElement(W + "p", new XElement(W + "r", new XElement(W + "br", new XAttribute(W + "type", "page")))));
            }
            int done = 0;
            foreach (var table in report.Tables)
            {
                token.ThrowIfCancellationRequested();
                body.Add(Paragraph(table.Name, "Heading1"), Paragraph(table.Comment));
                var borders = new XElement(W + "tblBorders");
                foreach (string edge in new[] { "top", "left", "bottom", "right", "insideH", "insideV" }) borders.Add(new XElement(W + edge, new XAttribute(W + "val", "single"), new XAttribute(W + "sz", 4), new XAttribute(W + "color", "000000")));
                var grid = new XElement(W + "tbl", new XElement(W + "tblPr", new XElement(W + "tblW", new XAttribute(W + "w", 15393), new XAttribute(W + "type", "dxa")), borders), new XElement(W + "tblGrid", Enumerable.Range(0, 7).Select(i => new XElement(W + "gridCol", new XAttribute(W + "w", 2199)))), WordRow(Headers, true));
                foreach (var col in table.Columns) { token.ThrowIfCancellationRequested(); grid.Add(WordRow(Values(col), false)); }
                body.Add(grid, Paragraph(""));
                if (progress != null) progress(++done, report.Tables.Count); else done++;
            }
            body.Add(new XElement(W + "sectPr", new XElement(W + "pgSz", new XAttribute(W + "w", 16838), new XAttribute(W + "h", 11906), new XAttribute(W + "orient", "landscape")), new XElement(W + "pgMar", new XAttribute(W + "top", 720), new XAttribute(W + "right", 720), new XAttribute(W + "bottom", 720), new XAttribute(W + "left", 720))));
            Part(zip, "word/document.xml", new XElement(W + "document", body));
            var styles = new XElement(W + "styles", new XElement(W + "docDefaults", new XElement(W + "rPrDefault", new XElement(W + "rPr", new XElement(W + "rFonts", new XAttribute(W + "ascii", "Calibri"), new XAttribute(W + "eastAsia", "Microsoft JhengHei")), new XElement(W + "sz", new XAttribute(W + "val", 20))))));
            foreach (string style in new[] { "Title", "Heading1" }) styles.Add(new XElement(W + "style", new XAttribute(W + "type", "paragraph"), new XAttribute(W + "styleId", style), new XElement(W + "name", new XAttribute(W + "val", style)), new XElement(W + "pPr", new XElement(W + "keepNext"), style == "Heading1" ? new XElement(W + "outlineLvl", new XAttribute(W + "val", 0)) : null), new XElement(W + "rPr", new XElement(W + "b"), new XElement(W + "sz", new XAttribute(W + "val", style == "Title" ? 36 : 28)))));
            Part(zip, "word/styles.xml", styles);
            Part(zip, "word/_rels/document.xml.rels", new XElement(P + "Relationships", Relationship("rId1", "styles", "styles.xml")));
            var types = Types("word/document.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml");
            types.Add(Override("word/styles.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"));
            Part(zip, "[Content_Types].xml", types);
            Part(zip, "_rels/.rels", new XElement(P + "Relationships", Relationship("rId1", "officeDocument", "word/document.xml")));
        }

        static string SheetName(string value, HashSet<string> used)
        {
            var clean = new string(Text(value).Select(c => "[]:*?/\\".IndexOf(c) >= 0 || char.IsControl(c) ? '_' : c).ToArray()).Trim('\'');
            if (clean.Length == 0) clean = "Table";
            if (clean.Length > 31) clean = ShortName(clean, 31);
            clean = clean.Trim('\'');
            if (clean.Length == 0) clean = "Table";
            if (string.Equals(clean, "History", StringComparison.OrdinalIgnoreCase)) clean += "_";
            string name = clean; int number = 1;
            while (!used.Add(name)) { string suffix = " (" + (++number) + ")"; name = ShortName(clean, 31 - suffix.Length) + suffix; }
            return name;
        }
        static string ShortName(string value, int length)
        {
            length = Math.Min(value.Length, length);
            if (length > 0 && char.IsHighSurrogate(value[length - 1])) length--;
            return value.Substring(0, length);
        }
        static string ColumnName(int index) { string result = ""; do { result = (char)('A' + index % 26) + result; index = index / 26 - 1; } while (index >= 0); return result; }
        static string ExcelText(string value)
        {
            // ST_Xstring escapes are interpreted even in inlineStr. Escape their leading
            // underscores first, then encode real CR so XML newline normalization is lossless.
            return Regex.Replace(value, "_x[0-9A-Fa-f]{4}_", match => "_x005F_" + match.Value.Substring(1)).Replace("\r", "_x000D_");
        }
        static void ExcelRow(XElement data, string[] values, int style, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var last = data.LastNode as XElement;
            int row = last == null ? 1 : (int)last.Attribute("r") + 1;
            if (row > 1048576) throw new InvalidOperationException("Excel worksheet exceeds 1,048,576 rows.");
            var element = new XElement(S + "row", new XAttribute("r", row));
            for (int i = 0; i < values.Length; i++)
            {
                token.ThrowIfCancellationRequested(); string value = Text(values[i]);
                if (value.Length > 32767) throw new InvalidOperationException("Excel cell exceeds 32,767 characters; metadata cannot be exported without loss.");
                element.Add(new XElement(S + "c", new XAttribute("r", ColumnName(i) + row), new XAttribute("t", "inlineStr"), new XAttribute("s", style), new XElement(S + "is", new XElement(S + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), ExcelText(value)))));
            }
            data.Add(element);
        }
        static void WriteExcel(ZipArchive zip, SchemaReportDocument report, ReportTemplate template, CancellationToken token, Action<int, int> progress)
        {
            var sheets = new XElement(S + "sheets"); var rels = new XElement(P + "Relationships");
            var types = Types("xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase); int count = 0, done = 0;
            bool inventory = template.Id == "xlsx.column-inventory";
            var inventoryData = new XElement(S + "sheetData");
            if (inventory) AddIntro(inventoryData, report, token);
            if (inventory) ExcelRow(inventoryData, new[] { "資料表 / Table", "資料表註解 / Table comment" }.Concat(Headers).ToArray(), 1, token);
            foreach (var table in report.Tables)
            {
                token.ThrowIfCancellationRequested();
                var data = inventory ? inventoryData : new XElement(S + "sheetData");
                if (!inventory) { AddIntro(data, report, token); ExcelRow(data, new[] { table.Name, table.Comment }, 0, token); ExcelRow(data, Headers, 1, token); }
                foreach (var col in table.Columns) ExcelRow(data, inventory ? new[] { table.Name, table.Comment }.Concat(Values(col)).ToArray() : Values(col), 0, token);
                if (inventory && table.Columns.Count == 0) ExcelRow(data, new[] { table.Name, table.Comment }, 0, token);
                if (!inventory) AddSheet(zip, data, SheetName(table.Name, used), 7, 5, ++count, sheets, rels, types);
                if (progress != null) progress(++done, report.Tables.Count); else done++;
            }
            if (inventory) AddSheet(zip, inventoryData, "欄位總覽", 9, 4, ++count, sheets, rels, types);
            if (count == 0) { var data = new XElement(S + "sheetData"); AddIntro(data, report, token); AddSheet(zip, data, "Report", 7, 3, ++count, sheets, rels, types); }
            rels.Add(Relationship("styles", "styles", "styles.xml"));
            types.Add(Override("xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"));
            Part(zip, "xl/styles.xml", new XElement(S + "styleSheet", new XElement(S + "fonts", new XAttribute("count", 1), new XElement(S + "font", new XElement(S + "sz", new XAttribute("val", 11)), new XElement(S + "name", new XAttribute("val", "Microsoft JhengHei")))), new XElement(S + "fills", new XAttribute("count", 3), new XElement(S + "fill", new XElement(S + "patternFill", new XAttribute("patternType", "none"))), new XElement(S + "fill", new XElement(S + "patternFill", new XAttribute("patternType", "gray125"))), new XElement(S + "fill", new XElement(S + "patternFill", new XAttribute("patternType", "solid"), new XElement(S + "fgColor", new XAttribute("rgb", "FFF4A340")), new XElement(S + "bgColor", new XAttribute("indexed", 64))))), new XElement(S + "borders", new XAttribute("count", 1), new XElement(S + "border")), new XElement(S + "cellStyleXfs", new XAttribute("count", 1), new XElement(S + "xf", new XAttribute("numFmtId", 0), new XAttribute("fontId", 0), new XAttribute("fillId", 0), new XAttribute("borderId", 0))), new XElement(S + "cellXfs", new XAttribute("count", 2), ExcelStyle(0), ExcelStyle(2))));
            Part(zip, "xl/workbook.xml", new XElement(S + "workbook", new XAttribute(XNamespace.Xmlns + "r", R), sheets));
            Part(zip, "xl/_rels/workbook.xml.rels", rels); Part(zip, "[Content_Types].xml", types);
            Part(zip, "_rels/.rels", new XElement(P + "Relationships", Relationship("rId1", "officeDocument", "xl/workbook.xml")));
        }
        static XElement ExcelStyle(int fill) { return new XElement(S + "xf", new XAttribute("numFmtId", 0), new XAttribute("fontId", 0), new XAttribute("fillId", fill), new XAttribute("borderId", 0), new XAttribute("xfId", 0), new XAttribute("applyAlignment", 1), new XAttribute("applyFill", 1), new XElement(S + "alignment", new XAttribute("vertical", "top"), new XAttribute("wrapText", 1))); }
        static void AddIntro(XElement data, SchemaReportDocument report, CancellationToken token)
        {
            ExcelRow(data, new[] { report.Title, report.DatabaseName, report.EngineName }, 0, token);
            ExcelRow(data, new[] { "作者 / Author", report.Author }, 0, token);
            ExcelRow(data, new[] { "產生時間 / Generated", report.GeneratedAt.ToString("yyyy-MM-dd HH:mm") }, 0, token);
        }
        static void AddSheet(ZipArchive zip, XElement data, string name, int columns, int headerRow, int id, XElement sheets, XElement rels, XElement types)
        {
            string path = "xl/worksheets/sheet" + id + ".xml";
            sheets.Add(new XElement(S + "sheet", new XAttribute("name", name), new XAttribute("sheetId", id), new XAttribute(R + "id", "rId" + id)));
            rels.Add(Relationship("rId" + id, "worksheet", "worksheets/sheet" + id + ".xml")); types.Add(Override(path, "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"));
            Part(zip, path, new XElement(S + "worksheet", new XElement(S + "sheetViews", new XElement(S + "sheetView", new XAttribute("workbookViewId", 0), new XElement(S + "pane", new XAttribute("ySplit", headerRow), new XAttribute("topLeftCell", "A" + (headerRow + 1)), new XAttribute("activePane", "bottomLeft"), new XAttribute("state", "frozen")))), new XElement(S + "cols", Enumerable.Range(1, columns).Select(i => new XElement(S + "col", new XAttribute("min", i), new XAttribute("max", i), new XAttribute("width", i >= columns - 1 ? 45 : 22), new XAttribute("customWidth", 1)))), data, new XElement(S + "autoFilter", new XAttribute("ref", "A" + headerRow + ":" + ColumnName(columns - 1) + Math.Max(headerRow, data.Elements().Count())))));
        }
    }
}
