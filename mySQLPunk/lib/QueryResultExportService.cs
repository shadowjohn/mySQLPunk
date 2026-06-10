using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.IO.Compression;
using System.Text;
using Newtonsoft.Json;

namespace mySQLPunk.lib
{
    public enum QueryResultExportFormat
    {
        Xlsx,
        Csv,
        Tsv,
        Json,
        Xml,
        Html,
        Markdown,
        Sql
    }

    public sealed class QueryResultStreamingExportResult
    {
        public long Rows { get; set; }
        public long BytesWritten { get; set; }
        public QueryResultExportFormat Format { get; set; }
    }

    public sealed class QueryResultExportSummary
    {
        public string Path { get; set; }
        public string FileName { get; set; }
        public string DirectoryPath { get; set; }
        public long Rows { get; set; }
        public long BytesWritten { get; set; }
        public QueryResultExportFormat Format { get; set; }
        public string FormatName { get; set; }

        public string BuildDetailText()
        {
            return Localization.T("Query.ExportSummaryFormat") + " " + FormatName + Environment.NewLine +
                Localization.T("Query.ExportSummaryRows") + " " + Rows.ToString("N0") + Environment.NewLine +
                Localization.T("Query.ExportSummarySize") + " " + FormatByteCount(BytesWritten) + Environment.NewLine +
                Localization.T("Query.ExportSummaryFile") + " " + FileName + Environment.NewLine +
                Localization.T("Query.ExportSummaryPath") + " " + Path;
        }

        public static string FormatByteCount(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            double value = bytes / 1024d;
            if (value < 1024) return value.ToString("0.##") + " KB";
            value /= 1024d;
            if (value < 1024) return value.ToString("0.##") + " MB";
            value /= 1024d;
            return value.ToString("0.##") + " GB";
        }
    }

    public static class QueryResultExportService
    {
        public static QueryResultExportFormat ResolveFormat(string path, int filterIndex)
        {
            string ext = Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
            switch (ext)
            {
                case ".xlsx": return QueryResultExportFormat.Xlsx;
                case ".csv": return QueryResultExportFormat.Csv;
                case ".tsv":
                case ".tab": return QueryResultExportFormat.Tsv;
                case ".json": return QueryResultExportFormat.Json;
                case ".xml": return QueryResultExportFormat.Xml;
                case ".html":
                case ".htm": return QueryResultExportFormat.Html;
                case ".md":
                case ".markdown": return QueryResultExportFormat.Markdown;
                case ".sql": return QueryResultExportFormat.Sql;
            }

            switch (filterIndex)
            {
                case 1: return QueryResultExportFormat.Csv;
                case 2: return QueryResultExportFormat.Xlsx;
                case 3: return QueryResultExportFormat.Tsv;
                case 4: return QueryResultExportFormat.Json;
                case 5: return QueryResultExportFormat.Xml;
                case 6: return QueryResultExportFormat.Html;
                case 7: return QueryResultExportFormat.Markdown;
                case 8: return QueryResultExportFormat.Sql;
                default: return QueryResultExportFormat.Csv;
            }
        }

        public static void Write(DataTable dt, string path, QueryResultExportFormat format)
        {
            if (format == QueryResultExportFormat.Xlsx)
            {
                WriteXlsx(dt, path);
                return;
            }

            File.WriteAllText(path, BuildText(dt, format), Encoding.UTF8);
        }

        public static QueryResultExportSummary BuildSummary(string path, QueryResultExportFormat format, long rows, long? bytesWritten = null)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException(Localization.T("Common.TargetPathRequired"), nameof(path));

            FileInfo file = new FileInfo(path);
            long bytes = bytesWritten.HasValue ? bytesWritten.Value : (file.Exists ? file.Length : 0);
            return new QueryResultExportSummary
            {
                Path = path,
                FileName = System.IO.Path.GetFileName(path),
                DirectoryPath = System.IO.Path.GetDirectoryName(path),
                Rows = rows,
                BytesWritten = bytes,
                Format = format,
                FormatName = GetFormatName(format)
            };
        }

