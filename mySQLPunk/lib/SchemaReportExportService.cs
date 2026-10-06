using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Xml;
using PdfSharp.Pdf.IO;

namespace mySQLPunk.lib
{
    /// <summary>Each operation exclusively owns its factory connection; no active UI connection is used.</summary>
    public static class SchemaReportExportService
    {
        public static List<string> ListTables(Func<IDatabase> databaseFactory, string databaseName, CancellationToken token)
        {
            if (databaseFactory == null) throw new ArgumentNullException(nameof(databaseFactory));
            token.ThrowIfCancellationRequested();
            using (var db = databaseFactory())
            {
                if (db == null) throw new InvalidOperationException("Database factory returned null.");
                token.ThrowIfCancellationRequested();
                var tables = db.GetTables(databaseName);
                token.ThrowIfCancellationRequested();
                if (tables == null) throw new InvalidOperationException(ReportText.Get("無法讀取資料表清單。", "Could not read the table list."));
                // MySQL on Linux can have two tables differing only by case. Names are never trimmed.
                return tables.Where(name => !string.IsNullOrEmpty(name)).Distinct(StringComparer.Ordinal)
                    .OrderBy(name => name, StringComparer.Ordinal).ToList();
            }
        }

        public static List<SchemaReportTableInfo> ListTableDetails(Func<IDatabase> databaseFactory, string databaseName, CancellationToken token)
        {
            if (databaseFactory == null) throw new ArgumentNullException(nameof(databaseFactory));
            token.ThrowIfCancellationRequested();
            using (var db = databaseFactory())
            {
                if (db == null) throw new InvalidOperationException("Database factory returned null.");
                token.ThrowIfCancellationRequested();
                var tables = db.GetTables(databaseName);
                token.ThrowIfCancellationRequested();
                if (tables == null) throw new InvalidOperationException(ReportText.Get("無法讀取資料表清單。", "Could not read the table list."));
                var comments = ReadTableComments(db, databaseName, token);
                var result = new List<SchemaReportTableInfo>();
                foreach (string name in tables.Where(name => !string.IsNullOrEmpty(name)).Distinct(StringComparer.Ordinal)
                    .OrderBy(name => name, StringComparer.Ordinal))
                {
                    token.ThrowIfCancellationRequested();
                    string comment;
                    result.Add(new SchemaReportTableInfo { Name = name, Comment = comments.TryGetValue(name, out comment) ? comment : "" });
                }
                return result;
            }
        }

