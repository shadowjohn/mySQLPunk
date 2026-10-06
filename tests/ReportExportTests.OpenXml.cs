using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using mySQLPunk.lib;

public static partial class ReportExportTests
{
    public static void TestOpenXml()
    {
        TestSpecification();
        string dir = Path.Combine(Path.GetTempPath(), "mySQLPunk-Office-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var report = SampleReport();
            report.Title = "資料庫 <&> 😀";
            report.Tables[0].Name = "a/b:*?[]\\";
            report.Tables[0].Columns[0].DefaultValue = "=SUM(A1:A2)";
            report.Tables[0].Columns[0].Comment = "中文 <&> 😀\nline two";
            report.Tables[0].Columns[0].Extra = "literal _x0041_ and _x000D_ and _x005F_\r\nreal CR";
            report.Tables.Add(new SchemaReportTable { Name = "A_B_____" });
            report.Tables.Add(new SchemaReportTable { Name = "a_b_____" });
            report.Tables.Add(new SchemaReportTable { Name = new string('字', 30) + "😀ending" });
            foreach (string id in new[] { "docx.classic-dictionary", "docx.project-handover", "xlsx.table-sheets", "xlsx.column-inventory" })
            {
                string path = Path.Combine(dir, id + ".zip"); int completed = 0;
                SchemaReportOpenXmlWriter.Write(path, report, ReportTemplateCatalog.Find(id), CancellationToken.None, (n, total) => { completed = n; Assert(total == report.Tables.Count, "Office progress total"); });
                Assert(completed == report.Tables.Count, "Office progress completion");
                using (var zip = ZipFile.OpenRead(path))
                {
                    foreach (var entry in zip.Entries) using (var stream = entry.Open()) XDocument.Load(stream);
                    Assert(zip.GetEntry("[Content_Types].xml") != null, "Office content types");
                    if (id.StartsWith("docx"))
                    {
                        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
                        XDocument xml; using (var stream = zip.GetEntry("word/document.xml").Open()) xml = XDocument.Load(stream);
                        Assert(xml.Descendants(w + "t").Any(t => t.Value == report.Title), "Word exact title roundtrip");
                        Assert(xml.Descendants(w + "t").Any(t => t.Value == report.Tables[0].Columns[0].Comment), "Word comment roundtrip");
                        Assert(xml.Descendants(w + "t").Any(t => t.Value == report.Tables[0].Columns[0].Extra), "Word CR and literal-escape roundtrip");
                        foreach (var table in xml.Descendants(w + "tbl"))
                        {
                            Assert(table.Elements().Select(e => e.Name.LocalName).Take(3).SequenceEqual(new[] { "tblPr", "tblGrid", "tr" }), "Word required table grid order");
                            Assert(table.Element(w + "tblGrid").Elements(w + "gridCol").Count() == 7, "Word seven grid columns");
                            Assert(table.Element(w + "tblGrid").Elements(w + "gridCol").Sum(e => (int)e.Attribute(w + "w")) <= 16838 - 1440, "Word grid fits page width");
                        }
                        foreach (var row in xml.Descendants(w + "tr").Where(r => r.Descendants(w + "tblHeader").Any()))
                            foreach (var cell in row.Elements(w + "tc"))
                            {
                                Assert(cell.Element(w + "tcPr").Elements().Select(e => e.Name.LocalName).SequenceEqual(new[] { "tcW", "shd", "tcMar", "vAlign" }), "Word schema cell-property order");
                                Assert((string)cell.Descendants(w + "vAlign").Single().Attribute(w + "val") == "center", "Word header centered");
                                Assert((string)cell.Descendants(w + "left").Single().Attribute(w + "w") == "75", "Word header 75 twips");
                            }
                        if (id.EndsWith("handover")) Assert(xml.Descendants(w + "fldSimple").Any(), "Handover contents field");
                    }
                    else
                    {
                        XNamespace s = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                        XDocument workbook; using (var stream = zip.GetEntry("xl/workbook.xml").Open()) workbook = XDocument.Load(stream);
                        var names = workbook.Descendants(s + "sheet").Select(e => (string)e.Attribute("name")).ToArray();
                        Assert(names.All(n => n.Length <= 31 && !n.Any(c => "[]:*?/\\".Contains(c))), "Safe sheet names");
                        Assert(names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == names.Length, "Unique sheet names");
                        bool formulaFound = false, escapedTextFound = false;
                        foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/")))
                        {
                            XDocument sheet; using (var stream = entry.Open()) sheet = XDocument.Load(stream);
                            Assert(!sheet.Descendants(s + "f").Any(), "No executable formulas");
                            formulaFound |= sheet.Descendants(s + "c").Any(c => (string)c.Attribute("t") == "inlineStr" && c.Value == "=SUM(A1:A2)");
                            escapedTextFound |= sheet.Descendants(s + "t").Any(t => DecodeExcelText(t.Value) == report.Tables[0].Columns[0].Extra);
                            Assert(sheet.Descendants(s + "pane").Any() && sheet.Descendants(s + "autoFilter").Any(), "Excel frozen and filtered");
                        }
                        Assert(formulaFound, "Formula-looking default exported as text");
                        Assert(escapedTextFound, "Excel consumer decoding preserves literal escapes and carriage returns");
                    }
                }
            }
            bool cancelled = false;
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                try { SchemaReportOpenXmlWriter.Write(Path.Combine(dir, "cancelled"), report, ReportTemplateCatalog.Find("docx.classic-dictionary"), cts.Token, null); }
                catch (OperationCanceledException) { cancelled = true; }
            }
            Assert(cancelled && !File.Exists(Path.Combine(dir, "cancelled")), "Pre-cancellation creates no file");
            bool midCancelled = false;
            using (var cts = new CancellationTokenSource())
            {
                try { SchemaReportOpenXmlWriter.Write(Path.Combine(dir, "mid-cancelled"), report, ReportTemplateCatalog.Find("xlsx.table-sheets"), cts.Token, (n, total) => cts.Cancel()); }
                catch (OperationCanceledException) { midCancelled = true; }
            }
            Assert(midCancelled, "Cancellation checked between table writes");
            report.Tables[0].Columns[0].Comment = new string('字', 32768);
            bool rejected = false;
            try { SchemaReportOpenXmlWriter.Write(Path.Combine(dir, "too-long"), report, ReportTemplateCatalog.Find("xlsx.table-sheets"), CancellationToken.None, null); }
            catch (InvalidOperationException) { rejected = true; }
            Assert(rejected, "Excel oversized cell fails explicitly");
        }
        finally { Directory.Delete(dir, true); }
    }
    // Source coverage retained for the explicitly gated report runner; not run during the AV incident.
    static void TestSpecification()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mysqlpunk-specification-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var report = new SchemaReportDocument { Title = "系統規格 <&> 😀", DatabaseName = "demo", EngineName = "MySQL" };
            var table = new SchemaReportTable { Name = "spec_table", Comment = "測試註解" };
            table.Columns.Add(new SchemaReportColumn { Name = "id", Type = "int(11) unsigned", Key = "PRI", Nullable = "NO" });
            table.Columns.Add(new SchemaReportColumn { Name = "display_name", Type = "varchar(80)", Comment = "中文 <&> 😀", Nullable = "YES" });
            table.Columns.Add(new SchemaReportColumn { Name = "amount", Type = "decimal(12,2) unsigned", Nullable = "NO" });
            table.Columns.Add(new SchemaReportColumn { Name = "state", Type = "enum('a','b')", Nullable = "" });
            table.Columns.Add(new SchemaReportColumn { Name = "multiline", Type = "text", Comment = "first\nsecond\tvalue", Nullable = "YES" });
            table.Indexes = new System.Data.DataTable();
            foreach (string field in new[] { "Key_name", "Column_name", "Seq_in_index" }) table.Indexes.Columns.Add(field);
            table.Indexes.Rows.Add("ix_composite", "amount", "2");
            table.Indexes.Rows.Add("ix_composite", "display_name", "1");
            report.Tables.Add(table);
            var template = ReportTemplateCatalog.Find("docx.schema-specification");
            string path = Path.Combine(dir, "specification.docx"); int completed = 0;
            SchemaReportOpenXmlWriter.Write(path, report, template, CancellationToken.None, (n, total) => { completed = n; Assert(total == 1, "Specification progress total"); });
            Assert(completed == 1, "Specification progress completion");
            XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            XDocument xml;
            using (var zip = ZipFile.OpenRead(path)) using (var stream = zip.GetEntry("word/document.xml").Open()) xml = XDocument.Load(stream);
            var page = xml.Descendants(w + "pgSz").Single();
            Assert((int)page.Attribute(w + "w") == 11906 && (int)page.Attribute(w + "h") == 16838, "Specification portrait A4");
            Assert(xml.Descendants(w + "t").Any(t => t.Value == report.Title), "Specification exact Unicode title");
            Assert(xml.Descendants(w + "t").Any(t => t.Value == "中文 <&> 😀"), "Specification exact Unicode comment");
            Assert(xml.Descendants(w + "pageBreakBefore").Any(), "Specification table begins on a new page");
            Assert(xml.Descendants(w + "br").Any() && xml.Descendants(w + "tab").Any(), "Specification preserves visible comment line breaks and tabs");
            foreach (var grid in xml.Descendants(w + "tblGrid"))
                Assert(grid.Elements(w + "gridCol").Sum(c => (int)c.Attribute(w + "w")) <= 9638, "Specification table fits portrait page");
            var fields = xml.Descendants(w + "tbl").Single(t => t.Element(w + "tblGrid").Elements(w + "gridCol").Count() == 7);
            Assert(fields.Elements(w + "tr").First().Descendants(w + "tblHeader").Any(), "Specification repeats field header");
            var rows = fields.Elements(w + "tr").Skip(1).Select(r => r.Elements(w + "tc").Select(c => string.Concat(c.Descendants(w + "t").Select(t => t.Value))).ToArray()).ToArray();
            Assert(rows[0][3] == "V" && rows[0][4] == "V", "Specification actual PK and NN");
            Assert(rows[0][5] == "int(11) unsigned" && rows[0][6] == "—", "Integer display width is not storage length");
            Assert(rows[1][5] == "varchar" && rows[1][6] == "80" && rows[1][4] == "-", "Character length and nullable flag");
            Assert(rows[2][5] == "decimal unsigned" && rows[2][6] == "12,2", "Precision scale and type modifier preserved");
            Assert(rows[3][5] == "enum('a','b')" && rows[3][6] == "—" && rows[3][4] == "?", "Unsupported type and unknown nullability stay explicit");
            Assert(!fields.Elements(w + "tr").First().Descendants(w + "t").Any(t => t.Value == "FK"), "Specification does not fabricate foreign keys");
            Assert(xml.Descendants(w + "t").Any(t => t.Value.Contains("ix_composite") && t.Value.IndexOf("display_name", StringComparison.Ordinal) < t.Value.IndexOf("amount", StringComparison.Ordinal)), "Composite index uses metadata column order");
            foreach (var cell in fields.Descendants(w + "tc"))
            {
                Assert((string)cell.Descendants(w + "vAlign").Single().Attribute(w + "val") == "center", "Specification vertical cell alignment");
                Assert((string)cell.Descendants(w + "tcMar").Single().Element(w + "left").Attribute(w + "w") == "75", "Specification 5px left padding");
            }
            Assert(fields.Elements(w + "tr").First().Descendants(w + "shd").All(s => (string)s.Attribute(w + "fill") == "D9E1F2"), "Specification blue field header");
            using (var cts = new CancellationTokenSource())
            {
                bool cancelled = false;
                try { SchemaReportOpenXmlWriter.Write(Path.Combine(dir, "cancelled.docx"), report, template, cts.Token, (n, total) => cts.Cancel()); }
                catch (OperationCanceledException) { cancelled = true; }
                Assert(cancelled, "Specification observes cancellation after progress");
            }
        }
        finally { Directory.Delete(dir, true); }
    }
    static string DecodeExcelText(string text)
    {
        return Regex.Replace(text, "_x([0-9A-Fa-f]{4})_", match => ((char)Convert.ToInt32(match.Groups[1].Value, 16)).ToString());
    }
}
