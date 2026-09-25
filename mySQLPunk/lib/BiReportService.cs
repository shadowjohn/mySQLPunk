using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;

namespace mySQLPunk.lib
{
    /// <summary>儀表板產生的報表：各資料集的載入結果與整份 HTML。</summary>
    public sealed class BiReport
    {
        public BiReport()
        {
            Data = new Dictionary<string, BiDatasetData>(StringComparer.OrdinalIgnoreCase);
            Errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public Dictionary<string, BiDatasetData> Data { get; private set; }
        public Dictionary<string, string> Errors { get; private set; }
        public string Html { get; set; }
        public int Rows { get { return Data.Values.Sum(item => item.Rows.Count); } }
    }

    /// <summary>
    /// 把儀表板輸出成單一 HTML 檔：每張圖以內嵌 SVG 繪製（長條、折線、圓餅、數字卡、表格），不含任何腳本，
    /// 以 CSP 禁止外部資源；所有文字都經 HTML 跳脫。可手動匯出，也可由自動執行作業定期產生並寄出。
    /// </summary>
    public static class BiReportService
    {
        private static readonly string[] Palette =
        {
            "#2563eb", "#16a34a", "#ea580c", "#9333ea", "#db2777", "#0d9488", "#ca8a04", "#4f46e5", "#dc2626", "#0891b2", "#65a30d", "#64748b"
        };

        /// <summary>執行所有資料集（各自唯讀）並產生報表；單一資料集失敗不會中斷其他資料集。</summary>
        public static BiReport Run(IDatabase database, string databaseName, BiDashboard dashboard, DateTime generatedLocal)
        {
            BiDashboardService.Validate(dashboard);
            BiReport report = new BiReport();
            foreach (BiDataset dataset in dashboard.Datasets)
            {
                try
                {
                    report.Data[dataset.Name] = BiDashboardService.Prepare(BiDashboardService.Query(database, databaseName, dataset), dataset);
                }
                catch (Exception exception)
                {
                    report.Errors[dataset.Name] = ExceptionMessageService.GetReason(exception);
                }
            }
            report.Html = BuildHtml(dashboard, report.Data, report.Errors, null, databaseName, generatedLocal);
            return report;
        }

        public static string BuildHtml(BiDashboard dashboard, IDictionary<string, BiDatasetData> data, IDictionary<string, string> errors,
            IList<BiFilter> filters, string databaseName, DateTime generatedLocal)
        {
            filters = filters ?? new List<BiFilter>();
            string title = string.IsNullOrWhiteSpace(dashboard.Title) ? Localization.T("Bi.Untitled") : dashboard.Title;
            StringBuilder html = new StringBuilder();
            html.Append("<!DOCTYPE html>\n<html><head><meta charset=\"utf-8\">");
            html.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; img-src data:\">");
            html.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
            html.Append("<title>").Append(E(title)).Append("</title><style>");
            html.Append("body{font-family:'Segoe UI','Microsoft JhengHei',sans-serif;margin:0;background:#f5f6f8;color:#1f2328}");
            html.Append("header{padding:18px 24px;background:#1b2b4b;color:#fff}header h1{margin:0;font-size:22px}header p{margin:4px 0 0;color:#b9c7e8;font-size:13px}");
            html.Append(".grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(340px,1fr));gap:14px;padding:18px 24px}");
            html.Append(".card{background:#fff;border:1px solid #d0d5dd;border-radius:8px;padding:12px 14px;overflow:hidden}.card.wide{grid-column:span 2}");
            html.Append(".card h2{margin:0 0 8px;font-size:15px}.meta{color:#667085;font-size:12px;margin-top:8px}.error{color:#b42318;font-size:13px}");
            html.Append(".number{font-size:40px;font-weight:700;text-align:center;margin:28px 0 4px}.caption{text-align:center;color:#667085;font-size:13px}");
            html.Append("table{border-collapse:collapse;width:100%;font-size:13px}th,td{border-bottom:1px solid #eaecf0;padding:4px 6px;text-align:left}td.num{text-align:right}");
            html.Append("svg text{font-family:inherit}@media(max-width:760px){.card.wide{grid-column:auto}}");
            html.Append("</style></head><body>");
            html.Append("<header><h1>").Append(E(title)).Append("</h1><p>");
            html.Append(E(Localization.Format("Bi.Report.Generated", databaseName ?? string.Empty, generatedLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))));
            if (filters.Count > 0) html.Append(" · ").Append(E(Localization.T("Bi.ActiveFilters") + " " + string.Join(", ", filters.Select(f => f.Field + " = " + f.Label))));
            html.Append("</p></header><main class=\"grid\">");

