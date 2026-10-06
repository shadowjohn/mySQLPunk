using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using mySQLPunk.lib;
using PdfSharp.Pdf.IO;

public static partial class ReportExportTests
{
    public static void TestPdf()
    {
        string directory = Path.Combine(Path.GetTempPath(), "mySQLPunk-report-pdf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var report = SampleReport();
            report.Tables[0].Comment = string.Concat(Enumerable.Repeat("資料表中文註解換行測試", 200));
            report.Tables[0].Columns[0].Comment = string.Concat(Enumerable.Repeat("中文欄位註解完整保留測試", 900)) + "尾端標記";
            report.Tables[0].Columns[0].Name = "長識別字_" + new string('x', 300);
            report.Tables[0].Columns[0].DefaultValue = new string('值', 1000);
            foreach (string id in new[] { "pdf.structure-handbook", "pdf.compact-review" })
            {
                string path = Path.Combine(directory, id + ".pdf");
                int completed = 0;
                SchemaReportPdfWriter.Write(path, report, ReportTemplateCatalog.Find(id), CancellationToken.None,
                    (current, total) => { completed = current; Assert(total == report.Tables.Count, "PDF progress total"); });
                Assert(completed == report.Tables.Count, "PDF progress completed");
                using (var pdf = PdfReader.Open(path, PdfDocumentOpenMode.Import))
                {
                    Assert(pdf.PageCount > 3, "Oversized CJK fields paginate across several PDF pages");
                    Assert(pdf.Info.Title == (report.Title ?? "Database structure report"), "PDF document title");
                    Assert(pdf.Pages.Cast<PdfSharp.Pdf.PdfPage>().All(page => page.Width.Point > page.Height.Point), "PDF readable landscape column layout");
                }
                string bytes = Encoding.ASCII.GetString(File.ReadAllBytes(path));
                Assert(bytes.StartsWith("%PDF-"), "Real PDF file signature");
                Assert(bytes.Contains("/FontFile2"), "PDF embeds Windows TrueType Chinese font");
                Assert(bytes.Contains("/ToUnicode"), "PDF preserves a Unicode text mapping");
            }

            string cancelledPath = Path.Combine(directory, "cancelled.pdf");
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                bool cancelled = false;
                try { SchemaReportPdfWriter.Write(cancelledPath, report, ReportTemplateCatalog.Find("pdf.compact-review"), cancel.Token, null); }
                catch (OperationCanceledException) { cancelled = true; }
                Assert(cancelled && !File.Exists(cancelledPath), "Pre-cancelled PDF does not create output");
            }
            using (var cancel = new CancellationTokenSource())
            {
                bool cancelled = false;
                try
                {
                    SchemaReportPdfWriter.Write(cancelledPath, report, ReportTemplateCatalog.Find("pdf.compact-review"), cancel.Token,
                        (current, total) => cancel.Cancel());
                }
                catch (OperationCanceledException) { cancelled = true; }
                Assert(cancelled && !File.Exists(cancelledPath), "Cancellation during PDF layout prevents saving partial output");
            }

            // PDFsharp font caches are process-wide. Independent writers must remain safe.
            var smallReport = SampleReport();
            Task.WaitAll(Enumerable.Range(0, 2).Select(index => Task.Run(() =>
                SchemaReportPdfWriter.Write(Path.Combine(directory, "parallel-" + index + ".pdf"), smallReport,
                    ReportTemplateCatalog.Find("pdf.compact-review"), CancellationToken.None, null))).ToArray());
            Assert(File.Exists(Path.Combine(directory, "parallel-0.pdf")) && File.Exists(Path.Combine(directory, "parallel-1.pdf")),
                "Concurrent PDF exports complete independently");

            foreach (int codePoint in new[] { 0x1F600, 0x20000 })
            {
                var unsupported = SampleReport();
                unsupported.Tables[0].Columns[0].Comment = "中文 " + char.ConvertFromUtf32(codePoint);
                string unsupportedPath = Path.Combine(directory, "unsupported-" + codePoint + ".pdf");
                bool rejected = false;
                try
                {
                    SchemaReportPdfWriter.Write(unsupportedPath, unsupported, ReportTemplateCatalog.Find("pdf.compact-review"),
                        CancellationToken.None, null);
                }
                catch (InvalidOperationException ex)
                {
                    rejected = ex.Message.Contains("U+" + codePoint.ToString("X4"));
                }
                Assert(rejected && !File.Exists(unsupportedPath), "Unavailable emoji/rare CJK glyph fails explicitly before saving");
            }
        }
        finally { Directory.Delete(directory, true); }
    }
}
