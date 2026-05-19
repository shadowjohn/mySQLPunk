using System;
using System.Collections.Generic;
using System.Data;
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
        Markdown
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
            }

            switch (filterIndex)
            {
                case 1: return QueryResultExportFormat.Xlsx;
                case 2: return QueryResultExportFormat.Csv;
                case 3: return QueryResultExportFormat.Tsv;
                case 4: return QueryResultExportFormat.Json;
                case 5: return QueryResultExportFormat.Xml;
                case 6: return QueryResultExportFormat.Html;
                case 7: return QueryResultExportFormat.Markdown;
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
            sb.AppendLine("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");

            int rowIndex = 1;
            sb.Append("<row r=\"").Append(rowIndex).Append("\">");
            for (int c = 0; c < dt.Columns.Count; c++)
            {
                AppendInlineStringCell(sb, rowIndex, c + 1, dt.Columns[c].ColumnName);
            }
            sb.AppendLine("</row>");

            foreach (DataRow row in dt.Rows)
            {
                if (row.RowState == DataRowState.Deleted) continue;
                rowIndex++;
                sb.Append("<row r=\"").Append(rowIndex).Append("\">");
                for (int c = 0; c < dt.Columns.Count; c++)
                {
                    AppendInlineStringCell(sb, rowIndex, c + 1, FormatExportValue(row[c]));
                }
                sb.AppendLine("</row>");
            }

            sb.AppendLine("</sheetData></worksheet>");
            return sb.ToString();
        }

        private static void AppendInlineStringCell(StringBuilder sb, int rowIndex, int columnIndex, string value)
        {
            sb.Append("<c r=\"").Append(GetExcelColumnName(columnIndex)).Append(rowIndex).Append("\" t=\"inlineStr\"><is><t");
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
                   "<fonts count=\"1\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
                   "<fills count=\"1\"><fill><patternFill patternType=\"none\"/></fill></fills>" +
                   "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
                   "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
                   "<cellXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/></cellXfs>" +
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
    }
}