        public static string GetFormatName(QueryResultExportFormat format)
        {
            switch (format)
            {
                case QueryResultExportFormat.Xlsx: return "Excel";
                case QueryResultExportFormat.Csv: return "CSV";
                case QueryResultExportFormat.Tsv: return "TSV";
                case QueryResultExportFormat.Json: return "JSON";
                case QueryResultExportFormat.Xml: return "XML";
                case QueryResultExportFormat.Html: return "HTML";
                case QueryResultExportFormat.Markdown: return "Markdown";
                case QueryResultExportFormat.Sql: return "SQL";
                default: return format.ToString();
            }
        }

        public static bool CanStreamFormat(QueryResultExportFormat format)
        {
            return format == QueryResultExportFormat.Csv ||
                format == QueryResultExportFormat.Tsv ||
                format == QueryResultExportFormat.Json ||
                format == QueryResultExportFormat.Xml ||
                format == QueryResultExportFormat.Html ||
                format == QueryResultExportFormat.Markdown ||
                format == QueryResultExportFormat.Sql;
        }

        public static QueryResultStreamingExportResult WriteStreaming(
            IDatabase database,
            string sql,
            IDictionary<string, object> parameters,
            string path,
            QueryResultExportFormat format,
            Action<long> progress = null)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (string.IsNullOrWhiteSpace(sql)) throw new ArgumentException(Localization.T("Common.SqlRequired"), nameof(sql));
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException(Localization.T("Common.TargetPathRequired"), nameof(path));
            if (!CanStreamFormat(format)) throw new NotSupportedException(Localization.T("Query.StreamingUnsupportedFormat"));

            DbConnection connection = BinaryCellStreamingService.GetOpenConnection(database);
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = sql;
                BinaryCellStreamingService.AddParameters(command, database, parameters);

