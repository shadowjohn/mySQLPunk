using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace mySQLPunk.lib.SqliteExport
{
    public sealed class SqliteSqlExportService
    {
        private readonly IDatabase db;
        private readonly string databaseName;

        private static readonly HashSet<string> KnownSpatialMetadataTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "spatial_ref_sys", "spatial_ref_sys_aux", "spatialite_history",
            "geometry_columns", "geometry_columns_auth", "geometry_columns_field_infos", "geometry_columns_statistics",
            "views_geometry_columns", "views_geometry_columns_auth", "views_geometry_columns_field_infos", "views_geometry_columns_statistics",
            "virts_geometry_columns", "virts_geometry_columns_auth", "virts_geometry_columns_field_infos", "virts_geometry_columns_statistics",
            "data_licenses", "sql_statements_log", "SpatialIndex",
            "gpkg_spatial_ref_sys", "gpkg_contents", "gpkg_geometry_columns",
            "gpkg_tile_matrix", "gpkg_tile_matrix_set", "gpkg_extensions",
            "gpkg_metadata", "gpkg_metadata_reference", "gpkg_data_columns",
            "gpkg_data_column_constraints", "gpkg_ogr_contents"
        };

        private static readonly string[] ShadowTableSuffixes = new string[]
        {
            "data", "idx", "content", "docsize", "config",   // FTS5
            "segments", "segdir", "stat",                    // FTS3/4
            "node", "rowid", "parent"                        // R-Tree
        };

        private static readonly char[] HexLookup = "0123456789ABCDEF".ToCharArray();

        public SqliteSqlExportService(IDatabase db, string databaseName)
        {
            this.db = db ?? throw new ArgumentNullException(nameof(db));
            this.databaseName = !string.IsNullOrWhiteSpace(databaseName) ? databaseName : throw new ArgumentException("Database name is required", nameof(databaseName));
        }

        public List<SqliteTableExportItem> LoadTableCatalog()
        {
            var list = new List<SqliteTableExportItem>();

            var virtualTables = GetVirtualTableNames();
            var spatialTableMap = GetSpatialTableDetails();

            // 取得所有一般與虛擬資料表（排除 sqlite_% 系統表）
            DataTable dt = null;
            try
            {
                dt = db.SelectSQL("SELECT name, sql FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name ASC;", new Dictionary<string, object>());
            }
            catch
            {
                dt = new DataTable();
            }

            foreach (DataRow row in dt.Rows)
            {
                string tableName = row["name"]?.ToString();
                if (string.IsNullOrWhiteSpace(tableName)) continue;

                // 排除影子表 (FTS / RTree 內部實體表，還原時由虛擬表自動生成，若重複建立會報錯)
                if (IsShadowTable(tableName, virtualTables)) continue;

                bool isVirtual = virtualTables.Contains(tableName);
                bool isSpatialMetadata = KnownSpatialMetadataTables.Contains(tableName);
                bool isSpatial = spatialTableMap.ContainsKey(tableName);
                string spatialInfo = isSpatial ? spatialTableMap[tableName] : (isSpatialMetadata ? "Spatial Metadata" : (isVirtual ? "Virtual Table" : ""));

                long count = 0;
                try
                {
                    count = db.CountRows(databaseName, tableName);
                }
                catch
                {
                    count = 0;
                }

                // 預設選取：除巨量空間中繼資料表（如 spatial_ref_sys）外預設勾選
                bool defaultSelect = !(isSpatialMetadata && string.Equals(tableName, "spatial_ref_sys", StringComparison.OrdinalIgnoreCase));

                list.Add(new SqliteTableExportItem(
                    tableName,
                    count,
                    defaultSelect,
                    isSpatial,
                    isSpatialMetadata,
                    isVirtual,
                    spatialInfo));
            }

            return list;
        }

        public List<string> LoadViews()
        {
            var views = new List<string>();
            try
            {
                DataTable dt = db.SelectSQL("SELECT name FROM sqlite_master WHERE type='view' AND name NOT LIKE 'sqlite_%' ORDER BY name ASC;", new Dictionary<string, object>());
                foreach (DataRow row in dt.Rows)
                {
                    string name = row[0]?.ToString();
                    if (!string.IsNullOrWhiteSpace(name)) views.Add(name);
                }
            }
            catch { }
            return views;
        }

        public List<string> LoadTriggers()
        {
            var triggers = new List<string>();
            try
            {
                DataTable dt = db.SelectSQL("SELECT name FROM sqlite_master WHERE type='trigger' AND sql IS NOT NULL AND name NOT LIKE 'sqlite_%' ORDER BY name ASC;", new Dictionary<string, object>());
                foreach (DataRow row in dt.Rows)
                {
                    string name = row[0]?.ToString();
                    if (!string.IsNullOrWhiteSpace(name)) triggers.Add(name);
                }
            }
            catch { }
            return triggers;
        }

        public Task<SqliteExportResult> ExportAsync(
            SqliteExportWizardOptions options,
            IProgress<SqliteExportProgress> progress,
            CancellationToken cancellation)
        {
            return Task.Run(() => ExportInternal(options, progress, cancellation), cancellation);
        }

        private SqliteExportResult ExportInternal(
            SqliteExportWizardOptions options,
            IProgress<SqliteExportProgress> progress,
            CancellationToken cancellation)
        {
            var clock = Stopwatch.StartNew();
            if (string.IsNullOrWhiteSpace(options.TargetPath))
                throw new ArgumentException("Target path is required", nameof(options.TargetPath));

            string targetPath = options.TargetPath;
            string targetDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(targetDir) && !Directory.Exists(targetDir))
                Directory.CreateDirectory(targetDir);

            string stagingPath = targetPath + ".staging." + Guid.NewGuid().ToString("N") + ".tmp";
            long totalProcessedRows = 0;
            long estimatedTotalRows = 0;
            int tableCount = 0;
            int viewCount = 0;
            int triggerCount = 0;
            int indexCount = 0;
            var tableManifest = new List<Tuple<string, long>>();

            var selectedTables = options.SelectedTables?.ToList() ?? new List<string>();
            var selectedViews = options.IncludeViews ? (options.SelectedViews?.ToList() ?? new List<string>()) : new List<string>();
            var selectedTriggers = options.IncludeTriggers ? (options.SelectedTriggers?.ToList() ?? new List<string>()) : new List<string>();

            // 空間中繼資料表排除過濾
            if (options.ExcludeSpatialMetadata)
            {
                selectedTables.RemoveAll(t => KnownSpatialMetadataTables.Contains(t));
            }

            // 預估總列數
            if (options.Mode != SqliteExportMode.SchemaOnly)
            {
                foreach (var table in selectedTables)
                {
                    try { estimatedTotalRows += db.CountRows(databaseName, table); } catch { }
                }
            }

            try
            {
                cancellation.ThrowIfCancellationRequested();

                using (var fileStream = new FileStream(stagingPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 65536))
                using (var writer = new StreamWriter(fileStream, new UTF8Encoding(false), 65536))
                {
                    writer.WriteLine("-- ========================================================");
                    writer.WriteLine("-- mySQLPunk SQLite Logical Dump");
                    writer.WriteLine($"-- Database: {databaseName}");
                    writer.WriteLine($"-- Created: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    writer.WriteLine($"-- Mode: {options.Mode}");
                    writer.WriteLine($"-- SingleTransaction: {options.SingleTransaction}");
                    writer.WriteLine("-- GIS / SpatiaLite / GeoPackage Binary Fidelity: Enabled");
                    writer.WriteLine("-- ========================================================");
                    writer.WriteLine();

                    // PRAGMA 設定
                    if (options.PragmaForeignKeysOff)
                        writer.WriteLine("PRAGMA foreign_keys = OFF;");
                    if (options.PragmaSynchronousOff)
                        writer.WriteLine("PRAGMA synchronous = OFF;");
                    writer.WriteLine();

                    // 交易起始
                    if (options.SingleTransaction)
                    {
                        writer.WriteLine("BEGIN TRANSACTION;");
                        writer.WriteLine();
                    }

                    // 1. DROP 語句
                    if (options.IncludeDropStatements && options.Mode != SqliteExportMode.DataOnly)
                    {
                        foreach (var tr in selectedTriggers)
                            writer.WriteLine($"DROP TRIGGER IF EXISTS {QuoteIdentifier(tr)};");
                        foreach (var v in selectedViews)
                            writer.WriteLine($"DROP VIEW IF EXISTS {QuoteIdentifier(v)};");
                        foreach (var t in selectedTables)
                            writer.WriteLine($"DROP TABLE IF EXISTS {QuoteIdentifier(t)};");
                        writer.WriteLine();
                    }

                    // 2. 結構 DDL (Tables)
                    if (options.Mode != SqliteExportMode.DataOnly)
                    {
                        ReportProgress(progress, SqliteExportStage.WritingSchema, "", 0, selectedTables.Count, 0, estimatedTotalRows, fileStream.Position, clock);

                        foreach (var table in selectedTables)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            string ddl = GetTableDdl(table);
                            if (!string.IsNullOrWhiteSpace(ddl))
                            {
                                writer.WriteLine(ddl.TrimEnd().TrimEnd(';') + ";");
                                writer.WriteLine();
                            }

                            // 側車欄位註解 DDL
                            if (options.IncludeComments)
                            {
                                string commentDdl = BuildColumnCommentsDump(table);
                                if (!string.IsNullOrWhiteSpace(commentDdl))
                                {
                                    writer.WriteLine(commentDdl.TrimEnd());
                                    writer.WriteLine();
                                }
                            }
                        }
                    }

                    // 3. 資料列 (Data)
                    if (options.Mode != SqliteExportMode.SchemaOnly)
                    {
                        int batchSize = options.InsertBatchSize > 0 ? options.InsertBatchSize : 1000;
                        for (int i = 0; i < selectedTables.Count; i++)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            string table = selectedTables[i];
                            long tableRows = 0;
                            long totalTableRows = 0;
                            try { totalTableRows = db.CountRows(databaseName, table); } catch { }

                            ReportProgress(progress, SqliteExportStage.WritingData, table, i, selectedTables.Count, totalProcessedRows, estimatedTotalRows, fileStream.Position, clock);

                            while (tableRows < totalTableRows || totalTableRows == 0)
                            {
                                cancellation.ThrowIfCancellationRequested();
                                DataTable page = db.SelectTablePage(databaseName, table, tableRows, batchSize);
                                if (page == null || page.Rows.Count == 0) break;

                                string insertPrefix = "INSERT INTO " + QuoteIdentifier(table) + " (";
                                var colNames = new string[page.Columns.Count];
                                for (int c = 0; c < page.Columns.Count; c++)
                                {
                                    colNames[c] = QuoteIdentifier(page.Columns[c].ColumnName);
                                }
                                string colsJoined = string.Join(", ", colNames);

                                for (int r = 0; r < page.Rows.Count; r++)
                                {
                                    writer.Write(insertPrefix);
                                    writer.Write(colsJoined);
                                    writer.Write(") VALUES (");

                                    DataRow row = page.Rows[r];
                                    for (int c = 0; c < page.Columns.Count; c++)
                                    {
                                        if (c > 0) writer.Write(", ");
                                        WriteSqliteLiteral(writer, row[c]);
                                    }
                                    writer.WriteLine(");");
                                }

                                tableRows += page.Rows.Count;
                                totalProcessedRows += page.Rows.Count;
                                ReportProgress(progress, SqliteExportStage.WritingData, table, i, selectedTables.Count, totalProcessedRows, estimatedTotalRows, fileStream.Position, clock);
                            }

                            if (tableRows > 0) writer.WriteLine();
                            tableCount++;
                            tableManifest.Add(Tuple.Create(table, tableRows));
                        }
                    }
                    else
                    {
                        tableCount = selectedTables.Count;
                    }

                    // 4. 檢視 (Views)
                    if (options.Mode != SqliteExportMode.DataOnly && selectedViews.Count > 0)
                    {
                        ReportProgress(progress, SqliteExportStage.WritingSchema, "Views", tableCount, selectedTables.Count, totalProcessedRows, estimatedTotalRows, fileStream.Position, clock);
                        foreach (var view in selectedViews)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            string ddl = GetViewDdl(view);
                            if (!string.IsNullOrWhiteSpace(ddl))
                            {
                                writer.WriteLine(ddl.TrimEnd().TrimEnd(';') + ";");
                                writer.WriteLine();
                            }
                            viewCount++;
                        }
                    }

                    // 5. 索引 (Indexes)
                    if (options.Mode != SqliteExportMode.DataOnly && options.IncludeIndexes)
                    {
                        ReportProgress(progress, SqliteExportStage.WritingIndexes, "Indexes", tableCount, selectedTables.Count, totalProcessedRows, estimatedTotalRows, fileStream.Position, clock);
                        var indexes = GetIndexesForTables(selectedTables);
                        foreach (var idx in indexes)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            writer.WriteLine(idx.TrimEnd().TrimEnd(';') + ";");
                            indexCount++;
                        }
                        if (indexes.Count > 0) writer.WriteLine();
                    }

                    // 6. 觸發器 (Triggers)
                    if (options.Mode != SqliteExportMode.DataOnly && selectedTriggers.Count > 0)
                    {
                        ReportProgress(progress, SqliteExportStage.WritingTriggers, "Triggers", tableCount, selectedTables.Count, totalProcessedRows, estimatedTotalRows, fileStream.Position, clock);
                        foreach (var triggerName in selectedTriggers)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            string ddl = GetTriggerDdl(triggerName);
                            if (!string.IsNullOrWhiteSpace(ddl))
                            {
                                writer.WriteLine(ddl.TrimEnd().TrimEnd(';') + ";");
                                writer.WriteLine();
                                triggerCount++;
                            }
                        }
                    }

                    // 交易結束
                    if (options.SingleTransaction)
                    {
                        writer.WriteLine("COMMIT;");
                        writer.WriteLine();
                    }

                    // 恢復 foreign_keys
                    if (options.PragmaForeignKeysOff)
                    {
                        writer.WriteLine("PRAGMA foreign_keys = ON;");
                    }

                    writer.Flush();

                    // 7. 寫入 Native Manifest 摘要
                    writer.WriteLine();
                    writer.WriteLine("-- ========================================================");
                    writer.WriteLine("-- mySQLPunk Native Manifest");
                    writer.WriteLine($"-- Tables: {tableCount}");
                    writer.WriteLine($"-- TotalRows: {totalProcessedRows}");
                    foreach (var pair in tableManifest)
                    {
                        writer.WriteLine($"-- Manifest-Table: {pair.Item1} | Rows: {pair.Item2}");
                    }
                    writer.WriteLine("-- ========================================================");
                    writer.Flush();
                }

                // 計算產出檔案的 SHA-256
                string sha256Hex;
                long finalLength;
                using (var fs = File.OpenRead(stagingPath))
                {
                    finalLength = fs.Length;
                    using (var sha = SHA256.Create())
                    {
                        byte[] hash = sha.ComputeHash(fs);
                        sha256Hex = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                    }
                }

                // 結尾附加 SHA256 到檔案中
                using (var appendWriter = new StreamWriter(stagingPath, true, new UTF8Encoding(false)))
                {
                    appendWriter.WriteLine($"-- Manifest-SHA256: {sha256Hex}");
                }

                // 原子替換到目標路徑
                if (File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                }
                File.Move(stagingPath, targetPath);

                clock.Stop();

                ReportProgress(progress, SqliteExportStage.Completed, "", tableCount, selectedTables.Count, totalProcessedRows, estimatedTotalRows, finalLength, clock);

                return new SqliteExportResult(
                    SqliteExportStage.Completed,
                    targetPath,
                    tableCount,
                    viewCount,
                    triggerCount,
                    indexCount,
                    totalProcessedRows,
                    finalLength,
                    sha256Hex,
                    clock.Elapsed);
            }
            catch (OperationCanceledException)
            {
                clock.Stop();
                TryDeleteFile(stagingPath);
                ReportProgress(progress, SqliteExportStage.Cancelled, "", tableCount, selectedTables.Count, totalProcessedRows, estimatedTotalRows, 0, clock);
                return new SqliteExportResult(
                    SqliteExportStage.Cancelled,
                    targetPath,
                    tableCount,
                    viewCount,
                    triggerCount,
                    indexCount,
                    totalProcessedRows,
                    0,
                    null,
                    clock.Elapsed,
                    "Export cancelled by user.");
            }
            catch (Exception ex)
            {
                clock.Stop();
                TryDeleteFile(stagingPath);
                ReportProgress(progress, SqliteExportStage.Failed, "", tableCount, selectedTables.Count, totalProcessedRows, estimatedTotalRows, 0, clock, ex.Message);
                return new SqliteExportResult(
                    SqliteExportStage.Failed,
                    targetPath,
                    tableCount,
                    viewCount,
                    triggerCount,
                    indexCount,
                    totalProcessedRows,
                    0,
                    null,
                    clock.Elapsed,
                    ex.Message);
            }
        }

        private static void WriteSqliteLiteral(StreamWriter writer, object value)
        {
            if (value == null || value == DBNull.Value)
            {
                writer.Write("NULL");
                return;
            }

            if (value is byte[] bytes)
            {
                // SpatiaLite / GeoPackage GIS 幾何與一般 BLOB 二進位無損輸出
                writer.Write("X'");
                char[] hexBuffer = new char[Math.Min(bytes.Length * 2, 8192)];
                int bufIdx = 0;
                for (int i = 0; i < bytes.Length; i++)
                {
                    byte b = bytes[i];
                    hexBuffer[bufIdx++] = HexLookup[b >> 4];
                    hexBuffer[bufIdx++] = HexLookup[b & 0x0F];
                    if (bufIdx >= hexBuffer.Length - 1)
                    {
                        writer.Write(hexBuffer, 0, bufIdx);
                        bufIdx = 0;
                    }
                }
                if (bufIdx > 0)
                {
                    writer.Write(hexBuffer, 0, bufIdx);
                }
                writer.Write("'");
                return;
            }

            if (value is string s)
            {
                writer.Write("'");
                writer.Write(s.Replace("'", "''"));
                writer.Write("'");
                return;
            }

            if (value is bool bVal)
            {
                writer.Write(bVal ? "1" : "0");
                return;
            }

            if (value is DateTime dt)
            {
                writer.Write("'");
                writer.Write(dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
                writer.Write("'");
                return;
            }

            if (value is float f)
            {
                writer.Write(f.ToString("R", CultureInfo.InvariantCulture));
                return;
            }

            if (value is double d)
            {
                writer.Write(d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }

            if (value is decimal dec)
            {
                writer.Write(dec.ToString(CultureInfo.InvariantCulture));
                return;
            }

            if (value is IFormattable formattable)
            {
                writer.Write(formattable.ToString(null, CultureInfo.InvariantCulture));
                return;
            }

            writer.Write("'");
            writer.Write(value.ToString().Replace("'", "''"));
            writer.Write("'");
        }

        public static string QuoteIdentifier(string name)
        {
            name = name ?? string.Empty;
            return "\"" + name.Replace("\"", "\"\"") + "\"";
        }

        private string GetTableDdl(string tableName)
        {
            try
            {
                string safe = tableName.Replace("'", "''");
                DataTable dt = db.SelectSQL($"SELECT sql FROM sqlite_master WHERE type='table' AND name='{safe}';", new Dictionary<string, object>());
                if (dt != null && dt.Rows.Count > 0 && dt.Rows[0][0] != DBNull.Value)
                    return dt.Rows[0][0].ToString();
            }
            catch { }
            return "";
        }

        private string GetViewDdl(string viewName)
        {
            try
            {
                string safe = viewName.Replace("'", "''");
                DataTable dt = db.SelectSQL($"SELECT sql FROM sqlite_master WHERE type='view' AND name='{safe}';", new Dictionary<string, object>());
                if (dt != null && dt.Rows.Count > 0 && dt.Rows[0][0] != DBNull.Value)
                    return dt.Rows[0][0].ToString();
            }
            catch { }
            return "";
        }

        private string GetTriggerDdl(string triggerName)
        {
            try
            {
                string safe = triggerName.Replace("'", "''");
                DataTable dt = db.SelectSQL($"SELECT sql FROM sqlite_master WHERE type='trigger' AND name='{safe}';", new Dictionary<string, object>());
                if (dt != null && dt.Rows.Count > 0 && dt.Rows[0][0] != DBNull.Value)
                    return dt.Rows[0][0].ToString();
            }
            catch { }
            return "";
        }

        private List<string> GetIndexesForTables(IEnumerable<string> tableNames)
        {
            var result = new List<string>();
            var set = new HashSet<string>(tableNames, StringComparer.OrdinalIgnoreCase);
            try
            {
                DataTable dt = db.SelectSQL("SELECT sql, tbl_name FROM sqlite_master WHERE type='index' AND sql IS NOT NULL AND name NOT LIKE 'sqlite_%';", new Dictionary<string, object>());
                foreach (DataRow row in dt.Rows)
                {
                    string tbl = row["tbl_name"]?.ToString();
                    string sql = row["sql"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(tbl) && set.Contains(tbl) && !string.IsNullOrWhiteSpace(sql))
                    {
                        result.Add(sql);
                    }
                }
            }
            catch { }
            return result;
        }

        private string BuildColumnCommentsDump(string tableName)
        {
            try
            {
                DataTable columns = db.GetColumns(databaseName, tableName);
                if (columns == null || columns.Rows.Count == 0) return "";

                var inserts = new List<string>();
                foreach (DataRow row in columns.Rows)
                {
                    string colName = null;
                    foreach (string key in new[] { "name", "Name", "COLUMN_NAME", "column_name", "Field" })
                    {
                        if (row.Table.Columns.Contains(key) && row[key] != DBNull.Value)
                        {
                            colName = row[key].ToString();
                            break;
                        }
                    }
                    string comment = null;
                    foreach (string key in new[] { "Comment", "comment" })
                    {
                        if (row.Table.Columns.Contains(key) && row[key] != DBNull.Value)
                        {
                            comment = row[key].ToString();
                            break;
                        }
                    }

                    if (string.IsNullOrWhiteSpace(colName) || string.IsNullOrWhiteSpace(comment)) continue;

                    inserts.Add("INSERT OR REPLACE INTO " + QuoteIdentifier(my_sqlite.ColumnCommentTableName) +
                                " (table_name, column_name, comment) VALUES (" +
                                "'" + tableName.Replace("'", "''") + "', " +
                                "'" + colName.Replace("'", "''") + "', " +
                                "'" + comment.Replace("'", "''") + "');");
                }

                if (inserts.Count == 0) return "";

                var sb = new StringBuilder();
                sb.AppendLine("-- SQLite column comments sidecar metadata");
                sb.AppendLine("CREATE TABLE IF NOT EXISTS " + QuoteIdentifier(my_sqlite.ColumnCommentTableName) + " (" +
                              "table_name TEXT NOT NULL, " +
                              "column_name TEXT NOT NULL, " +
                              "comment TEXT NOT NULL, " +
                              "PRIMARY KEY (table_name, column_name));");
                foreach (var ins in inserts) sb.AppendLine(ins);
                return sb.ToString();
            }
            catch
            {
                return "";
            }
        }

        private HashSet<string> GetVirtualTableNames()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                DataTable dt = db.SelectSQL("SELECT name FROM sqlite_master WHERE type='table' AND sql LIKE 'CREATE VIRTUAL%';", new Dictionary<string, object>());
                foreach (DataRow row in dt.Rows)
                {
                    string n = row[0]?.ToString();
                    if (!string.IsNullOrWhiteSpace(n)) names.Add(n);
                }
            }
            catch { }
            return names;
        }

        private static bool IsShadowTable(string tableName, HashSet<string> virtualNames)
        {
            if (virtualNames == null || virtualNames.Count == 0) return false;
            int idx = tableName.LastIndexOf('_');
            if (idx <= 0) return false;
            string prefix = tableName.Substring(0, idx);
            if (!virtualNames.Contains(prefix)) return false;
            string suffix = tableName.Substring(idx + 1);
            foreach (string known in ShadowTableSuffixes)
            {
                if (string.Equals(suffix, known, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private Dictionary<string, string> GetSpatialTableDetails()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // 1. 探測 SpatiaLite geometry_columns
            try
            {
                DataTable dt = db.SelectSQL("SELECT f_table_name, f_geometry_column, type, srid FROM geometry_columns;", new Dictionary<string, object>());
                if (dt != null)
                {
                    foreach (DataRow r in dt.Rows)
                    {
                        string tbl = r["f_table_name"]?.ToString();
                        string col = r["f_geometry_column"]?.ToString();
                        string type = r["type"]?.ToString();
                        string srid = r["srid"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(tbl))
                        {
                            map[tbl] = $"[SpatiaLite] {type} ({col}, SRID: {srid})";
                        }
                    }
                }
            }
            catch { }

            // 2. 探測 GeoPackage gpkg_geometry_columns
            try
            {
                DataTable dt = db.SelectSQL("SELECT table_name, column_name, geometry_type_name, srid FROM gpkg_geometry_columns;", new Dictionary<string, object>());
                if (dt != null)
                {
                    foreach (DataRow r in dt.Rows)
                    {
                        string tbl = r["table_name"]?.ToString();
                        string col = r["column_name"]?.ToString();
                        string type = r["geometry_type_name"]?.ToString();
                        string srid = r["srid"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(tbl))
                        {
                            map[tbl] = $"[GeoPackage] {type} ({col}, SRID: {srid})";
                        }
                    }
                }
            }
            catch { }

            return map;
        }

        private static void ReportProgress(
            IProgress<SqliteExportProgress> progress,
            SqliteExportStage stage,
            string currentObject,
            int completedTables,
            int totalTables,
            long processedRows,
            long estimatedTotalRows,
            long writtenBytes,
            Stopwatch clock,
            string errorMessage = null)
        {
            if (progress == null) return;

            double elapsedSec = clock.Elapsed.TotalSeconds;
            double rowsPerSec = elapsedSec > 0.05 ? processedRows / elapsedSec : 0;
            TimeSpan? remaining = null;
            if (rowsPerSec > 0 && estimatedTotalRows > processedRows)
            {
                double remainingSec = (estimatedTotalRows - processedRows) / rowsPerSec;
                if (remainingSec < 86400) remaining = TimeSpan.FromSeconds(remainingSec);
            }

            string msg;
            switch (stage)
            {
                case SqliteExportStage.ReadingCatalog:
                    msg = "Reading database catalog...";
                    break;
                case SqliteExportStage.WritingSchema:
                    msg = $"Writing schema for {currentObject}...";
                    break;
                case SqliteExportStage.WritingData:
                    msg = $"Writing data rows for {currentObject} ({processedRows:N0} rows)...";
                    break;
                case SqliteExportStage.WritingIndexes:
                    msg = "Writing indexes...";
                    break;
                case SqliteExportStage.WritingTriggers:
                    msg = "Writing triggers...";
                    break;
                case SqliteExportStage.WritingManifest:
                    msg = "Generating native manifest...";
                    break;
                case SqliteExportStage.Completed:
                    msg = $"Export completed: {completedTables} tables, {processedRows:N0} rows.";
                    break;
                case SqliteExportStage.Cancelled:
                    msg = "Export cancelled.";
                    break;
                case SqliteExportStage.Failed:
                    msg = "Export failed: " + errorMessage;
                    break;
                default:
                    msg = currentObject;
                    break;
            }

            progress.Report(new SqliteExportProgress(
                stage,
                currentObject,
                completedTables,
                totalTables,
                processedRows,
                estimatedTotalRows,
                writtenBytes,
                rowsPerSec,
                clock.Elapsed,
                remaining,
                msg));
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }
    }
}
