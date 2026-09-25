using System.Globalization;
using System.Net;
using System.Text;
using MySqlPunk.Core.Models;
using MySqlPunk.Core.Providers;

namespace MySqlPunk.Core.Services;

public sealed record DataDictionaryProgress(int Completed, int Total, DatabaseObjectInfo Current);

public sealed record DataDictionarySummary(int Tables, int Views, int Failed, long Bytes, string Path);

/// <summary>
/// Builds a self-contained HTML data dictionary (tables, views, columns, indexes, foreign keys, definitions)
/// from catalog metadata only. Every value from the server is HTML-encoded, so definitions or comments that
/// contain markup can never execute in the reader's browser.
/// </summary>
public static class DataDictionaryService
{
    public const long MaximumHtmlBytes = 64L * 1024 * 1024;

    public static async Task<IReadOnlyList<DataDictionaryEntry>> CollectAsync(
        IDatabaseSession session,
        string database,
        IReadOnlyList<DatabaseObjectInfo> objects,
        IProgress<DataDictionaryProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(objects);
        var entries = new List<DataDictionaryEntry>(objects.Count);
        for (var index = 0; index < objects.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = objects[index];
            progress?.Report(new DataDictionaryProgress(index, objects.Count, item));
            try
            {
                var structure = await session.GetTableStructureAsync(database, item, cancellationToken).ConfigureAwait(false);
                entries.Add(new DataDictionaryEntry(item, structure, null));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // A single object without privileges must not sink the whole dictionary; the reason is documented inline.
                entries.Add(new DataDictionaryEntry(item, null, exception.Message));
            }
        }

        return entries;
    }

