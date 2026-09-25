using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace mySQLPunk.lib
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum DataDictionaryTemplate
    {
        /// <summary>每張表：欄位、索引與 CREATE 語句。</summary>
        Full,

        /// <summary>每張表只列常用欄位資訊（名稱、型別、NULL、鍵、預設值、註解）。</summary>
        Compact,

        /// <summary>所有資料表的欄位集中在一張總表，方便複製到試算表。</summary>
        ColumnsOnly
    }

    /// <summary>資料字典的範本與個人化設定；也會存進自動執行作業。</summary>
    public sealed class DataDictionaryOptions
    {
        public DataDictionaryTemplate Template { get; set; }
        /// <summary>文件標題；空白時使用「資料字典：資料庫名稱」。</summary>
        public string Title { get; set; }
        public string Author { get; set; }
        /// <summary>主色，#RRGGBB。</summary>
        public string AccentColor { get; set; } = "#2563eb";
        public bool IncludeViews { get; set; } = true;
        public bool IncludeIndexes { get; set; } = true;
        public bool IncludeDdl { get; set; } = true;
        public bool IncludeToc { get; set; } = true;
        /// <summary>只輸出名稱符合的資料表／檢視；逗號分隔，可用 * 與 ? 萬用字元，空白代表全部。</summary>
        public string TableFilter { get; set; }

        public void Validate()
        {
            if (!Enum.IsDefined(typeof(DataDictionaryTemplate), Template)) throw new InvalidOperationException(Localization.T("Dict.Error.Template"));
            AccentColor = string.IsNullOrWhiteSpace(AccentColor) ? "#2563eb" : AccentColor.Trim();
            if (!Regex.IsMatch(AccentColor, "^#[0-9A-Fa-f]{6}$")) throw new InvalidOperationException(Localization.T("Dict.Error.Color"));
            if ((Title ?? string.Empty).Length > 200 || (Author ?? string.Empty).Length > 200) throw new InvalidOperationException(Localization.T("Dict.Error.TextTooLong"));
        }

        public bool Matches(string name)
        {
            string[] patterns = (TableFilter ?? string.Empty).Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim()).Where(item => item.Length > 0).ToArray();
            if (patterns.Length == 0) return true;
            return patterns.Any(pattern => Regex.IsMatch(name ?? string.Empty,
                "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        }
    }

    /// <summary>
    /// 資料字典：把一個資料庫的資料表／檢視結構整理成一份可讀的 HTML 文件
    /// （欄位、索引、CREATE 語句、註解），用瀏覽器開啟後可直接列印或另存 PDF。
    /// 全部走 IDatabase 的通用 metadata API，五種引擎共用同一份程式。
    /// </summary>
    public static class DataDictionaryService
    {
        public static string BuildHtml(IDatabase db, string databaseName, string engineName, string hostName, string appVersion)
        {
            return BuildHtml(db, databaseName, engineName, hostName, appVersion, new DataDictionaryOptions());
        }

        public static string BuildHtml(IDatabase db, string databaseName, string engineName, string hostName, string appVersion, DataDictionaryOptions options)
        {
            options = options ?? new DataDictionaryOptions();
            options.Validate();
            List<string> tables = SafeList(() => db.GetTables(databaseName)).Where(options.Matches).ToList();
            List<string> views = options.IncludeViews ? SafeList(() => db.GetViews(databaseName)).Where(options.Matches).ToList() : new List<string>();
            bool compact = options.Template == DataDictionaryTemplate.Compact;
            string documentTitle = string.IsNullOrWhiteSpace(options.Title) ? Localization.T("Dict.Title") + "：" + databaseName : options.Title.Trim();

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("<!doctype html>");
            sb.AppendLine("<html lang=\"zh-Hant\"><head><meta charset=\"utf-8\">");
            sb.AppendLine("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">");
            sb.AppendLine("<title>" + H(documentTitle) + "</title>");
            sb.AppendLine("<style>");
            sb.AppendLine(":root { --accent: " + options.AccentColor + "; }");
            sb.AppendLine(@"
body { font-family: 'Segoe UI', 'Microsoft JhengHei', sans-serif; margin: 0; color: #24292f; }
.page { max-width: 960px; margin: 0 auto; padding: 32px 40px 64px; }
h1 { font-size: 26px; border-bottom: 3px solid var(--accent); padding-bottom: 10px; }
h2 { font-size: 20px; color: var(--accent); margin-top: 40px; border-bottom: 1px solid #d0d7de; padding-bottom: 6px; }
h3 { font-size: 16px; margin: 26px 0 6px; }
table { border-collapse: collapse; width: 100%; font-size: 13px; margin: 8px 0 4px; }
th, td { border: 1px solid #d0d7de; padding: 5px 9px; text-align: left; vertical-align: top; word-break: break-word; }
th { background: #f6f8fa; font-weight: 600; white-space: nowrap; }
tr:nth-child(even) td { background: #fbfcfd; }
.meta { color: #57606a; font-size: 13px; line-height: 1.8; }
.toc { columns: 2; font-size: 14px; margin: 12px 0; }
.toc a { color: var(--accent); text-decoration: none; display: block; padding: 1px 0; }
details { margin: 6px 0 14px; }
summary { cursor: pointer; color: #57606a; font-size: 13px; }
pre { background: #f6f8fa; border: 1px solid #d0d7de; border-radius: 6px; padding: 10px 12px; font-size: 12px; overflow-x: auto; white-space: pre-wrap; }
.err { color: #cb2f2f; font-size: 13px; }
.print-hint { background: #eef4ff; border: 1px solid #b6ccf5; border-radius: 6px; padding: 8px 12px; font-size: 13px; }
@media print { .print-hint { display: none; } h2 { page-break-before: always; } h2:first-of-type { page-break-before: avoid; } }
");
            sb.AppendLine("</style></head><body><div class=\"page\">");

            // ── 封面資訊 ──
            sb.AppendLine("<h1>" + H(documentTitle) + "</h1>");
            sb.AppendLine("<p class=\"meta\">");
            if (!string.IsNullOrWhiteSpace(options.Author)) sb.AppendLine(H(Localization.T("Dict.Author")) + "：" + H(options.Author.Trim()) + "<br>");
            sb.AppendLine(H(Localization.T("Dict.Database")) + "：" + H(databaseName) + "<br>");
            sb.AppendLine(H(Localization.T("Dict.Engine")) + "：" + H(engineName) + "<br>");
            if (!string.IsNullOrWhiteSpace(hostName)) sb.AppendLine(H(Localization.T("Dict.Server")) + "：" + H(hostName) + "<br>");
            sb.AppendLine(H(Localization.T("Dict.GeneratedAt")) + "：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "<br>");
            sb.AppendLine(H(Localization.T("Dict.GeneratedBy")) + "：mySQLPunk " + H(appVersion) + "<br>");
            sb.AppendLine(H(Localization.T("Dict.TableCount")) + "：" + tables.Count + "，" + H(Localization.T("Dict.ViewCount")) + "：" + views.Count);
            sb.AppendLine("</p>");
            sb.AppendLine("<p class=\"print-hint\">" + H(Localization.T("Dict.PrintHint")) + "</p>");

            if (options.Template == DataDictionaryTemplate.ColumnsOnly)
            {
                RenderColumnsOnly(sb, db, databaseName, tables, views);
                sb.AppendLine("</div></body></html>");
                return sb.ToString();
            }

            // ── 目錄 ──
            if (options.IncludeToc && tables.Count > 0)
            {
                sb.AppendLine("<h2>" + H(Localization.T("Dict.Tables")) + "</h2><div class=\"toc\">");
                foreach (string t in tables) sb.AppendLine("<a href=\"#t-" + H(Anchor(t)) + "\">" + H(t) + "</a>");
                sb.AppendLine("</div>");
            }
            if (options.IncludeToc && views.Count > 0)
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
                        if (compact) RenderCompactColumns(sb, columns);
                        else RenderDataTable(sb, columns);
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine("<p class=\"err\">" + H(Localization.T("Dict.Columns")) + "：" + H(ex.Message) + "</p>");
                }

                if (options.IncludeIndexes)
                {
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
                }

                if (options.IncludeDdl && !compact)
                {
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
                        if (compact) RenderCompactColumns(sb, columns);
                        else RenderDataTable(sb, columns);
                    }
                }
                catch { }
                if (options.IncludeDdl && !compact)
                {
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
            }

            sb.AppendLine("</div></body></html>");
            return sb.ToString();
        }

        private static readonly string[][] CommonColumnFields =
        {
            new[] { "Field", "COLUMN_NAME", "column_name", "name", "Name" },
            new[] { "Type", "ProviderType", "DATA_TYPE", "data_type", "type" },
            new[] { "Null", "IS_NULLABLE", "is_nullable", "NULLABLE", "notnull" },
            new[] { "Key", "COLUMN_KEY", "pk" },
            new[] { "Default", "COLUMN_DEFAULT", "column_default", "dflt_value", "DATA_DEFAULT" },
            new[] { "Comment", "COMMENTS", "comment", "Description" }
        };

        private static readonly string[] CommonColumnHeaders = { "Dict.Col.Name", "Dict.Col.Type", "Dict.Col.Null", "Dict.Col.Key", "Dict.Col.Default", "Dict.Col.Comment" };

        /// <summary>精簡範本：只取各 provider 共通的欄位資訊，依固定順序排列。</summary>
        private static void RenderCompactColumns(StringBuilder sb, DataTable dt)
        {
            sb.Append("<table><tr>");
            foreach (string header in CommonColumnHeaders) sb.Append("<th>" + H(Localization.T(header)) + "</th>");
            sb.AppendLine("</tr>");
            foreach (DataRow row in dt.Rows)
            {
                sb.Append("<tr>");
                foreach (string[] names in CommonColumnFields) sb.Append("<td>" + H(Read(row, names)) + "</td>");
                sb.AppendLine("</tr>");
            }
            sb.AppendLine("</table>");
        }

        /// <summary>欄位總表範本：所有資料表（與檢視）的欄位集中成一張表。</summary>
        private static void RenderColumnsOnly(StringBuilder sb, IDatabase db, string databaseName, List<string> tables, List<string> views)
        {
            sb.Append("<table><tr><th>" + H(Localization.T("Dict.Col.Object")) + "</th>");
            foreach (string header in CommonColumnHeaders) sb.Append("<th>" + H(Localization.T(header)) + "</th>");
            sb.AppendLine("</tr>");
            foreach (string name in tables.Concat(views))
            {
                DataTable columns;
                try { columns = db.GetColumns(databaseName, name); }
                catch (Exception ex)
                {
                    sb.AppendLine("<tr><td>" + H(name) + "</td><td class=\"err\" colspan=\"6\">" + H(ex.Message) + "</td></tr>");
                    continue;
                }
                if (columns == null) continue;
                string label = views.Contains(name) ? name + "（" + Localization.T("Dict.View") + "）" : name;
                foreach (DataRow row in columns.Rows)
                {
                    sb.Append("<tr><td>" + H(label) + "</td>");
                    foreach (string[] names in CommonColumnFields) sb.Append("<td>" + H(Read(row, names)) + "</td>");
                    sb.AppendLine("</tr>");
                }
            }
            sb.AppendLine("</table>");
        }

        private static string Read(DataRow row, string[] names)
        {
            foreach (string name in names)
            {
                if (!row.Table.Columns.Contains(name) || row[name] == DBNull.Value) continue;
                string value = Convert.ToString(row[name], System.Globalization.CultureInfo.InvariantCulture);
                // SQLite 的 notnull／pk 是數字旗標，轉成與其他 provider 一致的 YES／NO、PRI。
                if (name == "notnull") return value == "1" ? "NO" : "YES";
                if (name == "pk") return value != "0" ? "PRI" : string.Empty;
                return value;
            }
            return string.Empty;
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
