using System;
using System.Collections.Generic;
using System.Data;
using System.Net;
using System.Text;

namespace mySQLPunk.lib
{
    /// <summary>
    /// 資料字典：把一個資料庫的資料表／檢視結構整理成一份可讀的 HTML 文件
    /// （欄位、索引、CREATE 語句、註解），用瀏覽器開啟後可直接列印或另存 PDF。
    /// 全部走 IDatabase 的通用 metadata API，五種引擎共用同一份程式。
    /// </summary>
    public static class DataDictionaryService
    {
        public static string BuildHtml(IDatabase db, string databaseName, string engineName, string hostName, string appVersion)
        {
            List<string> tables = SafeList(() => db.GetTables(databaseName));
            List<string> views = SafeList(() => db.GetViews(databaseName));

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("<!doctype html>");
            sb.AppendLine("<html lang=\"zh-Hant\"><head><meta charset=\"utf-8\">");
            sb.AppendLine("<title>" + H(databaseName) + " - " + H(Localization.T("Dict.Title")) + "</title>");
            sb.AppendLine("<style>");
            sb.AppendLine(@"
body { font-family: 'Segoe UI', 'Microsoft JhengHei', sans-serif; margin: 0; color: #24292f; }
.page { max-width: 960px; margin: 0 auto; padding: 32px 40px 64px; }
h1 { font-size: 26px; border-bottom: 3px solid #2563eb; padding-bottom: 10px; }
h2 { font-size: 20px; color: #2563eb; margin-top: 40px; border-bottom: 1px solid #d0d7de; padding-bottom: 6px; }
h3 { font-size: 16px; margin: 26px 0 6px; }
table { border-collapse: collapse; width: 100%; font-size: 13px; margin: 8px 0 4px; }
th, td { border: 1px solid #d0d7de; padding: 5px 9px; text-align: left; vertical-align: top; word-break: break-word; }
th { background: #f6f8fa; font-weight: 600; white-space: nowrap; }
tr:nth-child(even) td { background: #fbfcfd; }
.meta { color: #57606a; font-size: 13px; line-height: 1.8; }
.toc { columns: 2; font-size: 14px; margin: 12px 0; }
.toc a { color: #2563eb; text-decoration: none; display: block; padding: 1px 0; }
details { margin: 6px 0 14px; }
summary { cursor: pointer; color: #57606a; font-size: 13px; }
pre { background: #f6f8fa; border: 1px solid #d0d7de; border-radius: 6px; padding: 10px 12px; font-size: 12px; overflow-x: auto; white-space: pre-wrap; }
.err { color: #cb2f2f; font-size: 13px; }
.print-hint { background: #eef4ff; border: 1px solid #b6ccf5; border-radius: 6px; padding: 8px 12px; font-size: 13px; }
@media print { .print-hint { display: none; } h2 { page-break-before: always; } h2:first-of-type { page-break-before: avoid; } }
");
            sb.AppendLine("</style></head><body><div class=\"page\">");

            // ── 封面資訊 ──
            sb.AppendLine("<h1>" + H(Localization.T("Dict.Title")) + "：" + H(databaseName) + "</h1>");
            sb.AppendLine("<p class=\"meta\">");
            sb.AppendLine(H(Localization.T("Dict.Engine")) + "：" + H(engineName) + "<br>");
            if (!string.IsNullOrWhiteSpace(hostName)) sb.AppendLine(H(Localization.T("Dict.Server")) + "：" + H(hostName) + "<br>");
            sb.AppendLine(H(Localization.T("Dict.GeneratedAt")) + "：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "<br>");
            sb.AppendLine(H(Localization.T("Dict.GeneratedBy")) + "：mySQLPunk " + H(appVersion) + "<br>");
            sb.AppendLine(H(Localization.T("Dict.TableCount")) + "：" + tables.Count + "，" + H(Localization.T("Dict.ViewCount")) + "：" + views.Count);
            sb.AppendLine("</p>");
            sb.AppendLine("<p class=\"print-hint\">" + H(Localization.T("Dict.PrintHint")) + "</p>");

            // ── 目錄 ──
            if (tables.Count > 0)
            {
                sb.AppendLine("<h2>" + H(Localization.T("Dict.Tables")) + "</h2><div class=\"toc\">");
                foreach (string t in tables) sb.AppendLine("<a href=\"#t-" + H(Anchor(t)) + "\">" + H(t) + "</a>");
                sb.AppendLine("</div>");
            }
            if (views.Count > 0)
            {
                sb.AppendLine("<div class=\"toc\" style=\"margin-top:0\">");
                foreach (string v in views) sb.AppendLine("<a href=\"#v-" + H(Anchor(v)) + "\">" + H(v) + "（" + H(Localization.T("Dict.View")) + "）</a>");
                sb.AppendLine("</div>");
            }

            // ── 各資料表 ──
            foreach (string table in tables)
            {
                sb.AppendLine("<h2 id=\"t-" + H(Anchor(table)) + "\">" + H(table) + "</h2>");
                try
                {
                    DataTable columns = db.GetColumns(databaseName, table);
                    if (columns != null && columns.Rows.Count > 0)
                    {
                        sb.AppendLine("<h3>" + H(Localization.T("Dict.Columns")) + "</h3>");
                        RenderDataTable(sb, columns);
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine("<p class=\"err\">" + H(Localization.T("Dict.Columns")) + "：" + H(ex.Message) + "</p>");
                }

                try
                {
                    DataTable indexes = db.GetIndexes(databaseName, table);
                    if (indexes != null && indexes.Rows.Count > 0)
                    {
                        sb.AppendLine("<h3>" + H(Localization.T("Dict.Indexes")) + "</h3>");
                        RenderDataTable(sb, indexes);
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine("<p class=\"err\">" + H(Localization.T("Dict.Indexes")) + "：" + H(ex.Message) + "</p>");
                }

                try
                {
                    string ddl = db.GetTableCreateStatement(databaseName, table);
                    if (!string.IsNullOrWhiteSpace(ddl))
                    {
                        sb.AppendLine("<details><summary>" + H(Localization.T("Dict.CreateStatement")) + "</summary><pre>" + H(ddl) + "</pre></details>");
                    }
                }
                catch { }
            }

            // ── 各檢視 ──
            foreach (string view in views)
            {
                sb.AppendLine("<h2 id=\"v-" + H(Anchor(view)) + "\">" + H(view) + "（" + H(Localization.T("Dict.View")) + "）</h2>");
                try
                {
                    DataTable columns = db.GetColumns(databaseName, view);
                    if (columns != null && columns.Rows.Count > 0)
                    {
                        sb.AppendLine("<h3>" + H(Localization.T("Dict.Columns")) + "</h3>");
                        RenderDataTable(sb, columns);
                    }
                }
                catch { }
                try
                {
                    string ddl = db.GetViewCreateStatement(databaseName, view);
                    if (!string.IsNullOrWhiteSpace(ddl))
                    {
                        sb.AppendLine("<details open><summary>" + H(Localization.T("Dict.CreateStatement")) + "</summary><pre>" + H(ddl) + "</pre></details>");
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine("<p class=\"err\">" + H(ex.Message) + "</p>");
                }
            }

            sb.AppendLine("</div></body></html>");
            return sb.ToString();
        }

        /// <summary>把 provider 回傳的 metadata DataTable 原樣轉成 HTML 表格（各引擎欄位名不同，不硬套格式）。</summary>
        private static void RenderDataTable(StringBuilder sb, DataTable dt)
        {
            sb.AppendLine("<table><tr>");
            foreach (DataColumn col in dt.Columns) sb.Append("<th>" + H(col.ColumnName) + "</th>");
            sb.AppendLine("</tr>");
            foreach (DataRow row in dt.Rows)
            {
                sb.Append("<tr>");
                foreach (DataColumn col in dt.Columns)
                {
                    object value = row[col];
                    sb.Append("<td>" + H(value == null || value == DBNull.Value ? "" : value.ToString()) + "</td>");
                }
                sb.AppendLine("</tr>");
            }
            sb.AppendLine("</table>");
        }

        private static List<string> SafeList(Func<List<string>> getter)
        {
            try { return getter() ?? new List<string>(); }
            catch { return new List<string>(); }
        }

        private static string Anchor(string name)
        {
            return Uri.EscapeDataString(name ?? "");
        }

        private static string H(string text)
        {
            return WebUtility.HtmlEncode(text ?? "");
        }
    }
}