    public static string BuildHtml(
        ConnectionProfile profile,
        string database,
        IReadOnlyList<DataDictionaryEntry> entries,
        string generatorVersion,
        DateTimeOffset? generatedAt = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(entries);
        var tables = entries.Where(entry => entry.Object.Kind == DatabaseObjectKind.Table).ToList();
        var views = entries.Where(entry => entry.Object.Kind == DatabaseObjectKind.View).ToList();
        var timestamp = (generatedAt ?? DateTimeOffset.Now).ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
        var title = $"資料字典：{database}";

        var html = new StringBuilder();
        html.AppendLine("<!doctype html>");
        html.AppendLine("<html lang=\"zh-Hant\"><head><meta charset=\"utf-8\">");
        html.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        html.AppendLine("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">");
        html.Append("<title>").Append(H(title)).AppendLine("</title>");
        html.AppendLine("<style>");
        html.AppendLine("""
            body { font-family: 'Segoe UI', 'Noto Sans TC', 'PingFang TC', sans-serif; margin: 0; color: #24292f; background: #fff; }
            .page { max-width: 1040px; margin: 0 auto; padding: 32px 40px 64px; }
            h1 { font-size: 26px; border-bottom: 3px solid #2563eb; padding-bottom: 10px; }
            h2 { font-size: 20px; color: #2563eb; margin-top: 40px; border-bottom: 1px solid #d0d7de; padding-bottom: 6px; }
            h3 { font-size: 15px; margin: 22px 0 6px; }
            table { border-collapse: collapse; width: 100%; font-size: 13px; margin: 6px 0 4px; }
            th, td { border: 1px solid #d0d7de; padding: 5px 9px; text-align: left; vertical-align: top; word-break: break-word; }
            th { background: #f6f8fa; font-weight: 600; white-space: nowrap; }
            tr:nth-child(even) td { background: #fbfcfd; }
            .meta { color: #57606a; font-size: 13px; line-height: 1.8; }
            .toc { columns: 2; font-size: 14px; margin: 12px 0; }
            .toc a { color: #2563eb; text-decoration: none; display: block; padding: 1px 0; }
            .badge { display: inline-block; font-size: 11px; padding: 1px 6px; border-radius: 4px; background: #eef4ff; color: #1d4ed8; margin-left: 6px; vertical-align: middle; }
            details { margin: 6px 0 14px; }
            summary { cursor: pointer; color: #57606a; font-size: 13px; }
            pre { background: #f6f8fa; border: 1px solid #d0d7de; border-radius: 6px; padding: 10px 12px; font-size: 12px; overflow-x: auto; white-space: pre-wrap; }
            .err { color: #cb2f2f; font-size: 13px; }
            .print-hint { background: #eef4ff; border: 1px solid #b6ccf5; border-radius: 6px; padding: 8px 12px; font-size: 13px; }
            @media print { .print-hint { display: none; } h2 { page-break-before: always; } h2:first-of-type { page-break-before: avoid; } }
            """);
        html.AppendLine("</style></head><body><div class=\"page\">");

        html.Append("<h1>").Append(H(title)).AppendLine("</h1>");
        html.AppendLine("<p class=\"meta\">");
        html.Append("資料庫類型：").Append(H(profile.ProviderDisplayName)).AppendLine("<br>");
        if (profile.Provider != DatabaseProviderKind.Sqlite)
        {
            html.Append("伺服器：").Append(H($"{profile.Host}:{profile.Port}")).AppendLine("<br>");
        }

        html.Append("連線名稱：").Append(H(profile.Name)).AppendLine("<br>");
        html.Append("產生時間：").Append(H(timestamp)).AppendLine("<br>");
        html.Append("產生工具：mySQLPunk ").Append(H(generatorVersion)).AppendLine("（Linux／macOS 預覽版）<br>");
        html.Append("資料表：").Append(tables.Count).Append("，檢視表：").Append(views.Count);
        var failed = entries.Count(entry => entry.Error is not null);
        if (failed > 0)
        {
            html.Append("，無法讀取：").Append(failed);
        }

        html.AppendLine("</p>");
        html.AppendLine("<p class=\"print-hint\">本文件只包含結構 metadata，不含資料列；可用瀏覽器「列印 → 另存 PDF」保存。</p>");

        if (tables.Count > 0)
        {
            html.AppendLine("<h2>資料表</h2><div class=\"toc\">");
            foreach (var entry in tables)
            {
                html.Append("<a href=\"#").Append(entry.Anchor).Append("\">").Append(H(entry.Object.DisplayName)).AppendLine("</a>");
            }

            html.AppendLine("</div>");
        }

        if (views.Count > 0)
        {
            html.AppendLine("<h2>檢視表</h2><div class=\"toc\">");
            foreach (var entry in views)
            {
                html.Append("<a href=\"#").Append(entry.Anchor).Append("\">").Append(H(entry.Object.DisplayName)).AppendLine("</a>");
            }

            html.AppendLine("</div>");
        }

        foreach (var entry in tables.Concat(views))
        {
            RenderEntry(html, entry);
        }

        html.AppendLine("</div></body></html>");
        if (Encoding.UTF8.GetByteCount(html.ToString()) > MaximumHtmlBytes)
        {
            throw new InvalidOperationException($"資料字典超過 {MaximumHtmlBytes / (1024 * 1024)} MiB 安全上限；請縮小範圍後再匯出。");
        }

        return html.ToString();
    }

    public static async Task<DataDictionarySummary> WriteFileAsync(
        string html,
        string path,
        int tables,
        int views,
        int failed,
        CancellationToken cancellationToken = default)
    {
        var (bytes, targetPath) = await HtmlReportFile.WriteAsync(html, path, cancellationToken).ConfigureAwait(false);
        return new DataDictionarySummary(tables, views, failed, bytes, targetPath);
    }

    private static void RenderEntry(StringBuilder html, DataDictionaryEntry entry)
    {
        var isView = entry.Object.Kind == DatabaseObjectKind.View;
        html.Append("<h2 id=\"").Append(entry.Anchor).Append("\">").Append(H(entry.Object.DisplayName));
        html.Append("<span class=\"badge\">").Append(isView ? "檢視表" : "資料表").AppendLine("</span></h2>");
        if (entry.Structure is null)
        {
            html.Append("<p class=\"err\">無法讀取結構：").Append(H(entry.Error ?? string.Empty)).AppendLine("</p>");
            return;
        }

        var structure = entry.Structure;
        if (!string.IsNullOrWhiteSpace(structure.Comment))
        {
            html.Append("<p class=\"meta\">註解：").Append(H(structure.Comment)).AppendLine("</p>");
        }

        html.AppendLine("<h3>欄位</h3>");
        if (structure.Columns.Count == 0)
        {
            html.AppendLine("<p class=\"meta\">（沒有欄位資訊）</p>");
        }
        else
        {
            html.AppendLine("<table><tr><th>#</th><th>名稱</th><th>型別</th><th>NULL</th><th>PK</th><th>預設值</th><th>額外</th><th>定序</th><th>註解</th></tr>");
            foreach (var column in structure.Columns)
            {
                html.Append("<tr><td>").Append(column.Ordinal + 1).Append("</td>");
                html.Append("<td>").Append(H(column.Name)).Append("</td>");
                html.Append("<td>").Append(H(column.DataType)).Append("</td>");
                html.Append("<td>").Append(column.IsNullable ? "是" : "否").Append("</td>");
                html.Append("<td>").Append(column.IsPrimaryKey ? "✓" : string.Empty).Append("</td>");
                html.Append("<td>").Append(H(column.DefaultValue)).Append("</td>");
                html.Append("<td>").Append(H(column.Extra)).Append("</td>");
                html.Append("<td>").Append(H(column.Collation)).Append("</td>");
                html.Append("<td>").Append(H(column.Comment)).AppendLine("</td></tr>");
            }

            html.AppendLine("</table>");
        }

        if (structure.Indexes.Count > 0)
        {
            html.AppendLine("<h3>索引</h3>");
            html.AppendLine("<table><tr><th>名稱</th><th>唯一</th><th>PK</th><th>類型</th><th>欄位</th><th>定義</th></tr>");
            foreach (var index in structure.Indexes)
            {
                html.Append("<tr><td>").Append(H(index.Name)).Append("</td>");
                html.Append("<td>").Append(index.IsUnique ? "✓" : string.Empty).Append("</td>");
                html.Append("<td>").Append(index.IsPrimaryKey ? "✓" : string.Empty).Append("</td>");
                html.Append("<td>").Append(H(index.IndexType)).Append("</td>");
                html.Append("<td>").Append(H(string.Join(", ", index.Columns))).Append("</td>");
                html.Append("<td>").Append(H(index.Definition)).AppendLine("</td></tr>");
            }

            html.AppendLine("</table>");
        }

        if (structure.ForeignKeys.Count > 0)
        {
            html.AppendLine("<h3>外鍵</h3>");
            html.AppendLine("<table><tr><th>名稱</th><th>欄位</th><th>參照</th><th>ON UPDATE</th><th>ON DELETE</th></tr>");
            foreach (var foreignKey in structure.ForeignKeys)
            {
                html.Append("<tr><td>").Append(H(foreignKey.Name)).Append("</td>");
                html.Append("<td>").Append(H(string.Join(", ", foreignKey.Columns))).Append("</td>");
                html.Append("<td>").Append(H($"{foreignKey.ReferencedTable} ({string.Join(", ", foreignKey.ReferencedColumns)})")).Append("</td>");
                html.Append("<td>").Append(H(foreignKey.OnUpdate)).Append("</td>");
                html.Append("<td>").Append(H(foreignKey.OnDelete)).AppendLine("</td></tr>");
            }

            html.AppendLine("</table>");
        }

        if (!string.IsNullOrWhiteSpace(structure.Definition))
        {
            html.Append(isView ? "<details open><summary>" : "<details><summary>")
                .Append(isView ? "檢視表定義" : "建立語法")
                .Append("</summary><pre>")
                .Append(H(structure.Definition))
                .AppendLine("</pre></details>");
        }
    }

    private static string H(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);
}

public sealed record DataDictionaryEntry(DatabaseObjectInfo Object, TableStructureInfo? Structure, string? Error)
{
    /// <summary>Anchor derived from a hash of the qualified name, so arbitrary identifiers never leak into attributes.</summary>
    public string Anchor
    {
        get
        {
            var qualified = $"{(Object.Kind == DatabaseObjectKind.View ? "v" : "t")}:{Object.Schema}.{Object.Name}";
            var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(qualified));
            return "o-" + Convert.ToHexString(hash.AsSpan(0, 10)).ToLowerInvariant();
        }
    }
}
