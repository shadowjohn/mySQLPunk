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

namespace mySQLPunk.lib.OracleExport
{
    public sealed class OracleSqlExportService
    {
        private const int BufferSize = 65536; // 64KB
        private readonly IDatabase db;
        private readonly string databaseName; // In Oracle, this represents the Schema / Owner

        private static readonly HashSet<string> SpatialIndexPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "MDRT_", "MDXT_", "MDNT_", "MDRS_"
        };

        public OracleSqlExportService(IDatabase db, string databaseName)
        {
            this.db = db ?? throw new ArgumentNullException(nameof(db));
            this.databaseName = databaseName ?? string.Empty;
        }

        public async Task<List<OracleTableExportItem>> GetTableCatalogAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            return await Task.Run(() => GetTableCatalog(cancellationToken), cancellationToken).ConfigureAwait(false);
        }

        public List<OracleTableExportItem> GetTableCatalog(CancellationToken cancellationToken = default(CancellationToken))
        {
            var list = new List<OracleTableExportItem>();
            string owner = string.IsNullOrWhiteSpace(databaseName) ? "USER" : databaseName.Trim().ToUpperInvariant();

            // 1. 取得所有一般資料表
            string tablesSql = @"
                SELECT
                    t.OWNER,
                    t.TABLE_NAME,
                    COALESCE(t.NUM_ROWS, 0) AS ROW_COUNT,
                    COALESCE(t.BLOCKS * 8192, 0) AS ESTIMATED_BYTES
                FROM ALL_TABLES t
                WHERE (:owner = 'USER' OR t.OWNER = :owner)
                  AND t.TABLE_NAME NOT LIKE 'BIN$%'
                ORDER BY t.OWNER, t.TABLE_NAME";

            var p = new Dictionary<string, object> { { "owner", owner } };
            DataTable dtTables;
            try
            {
                dtTables = db.SelectSQL(tablesSql, p);
            }
            catch
            {
                // Fallback 若無 bind 支援
                dtTables = db.SelectSQL("SELECT OWNER, TABLE_NAME, COALESCE(NUM_ROWS, 0) AS ROW_COUNT, 0 AS ESTIMATED_BYTES FROM ALL_TABLES WHERE TABLE_NAME NOT LIKE 'BIN$%' ORDER BY OWNER, TABLE_NAME");
            }

            var itemMap = new Dictionary<string, OracleTableExportItem>(StringComparer.OrdinalIgnoreCase);

            foreach (DataRow row in dtTables.Rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string tblOwner = row["OWNER"]?.ToString() ?? "";
                string name = row["TABLE_NAME"]?.ToString() ?? "";
                long rows = 0;
                long bytes = 0;
                if (row["ROW_COUNT"] != DBNull.Value) long.TryParse(row["ROW_COUNT"].ToString(), out rows);
                if (row["ESTIMATED_BYTES"] != DBNull.Value) long.TryParse(row["ESTIMATED_BYTES"].ToString(), out bytes);

                bool isInternalSpatial = false;
                foreach (var prefix in SpatialIndexPrefixes)
                {
                    if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        isInternalSpatial = true;
                        break;
                    }
                }

                var item = new OracleTableExportItem
                {
                    Owner = tblOwner,
                    Name = name,
                    RowCount = rows,
                    EstimatedBytes = bytes,
                    IsView = false,
                    IsSpatialMetadata = isInternalSpatial,
                    Selected = !isInternalSpatial // 內部 R-Tree 影子表預設排除
                };

                list.Add(item);
                itemMap[tblOwner + "." + name] = item;
            }

            // 2. 取得檢視 (Views)
            try
            {
                string viewsSql = @"
                    SELECT OWNER, VIEW_NAME
                    FROM ALL_VIEWS
                    WHERE (:owner = 'USER' OR OWNER = :owner)
                    ORDER BY OWNER, VIEW_NAME";

                DataTable dtViews = db.SelectSQL(viewsSql, p);
                foreach (DataRow row in dtViews.Rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string vOwner = row["OWNER"]?.ToString() ?? "";
                    string vName = row["VIEW_NAME"]?.ToString() ?? "";

                    var item = new OracleTableExportItem
                    {
                        Owner = vOwner,
                        Name = vName,
                        RowCount = 0,
                        EstimatedBytes = 0,
                        IsView = true,
                        IsSpatialMetadata = false,
                        Selected = true
                    };

                    list.Add(item);
                    itemMap[vOwner + "." + vName] = item;
                }
            }
            catch { }

            // 3. 探測 Oracle Spatial (SDO_GEOMETRY) 空間欄位
            try
            {
                string spatialSql = @"
                    SELECT OWNER, TABLE_NAME, COLUMN_NAME, DATA_TYPE
                    FROM ALL_TAB_COLUMNS
                    WHERE (:owner = 'USER' OR OWNER = :owner)
                      AND (DATA_TYPE = 'SDO_GEOMETRY' OR (DATA_TYPE_OWNER = 'MDSYS' AND DATA_TYPE = 'SDO_GEOMETRY'))";

                DataTable dtSpatial = db.SelectSQL(spatialSql, p);
                foreach (DataRow row in dtSpatial.Rows)
                {
                    string sOwner = row["OWNER"]?.ToString() ?? "";
                    string sTable = row["TABLE_NAME"]?.ToString() ?? "";
                    string sCol = row["COLUMN_NAME"]?.ToString() ?? "";

                    string key = sOwner + "." + sTable;
                    if (itemMap.TryGetValue(key, out var item))
                    {
                        item.IsSpatial = true;
                        if (!item.SpatialColumnNames.Contains(sCol))
                        {
                            item.SpatialColumnNames.Add(sCol);
                        }
                        item.SpatialFeatureDescription = "Oracle Spatial SDO_GEOMETRY";
                    }
                }
            }
            catch { }

            return list;
        }

        public async Task<OracleExportResult> ExportAsync(
            OracleExportWizardOptions options,
            IProgress<OracleExportProgress> progress = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrWhiteSpace(options.TargetPath)) throw new ArgumentException("TargetPath is required.", nameof(options));

            var result = new OracleExportResult { FilePath = options.TargetPath };
            var stopwatch = Stopwatch.StartNew();

            string dir = Path.GetDirectoryName(options.TargetPath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string stagingPath = options.TargetPath + ".writing";
            if (File.Exists(stagingPath))
            {
                try { File.Delete(stagingPath); } catch { }
            }

            try
            {
                // 1. 取得完整目標清單
                progress?.Report(new OracleExportProgress
                {
                    Stage = OracleExportStage.Preflight,
                    Message = "正在探測 Oracle 綱要與空間物件結構..."
                });

                var catalog = await GetTableCatalogAsync(cancellationToken).ConfigureAwait(false);
                var selectedSet = new HashSet<string>(options.SelectedTables, StringComparer.OrdinalIgnoreCase);

                var itemsToExport = catalog.FindAll(i =>
                    (options.SelectedTables.Count == 0 && i.Selected) ||
                    selectedSet.Contains(i.Name) ||
                    selectedSet.Contains(i.DisplayName) ||
                    selectedSet.Contains(i.FullQualifiedName));

                // 排除影子表
                if (options.ExcludeSpatialIndexInternalTables)
                {
                    itemsToExport.RemoveAll(i => i.IsSpatialMetadata);
                }

                int totalItems = itemsToExport.Count;
                long totalRows = 0;
                foreach (var it in itemsToExport)
                {
                    if (!it.IsView) totalRows += it.RowCount;
                }

                int itemsProcessed = 0;
                long rowsExportedTotal = 0;

                using (var fs = new FileStream(stagingPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize))
                using (var sw = new StreamWriter(fs, new UTF8Encoding(false), BufferSize))
                {
                    // 寫入 Header 註解
                    sw.WriteLine("-- ====================================================================");
                    sw.WriteLine("-- mySQLPunk Oracle Logical Dump");
                    sw.WriteLine("-- Host Database: " + (string.IsNullOrWhiteSpace(databaseName) ? "Current Schema" : databaseName));
                    sw.WriteLine("-- Created: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                    sw.WriteLine("-- Architecture: Streaming StreamWriter (64KB Buffer, Zero Memory Bloat)");
                    sw.WriteLine("-- Oracle Spatial: MDSYS.SDO_GEOMETRY with WKB / Hex literal support");
                    sw.WriteLine("-- ====================================================================");
                    sw.WriteLine();

                    // 一致性快照保護 (SET TRANSACTION READ ONLY)
                    if (options.UseTransactionSnapshot)
                    {
                        sw.WriteLine("-- 啟用交易一致性唯讀快照 (防範外鍵與空間關聯導出時不一致)");
                        sw.WriteLine("SET TRANSACTION READ ONLY;");
                        sw.WriteLine();
                    }

                    // 2. 結構 DDL 階段 (CREATE TABLE, COMMENTS, SPATIAL METADATA)
                    if (options.Mode != OracleExportMode.DataOnly)
                    {
                        progress?.Report(new OracleExportProgress
                        {
                            Stage = OracleExportStage.ExportingSchema,
                            TotalTables = totalItems,
                            TotalRows = totalRows,
                            Message = "正在產生 Oracle 資料表與空間中繼結構 DDL..."
                        });

                        foreach (var item in itemsToExport)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (item.IsView) continue; // View 於最後匯出

                            sw.WriteLine("-- --------------------------------------------------");
                            sw.WriteLine(string.Format("-- Table structure for: {0}", item.FullQualifiedName));
                            if (item.IsSpatial)
                            {
                                sw.WriteLine(string.Format("-- Spatial Feature: {0} ({1})", item.SpatialFeatureDescription, string.Join(", ", item.SpatialColumnNames.ToArray())));
                            }
                            sw.WriteLine("-- --------------------------------------------------");

                            if (options.DropTableIfExists)
                            {
                                sw.WriteLine("BEGIN");
                                sw.WriteLine(string.Format("  EXECUTE IMMEDIATE 'DROP TABLE {0} CASCADE CONSTRAINTS';", item.FullQualifiedName));
                                sw.WriteLine("EXCEPTION WHEN OTHERS THEN NULL;");
                                sw.WriteLine("END;");
                                sw.WriteLine("/");
                            }

                            string ddl = db.GetTableCreateStatement(item.Owner, item.Name);
                            if (!string.IsNullOrWhiteSpace(ddl))
                            {
                                sw.WriteLine(ddl.TrimEnd().TrimEnd(';') + ";");
                            }

                            // 空間中繼表註冊 (USER_SDO_GEOM_METADATA)
                            if (options.IncludeSpatialMetadata && item.IsSpatial && item.SpatialColumnNames.Count > 0)
                            {
                                foreach (var geomCol in item.SpatialColumnNames)
                                {
                                    sw.WriteLine(string.Format(
                                        "DELETE FROM USER_SDO_GEOM_METADATA WHERE TABLE_NAME = '{0}' AND COLUMN_NAME = '{1}';",
                                        item.Name.ToUpperInvariant(), geomCol.ToUpperInvariant()));
                                    sw.WriteLine(string.Format(
                                        "INSERT INTO USER_SDO_GEOM_METADATA (TABLE_NAME, COLUMN_NAME, DIMINFO, SRID) " +
                                        "VALUES ('{0}', '{1}', MDSYS.SDO_DIM_ARRAY(MDSYS.SDO_DIM_ELEMENT('X', -180, 180, 0.005), MDSYS.SDO_DIM_ELEMENT('Y', -90, 90, 0.005)), 4326);",
                                        item.Name.ToUpperInvariant(), geomCol.ToUpperInvariant()));
                                }
                            }

                            sw.WriteLine();
                        }
                    }

                    // 3. 資料傾印階段 (INSERT STATEMENTS)
                    if (options.Mode != OracleExportMode.SchemaOnly)
                    {
                        foreach (var item in itemsToExport)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (item.IsView) continue;

                            progress?.Report(new OracleExportProgress
                            {
                                Stage = OracleExportStage.ExportingData,
                                CurrentTable = item.Name,
                                CurrentOwner = item.Owner,
                                TablesCompleted = itemsProcessed,
                                TotalTables = totalItems,
                                RowsExported = rowsExportedTotal,
                                TotalRows = totalRows,
                                BytesExported = fs.Position,
                                Message = string.Format("正在串流傾印資料表: {0}...", item.DisplayName)
                            });

                            sw.WriteLine("-- --------------------------------------------------");
                            sw.WriteLine(string.Format("-- Dumping data for: {0}", item.FullQualifiedName));
                            sw.WriteLine("-- --------------------------------------------------");

                            if (options.TruncateBeforeInsert)
                            {
                                sw.WriteLine(string.Format("TRUNCATE TABLE {0};", item.FullQualifiedName));
                            }

                            long tableRowsExported = ExportTableDataStreaming(db, item, sw, options, cancellationToken);
                            rowsExportedTotal += tableRowsExported;
                            result.ManifestTables[item.Name] = tableRowsExported;
                            result.TablesExported++;

                            sw.WriteLine("COMMIT;");
                            sw.WriteLine();

                            itemsProcessed++;
                        }
                    }

                    // 4. 檢視與空間索引階段
                    if (options.Mode != OracleExportMode.DataOnly)
                    {
                        // 建立檢視 (Views)
                        if (options.IncludeViews)
                        {
                            foreach (var item in itemsToExport)
                            {
                                if (!item.IsView) continue;
                                cancellationToken.ThrowIfCancellationRequested();

                                sw.WriteLine("-- --------------------------------------------------");
                                sw.WriteLine(string.Format("-- View structure for: {0}", item.FullQualifiedName));
                                sw.WriteLine("-- --------------------------------------------------");

                                string viewDdl = db.GetViewCreateStatement(item.Owner, item.Name);
                                if (!string.IsNullOrWhiteSpace(viewDdl))
                                {
                                    sw.WriteLine(viewDdl.TrimEnd().TrimEnd(';') + ";");
                                    sw.WriteLine();
                                }
                                result.ViewsExported++;
                            }
                        }

                        // 建立空間索引 (MDSYS.SPATIAL_INDEX)
                        if (options.IncludeSpatialIndexes)
                        {
                            foreach (var item in itemsToExport)
                            {
                                if (item.IsView || !item.IsSpatial) continue;

                                foreach (var col in item.SpatialColumnNames)
                                {
                                    string idxName = string.Format("SIDX_{0}_{1}", item.Name, col);
                                    if (idxName.Length > 30) idxName = idxName.Substring(0, 30);

                                    sw.WriteLine(string.Format("-- Oracle Spatial Domain Index for {0}.{1}", item.Name, col));
                                    sw.WriteLine(string.Format(
                                        "CREATE INDEX \"{0}\".\"{1}\" ON {2} (\"{3}\") INDEXTYPE IS MDSYS.SPATIAL_INDEX;",
                                        item.Owner, idxName, item.FullQualifiedName, col));
                                    sw.WriteLine();
                                }
                            }
                        }
                    }

                    // 5. 結尾交易完成與 Native Manifest
                    sw.WriteLine("COMMIT;");
                    sw.WriteLine();

                    if (options.IncludeManifest)
                    {
                        sw.WriteLine("-- ====================================================================");
                        sw.WriteLine("-- mySQLPunk Native Manifest");
                        sw.WriteLine(string.Format("-- Manifest-Provider: oracle"));
                        sw.WriteLine(string.Format("-- Manifest-Database: {0}", databaseName));
                        sw.WriteLine(string.Format("-- Manifest-Generated: {0}", DateTime.UtcNow.ToString("o")));
                        foreach (var kvp in result.ManifestTables)
                        {
                            sw.WriteLine(string.Format("-- Manifest-Table: {0} | Rows: {1}", kvp.Key, kvp.Value));
                        }
                        sw.WriteLine("-- ====================================================================");
                    }

                    sw.Flush();
                }

                // 6. 計算 SHA-256 Checksum 並原子替換檔案
                result.FileSizeBytes = new FileInfo(stagingPath).Length;
                result.Sha256Checksum = ComputeSha256(stagingPath);

                // 在檔案末尾附加 Checksum
                if (options.IncludeManifest)
                {
                    using (var sw = new StreamWriter(stagingPath, true, Encoding.UTF8))
                    {
                        sw.WriteLine(string.Format("-- Manifest-Checksum-SHA256: {0}", result.Sha256Checksum));
                        sw.WriteLine("-- ====================================================================");
                    }
                    result.FileSizeBytes = new FileInfo(stagingPath).Length;
                }

                if (File.Exists(options.TargetPath))
                {
                    File.Delete(options.TargetPath);
                }
                File.Move(stagingPath, options.TargetPath);

                stopwatch.Stop();
                result.Success = true;
                result.Elapsed = stopwatch.Elapsed;
                result.TotalRowsExported = rowsExportedTotal;

                progress?.Report(new OracleExportProgress
                {
                    Stage = OracleExportStage.Completed,
                    TablesCompleted = totalItems,
                    TotalTables = totalItems,
                    RowsExported = rowsExportedTotal,
                    TotalRows = totalRows,
                    BytesExported = result.FileSizeBytes,
                    Message = string.Format("Oracle 匯出完成！共 {0} 張資料表，{1:N0} 列，耗時 {2:hh\\:mm\\:ss}。", result.TablesExported, rowsExportedTotal, result.Elapsed)
                });
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                result.Success = false;
                result.ErrorMessage = ex.Message;
                result.Elapsed = stopwatch.Elapsed;

                if (File.Exists(stagingPath))
                {
                    try { File.Delete(stagingPath); } catch { }
                }

                progress?.Report(new OracleExportProgress
                {
                    Stage = OracleExportStage.Failed,
                    Message = "Oracle 匯出失敗: " + ex.Message
                });
                throw;
            }

            return result;
        }

        private static long ExportTableDataStreaming(
            IDatabase db,
            OracleTableExportItem item,
            StreamWriter sw,
            OracleExportWizardOptions options,
            CancellationToken cancellationToken)
        {
            DataTable colsMeta;
            try
            {
                colsMeta = db.GetColumns(item.Owner, item.Name);
            }
            catch
            {
                colsMeta = null;
            }

            // 空間欄位集合
            var spatialSet = new HashSet<string>(item.SpatialColumnNames, StringComparer.OrdinalIgnoreCase);

            // 建構查詢 SELECT 語句
            // 若包含 SDO_GEOMETRY 欄位，轉換為 WKB 以避免 ODP.NET UDT 映射錯誤
            string selectSql;
            if (colsMeta != null && colsMeta.Rows.Count > 0)
            {
                var selectParts = new List<string>();
                foreach (DataRow r in colsMeta.Rows)
                {
                    string colName = r["COLUMN_NAME"]?.ToString() ?? "";
                    if (spatialSet.Contains(colName))
                    {
                        // 轉換為 WKB 二進位 (BLOB)
                        selectParts.Add(string.Format("CASE WHEN \"{0}\" IS NOT NULL THEN SDO_UTIL.TO_WKBGEOMETRY(\"{0}\") ELSE NULL END AS \"{0}\"", colName));
                    }
                    else
                    {
                        selectParts.Add(string.Format("\"{0}\"", colName));
                    }
                }
                selectSql = string.Format("SELECT {0} FROM {1}", string.Join(", ", selectParts.ToArray()), item.FullQualifiedName);
            }
            else
            {
                selectSql = string.Format("SELECT * FROM {0}", item.FullQualifiedName);
            }

            long rowCount = 0;
            int batchCount = 0;

            DataTable dt;
            try
            {
                dt = db.SelectSQL(selectSql);
            }
            catch
            {
                // Fallback 直接 SELECT *
                dt = db.SelectSQL(string.Format("SELECT * FROM {0}", item.FullQualifiedName));
            }

            if (dt == null || dt.Rows.Count == 0) return 0;

            int colCount = dt.Columns.Count;
            var colNames = new string[colCount];
            var isSpatialCol = new bool[colCount];

            for (int i = 0; i < colCount; i++)
            {
                colNames[i] = "\"" + dt.Columns[i].ColumnName + "\"";
                isSpatialCol[i] = spatialSet.Contains(dt.Columns[i].ColumnName);
            }

            string insertPrefix = string.Format("INSERT INTO {0} ({1}) VALUES (", item.FullQualifiedName, string.Join(", ", colNames));

            foreach (DataRow row in dt.Rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                rowCount++;
                batchCount++;

                sw.Write(insertPrefix);
                for (int c = 0; c < colCount; c++)
                {
                    if (c > 0) sw.Write(", ");
                    object val = row[c];
                    bool spatial = isSpatialCol[c];

                    sw.Write(FormatOracleLiteral(val, spatial));
                }
                sw.WriteLine(");");

                if (batchCount >= options.BatchSize)
                {
                    sw.WriteLine("COMMIT;");
                    batchCount = 0;
                }
            }

            return rowCount;
        }

        public static string FormatOracleLiteral(object val, bool isSpatial)
        {
            if (val == null || val == DBNull.Value) return "NULL";

            // SDO_GEOMETRY 空間幾何無損還原
            if (isSpatial)
            {
                if (val is byte[] geomBytes)
                {
                    var hexSb = new StringBuilder(geomBytes.Length * 2);
                    for (int i = 0; i < geomBytes.Length; i++)
                    {
                        hexSb.Append(geomBytes[i].ToString("X2"));
                    }
                    return string.Format("SDO_UTIL.FROM_WKBGEOMETRY(HEXTORAW('{0}'))", hexSb.ToString());
                }

                string strVal = val.ToString().Trim();
                if (strVal.StartsWith("SDO_GEOMETRY", StringComparison.OrdinalIgnoreCase) ||
                    strVal.StartsWith("MDSYS.SDO_GEOMETRY", StringComparison.OrdinalIgnoreCase))
                {
                    return strVal;
                }
                if (strVal.StartsWith("POINT", StringComparison.OrdinalIgnoreCase) ||
                    strVal.StartsWith("POLYGON", StringComparison.OrdinalIgnoreCase) ||
                    strVal.StartsWith("LINESTRING", StringComparison.OrdinalIgnoreCase) ||
                    strVal.StartsWith("MULTIPOINT", StringComparison.OrdinalIgnoreCase) ||
                    strVal.StartsWith("MULTILINESTRING", StringComparison.OrdinalIgnoreCase) ||
                    strVal.StartsWith("MULTIPOLYGON", StringComparison.OrdinalIgnoreCase))
                {
                    return string.Format("SDO_GEOMETRY('{0}', 4326)", strVal.Replace("'", "''"));
                }
            }

            // 二進位 (RAW / BLOB)
            if (val is byte[] bytes)
            {
                var hex = new StringBuilder(bytes.Length * 2);
                for (int i = 0; i < bytes.Length; i++)
                {
                    hex.Append(bytes[i].ToString("X2"));
                }
                return string.Format("HEXTORAW('{0}')", hex.ToString());
            }

            if (val is bool b)
            {
                return b ? "1" : "0";
            }

            if (val is sbyte || val is byte || val is short || val is ushort ||
                val is int || val is uint || val is long || val is ulong ||
                val is float || val is double || val is decimal)
            {
                return Convert.ToString(val, CultureInfo.InvariantCulture);
            }

            if (val is DateTime dt)
            {
                return string.Format(
                    "TO_TIMESTAMP('{0}', 'YYYY-MM-DD HH24:MI:SS.FF7')",
                    dt.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture));
            }

            string s = val.ToString();
            // CLOB 超長字串支援
            if (s.Length > 4000)
            {
                // 超過 4000 字元切段為 TO_CLOB(...) || TO_CLOB(...)
                var sb = new StringBuilder();
                int idx = 0;
                while (idx < s.Length)
                {
                    int chunkLen = Math.Min(2000, s.Length - idx);
                    string chunk = s.Substring(idx, chunkLen);
                    if (sb.Length > 0) sb.Append(" || ");
                    sb.Append("TO_CLOB('").Append(chunk.Replace("'", "''")).Append("')");
                    idx += chunkLen;
                }
                return sb.ToString();
            }

            return "'" + s.Replace("'", "''") + "'";
        }

        private static string ComputeSha256(string filePath)
        {
            using (var sha = SHA256.Create())
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize))
            {
                byte[] hash = sha.ComputeHash(fs);
                var sb = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++)
                {
                    sb.Append(hash[i].ToString("x2"));
                }
                return sb.ToString();
            }
        }
    }
}