        public static void Run(Func<IDatabase> databaseFactory, ReportExportRequest request, CancellationToken token, IProgress<ReportExportProgress> progress)
        {
            if (databaseFactory == null) throw new ArgumentNullException(nameof(databaseFactory));
            if (request == null) throw new ArgumentNullException(nameof(request));
            var template = ReportTemplateCatalog.Find(request.TemplateId);
            if (string.IsNullOrWhiteSpace(request.DatabaseName) || request.Tables == null || request.Tables.Count == 0)
                throw new ArgumentException(ReportText.Get("請至少選擇一張資料表。", "Select at least one table."));
            if (request.Tables.Any(string.IsNullOrEmpty) || request.Tables.Distinct(StringComparer.Ordinal).Count() != request.Tables.Count)
                throw new ArgumentException(ReportText.Get("資料表選取清單無效。", "Invalid table selection."));
            if ((request.Title ?? "").Length > 200 || (request.Author ?? "").Length > 200)
                throw new ArgumentException(ReportText.Get("標題與作者不可超過 200 字。", "Title and author are limited to 200 characters."));
            string destination = Path.GetFullPath(request.OutputPath ?? "");
            if (!string.Equals(Path.GetExtension(destination), template.Extension, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(ReportText.Get("輸出副檔名必須符合所選格式。", "Output extension must match the selected format."));
            if (!Directory.Exists(Path.GetDirectoryName(destination))) throw new DirectoryNotFoundException(Path.GetDirectoryName(destination));
            if (File.Exists(destination) && !request.Overwrite) throw new IOException(ReportText.Get("目標檔案已存在，請確認覆寫。", "Destination exists; confirm overwrite."));
            token.ThrowIfCancellationRequested();
            Report(progress, "collect", ReportText.Get("正在開啟專用連線…", "Opening a dedicated connection…"), 0, 0, true);
            var document = new SchemaReportDocument
            {
                DatabaseName = request.DatabaseName, EngineName = request.EngineName,
                Title = string.IsNullOrWhiteSpace(request.Title) ? request.DatabaseName + ReportText.Get(" 資料庫結構報告", " database schema report") : request.Title.Trim(),
                Author = (request.Author ?? "").Trim()
            };
            using (var db = databaseFactory())
            {
                if (db == null) throw new InvalidOperationException("Database factory returned null.");
                token.ThrowIfCancellationRequested();
                var available = db.GetTables(request.DatabaseName);
                token.ThrowIfCancellationRequested();
                if (available == null || request.Tables.Any(name => !available.Contains(name, StringComparer.Ordinal)))
                    throw new InvalidOperationException(ReportText.Get("選取的資料表已變更，請重新開啟精靈。", "Selected tables changed. Reopen the wizard."));
                var comments = ReadTableComments(db, request.DatabaseName, token);
                foreach (string tableName in request.Tables)
                {
                    Report(progress, "collect", tableName, document.Tables.Count, request.Tables.Count, false);
                    token.ThrowIfCancellationRequested();
                    DataTable columns = db.GetColumns(request.DatabaseName, tableName);
                    token.ThrowIfCancellationRequested();
                    RequireMetadata(columns, tableName, true);
                    DataTable indexes = ReadIndexes(db, request.DatabaseName, tableName, token);
                    token.ThrowIfCancellationRequested();
                    RequireMetadata(indexes, tableName, false);
                    string comment;
                    var table = new SchemaReportTable { Name = tableName, Comment = comments.TryGetValue(tableName, out comment) ? comment : "", Indexes = indexes.Copy() };
                    foreach (DataRow row in columns.Rows)
                    {
                        token.ThrowIfCancellationRequested();
                        var column = NormalizeColumn(row);
                        if (string.IsNullOrEmpty(column.Name)) throw new InvalidOperationException(ReportText.Get("欄位資訊缺少名稱：", "Column name missing: ") + tableName);
                        ApplyIndexKey(column, indexes);
                        table.Columns.Add(column);
                    }
                    // Current six templates focus on fields; do not fetch unused DDL or any table data.
                    document.Tables.Add(table);
                    Report(progress, "collect", tableName, document.Tables.Count, request.Tables.Count, false);
                }
            }
            WriteDocument(document, template, destination, request.Overwrite, token, progress);
        }

        public static void WriteDocument(SchemaReportDocument document, ReportTemplate template, string destination, bool overwrite, CancellationToken token, IProgress<ReportExportProgress> progress)
        {
            token.ThrowIfCancellationRequested();
            string path = Path.GetFullPath(destination);
            string temporary = Path.Combine(Path.GetDirectoryName(path), ".mysqlpunk-report-" + Guid.NewGuid().ToString("N") + template.Extension);
            try
            {
                Report(progress, "render", ReportText.Get("正在編排文件…", "Formatting document…"), 0, 0, true);
                Action<int, int> onTable = (done, total) => Report(progress, "render", ReportText.Get("正在編排資料表", "Formatting tables"), done, total, false);
                if (template.Format == ReportExportFormat.Pdf) SchemaReportPdfWriter.Write(temporary, document, template, token, onTable);
                else SchemaReportOpenXmlWriter.Write(temporary, document, template, token, onTable);
                token.ThrowIfCancellationRequested();
                Report(progress, "validate", ReportText.Get("正在驗證輸出檔…", "Validating output…"), 0, 0, true);
                ValidateOutput(temporary, template.Format, token);
                token.ThrowIfCancellationRequested();
                // Publish is the commit point. Cancellation after it must be reported as successful completion.
                if (overwrite && File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private static void ValidateOutput(string path, ReportExportFormat format, CancellationToken token)
        {
            if (new FileInfo(path).Length == 0) throw new InvalidDataException("Empty report.");
            if (format == ReportExportFormat.Pdf)
            {
                using (var pdf = PdfReader.Open(path, PdfDocumentOpenMode.Import))
                    if (pdf.PageCount == 0) throw new InvalidDataException("PDF contains no pages.");
                return;
            }
            using (var zip = ZipFile.OpenRead(path))
            {
                string required = format == ReportExportFormat.Docx ? "word/document.xml" : "xl/workbook.xml";
                if (zip.GetEntry("[Content_Types].xml") == null || zip.GetEntry(required) == null)
                    throw new InvalidDataException("Report package is incomplete.");
                foreach (var entry in zip.Entries.Where(item => item.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || item.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)))
                {
                    token.ThrowIfCancellationRequested();
                    using (var input = entry.Open())
                    using (var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                        while (reader.Read()) { token.ThrowIfCancellationRequested(); }
                }
            }
        }

        private static Dictionary<string, string> ReadTableComments(IDatabase db, string databaseName, CancellationToken token)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            token.ThrowIfCancellationRequested();
            DataTable rows;
            string provider = ConnectionConfigurationService.NormalizeProvider(db.ProviderName);
            if (provider == "sqlite")
            {
                // GetTableStatus counts every table's data rows on SQLite; use only comment metadata.
                DataTable exists = db.SelectSQL("SELECT name FROM sqlite_master WHERE type='table' AND name='__mysqlpunk_table_comments';");
                RequireMetadata(exists, "table comments", false);
                token.ThrowIfCancellationRequested();
                if (exists.Rows.Count == 0) return result;
                rows = db.SelectSQL("SELECT table_name AS Name, comment AS Comment FROM __mysqlpunk_table_comments;");
            }
            else if (provider == "mysql")
                rows = db.SelectSQL("SELECT TABLE_NAME AS Name, TABLE_COMMENT AS Comment FROM information_schema.TABLES WHERE TABLE_SCHEMA = ?database AND TABLE_TYPE = 'BASE TABLE';", new Dictionary<string, object> { { "database", databaseName } });
            else if (provider == "mssql")
            {
                string catalog = "[" + databaseName.Replace("]", "]]") + "]";
                rows = db.SelectSQL("SELECT CASE WHEN s.name = 'dbo' THEN t.name ELSE s.name + '.' + t.name END AS Name, COALESCE(CAST(ep.value AS NVARCHAR(4000)), '') AS Comment FROM " + catalog + ".sys.tables t JOIN " + catalog + ".sys.schemas s ON s.schema_id=t.schema_id LEFT JOIN " + catalog + ".sys.extended_properties ep ON ep.major_id=t.object_id AND ep.minor_id=0 AND ep.name='MS_Description';");
            }
            else if (provider == "postgresql")
                rows = db.SelectSQL("SELECT CASE WHEN n.nspname='public' THEN c.relname ELSE n.nspname || '.' || c.relname END AS \"Name\", COALESCE(obj_description(c.oid),'') AS \"Comment\" FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE c.relkind IN ('r','p') AND n.nspname NOT IN ('pg_catalog','information_schema');");
            else if (provider == "oracle")
                rows = db.SelectSQL("SELECT TABLE_NAME AS NAME, COMMENTS AS COMMENTS FROM ALL_TAB_COMMENTS WHERE OWNER = :owner", new Dictionary<string, object> { { "owner", databaseName.ToUpperInvariant() } });
            else if (provider == "snowflake")
                rows = db.SelectSQL("SELECT TABLE_SCHEMA || '.' || TABLE_NAME AS \"Name\", COMMENT AS \"Comment\" FROM \"" + databaseName.Replace("\"", "\"\"") + "\".INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE='BASE TABLE';");
            else throw new NotSupportedException(ReportText.Get("此資料庫尚不支援結構報表。", "Schema reports are not supported for this provider."));
            token.ThrowIfCancellationRequested();
            RequireMetadata(rows, "table comments", false);
            foreach (DataRow row in rows.Rows)
            {
                token.ThrowIfCancellationRequested();
                string name = Read(row, "Name", "TABLE_NAME", "table_name");
                if (!string.IsNullOrEmpty(name)) result[name] = Read(row, "Comment", "COMMENTS", "comment");
            }
            return result;
        }

        private static DataTable ReadIndexes(IDatabase db, string databaseName, string tableName, CancellationToken token)
        {
            if (ConnectionConfigurationService.NormalizeProvider(db.ProviderName) != "sqlite") return db.GetIndexes(databaseName, tableName);
            // Existing SQLite UI metadata appends DESC to a name. Read exact column names for reports.
            DataTable list = db.SelectSQL("PRAGMA index_list('" + tableName.Replace("'", "''") + "');");
            token.ThrowIfCancellationRequested(); RequireMetadata(list, tableName, false);
            var result = new DataTable();
            foreach (string name in new[] { "Key_name", "Column_name", "Non_unique", "Collation" }) result.Columns.Add(name);
            foreach (DataRow index in list.Rows)
            {
                string name = Read(index, "name");
                token.ThrowIfCancellationRequested();
                DataTable columns = db.SelectSQL("PRAGMA index_xinfo('" + name.Replace("'", "''") + "');");
                token.ThrowIfCancellationRequested(); RequireMetadata(columns, name, false);
                foreach (DataRow column in columns.Rows)
                    if (Read(column, "key") != "0" && !string.IsNullOrEmpty(Read(column, "name")))
                        result.Rows.Add(Read(index, "origin") == "pk" ? "PRIMARY" : name, Read(column, "name"), Read(index, "unique") == "1" ? "0" : "1", Read(column, "desc") == "1" ? "D" : "A");
            }
            return result;
        }

        private static void RequireMetadata(DataTable table, string name, bool requireRows)
        {
            if (table == null || table.ExtendedProperties.Contains("QueryError") || (requireRows && table.Rows.Count == 0))
                throw new InvalidOperationException(ReportText.Get("無法完整讀取結構：", "Could not read complete schema: ") + name);
        }

        private static SchemaReportColumn NormalizeColumn(DataRow row)
        {
            string key = Read(row, "Key", "COLUMN_KEY");
            int sqlitePk;
            if (int.TryParse(Read(row, "pk"), out sqlitePk) && sqlitePk > 0) key = "PRI";
            string nullable = Read(row, "Null", "IS_NULLABLE", "NULLABLE");
            if (row.Table.Columns.Contains("notnull")) nullable = Read(row, "notnull") == "1" ? "NO" : "YES";
            if (nullable == "Y") nullable = "YES";
            if (nullable == "N") nullable = "NO";
            var extras = new List<string>();
            foreach (string field in new[] { "Extra", "GenerationExpression", "IsIdentity", "IdentityGeneration", "IsGenerated", "IDENTITY_COLUMN", "VIRTUAL_COLUMN" })
            {
                string value = Read(row, field);
                if (value.Length > 0 && value != "NO" && value != "NEVER") extras.Add(field == "Extra" ? value : field + ": " + value);
            }
            object defaultValue = Value(row, "Default", "COLUMN_DEFAULT", "dflt_value", "DATA_DEFAULT");
            return new SchemaReportColumn
            {
                Name = Read(row, "Field", "COLUMN_NAME", "name"), Type = ReadType(row), Nullable = nullable,
                Key = key, DefaultValue = defaultValue == null || defaultValue == DBNull.Value ? "—" : Convert.ToString(defaultValue, CultureInfo.InvariantCulture),
                Comment = Read(row, "Comment", "COMMENTS", "Description"), Extra = string.Join("; ", extras)
            };
        }

        private static string ReadType(DataRow row)
        {
            string type = Read(row, "ProviderType", "Type", "DATA_TYPE");
            if (type.Contains("(")) return type;
            string lower = type.ToLowerInvariant();
            string length = Read(row, "CHARACTER_MAXIMUM_LENGTH", "CHAR_LENGTH");
            string precision = Read(row, "NUMERIC_PRECISION", "DATA_PRECISION");
            string scale = Read(row, "NUMERIC_SCALE", "DATA_SCALE");
            if ((lower.Contains("char") || lower == "binary" || lower == "varbinary") && length.Length > 0)
                return type + "(" + (length == "-1" ? "max" : length) + ")";
            if ((lower == "decimal" || lower == "numeric" || lower == "number") && precision.Length > 0)
                return type + "(" + precision + (scale.Length > 0 ? "," + scale : "") + ")";
            return type;
        }

        private static void ApplyIndexKey(SchemaReportColumn column, DataTable indexes)
        {
            foreach (DataRow row in indexes.Rows)
            {
                if (!string.Equals(Read(row, "Column_name", "COLUMN_NAME", "ColumnName"), column.Name, StringComparison.Ordinal)) continue;
                string name = Read(row, "Key_name", "INDEX_NAME", "IndexName");
                if (string.Equals(name, "PRIMARY", StringComparison.OrdinalIgnoreCase)) { column.Key = "PRI"; return; }
                if (column.Key == "PRI") continue;
                if (Read(row, "Non_unique") == "0") column.Key = "UNI";
                else if (string.IsNullOrEmpty(column.Key)) column.Key = "MUL";
            }
        }

        private static object Value(DataRow row, params string[] names)
        {
            foreach (string name in names)
            {
                var column = row.Table.Columns.Cast<DataColumn>().FirstOrDefault(item => string.Equals(item.ColumnName, name, StringComparison.OrdinalIgnoreCase));
                if (column != null) return row[column];
            }
            return null;
        }
        private static string Read(DataRow row, params string[] names)
        {
            object value = Value(row, names);
            return value == null || value == DBNull.Value ? "" : Convert.ToString(value, CultureInfo.InvariantCulture);
        }
        private static void Report(IProgress<ReportExportProgress> progress, string stage, string message, int completed, int total, bool indeterminate)
        {
            progress?.Report(new ReportExportProgress { Stage = stage, Message = message, Completed = completed, Total = total, IsIndeterminate = indeterminate });
        }
    }
}