                using (DbDataReader reader = command.ExecuteReader(CommandBehavior.SequentialAccess))
                using (CountingFileStream stream = new CountingFileStream(path))
                using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    long rows;
                    switch (format)
                    {
                        case QueryResultExportFormat.Json:
                            rows = WriteStreamingJson(reader, writer, progress);
                            break;
                        case QueryResultExportFormat.Xml:
                            rows = WriteStreamingXml(reader, writer, progress);
                            break;
                        case QueryResultExportFormat.Html:
                            rows = WriteStreamingHtml(reader, writer, progress);
                            break;
                        case QueryResultExportFormat.Markdown:
                            rows = WriteStreamingMarkdown(reader, writer, progress);
                            break;
                        case QueryResultExportFormat.Sql:
                            rows = WriteStreamingSql(reader, writer, progress);
                            break;
                        default:
                            rows = WriteStreamingDelimited(reader, writer, format == QueryResultExportFormat.Tsv ? '\t' : ',', progress);
                            break;
                    }
                    writer.Flush();
                    return new QueryResultStreamingExportResult
                    {
                        Rows = rows,
                        BytesWritten = stream.BytesWritten,
                        Format = format
                    };
                }
            }
        }

        public static string BuildText(DataTable dt, QueryResultExportFormat format)
        {
            switch (format)
            {
                case QueryResultExportFormat.Tsv:
                    return BuildDelimited(dt, '\t');
                case QueryResultExportFormat.Json:
                    return BuildJson(dt);
                case QueryResultExportFormat.Xml:
                    return BuildXml(dt);
                case QueryResultExportFormat.Html:
                    return BuildHtml(dt);
                case QueryResultExportFormat.Markdown:
                    return BuildMarkdown(dt);
                case QueryResultExportFormat.Sql:
                    return BuildSql(dt);
                default:
                    int exportedRows;
                    return BuildCsv(dt, out exportedRows);
            }
        }

        public static int CountExportRows(DataTable dt)
        {
            if (dt == null) return 0;
            int count = 0;
            foreach (DataRow row in dt.Rows)
            {
                if (row.RowState != DataRowState.Deleted) count++;
            }
            return count;
        }

        public static string BuildCsv(DataTable dt, out int exportedRows)
        {
            if (dt == null) throw new ArgumentNullException(nameof(dt));

            exportedRows = 0;
            StringBuilder sb = new StringBuilder();

            for (int c = 0; c < dt.Columns.Count; c++)
            {
                if (c > 0) sb.Append(',');
                sb.Append(CsvEscape(dt.Columns[c].ColumnName));
            }
            sb.AppendLine();

            foreach (DataRow row in dt.Rows)
            {
                if (row.RowState == DataRowState.Deleted) continue;

                for (int c = 0; c < dt.Columns.Count; c++)
                {
                    if (c > 0) sb.Append(',');
                    sb.Append(CsvEscape(FormatExportValue(row[c])));
                }
                sb.AppendLine();
                exportedRows++;
            }

            return sb.ToString();
        }

        private static string BuildDelimited(DataTable dt, char delimiter)
        {
            if (dt == null) throw new ArgumentNullException(nameof(dt));

            StringBuilder sb = new StringBuilder();
            for (int c = 0; c < dt.Columns.Count; c++)
            {
                if (c > 0) sb.Append(delimiter);
                sb.Append(DelimitedEscape(dt.Columns[c].ColumnName, delimiter));
            }
            sb.AppendLine();

            foreach (DataRow row in dt.Rows)
            {
                if (row.RowState == DataRowState.Deleted) continue;
                for (int c = 0; c < dt.Columns.Count; c++)
                {
                    if (c > 0) sb.Append(delimiter);
                    sb.Append(DelimitedEscape(FormatExportValue(row[c]), delimiter));
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }

        private static string BuildJson(DataTable dt)
        {
            if (dt == null) throw new ArgumentNullException(nameof(dt));

            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
            foreach (DataRow row in dt.Rows)
            {
                if (row.RowState == DataRowState.Deleted) continue;

                Dictionary<string, object> item = new Dictionary<string, object>();
                foreach (DataColumn column in dt.Columns)
                {
                    object value = row[column];
                    item[column.ColumnName] = value == DBNull.Value ? null : ConvertExportValue(value);
                }
                rows.Add(item);
            }

            return JsonConvert.SerializeObject(rows, Formatting.Indented);
        }

        private static string BuildXml(DataTable dt)
        {
            if (dt == null) throw new ArgumentNullException(nameof(dt));

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            sb.AppendLine("<results>");

            foreach (DataRow row in dt.Rows)
            {
                if (row.RowState == DataRowState.Deleted) continue;
                sb.AppendLine("  <row>");
                foreach (DataColumn column in dt.Columns)
                {
                    object value = row[column];
                    string columnName = XmlEscape(column.ColumnName);
                    if (value == DBNull.Value || value == null)
                    {
                        sb.Append("    <field name=\"").Append(columnName).AppendLine("\" isNull=\"true\" />");
                    }
                    else
                    {
                        sb.Append("    <field name=\"").Append(columnName).Append("\">")
                          .Append(XmlEscape(FormatExportValue(value)))
                          .AppendLine("</field>");
                    }
                }
                sb.AppendLine("  </row>");
            }

            sb.AppendLine("</results>");
            return sb.ToString();
        }

        private static string BuildHtml(DataTable dt)
        {
            if (dt == null) throw new ArgumentNullException(nameof(dt));

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("<!doctype html>");
            sb.AppendLine("<html><head><meta charset=\"utf-8\"><title>mySQLPunk export</title>");
            sb.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif}table{border-collapse:collapse}th,td{border:1px solid #ccc;padding:4px 8px;white-space:pre-wrap}th{background:#f2f2f2}</style>");
            sb.AppendLine("</head><body><table>");
            sb.AppendLine("<thead><tr>");
            foreach (DataColumn column in dt.Columns)
            {
                sb.Append("<th>").Append(HtmlEscape(column.ColumnName)).AppendLine("</th>");
            }
            sb.AppendLine("</tr></thead><tbody>");

            foreach (DataRow row in dt.Rows)
            {
                if (row.RowState == DataRowState.Deleted) continue;
                sb.AppendLine("<tr>");
                foreach (DataColumn column in dt.Columns)
                {
                    sb.Append("<td>").Append(HtmlEscape(FormatExportValue(row[column]))).AppendLine("</td>");
                }
                sb.AppendLine("</tr>");
            }

            sb.AppendLine("</tbody></table></body></html>");
            return sb.ToString();
        }

        private static string BuildMarkdown(DataTable dt)
        {
            if (dt == null) throw new ArgumentNullException(nameof(dt));

            StringBuilder sb = new StringBuilder();
            sb.Append("| ");
            for (int c = 0; c < dt.Columns.Count; c++)
            {
                if (c > 0) sb.Append(" | ");
                sb.Append(MarkdownEscape(dt.Columns[c].ColumnName));
            }
            sb.AppendLine(" |");

            sb.Append("| ");
            for (int c = 0; c < dt.Columns.Count; c++)
            {
                if (c > 0) sb.Append(" | ");
                sb.Append("---");
            }
            sb.AppendLine(" |");

            foreach (DataRow row in dt.Rows)
            {
                if (row.RowState == DataRowState.Deleted) continue;
                sb.Append("| ");
                for (int c = 0; c < dt.Columns.Count; c++)
                {
                    if (c > 0) sb.Append(" | ");
                    sb.Append(MarkdownEscape(FormatExportValue(row[c])));
                }
                sb.AppendLine(" |");
            }

            return sb.ToString();
        }

        private static string BuildSql(DataTable dt)
        {
            if (dt == null) throw new ArgumentNullException(nameof(dt));

            StringBuilder sb = new StringBuilder();
            using (StringWriter writer = new StringWriter(sb))
            {
                writer.WriteLine("-- mySQLPunk query result export");
                foreach (DataRow row in dt.Rows)
                {
                    if (row.RowState == DataRowState.Deleted) continue;
                    AppendSqlInsert(writer, GetDataTableColumnNames(dt), index => row[index]);
                }
            }
            return sb.ToString();
        }

        private static long WriteStreamingDelimited(DbDataReader reader, TextWriter writer, char delimiter, Action<long> progress)
        {
            string[] columnNames = GetReaderColumnNames(reader);
            for (int c = 0; c < columnNames.Length; c++)
            {
                if (c > 0) writer.Write(delimiter);
                writer.Write(DelimitedEscape(columnNames[c], delimiter));
            }
            writer.WriteLine();

            long rows = 0;
            while (reader.Read())
            {
                for (int c = 0; c < reader.FieldCount; c++)
                {
                    if (c > 0) writer.Write(delimiter);
                    writer.Write(DelimitedEscape(FormatExportValue(ReadStreamingValue(reader, c)), delimiter));
                }
                writer.WriteLine();
                rows++;
                if (progress != null) progress(rows);
            }

            return rows;
        }

        private static long WriteStreamingJson(DbDataReader reader, TextWriter writer, Action<long> progress)
        {
            string[] columnNames = GetReaderColumnNames(reader);
            long rows = 0;
            writer.Write("[");

            while (reader.Read())
            {
                if (rows > 0) writer.Write(",");
                writer.WriteLine();

                Dictionary<string, object> item = new Dictionary<string, object>();
                for (int c = 0; c < reader.FieldCount; c++)
                {
                    object value = ReadStreamingValue(reader, c);
                    item[columnNames[c]] = value == DBNull.Value ? null : ConvertExportValue(value);
                }

                writer.Write(JsonConvert.SerializeObject(item, Formatting.None));
                rows++;
                if (progress != null) progress(rows);
            }

            if (rows > 0) writer.WriteLine();
            writer.Write("]");
            return rows;
        }

        private static long WriteStreamingXml(DbDataReader reader, TextWriter writer, Action<long> progress)
        {
            string[] columnNames = GetReaderColumnNames(reader);
            long rows = 0;

            writer.WriteLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            writer.WriteLine("<results>");
            while (reader.Read())
            {
                writer.WriteLine("  <row>");
                for (int c = 0; c < reader.FieldCount; c++)
                {
                    object value = ReadStreamingValue(reader, c);
                    string columnName = XmlEscape(columnNames[c]);
                    if (value == DBNull.Value || value == null)
                    {
                        writer.Write("    <field name=\"");
                        writer.Write(columnName);
                        writer.WriteLine("\" isNull=\"true\" />");
                    }
                    else
                    {
                        writer.Write("    <field name=\"");
                        writer.Write(columnName);
                        writer.Write("\">");
                        writer.Write(XmlEscape(FormatExportValue(value)));
                        writer.WriteLine("</field>");
                    }
                }
                writer.WriteLine("  </row>");
                rows++;
                if (progress != null) progress(rows);
            }
            writer.WriteLine("</results>");

            return rows;
        }

        private static long WriteStreamingHtml(DbDataReader reader, TextWriter writer, Action<long> progress)
        {
            string[] columnNames = GetReaderColumnNames(reader);
            long rows = 0;

            writer.WriteLine("<!doctype html>");
            writer.WriteLine("<html><head><meta charset=\"utf-8\"><title>mySQLPunk export</title>");
            writer.WriteLine("<style>body{font-family:Segoe UI,Arial,sans-serif}table{border-collapse:collapse}th,td{border:1px solid #ccc;padding:4px 8px;white-space:pre-wrap}th{background:#f2f2f2}</style>");
            writer.WriteLine("</head><body><table>");
            writer.WriteLine("<thead><tr>");
            for (int c = 0; c < columnNames.Length; c++)
            {
                writer.Write("<th>");
                writer.Write(HtmlEscape(columnNames[c]));
                writer.WriteLine("</th>");
            }
            writer.WriteLine("</tr></thead><tbody>");

            while (reader.Read())
            {
                writer.WriteLine("<tr>");
                for (int c = 0; c < reader.FieldCount; c++)
                {
                    writer.Write("<td>");
                    writer.Write(HtmlEscape(FormatExportValue(ReadStreamingValue(reader, c))));
                    writer.WriteLine("</td>");
                }
                writer.WriteLine("</tr>");
                rows++;
                if (progress != null) progress(rows);
            }

            writer.WriteLine("</tbody></table></body></html>");
            return rows;
        }

        private static long WriteStreamingMarkdown(DbDataReader reader, TextWriter writer, Action<long> progress)
        {
            string[] columnNames = GetReaderColumnNames(reader);
            long rows = 0;

            writer.Write("| ");
            for (int c = 0; c < columnNames.Length; c++)
            {
                if (c > 0) writer.Write(" | ");
                writer.Write(MarkdownEscape(columnNames[c]));
            }
            writer.WriteLine(" |");

            writer.Write("| ");
            for (int c = 0; c < columnNames.Length; c++)
            {
                if (c > 0) writer.Write(" | ");
                writer.Write("---");
            }
            writer.WriteLine(" |");

            while (reader.Read())
            {
                writer.Write("| ");
                for (int c = 0; c < reader.FieldCount; c++)
                {
                    if (c > 0) writer.Write(" | ");
                    writer.Write(MarkdownEscape(FormatExportValue(ReadStreamingValue(reader, c))));
                }
                writer.WriteLine(" |");
                rows++;
                if (progress != null) progress(rows);
            }

            return rows;
        }

        private static long WriteStreamingSql(DbDataReader reader, TextWriter writer, Action<long> progress)
        {
            string[] columnNames = GetReaderColumnNames(reader);
            long rows = 0;
            writer.WriteLine("-- mySQLPunk query result export");

            while (reader.Read())
            {
                AppendSqlInsert(writer, columnNames, index => ReadStreamingValue(reader, index));
                rows++;
                if (progress != null) progress(rows);
            }

            return rows;
        }

        private static string[] GetDataTableColumnNames(DataTable dt)
        {
            string[] names = new string[dt.Columns.Count];
            for (int i = 0; i < dt.Columns.Count; i++)
            {
                names[i] = string.IsNullOrWhiteSpace(dt.Columns[i].ColumnName)
                    ? "Column" + (i + 1).ToString()
                    : dt.Columns[i].ColumnName;
            }
            return names;
        }

        private static void AppendSqlInsert(TextWriter writer, string[] columnNames, Func<int, object> valueProvider)
        {
            writer.Write("INSERT INTO ");
            writer.Write(SqlIdentifier("query_result"));
            writer.Write(" (");
            for (int c = 0; c < columnNames.Length; c++)
            {
                if (c > 0) writer.Write(", ");
                writer.Write(SqlIdentifier(columnNames[c]));
            }
            writer.Write(") VALUES (");
            for (int c = 0; c < columnNames.Length; c++)
            {
                if (c > 0) writer.Write(", ");
                writer.Write(SqlLiteral(valueProvider(c)));
            }
            writer.WriteLine(");");
        }

        private static string SqlIdentifier(string value)
        {
            value = string.IsNullOrWhiteSpace(value) ? "Column" : value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private static string SqlLiteral(object value)
        {
            if (value == null || value == DBNull.Value) return "NULL";

            byte[] bytes = value as byte[];
            if (bytes != null) return "X'" + ToHex(bytes) + "'";

            if (value is bool) return (bool)value ? "1" : "0";
            if (value is DateTime) return "'" + ((DateTime)value).ToString("yyyy-MM-dd HH:mm:ss").Replace("'", "''") + "'";
            if (value is DateTimeOffset) return "'" + ((DateTimeOffset)value).ToString("yyyy-MM-dd HH:mm:ss zzz").Replace("'", "''") + "'";
            if (value is byte || value is sbyte || value is short || value is ushort ||
                value is int || value is uint || value is long || value is ulong ||
                value is float || value is double || value is decimal)
            {
                return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
            }

            return "'" + value.ToString().Replace("'", "''") + "'";
        }

        private static string ToHex(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return string.Empty;
            StringBuilder sb = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
            {
                sb.Append(bytes[i].ToString("X2"));
            }
            return sb.ToString();
        }

        private static string[] GetReaderColumnNames(DbDataReader reader)
        {
            string[] names = new string[reader.FieldCount];
            Dictionary<string, int> seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < reader.FieldCount; i++)
            {
                string name = reader.GetName(i);
                if (string.IsNullOrWhiteSpace(name)) name = "Column" + (i + 1).ToString();
                int count;
                if (seen.TryGetValue(name, out count))
                {
                    count++;
                    seen[name] = count;
                    name = name + "_" + count.ToString();
                }
                else
                {
                    seen[name] = 1;
                }
                names[i] = name;
            }
            return names;
        }

        private static object ReadStreamingValue(DbDataReader reader, int ordinal)
        {
            if (reader.IsDBNull(ordinal)) return DBNull.Value;
            return reader.GetValue(ordinal);
        }

        private static void WriteXlsx(DataTable dt, string path)
        {
            if (dt == null) throw new ArgumentNullException(nameof(dt));
            if (File.Exists(path)) File.Delete(path);

            using (ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                AddZipEntry(archive, "[Content_Types].xml", BuildXlsxContentTypes());
                AddZipEntry(archive, "_rels/.rels", BuildXlsxRootRelationships());
                AddZipEntry(archive, "xl/workbook.xml", BuildXlsxWorkbook());
                AddZipEntry(archive, "xl/_rels/workbook.xml.rels", BuildXlsxWorkbookRelationships());
                AddZipEntry(archive, "xl/worksheets/sheet1.xml", BuildXlsxSheet(dt));
                AddZipEntry(archive, "xl/styles.xml", BuildXlsxStyles());
            }
        }

        private static void AddZipEntry(ZipArchive archive, string path, string content)
        {
            ZipArchiveEntry entry = archive.CreateEntry(path, CompressionLevel.Optimal);
            using (Stream stream = entry.Open())
            using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(content);
            }
        }

        private static string BuildXlsxSheet(DataTable dt)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            sb.Append("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
            sb.Append(BuildXlsxColumnDefinitions(dt));
            sb.AppendLine("<sheetData>");

            int rowIndex = 1;
            sb.Append("<row r=\"").Append(rowIndex).Append("\">");
            for (int c = 0; c < dt.Columns.Count; c++)
            {
                AppendInlineStringCell(sb, rowIndex, c + 1, dt.Columns[c].ColumnName, 1);
            }
            sb.AppendLine("</row>");

            foreach (DataRow row in dt.Rows)
            {
                if (row.RowState == DataRowState.Deleted) continue;
                rowIndex++;
                sb.Append("<row r=\"").Append(rowIndex).Append("\">");
                for (int c = 0; c < dt.Columns.Count; c++)
                {
                    AppendInlineStringCell(sb, rowIndex, c + 1, FormatExportValue(row[c]), 0);
                }
                sb.AppendLine("</row>");
            }

            sb.AppendLine("</sheetData>");
            string autoFilter = BuildXlsxAutoFilterReference(dt, rowIndex);
            if (!string.IsNullOrWhiteSpace(autoFilter))
            {
                sb.Append("<autoFilter ref=\"").Append(autoFilter).AppendLine("\"/>");
            }
            sb.AppendLine("</worksheet>");
            return sb.ToString();
        }

        private static string BuildXlsxColumnDefinitions(DataTable dt)
        {
            if (dt == null || dt.Columns.Count == 0) return string.Empty;

            StringBuilder sb = new StringBuilder();
            sb.Append("<cols>");
            for (int c = 0; c < dt.Columns.Count; c++)
            {
                double width = Math.Min(60, Math.Max(10, (dt.Columns[c].ColumnName ?? string.Empty).Length + 2));
                sb.Append("<col min=\"").Append(c + 1).Append("\" max=\"").Append(c + 1)
                    .Append("\" width=\"").Append(width.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Append("\" customWidth=\"1\"/>");
            }
            sb.Append("</cols>");
            return sb.ToString();
        }

        private static string BuildXlsxAutoFilterReference(DataTable dt, int lastRowIndex)
        {
            if (dt == null || dt.Columns.Count == 0 || lastRowIndex <= 0) return string.Empty;
            return "A1:" + GetExcelColumnName(dt.Columns.Count) + Math.Max(1, lastRowIndex).ToString();
        }

        private static void AppendInlineStringCell(StringBuilder sb, int rowIndex, int columnIndex, string value, int styleIndex)
        {
            sb.Append("<c r=\"").Append(GetExcelColumnName(columnIndex)).Append(rowIndex).Append("\" t=\"inlineStr\"");
            if (styleIndex > 0)
            {
                sb.Append(" s=\"").Append(styleIndex).Append("\"");
            }
            sb.Append("><is><t");
            if (!string.IsNullOrEmpty(value) && (value.StartsWith(" ") || value.EndsWith(" ") || value.Contains("\n") || value.Contains("\r") || value.Contains("\t")))
            {
                sb.Append(" xml:space=\"preserve\"");
            }
            sb.Append(">").Append(XmlEscape(value)).Append("</t></is></c>");
        }

        private static string BuildXlsxContentTypes()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                   "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                   "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                   "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                   "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                   "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                   "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
                   "</Types>";
        }

        private static string BuildXlsxRootRelationships()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                   "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                   "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                   "</Relationships>";
        }

        private static string BuildXlsxWorkbook()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                   "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                   "<sheets><sheet name=\"Results\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>";
        }

        private static string BuildXlsxWorkbookRelationships()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                   "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                   "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
                   "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
                   "</Relationships>";
        }

        private static string BuildXlsxStyles()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                   "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                   "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/><color rgb=\"FFFFFFFF\"/></font></fonts>" +
                   "<fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF2563EB\"/><bgColor indexed=\"64\"/></patternFill></fill></fills>" +
                   "<borders count=\"2\"><border><left/><right/><top/><bottom/><diagonal/></border><border><left/><right/><top/><bottom style=\"thin\"><color rgb=\"FF94A3B8\"/></bottom><diagonal/></border></borders>" +
                   "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
                   "<cellXfs count=\"2\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/><xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\"/></cellXfs>" +
                   "</styleSheet>";
        }

        private static object ConvertExportValue(object value)
        {
            if (value == null || value == DBNull.Value) return null;
            if (value is byte[]) return FormatExportValue(value);
            if (value is DateTime) return ((DateTime)value).ToString("o");
            return value;
        }

        private static string FormatExportValue(object value)
        {
            if (value == null || value == DBNull.Value) return string.Empty;
            byte[] bytes = value as byte[];
            if (bytes != null) return FormatBinaryCellValue(bytes);
            if (value is DateTime) return ((DateTime)value).ToString("yyyy-MM-dd HH:mm:ss");
            return value.ToString();
        }

        private static string FormatBinaryCellValue(byte[] bytes)
        {
            if (bytes == null) return "";
            string wkt;
            if (GeometryWktConverter.TryGeometryBytesToWkt(bytes, out wkt))
            {
                return "[Geometry] " + wkt;
            }

            int previewLength = Math.Min(bytes.Length, 12);
            StringBuilder preview = new StringBuilder(previewLength * 2);
            for (int i = 0; i < previewLength; i++)
            {
                preview.Append(bytes[i].ToString("X2"));
            }
            if (bytes.Length > previewLength) preview.Append("...");

            return "[BLOB " + bytes.Length + " bytes] 0x" + preview;
        }

        private static string CsvEscape(string value)
        {
            value = value ?? string.Empty;
            if (value.Contains(",") || value.Contains("\"") || value.Contains("\r") || value.Contains("\n"))
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }

        private static string DelimitedEscape(string value, char delimiter)
        {
            value = value ?? string.Empty;
            if (value.Contains(delimiter.ToString()) || value.Contains("\"") || value.Contains("\r") || value.Contains("\n"))
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }

        private static string HtmlEscape(string value)
        {
            return System.Web.HttpUtility.HtmlEncode(value ?? string.Empty);
        }

        private static string XmlEscape(string value)
        {
            return System.Security.SecurityElement.Escape(value ?? string.Empty) ?? string.Empty;
        }

        private static string MarkdownEscape(string value)
        {
            value = (value ?? string.Empty).Replace("\r\n", "<br>").Replace("\n", "<br>").Replace("\r", "<br>");
            return value.Replace("\\", "\\\\").Replace("|", "\\|");
        }

        private static string GetExcelColumnName(int columnNumber)
        {
            string columnName = string.Empty;
            while (columnNumber > 0)
            {
                int modulo = (columnNumber - 1) % 26;
                columnName = Convert.ToChar('A' + modulo) + columnName;
                columnNumber = (columnNumber - modulo) / 26;
            }
            return columnName;
        }

        private sealed class CountingFileStream : FileStream
        {
            public long BytesWritten { get; private set; }

            public CountingFileStream(string path)
                : base(path, FileMode.Create, FileAccess.Write, FileShare.None)
            {
            }

            public override void Write(byte[] array, int offset, int count)
            {
                base.Write(array, offset, count);
                BytesWritten += count;
            }
        }
    }
}