            for (int index = 0; index < dashboard.Widgets.Count; index++)
            {
                BiWidget widget = dashboard.Widgets[index];
                html.Append("<section class=\"card").Append(widget.Span >= 2 ? " wide" : string.Empty).Append("\"><h2>")
                    .Append(E(string.IsNullOrWhiteSpace(widget.Title) ? BiDashboardService.AggregateCaption(widget) : widget.Title)).Append("</h2>");
                BiDatasetData dataset;
                string error;
                if (!data.TryGetValue(widget.Dataset, out dataset))
                {
                    html.Append("<p class=\"error\">").Append(E(errors != null && errors.TryGetValue(widget.Dataset, out error)
                        ? Localization.Format("Bi.DatasetError", widget.Dataset, error)
                        : Localization.T("Bi.NotLoaded"))).Append("</p></section>");
                    continue;
                }

                BiWidgetResult result = BiDashboardService.Compute(widget, index, dataset, filters);
                if (!string.IsNullOrEmpty(result.Error))
                {
                    html.Append("<p class=\"error\">").Append(E(result.Error)).Append("</p>");
                }
                else
                {
                    switch (widget.Kind)
                    {
                        case BiChartKind.Number:
                            html.Append("<div class=\"number\">").Append(E(BiDashboardService.FormatNumber(result.Total))).Append("</div><div class=\"caption\">")
                                .Append(E(BiDashboardService.AggregateCaption(widget))).Append("</div>");
                            break;
                        case BiChartKind.Table:
                            AppendTable(html, result.Table);
                            break;
                        case BiChartKind.Pie:
                            html.Append(result.Points.Count == 0 ? NoData() : PieSvg(result.Points));
                            break;
                        case BiChartKind.Line:
                            html.Append(result.Points.Count == 0 ? NoData() : LineSvg(result.Points, widget.Span >= 2 ? 760 : 360));
                            break;
                        default:
                            html.Append(result.Points.Count == 0 ? NoData() : BarSvg(result.Points, widget.Span >= 2 ? 760 : 360));
                            break;
                    }
                }
                html.Append("<div class=\"meta\">").Append(E(Localization.Format("Bi.CardFooter", widget.Dataset, result.MatchedRows)));
                if (result.GroupCount > result.Points.Count && widget.Kind != BiChartKind.Table) html.Append(" · ").Append(E(Localization.Format("Bi.CardTop", result.Points.Count, result.GroupCount)));
                if (dataset.Truncated) html.Append(" · ").Append(E(Localization.Format("Bi.Truncated", BiDashboardService.MaximumRows)));
                html.Append("</div></section>");
            }

            html.Append("</main></body></html>");
            return html.ToString();
        }

        private static string NoData()
        {
            return "<p class=\"meta\">" + E(Localization.T("Bi.NoData")) + "</p>";
        }

