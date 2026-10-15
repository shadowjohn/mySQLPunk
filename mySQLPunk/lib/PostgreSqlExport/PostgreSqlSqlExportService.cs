using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace mySQLPunk.lib.PostgreSqlExport
{
    public sealed class PostgreSqlSqlExportService
    {
        private const int BufferSize = 65536; // 64KB
        private readonly IDatabase db;
        private readonly string databaseName;

        private static readonly HashSet<string> SpatialMetadataTableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "spatial_ref_sys", "spatial_ref_sys_aux", "geometry_columns", "geography_columns",
            "raster_columns", "raster_overviews", "layer", "topology"
        };

        public PostgreSqlSqlExportService(IDatabase db, string databaseName)
        {
            this.db = db ?? throw new ArgumentNullException(nameof(db));
            this.databaseName = databaseName ?? string.Empty;
        }

        public async Task<List<PostgreSqlTableExportItem>> GetTableCatalogAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            return await Task.Run(() => GetTableCatalog(cancellationToken), cancellationToken).ConfigureAwait(false);
        }

        public List<PostgreSqlTableExportItem> GetTableCatalog(CancellationToken cancellationToken = default(CancellationToken))
        {
            var list = new List<PostgreSqlTableExportItem>();

            // 1. 取得所有一般資料表與檢視
            string catalogSql = @"
                SELECT
                    n.nspname AS ""Schema"",
                    c.relname AS ""Name"",
                    c.relkind AS ""Kind"",
                    pg_total_relation_size(c.oid) AS ""EstimatedBytes"",
                    COALESCE(s.n_live_tup, 0) AS ""RowCount""
                FROM pg_class c
                JOIN pg_namespace n ON n.oid = c.relnamespace
                LEFT JOIN pg_stat_user_tables s ON s.relid = c.oid
                WHERE n.nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast')
                  AND c.relkind IN ('r', 'p', 'v', 'm')
                ORDER BY n.nspname, c.relname;";

            DataTable dt = db.SelectSQL(catalogSql);
            var itemMap = new Dictionary<string, PostgreSqlTableExportItem>(StringComparer.OrdinalIgnoreCase);

            foreach (DataRow row in dt.Rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string schema = row["Schema"]?.ToString() ?? "public";
                string name = row["Name"]?.ToString() ?? string.Empty;
                string kind = row["Kind"]?.ToString() ?? "r";
                long bytes = 0;
                long rows = 0;
                if (row["EstimatedBytes"] != DBNull.Value) long.TryParse(row["EstimatedBytes"].ToString(), out bytes);
                if (row["RowCount"] != DBNull.Value) long.TryParse(row["RowCount"].ToString(), out rows);

                bool isView = (kind == "v" || kind == "m");
                bool isMeta = SpatialMetadataTableNames.Contains(name);

                var item = new PostgreSqlTableExportItem
                {
                    Schema = schema,
                    Name = name,
                    EstimatedBytes = bytes,
                    RowCount = rows,
                    IsView = isView,
                    IsSpatialMetadata = isMeta,
                    Selected = !isMeta // 龐大空間中繼表預設不勾選
                };

                list.Add(item);
                itemMap[schema + "." + name] = item;
            }

            // 2. 探測 PostGIS 空間幾何欄位 (geometry, geography, raster)
            try
            {
                string spatialColsSql = @"
                    SELECT
                        c.table_schema,
                        c.table_name,
                        c.column_name,
                        c.udt_name
                    FROM information_schema.columns c
                    WHERE c.table_schema NOT IN ('pg_catalog', 'information_schema', 'pg_toast')
                      AND (c.udt_name IN ('geometry', 'geography', 'raster') OR c.data_type = 'USER-DEFINED');";

                DataTable spatialDt = db.SelectSQL(spatialColsSql);
                foreach (DataRow row in spatialDt.Rows)
                {
                    string schema = row["table_schema"]?.ToString() ?? "";
                    string name = row["table_name"]?.ToString() ?? "";
                    string col = row["column_name"]?.ToString() ?? "";
                    string udt = row["udt_name"]?.ToString() ?? "geometry";

                    string key = schema + "." + name;
                    if (itemMap.TryGetValue(key, out var item))
                    {
                        item.IsSpatial = true;
                        if (!item.SpatialColumnNames.Contains(col))
                        {
                            item.SpatialColumnNames.Add(col);
                        }
                        item.SpatialFeatureDescription = "PostGIS " + udt.ToUpperInvariant();
                    }
                }

                // 3. 嘗試讀取 geometry_columns 取得精準 SRID 與空間類型
                try
                {
                    string geomViewSql = @"
                        SELECT f_table_schema, f_table_name, f_geometry_column, coord_dimension, srid, type
                        FROM geometry_columns;";
                    DataTable geomViewDt = db.SelectSQL(geomViewSql);
                    foreach (DataRow row in geomViewDt.Rows)
                    {
                        string schema = row["f_table_schema"]?.ToString() ?? "";
                        string name = row["f_table_name"]?.ToString() ?? "";
                        string col = row["f_geometry_column"]?.ToString() ?? "";
                        string dim = row["coord_dimension"]?.ToString() ?? "2";
                        string srid = row["srid"]?.ToString() ?? "0";
                        string type = row["type"]?.ToString() ?? "GEOMETRY";

                        string key = schema + "." + name;
                        if (itemMap.TryGetValue(key, out var item))
                        {
                            item.IsSpatial = true;
                            item.SpatialFeatureDescription = string.Format("PostGIS: {0} ({1}, SRID {2}, {3}D)", col, type, srid, dim);
                        }
                    }
                }
                catch
                {
                    // geometry_columns view 可能不存在或未裝 PostGIS，安全略過
                }
            }
            catch
            {
                // information_schema 查空間欄位若失敗不中斷基本結構清單
            }

            return list;
        }

        public async Task<PostgreSqlExportResult> ExportAsync(
            PostgreSqlExportWizardOptions options,
            IProgress<PostgreSqlExportProgress> progress = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrWhiteSpace(options.TargetPath)) throw new ArgumentException("Target path cannot be empty.", nameof(options.TargetPath));

            var result = new PostgreSqlExportResult
            {
                TargetPath = options.TargetPath,
                Stage = PostgreSqlExportStage.Preflight
            };

            var clock = Stopwatch.StartNew();
            string stagingFile = options.TargetPath + ".tmp_" + Guid.NewGuid().ToString("N");
            string dir = Path.GetDirectoryName(options.TargetPath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            FileStream fs = null;
            StreamWriter writer = null;
            IncrementalHash sha256 = null;

            try
            {
                progress?.Report(new PostgreSqlExportProgress
                {
                    Stage = PostgreSqlExportStage.Preflight,
                    Message = "正在探測資料庫結構與 PostGIS 空間中繼資料..."
                });

                var catalog = await GetTableCatalogAsync(cancellationToken).ConfigureAwait(false);
                var selectedItems = new List<PostgreSqlTableExportItem>();
                var selectedSet = new HashSet<string>(options.SelectedTables, StringComparer.OrdinalIgnoreCase);

                bool hasSpatialObjects = false;
                foreach (var item in catalog)
                {
                    bool match = selectedSet.Count == 0 ||
                                 selectedSet.Contains(item.FullQualifiedName) ||
                                 selectedSet.Contains(item.DisplayName) ||
                                 selectedSet.Contains(item.Name);

                    if (match)
                    {
                        selectedItems.Add(item);
                        if (item.IsSpatial) hasSpatialObjects = true;
                    }
                }

                fs = new FileStream(stagingFile, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize);
                writer = new StreamWriter(fs, new UTF8Encoding(false), BufferSize);
                sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

                void WriteLine(string text)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    writer.WriteLine(text);
                    byte[] bytes = Encoding.UTF8.GetBytes(text + "\n");
                    sha256.AppendData(bytes);
                    result.BytesWritten += bytes.Length;
                }

                // 腳本檔頭
                WriteLine("-- ====================================================================");
                WriteLine("-- mySQLPunk PostgreSQL Logical SQL Dump");
                WriteLine(string.Format("-- Database: {0}", databaseName));
                WriteLine(string.Format("-- Generated At: {0:yyyy-MM-dd HH:mm:ss} UTC", DateTime.UtcNow));
                WriteLine("-- Provider: PostgreSQL / PostGIS");
                WriteLine("-- ====================================================================");
                WriteLine("");
                WriteLine("SET statement_timeout = 0;");
                WriteLine("SET lock_timeout = 0;");
                WriteLine("SET client_encoding = 'UTF8';");
                WriteLine("SET standard_conforming_strings = on;");
                WriteLine("SET check_function_bodies = false;");
                WriteLine("SET client_min_messages = warning;");
                WriteLine("SET row_security = off;");
                WriteLine("");

                // PostGIS 擴充模組開頭宣告
                if (options.IncludeCreateExtensionPostgis && hasSpatialObjects)
                {
                    WriteLine("-- 🌐 PostGIS 空間延伸模組自動載入");
                    WriteLine("CREATE EXTENSION IF NOT EXISTS postgis;");
                    WriteLine("");
                }

                // 交易快照 (REPEATABLE READ)
                if (options.UseTransactionSnapshot)
                {
                    WriteLine("-- 開啟 REPEATABLE READ 一致性交易快照");
                    WriteLine("BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ;");
                    WriteLine("");
                }

                int totalTables = selectedItems.Count;
                int tableIndex = 0;
                long totalRowsExported = 0;

                // 1. 結構 (DDL) 匯出
                if (options.Mode == PostgreSqlExportMode.SchemaAndData || options.Mode == PostgreSqlExportMode.SchemaOnly)
                {
                    result.Stage = PostgreSqlExportStage.ExportingSchema;
                    foreach (var item in selectedItems)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        tableIndex++;

                        progress?.Report(new PostgreSqlExportProgress
                        {
                            Stage = PostgreSqlExportStage.ExportingSchema,
                            CurrentSchema = item.Schema,
                            CurrentTable = item.Name,
                            TablesCompleted = tableIndex - 1,
                            TotalTables = totalTables,
                            BytesExported = result.BytesWritten,
                            Message = string.Format("正在產生結構 DDL: {0} ({1}/{2})", item.DisplayName, tableIndex, totalTables)
                        });

                        WriteLine(string.Format("-- --------------------------------------------------------------------"));
                        WriteLine(string.Format("-- Table Structure for: {0}", item.FullQualifiedName));
                        if (item.IsSpatial)
                        {
                            WriteLine(string.Format("-- Spatial Feature: {0}", item.SpatialFeatureDescription));
                        }
                        WriteLine(string.Format("-- --------------------------------------------------------------------"));

                        if (options.DropTableIfExists)
                        {
                            WriteLine(string.Format("DROP TABLE IF EXISTS {0} CASCADE;", item.FullQualifiedName));
                        }

                        string ddl = GetTableDdl(item.Schema, item.Name, options);
                        if (!string.IsNullOrWhiteSpace(ddl))
                        {
                            WriteLine(ddl.Trim());
                            WriteLine("");
                        }

                        if (item.IsView) result.ViewCount++;
                        else result.TableCount++;
                    }
                }

                // 2. 資料 (Data) 匯出
                if (options.Mode == PostgreSqlExportMode.SchemaAndData || options.Mode == PostgreSqlExportMode.DataOnly)
                {
                    result.Stage = PostgreSqlExportStage.ExportingData;
                    tableIndex = 0;

                    foreach (var item in selectedItems)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        tableIndex++;
                        if (item.IsView) continue; // 檢視不傾印資料

                        progress?.Report(new PostgreSqlExportProgress
                        {
                            Stage = PostgreSqlExportStage.ExportingData,
                            CurrentSchema = item.Schema,
                            CurrentTable = item.Name,
                            TablesCompleted = tableIndex - 1,
                            TotalTables = totalTables,
                            RowsExported = totalRowsExported,
                            BytesExported = result.BytesWritten,
                            Message = string.Format("正在匯出資料: {0} ({1}/{2})", item.DisplayName, tableIndex, totalTables)
                        });

                        WriteLine(string.Format("-- --------------------------------------------------------------------"));
                        WriteLine(string.Format("-- Data for Table: {0}", item.FullQualifiedName));
                        WriteLine(string.Format("-- --------------------------------------------------------------------"));

                        if (options.TruncateBeforeInsert)
                        {
                            WriteLine(string.Format("TRUNCATE TABLE {0} CASCADE;", item.FullQualifiedName));
                        }

                        long tableRows = ExportTableData(item, writer, sha256, WriteLine, options, cancellationToken);
                        totalRowsExported += tableRows;
                        result.ManifestTableRows[item.DisplayName] = tableRows;
                        WriteLine("");
                    }
                }

                // 3. 關閉交易
                if (options.UseTransactionSnapshot)
                {
                    WriteLine("COMMIT;");
                    WriteLine("");
                }

                // 4. Native Manifest 結尾清單與 SHA-256
                if (options.IncludeManifest)
                {
                    WriteLine("-- ====================================================================");
                    WriteLine("-- mySQLPunk Native Manifest");
                    WriteLine("-- ====================================================================");
                    WriteLine(string.Format("-- Manifest-Version: 1.0"));
                    WriteLine(string.Format("-- Manifest-Database: {0}", databaseName));
                    WriteLine(string.Format("-- Manifest-Provider: postgresql"));
                    WriteLine(string.Format("-- Manifest-Total-Tables: {0}", result.ManifestTableRows.Count));

                    foreach (var kvp in result.ManifestTableRows)
                    {
                        WriteLine(string.Format("-- Manifest-Table: {0} | Rows: {1}", kvp.Key, kvp.Value));
                    }

                    byte[] hashBytes = sha256.GetHashAndReset();
                    string sha256Hex = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
                    result.ManifestSha256 = sha256Hex;
                    WriteLine(string.Format("-- Manifest-Checksum-SHA256: {0}", sha256Hex));
                    WriteLine("-- ====================================================================");
                }

                await writer.FlushAsync().ConfigureAwait(false);
                writer.Close();
                writer = null;
                fs.Close();
                fs = null;

                // 原子替換
                if (File.Exists(options.TargetPath))
                {
                    File.Delete(options.TargetPath);
                }
                File.Move(stagingFile, options.TargetPath);

                result.Success = true;
                result.Stage = PostgreSqlExportStage.Completed;
                result.TotalRows = totalRowsExported;
                clock.Stop();
                result.Elapsed = clock.Elapsed;

                progress?.Report(new PostgreSqlExportProgress
                {
                    Stage = PostgreSqlExportStage.Completed,
                    TablesCompleted = totalTables,
                    TotalTables = totalTables,
                    RowsExported = totalRowsExported,
                    TotalRows = totalRowsExported,
                    BytesExported = result.BytesWritten,
                    Message = string.Format("PostgreSQL 傾印完成：{0} 張物件，共 {1:N0} 列資料。", totalTables, totalRowsExported)
                });

                return result;
            }
            catch (OperationCanceledException)
            {
                result.Success = false;
                result.Stage = PostgreSqlExportStage.Cancelled;
                result.ErrorMessage = "使用者取消了匯出操作。";
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Stage = PostgreSqlExportStage.Failed;
                result.ErrorMessage = ex.Message;
                return result;
            }
            finally
            {
                if (writer != null) { try { writer.Dispose(); } catch { } }
                if (fs != null) { try { fs.Dispose(); } catch { } }
                if (File.Exists(stagingFile))
                {
                    try { File.Delete(stagingFile); } catch { }
                }
            }
        }

        private string GetTableDdl(string schema, string tableName, PostgreSqlExportWizardOptions options)
        {
            var p = new Dictionary<string, object> { { "schema", schema }, { "tableName", tableName } };

            // 查詢精確欄位定義 (包含 pg_catalog.format_type 原生型別、預設值、Generated Columns 與註解)
            string colSql = @"
                SELECT
                    c.column_name,
                    c.data_type,
                    c.is_nullable,
                    c.column_default,
                    pg_catalog.format_type(a.atttypid, a.atttypmod) AS ""ProviderType"",
                    c.is_identity AS ""IsIdentity"",
                    c.identity_generation AS ""IdentityGeneration"",
                    c.is_generated AS ""IsGenerated"",
                    COALESCE(pg_get_expr(ad.adbin, ad.adrelid), '') AS ""GenerationExpression"",
                    COALESCE(col_description(cls.oid, a.attnum), '') AS ""Comment""
                FROM information_schema.columns c
                LEFT JOIN pg_namespace n ON n.nspname = c.table_schema
                LEFT JOIN pg_class cls ON cls.relnamespace = n.oid AND cls.relname = c.table_name
                LEFT JOIN pg_attribute a ON a.attrelid = cls.oid AND a.attname = c.column_name AND a.attnum > 0 AND NOT a.attisdropped
                LEFT JOIN pg_attrdef ad ON ad.adrelid = cls.oid AND ad.adnum = a.attnum
                WHERE c.table_schema = :schema AND c.table_name = :tableName
                ORDER BY c.ordinal_position;";

            DataTable columns;
            try
            {
                columns = db.SelectSQL(colSql, p);
            }
            catch
            {
                // PG 11 以下退回簡化欄位查詢
                string fallbackSql = @"
                    SELECT
                        c.column_name,
                        c.data_type,
                        c.is_nullable,
                        c.column_default,
                        pg_catalog.format_type(a.atttypid, a.atttypmod) AS ""ProviderType"",
                        'NO' AS ""IsIdentity"",
                        '' AS ""IdentityGeneration"",
                        'NEVER' AS ""IsGenerated"",
                        '' AS ""GenerationExpression"",
                        COALESCE(col_description(cls.oid, a.attnum), '') AS ""Comment""
                    FROM information_schema.columns c
                    LEFT JOIN pg_namespace n ON n.nspname = c.table_schema
                    LEFT JOIN pg_class cls ON cls.relnamespace = n.oid AND cls.relname = c.table_name
                    LEFT JOIN pg_attribute a ON a.attrelid = cls.oid AND a.attname = c.column_name AND a.attnum > 0 AND NOT a.attisdropped
                    WHERE c.table_schema = :schema AND c.table_name = :tableName
                    ORDER BY c.ordinal_position;";
                columns = db.SelectSQL(fallbackSql, p);
            }

            if (columns.Rows.Count == 0) return string.Empty;

            var defs = new List<string>();
            var columnComments = new StringBuilder();

            foreach (DataRow row in columns.Rows)
            {
                string name = row["column_name"]?.ToString() ?? "";
                string providerType = row["ProviderType"]?.ToString();
                if (string.IsNullOrWhiteSpace(providerType))
                {
                    providerType = row["data_type"]?.ToString() ?? "text";
                }

                string nullable = string.Equals(row["is_nullable"]?.ToString(), "YES", StringComparison.OrdinalIgnoreCase) ? "NULL" : "NOT NULL";
                string def = "  \"" + name.Replace("\"", "\"\"") + "\" " + providerType;

                string isIdentity = row["IsIdentity"]?.ToString();
                string isGenerated = row["IsGenerated"]?.ToString();

                if (string.Equals(isGenerated, "ALWAYS", StringComparison.OrdinalIgnoreCase))
                {
                    string expr = row["GenerationExpression"]?.ToString();
                    def += " GENERATED ALWAYS AS (" + expr + ") STORED";
                }
                else if (string.Equals(isIdentity, "YES", StringComparison.OrdinalIgnoreCase))
                {
                    string gen = row["IdentityGeneration"]?.ToString();
                    def += string.Equals(gen, "ALWAYS", StringComparison.OrdinalIgnoreCase)
                        ? " GENERATED ALWAYS AS IDENTITY"
                        : " GENERATED BY DEFAULT AS IDENTITY";
                }
                else
                {
                    string defVal = row["column_default"]?.ToString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(defVal))
                    {
                        def += " DEFAULT " + defVal;
                    }
                    def += " " + nullable;
                }

                defs.Add(def);

                if (options.IncludeComments)
                {
                    string comment = row["Comment"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(comment))
                    {
                        columnComments.Append("\r\nCOMMENT ON COLUMN \"" + schema + "\".\"" + tableName + "\".\"" + name.Replace("\"", "\"\"") +
                                              "\" IS '" + comment.Replace("'", "''") + "';");
                    }
                }
            }

            // 主鍵
            try
            {
                string pkSql = @"
                    SELECT a.attname
                    FROM pg_index ix
                    JOIN pg_attribute a ON a.attrelid = ix.indrelid AND a.attnum = ANY(ix.indkey)
                    JOIN pg_class t ON t.oid = ix.indrelid
                    JOIN pg_namespace n ON n.oid = t.relnamespace
                    WHERE n.nspname = :schema AND t.relname = :tableName AND ix.indisprimary
                    ORDER BY array_position(ix.indkey, a.attnum);";
                DataTable pkDt = db.SelectSQL(pkSql, p);
                if (pkDt.Rows.Count > 0)
                {
                    var pkCols = new List<string>();
                    foreach (DataRow r in pkDt.Rows)
                    {
                        pkCols.Add("\"" + r["attname"]?.ToString().Replace("\"", "\"\"") + "\"");
                    }
                    defs.Add("  PRIMARY KEY (" + string.Join(", ", pkCols) + ")");
                }
            }
            catch { }

            var sb = new StringBuilder();
            sb.Append("CREATE TABLE \"" + schema + "\".\"" + tableName + "\" (\r\n");
            sb.Append(string.Join(",\r\n", defs));
            sb.Append("\r\n);");

            // 表註解
            if (options.IncludeComments)
            {
                try
                {
                    string tableCommentSql = @"
                        SELECT obj_description(c.oid, 'pg_class') AS comment
                        FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                        WHERE n.nspname = :schema AND c.relname = :tableName;";
                    DataTable tcDt = db.SelectSQL(tableCommentSql, p);
                    if (tcDt.Rows.Count > 0 && tcDt.Rows[0]["comment"] != DBNull.Value)
                    {
                        string cText = tcDt.Rows[0]["comment"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(cText))
                        {
                            sb.Append("\r\nCOMMENT ON TABLE \"" + schema + "\".\"" + tableName + "\" IS '" + cText.Replace("'", "''") + "';");
                        }
                    }
                }
                catch { }

                sb.Append(columnComments.ToString());
            }

            // 索引 (包含 PostGIS GIST / SP-GIST 空間索引)
            if (options.IncludeIndexes)
            {
                try
                {
                    string indexSql = @"
                        SELECT i.indexdef
                        FROM pg_indexes i
                        WHERE i.schemaname = :schema AND i.tablename = :tableName
                          AND NOT EXISTS (
                              SELECT 1 FROM pg_constraint con
                              JOIN pg_class ic ON ic.oid = con.conindid
                              WHERE con.contype = 'p' AND ic.relname = i.indexname
                          )
                        ORDER BY i.indexname;";
                    DataTable idxDt = db.SelectSQL(indexSql, p);
                    foreach (DataRow r in idxDt.Rows)
                    {
                        string idxDef = r["indexdef"]?.ToString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(idxDef))
                        {
                            sb.Append("\r\n" + idxDef + ";");
                        }
                    }
                }
                catch { }
            }

            return sb.ToString();
        }

        private long ExportTableData(
            PostgreSqlTableExportItem item,
            StreamWriter writer,
            IncrementalHash sha256,
            Action<string> writeLine,
            PostgreSqlExportWizardOptions options,
            CancellationToken cancellationToken)
        {
            long rowCount = 0;
            string selectSql = string.Format("SELECT * FROM {0};", item.FullQualifiedName);

            DataTable dt = db.SelectSQL(selectSql);
            if (dt == null || dt.Rows.Count == 0) return 0;

            int colCount = dt.Columns.Count;
            var colNames = new List<string>();
            var isSpatialCol = new bool[colCount];
            var isByteaCol = new bool[colCount];

            for (int i = 0; i < colCount; i++)
            {
                string colName = dt.Columns[i].ColumnName;
                colNames.Add("\"" + colName.Replace("\"", "\"\"") + "\"");

                if (item.SpatialColumnNames.Contains(colName))
                {
                    isSpatialCol[i] = true;
                }
            }

            string insertHeader = string.Format("INSERT INTO {0} ({1}) VALUES", item.FullQualifiedName, string.Join(", ", colNames));
            var batchRows = new List<string>();

            foreach (DataRow row in dt.Rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                rowCount++;

                var values = new List<string>();
                for (int i = 0; i < colCount; i++)
                {
                    object val = row[i];
                    values.Add(FormatPostgreSqlValue(val, isSpatialCol[i]));
                }

                batchRows.Add("(" + string.Join(", ", values) + ")");

                if (batchRows.Count >= options.BatchSize)
                {
                    writeLine(insertHeader);
                    writeLine(string.Join(",\r\n", batchRows) + ";");
                    batchRows.Clear();
                }
            }

            if (batchRows.Count > 0)
            {
                writeLine(insertHeader);
                writeLine(string.Join(",\r\n", batchRows) + ";");
                batchRows.Clear();
            }

            return rowCount;
        }

        private static string FormatPostgreSqlValue(object val, bool isSpatial)
        {
            if (val == null || val == DBNull.Value) return "NULL";

            if (val is bool b)
            {
                return b ? "TRUE" : "FALSE";
            }

            if (val is byte[] bytes)
            {
                string hex = BitConverter.ToString(bytes).Replace("-", "");
                if (isSpatial)
                {
                    // PostGIS 空間幾何無損 Hex 傾印 (EWKB)
                    return "'\\x" + hex + "'::geometry";
                }
                return "'\\x" + hex + "'::bytea";
            }

            if (val is sbyte || val is byte || val is short || val is ushort ||
                val is int || val is uint || val is long || val is ulong)
            {
                return Convert.ToString(val, CultureInfo.InvariantCulture);
            }

            if (val is float || val is double || val is decimal)
            {
                return Convert.ToString(val, CultureInfo.InvariantCulture);
            }

            if (val is DateTime dt)
            {
                return "'" + dt.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture) + "'";
            }

            if (val is Guid g)
            {
                return "'" + g.ToString() + "'::uuid";
            }

            string str = val.ToString();

            // 若欄位為空間幾何且值以文字傳入 (如 WKT 或 Hex 字串)
            if (isSpatial)
            {
                string trimmed = str.Trim();
                if (trimmed.StartsWith("01", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("\\x", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("SRID=", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("POINT", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("LINESTRING", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("POLYGON", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("MULTIPOINT", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("MULTILINESTRING", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("MULTIPOLYGON", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("GEOMETRYCOLLECTION", StringComparison.OrdinalIgnoreCase))
                {
                    return "'" + str.Replace("'", "''") + "'::geometry";
                }
            }

            return "'" + str.Replace("'", "''") + "'";
        }
    }
}
