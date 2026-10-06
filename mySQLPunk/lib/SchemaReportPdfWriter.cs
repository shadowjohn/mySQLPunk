using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading;
using PdfSharp;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace mySQLPunk.lib
{
    /// <summary>Native fixed-layout schema reports. The caller publishes the temporary file.</summary>
    public static class SchemaReportPdfWriter
    {
        // Serialize our GDI/font operations without mutating PDFsharp's process-wide font settings.
        private static readonly object FontGate = new object();

        public static void Write(string path, SchemaReportDocument report, ReportTemplate template,
            CancellationToken token, Action<int, int> progress)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));
            if (template == null) throw new ArgumentNullException(nameof(template));
            if (template.Format != ReportExportFormat.Pdf ||
                (template.Id != "pdf.structure-handbook" && template.Id != "pdf.compact-review"))
                throw new ArgumentException("Unsupported PDF report template.", nameof(template));
            token.ThrowIfCancellationRequested();
            while (!Monitor.TryEnter(FontGate, 100)) token.ThrowIfCancellationRequested();
            try
            {
                token.ThrowIfCancellationRequested();
                using (var document = new PdfDocument())
                using (var layout = new Layout(document, template.Id == "pdf.compact-review", token))
                {
                    document.Info.Title = report.Title ?? "Database structure report";
                    document.Info.Author = report.Author ?? "";
                    layout.NewPage();
                    layout.Paragraph(report.Title ?? "Database structure report", layout.TitleFont);
                    layout.Paragraph("Database: " + report.DatabaseName + "    Engine: " + report.EngineName);
                    layout.Paragraph("Author: " + report.Author + "    Generated: " + report.GeneratedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
                    layout.Paragraph("Schema only / 資料庫結構    Tables: " + report.Tables.Count);
                    if (!layout.Compact)
                    {
                        layout.Paragraph("Contents / 資料表導覽", layout.HeadingFont);
                        for (int i = 0; i < report.Tables.Count; i++)
                        {
                            token.ThrowIfCancellationRequested();
                            var table = report.Tables[i];
                            string title = table.Name;
                            if (!string.IsNullOrWhiteSpace(table.Comment)) title += " - " + table.Comment;
                            layout.Paragraph((i + 1) + ". " + title);
                        }
                    }
                    for (int i = 0; i < report.Tables.Count; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        var table = report.Tables[i];
                        if (!layout.Compact || layout.Remaining < 100) layout.NewPage();
                        layout.Paragraph((i + 1) + ". " + table.Name, layout.HeadingFont);
                        layout.Paragraph(table.Comment ?? "");
                        layout.Headers();
                        foreach (var column in table.Columns)
                        {
                            token.ThrowIfCancellationRequested();
                            layout.Row(new[] { column.Name, column.Comment, column.Type, column.Key,
                                column.Nullable, column.DefaultValue, column.Extra });
                        }
                        layout.Gap();
                        progress?.Invoke(i + 1, report.Tables.Count);
                    }
                    layout.Finish();
                    token.ThrowIfCancellationRequested();
                    document.Save(path);
                    token.ThrowIfCancellationRequested();
                }
            }
            finally { Monitor.Exit(FontGate); }
        }

        private sealed class Layout : IDisposable
        {
            private readonly PdfDocument document;
            private readonly CancellationToken token;
            private XGraphics graphics;
            private double y;
            private const double Margin = 36;
            private const double Bottom = 552;
            private readonly double[] widths = { 112, 192, 104, 43, 54, 125, 140 };
            private readonly XFont font;
            private readonly double lineHeight;
            public readonly bool Compact;
            public readonly XFont TitleFont;
            public readonly XFont HeadingFont;
            public double Remaining { get { return Bottom - y; } }

            public Layout(PdfDocument document, bool compact, CancellationToken token)
            {
                this.document = document;
                this.token = token;
                Compact = compact;
                Exception fontError = null;
                bool fontReady = false;
                // JhengHei may be a TTC face that PDFsharp cannot parse. DFKai-SB is
                // Windows' Traditional Chinese TrueType face; use GDI's actual family API.
                foreach (string name in new[] { "Microsoft JhengHei", "DFKai-SB" })
                {
                    try
                    {
                        using (var family = new FontFamily(name))
                        {
                            if (!string.Equals(family.GetName(1033), name, StringComparison.OrdinalIgnoreCase))
                                continue;
                            font = new XFont(family, compact ? 8.5 : 10, XFontStyleEx.Regular);
                            TitleFont = new XFont(family, compact ? 17 : 23, XFontStyleEx.Bold);
                            HeadingFont = new XFont(family, compact ? 12 : 15, XFontStyleEx.Bold);
                        }
                        fontReady = true;
                        break;
                    }
                    catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException)
                    { fontError = ex; }
                }
                if (!fontReady)
                    throw new InvalidOperationException("PDF export requires a usable Microsoft JhengHei or DFKai-SB Windows Chinese font.", fontError);
                lineHeight = compact ? 12 : 15;
            }

            public void NewPage()
            {
                token.ThrowIfCancellationRequested();
                graphics?.Dispose();
                var page = document.AddPage();
                page.Size = PageSize.A4;
                page.Orientation = PageOrientation.Landscape;
                graphics = XGraphics.FromPdfPage(page);
                y = Margin;
            }

            public void Paragraph(string text, XFont selectedFont = null)
            {
                selectedFont = selectedFont ?? font;
                double height = selectedFont.Size * 1.5;
                foreach (var line in Wrap(text, 770, selectedFont))
                {
                    token.ThrowIfCancellationRequested();
                    if (y + height > Bottom) NewPage();
                    graphics.DrawString(line, selectedFont, XBrushes.Black, new XPoint(Margin, y + selectedFont.Size));
                    y += height;
                }
                y += Compact ? 4 : 8;
            }

            public void Headers()
            {
                if (Remaining < lineHeight + 10) NewPage();
                DrawFragment(new[] { new List<string> { "Name" }, new List<string> { "Comment / 註解" },
                    new List<string> { "Type" }, new List<string> { "Key" }, new List<string> { "NULL" },
                    new List<string> { "Default" }, new List<string> { "Extra" } }, 0, 1, true);
            }

            public void Row(string[] values)
            {
                var lines = values.Select((value, index) => Wrap(value, widths[index] - 8, font)).ToArray();
                int total = lines.Max(cell => cell.Count);
                int offset = 0;
                // A row can exceed a whole page. Continue each cell at the same line offset.
                while (offset < total)
                {
                    token.ThrowIfCancellationRequested();
                    int available = (int)Math.Floor((Remaining - 8) / lineHeight);
                    if (available < 1) { NewPage(); Headers(); continue; }
                    int count = Math.Min(available, total - offset);
                    DrawFragment(lines, offset, count, false);
                    offset += count;
                    if (offset < total) { NewPage(); Headers(); }
                }
            }

            private void DrawFragment(List<string>[] lines, int offset, int count, bool header)
            {
                double height = count * lineHeight + 8;
                double x = Margin;
                var border = new XPen(XColor.FromArgb(190, 200, 208), 0.5);
                for (int cell = 0; cell < lines.Length; cell++)
                {
                    graphics.DrawRectangle(border, header ? new XSolidBrush(XColor.FromArgb(226, 237, 245)) : XBrushes.White,
                        x, y, widths[cell], height);
                    for (int line = 0; line < count && offset + line < lines[cell].Count; line++)
                        graphics.DrawString(lines[cell][offset + line], font, XBrushes.Black,
                            new XPoint(x + 4, y + 4 + font.Size + line * lineHeight));
                    x += widths[cell];
                }
                y += height;
            }

            private List<string> Wrap(string text, double width, XFont selectedFont)
            {
                ValidateGlyphs(text ?? "", selectedFont);
                var result = new List<string>();
                foreach (var paragraph in (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                {
                    string current = "";
                    var elements = StringInfo.GetTextElementEnumerator(paragraph.Replace('\t', ' '));
                    while (elements.MoveNext())
                    {
                        token.ThrowIfCancellationRequested();
                        string element = elements.GetTextElement();
                        if (current.Length != 0 && graphics.MeasureString(current + element, selectedFont).Width > width)
                        { result.Add(current); current = ""; }
                        current += element;
                    }
                    result.Add(current);
                }
                return result;
            }

            private void ValidateGlyphs(string text, XFont selectedFont)
            {
                for (int i = 0; i < text.Length; i++)
                {
                    token.ThrowIfCancellationRequested();
                    char character = text[i];
                    if (character == '\r' || character == '\n' || character == '\t') continue;
                    int codePoint;
                    if (char.IsHighSurrogate(character))
                    {
                        if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                            throw new InvalidOperationException("PDF report text contains an invalid Unicode surrogate.");
                        codePoint = char.ConvertToUtf32(character, text[++i]);
                    }
                    else
                    {
                        if (char.IsLowSurrogate(character))
                            throw new InvalidOperationException("PDF report text contains an invalid Unicode surrogate.");
                        codePoint = character;
                    }
                    if (GlyphHelper.GlyphIndexFromCodePoint(codePoint, selectedFont) == 0)
                        throw new InvalidOperationException("The installed PDF font cannot display U+" +
                            codePoint.ToString("X4", CultureInfo.InvariantCulture) +
                            ". Use DOCX or XLSX to retain this report text.");
                }
            }

            public void Gap() { y += Compact ? 12 : 20; }
            public void Finish()
            {
                graphics?.Dispose();
                graphics = null;
                for (int i = 0; i < document.PageCount; i++)
                {
                    token.ThrowIfCancellationRequested();
                    using (var footer = XGraphics.FromPdfPage(document.Pages[i], XGraphicsPdfPageOptions.Append))
                        footer.DrawString((i + 1) + " / " + document.PageCount, font, XBrushes.Gray,
                            new XRect(Margin, 566, 770, 15), XStringFormats.Center);
                }
            }
            public void Dispose() { graphics?.Dispose(); }
        }
    }
}