        private static void AppendTable(StringBuilder html, DataTable table)
        {
            if (table == null)
            {
                html.Append(NoData());
                return;
            }
            html.Append("<table><thead><tr>");
            foreach (DataColumn column in table.Columns) html.Append("<th>").Append(E(column.ColumnName)).Append("</th>");
            html.Append("</tr></thead><tbody>");
            foreach (DataRow row in table.Rows)
            {
                html.Append("<tr>");
                foreach (DataColumn column in table.Columns)
                {
                    object value = row[column];
                    bool number = value is decimal;
                    html.Append(number ? "<td class=\"num\">" : "<td>")
                        .Append(E(value == null || value is DBNull ? string.Empty : number ? BiDashboardService.FormatNumber((decimal)value) : Convert.ToString(value, CultureInfo.InvariantCulture)))
                        .Append("</td>");
                }
                html.Append("</tr>");
            }
            html.Append("</tbody></table>");
        }

        public static string BarSvg(IList<BiPoint> points, int width)
        {
            const int rowHeight = 24;
            int labelWidth = Math.Min(140, width / 3);
            int barWidth = width - labelWidth - 70;
            decimal max = Math.Max(1e-9m, points.Max(point => Math.Abs(point.Value ?? 0m)));
            int height = points.Count * rowHeight + 4;
            StringBuilder svg = new StringBuilder();
            svg.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" role=\"img\" width=\"100%\" viewBox=\"0 0 ").Append(width).Append(' ').Append(height).Append("\">");
            for (int i = 0; i < points.Count; i++)
            {
                BiPoint point = points[i];
                double length = (double)(Math.Abs(point.Value ?? 0m) / max) * barWidth;
                int y = i * rowHeight;
                svg.Append("<text x=\"").Append(labelWidth - 6).Append("\" y=\"").Append(y + 16).Append("\" font-size=\"12\" text-anchor=\"end\">").Append(E(Clip(point.Label, 22))).Append("</text>");
                svg.Append("<rect x=\"").Append(labelWidth).Append("\" y=\"").Append(y + 5).Append("\" width=\"").Append(N(Math.Max(1, length))).Append("\" height=\"15\" fill=\"#2563eb\"><title>")
                    .Append(E(point.Label + ": " + BiDashboardService.FormatNumber(point.Value))).Append("</title></rect>");
                svg.Append("<text x=\"").Append(N(labelWidth + length + 4)).Append("\" y=\"").Append(y + 16).Append("\" font-size=\"11\" fill=\"#667085\">").Append(E(BiDashboardService.FormatNumber(point.Value))).Append("</text>");
            }
            svg.Append("</svg>");
            return svg.ToString();
        }

        public static string LineSvg(IList<BiPoint> points, int width)
        {
            const int height = 200;
            const int left = 52;
            const int bottom = 22;
            decimal max = points.Max(point => point.Value ?? 0m);
            decimal min = Math.Min(0m, points.Min(point => point.Value ?? 0m));
            if (max == min) max = min + 1m;
            double plotWidth = width - left - 8;
            double plotHeight = height - bottom - 8;
            double slot = plotWidth / points.Count;
            StringBuilder svg = new StringBuilder();
            svg.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" role=\"img\" width=\"100%\" viewBox=\"0 0 ").Append(width).Append(' ').Append(height).Append("\">");
            for (int step = 0; step <= 4; step++)
            {
                double y = 8 + plotHeight - plotHeight * step / 4.0;
                svg.Append("<line x1=\"").Append(left).Append("\" x2=\"").Append(width - 8).Append("\" y1=\"").Append(N(y)).Append("\" y2=\"").Append(N(y)).Append("\" stroke=\"#eaecf0\"/>");
                svg.Append("<text x=\"").Append(left - 4).Append("\" y=\"").Append(N(y + 4)).Append("\" font-size=\"10\" text-anchor=\"end\" fill=\"#667085\">")
                    .Append(E(BiDashboardService.FormatNumber(min + (max - min) * step / 4m))).Append("</text>");
            }
            List<string> coordinates = new List<string>();
            int labelEvery = Math.Max(1, (int)Math.Ceiling(points.Count * 70.0 / plotWidth));
            for (int i = 0; i < points.Count; i++)
            {
                double x = left + slot * (i + 0.5);
                double y = 8 + plotHeight - (double)(((points[i].Value ?? 0m) - min) / (max - min)) * plotHeight;
                coordinates.Add(N(x) + "," + N(y));
                svg.Append("<circle cx=\"").Append(N(x)).Append("\" cy=\"").Append(N(y)).Append("\" r=\"3\" fill=\"#2563eb\"><title>")
                    .Append(E(points[i].Label + ": " + BiDashboardService.FormatNumber(points[i].Value))).Append("</title></circle>");
                if (i % labelEvery == 0)
                {
                    svg.Append("<text x=\"").Append(N(x)).Append("\" y=\"").Append(height - 6).Append("\" font-size=\"10\" text-anchor=\"middle\" fill=\"#667085\">").Append(E(Clip(points[i].Label, 12))).Append("</text>");
                }
            }
            if (coordinates.Count > 1)
            {
                svg.Append("<polyline fill=\"none\" stroke=\"#2563eb\" stroke-width=\"2\" points=\"").Append(string.Join(" ", coordinates)).Append("\"/>");
            }
            svg.Append("</svg>");
            return svg.ToString();
        }

        public static string PieSvg(IList<BiPoint> points)
        {
            List<BiPoint> positive = points.Where(point => (point.Value ?? 0m) > 0m).ToList();
            decimal total = positive.Sum(point => point.Value.Value);
            if (total <= 0m) return "<p class=\"meta\">" + E(Localization.T("Bi.PieNeedsPositive")) + "</p>";
            const double cx = 90, cy = 90, r = 80;
            int height = Math.Max(180, positive.Count * 18 + 8);
            StringBuilder svg = new StringBuilder();
            svg.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" role=\"img\" width=\"100%\" viewBox=\"0 0 360 ").Append(height).Append("\">");
            double start = -Math.PI / 2;
            for (int i = 0; i < positive.Count; i++)
            {
                double fraction = (double)(positive[i].Value.Value / total);
                string color = Palette[points.IndexOf(positive[i]) % Palette.Length];
                string tip = "<title>" + E(positive[i].Label + ": " + BiDashboardService.FormatNumber(positive[i].Value) + " (" + Percent(fraction) + ")") + "</title>";
                if (fraction >= 0.9999)
                {
                    svg.Append("<circle cx=\"90\" cy=\"90\" r=\"80\" fill=\"").Append(color).Append("\">").Append(tip).Append("</circle>");
                }
                else
                {
                    double end = start + fraction * 2 * Math.PI;
                    svg.Append("<path d=\"M").Append(N(cx)).Append(',').Append(N(cy))
                        .Append(" L").Append(N(cx + r * Math.Cos(start))).Append(',').Append(N(cy + r * Math.Sin(start)))
                        .Append(" A").Append(N(r)).Append(',').Append(N(r)).Append(" 0 ").Append(fraction > 0.5 ? 1 : 0).Append(",1 ")
                        .Append(N(cx + r * Math.Cos(end))).Append(',').Append(N(cy + r * Math.Sin(end))).Append(" Z\" fill=\"").Append(color)
                        .Append("\" stroke=\"#fff\" stroke-width=\"1.5\">").Append(tip).Append("</path>");
                    start = end;
                }
                int ly = 12 + i * 18;
                svg.Append("<rect x=\"190\" y=\"").Append(ly - 9).Append("\" width=\"10\" height=\"10\" fill=\"").Append(color).Append("\"/>");
                svg.Append("<text x=\"206\" y=\"").Append(ly).Append("\" font-size=\"12\">").Append(E(Clip(positive[i].Label, 16) + "  " + Percent(fraction))).Append("</text>");
            }
            svg.Append("</svg>");
            return svg.ToString();
        }

        private static string Percent(double fraction)
        {
            return (fraction * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
        }

        private static string Clip(string value, int length)
        {
            value = value ?? string.Empty;
            return value.Length <= length ? value : value.Substring(0, length - 1) + "…";
        }

        private static string N(double value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static string E(string value)
        {
            return WebUtility.HtmlEncode(value ?? string.Empty);
        }
    }
}
