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
using Oracle.ManagedDataAccess.Client;
using mySQLPunk.lib.OracleImport;

namespace mySQLPunk.lib.OracleExport
{
    // Optional recording-provider contract; production uses native SequentialAccess.
    public interface IOracleExportReaderSource
    {
        IDataReader OpenExportReader(string sql, CancellationToken cancellationToken);
    }

    public sealed class OracleSqlExportService
    {
        private const int BufferSize = 65536; // 64KB
        private readonly IDatabase db;
        private OracleTransaction sourceTransaction;
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
            string owner = string.IsNullOrWhiteSpace(databaseName) ? "USER" : databaseName.Trim();

            // 1. 取得所有一般資料表
            string tablesSql = @"
                SELECT
                    t.OWNER,
                    t.TABLE_NAME,
                    COALESCE(t.NUM_ROWS, 0) AS ROW_COUNT,
                    COALESCE(t.BLOCKS * 8192, 0) AS ESTIMATED_BYTES
                FROM ALL_TABLES t
                WHERE t.OWNER = :owner
                  AND t.TABLE_NAME NOT LIKE 'BIN$%'
                ORDER BY t.OWNER, t.TABLE_NAME";

            var p = new Dictionary<string, object> { { "owner", owner } };
            DataTable dtTables = QueryMetadata(tablesSql, p, cancellationToken);

            var itemMap = new Dictionary<string, OracleTableExportItem>(StringComparer.OrdinalIgnoreCase);

            foreach (DataRow row in dtTables.Rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string tblOwner = row["OWNER"]?.ToString() ?? "";
                string name = row["TABLE_NAME"]?.ToString() ?? "";
                if (!string.Equals(tblOwner, owner, StringComparison.Ordinal)) continue;
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
                    WHERE OWNER = :owner
                    ORDER BY OWNER, VIEW_NAME";

                DataTable dtViews = QueryMetadata(viewsSql, p, cancellationToken);
                foreach (DataRow row in dtViews.Rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string vOwner = row["OWNER"]?.ToString() ?? "";
                    string vName = row["VIEW_NAME"]?.ToString() ?? "";
                    if (!string.Equals(vOwner, owner, StringComparison.Ordinal)) continue;

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
            catch { throw; }

            // 3. 探測 Oracle Spatial (SDO_GEOMETRY) 空間欄位
            try
            {
                string spatialSql = @"
                    SELECT OWNER, TABLE_NAME, COLUMN_NAME, DATA_TYPE
                    FROM ALL_TAB_COLUMNS
                    WHERE OWNER = :owner
                      AND (DATA_TYPE = 'SDO_GEOMETRY' OR (DATA_TYPE_OWNER = 'MDSYS' AND DATA_TYPE = 'SDO_GEOMETRY'))";

                DataTable dtSpatial = QueryMetadata(spatialSql, p, cancellationToken);
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
            catch { throw; }

            return list;
        }

        // STANDARD SQL bounds until a verified streaming LOB codec exists.
        public const int MaxBinaryLiteralBytes = 2000;
        public const int MaxTextLiteralBytes = 4000;
        public const int MaxRowValueBytes = 8 * 1024 * 1024;
        public const int MaxRowSqlBytes = 28 * 1024 * 1024;
        private const int MaxMetadataChars = 1024 * 1024;

        public async Task<OracleExportResult> ExportAsync(OracleExportWizardOptions options, IProgress<OracleExportProgress> progress = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrWhiteSpace(options.TargetPath)) throw new ArgumentException("TargetPath is required.");
            if (string.IsNullOrWhiteSpace(databaseName) || databaseName == "USER") throw new InvalidOperationException("An explicit Oracle owner is required.");
            if (options.BatchSize <= 0) throw new ArgumentException("BatchSize must be positive.");
            var result = new OracleExportResult { FilePath = options.TargetPath };
            var clock = Stopwatch.StartNew();
            string directory = Path.GetDirectoryName(options.TargetPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            string stagingPath = options.TargetPath + "." + Guid.NewGuid().ToString("N") + ".writing";
            bool publicationAttempted = false, snapshotActive = false;
            try
            {
                if (options.UseTransactionSnapshot)
                {
                    var native = db as my_oracle;
                    if (native != null)
                    {
                        var session = QueryMetadata("SELECT SYS_CONTEXT('USERENV','SESSION_USER') FROM DUAL", null, cancellationToken);
                        if (session == null || session.Rows.Count != 1 || Convert.ToString(session.Rows[0][0]) == "SYS")
                            throw new NotSupportedException("Oracle SYS sessions do not provide READ ONLY snapshot guarantees; use a dedicated non-SYS source session.");
                        sourceTransaction = native.MCT.BeginTransaction();
                    }
                    snapshotActive = true;
                    ExecuteSourceChecked("SET TRANSACTION READ ONLY", cancellationToken);
                }
                if (db is my_oracle)
                {
                    var charset = QueryMetadata("SELECT VALUE FROM NLS_DATABASE_PARAMETERS WHERE PARAMETER='NLS_CHARACTERSET'", null, cancellationToken);
                    if (charset == null || charset.Rows.Count != 1 || Convert.ToString(charset.Rows[0][0]) != "AL32UTF8")
                        throw new NotSupportedException("The bounded text codec is verified structurally for AL32UTF8 source databases only.");
                }
                var catalog = await GetTableCatalogAsync(cancellationToken).ConfigureAwait(false);
                var selection = new HashSet<string>(StringComparer.Ordinal);
                foreach (string name in options.SelectedTables) selection.Add(OracleImportRunner.ResolveManifestTarget(name, databaseName));
                var items = catalog.FindAll(item => (selection.Count == 0 ? item.Selected : selection.Contains(item.FullQualifiedName)) &&
                    (!options.ExcludeSpatialIndexInternalTables || !item.IsSpatialMetadata) && (!item.IsView || (options.IncludeViews && options.Mode != OracleExportMode.DataOnly)));
                if (items.Count == 0) throw new InvalidOperationException("No Oracle objects selected.");
                items = OrderSchemaDependencies(items, options.Mode, cancellationToken);
                var columns = new Dictionary<string, List<ExportColumn>>(StringComparer.Ordinal);
                foreach (var item in items)
                    if (!item.IsView && options.Mode != OracleExportMode.SchemaOnly) columns[item.FullQualifiedName] = GetWritableColumns(item, cancellationToken);
                long totalRows = 0;
                foreach (var item in items) if (!item.IsView) totalRows += item.RowCount;
                var deferred = new List<string>();
                using (var stream = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, BufferSize))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false), BufferSize))
                {
                    writer.WriteLine("-- mySQLPunk Oracle Logical Dump");
                    writer.WriteLine("-- Owner: " + databaseName);
                    writer.WriteLine("-- Bounded v1: literal chunks <= 2000 binary / 4000 UTF-8 text bytes; total row values <= 8 MiB; block SQL <= 28 MiB.");
                    writer.WriteLine("-- Native source: AL32UTF8, non-SYS snapshot sessions. Identity data, NCLOB, LRS/curves/compound geometry, unsupported types and over-budget rows fail before publication.");
                    writer.WriteLine("-- Oracle DDL implicitly commits. Spatial metadata restore requires SESSION_USER equal to the dump owner.");
                    // READ ONLY belongs exclusively to the source session and is never restore SQL.
                    foreach (var item in items)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (item.IsView) continue;
                        if (options.Mode != OracleExportMode.DataOnly)
                        {
                            progress?.Report(new OracleExportProgress { Stage = OracleExportStage.ExportingSchema, CurrentTable = item.Name, Message = "Reading native DDL: " + item.DisplayName });
                            if (options.DropTableIfExists)
                            {
                                writer.WriteLine("BEGIN EXECUTE IMMEDIATE 'DROP TABLE " + item.FullQualifiedName.Replace("'", "''") + " CASCADE CONSTRAINTS'; EXCEPTION WHEN OTHERS THEN IF SQLCODE != -942 THEN RAISE; END IF; END;");
                                writer.WriteLine("/");
                            }
                            WriteTableDdl(item, options, writer, deferred, cancellationToken);
                            if (item.IsSpatial && options.IncludeSpatialMetadata) WriteSpatialMetadata(item, writer, cancellationToken);
                        }
                        if (options.Mode != OracleExportMode.SchemaOnly)
                        {
                            if (options.TruncateBeforeInsert) writer.WriteLine("TRUNCATE TABLE " + item.FullQualifiedName + ";");
                            long rows = ExportTableDataStreaming(item, columns[item.FullQualifiedName], writer, options, cancellationToken,
                                rowCount => progress?.Report(new OracleExportProgress
                                {
                                    Stage = OracleExportStage.ExportingData, CurrentOwner = item.Owner, CurrentTable = item.Name,
                                    TotalTables = items.Count, TablesCompleted = result.TablesExported,
                                    TotalRows = totalRows, RowsExported = result.TotalRowsExported + rowCount, BytesExported = stream.Position,
                                    Message = "Streaming " + item.DisplayName + ": " + rowCount + " rows"
                                }));
                            result.TotalRowsExported += rows;
                            result.ManifestTables[item.FullQualifiedName] = rows;
                            writer.WriteLine("COMMIT;");
                        }
                        result.TablesExported++;
                    }
                    if (options.Mode != OracleExportMode.DataOnly)
                    {
                        foreach (string ddl in deferred) WriteStatement(writer, ddl);
                        foreach (var item in items)
                        {
                            if (!item.IsView) continue;
                            cancellationToken.ThrowIfCancellationRequested();
                            string ddl = db is my_oracle ? GetNativeDdl("VIEW", item.Owner, item.Name, cancellationToken) : db.GetViewCreateStatement(item.Owner, item.Name);
                            if (string.IsNullOrWhiteSpace(ddl)) throw new InvalidOperationException("View DDL unavailable: " + item.DisplayName);
                            WriteStatement(writer, ddl);
                            result.ViewsExported++;
                        }
                    }
                    writer.WriteLine("COMMIT;");
                    if (options.IncludeManifest)
                    {
                        writer.WriteLine("-- mySQLPunk Native Manifest");
                        writer.WriteLine("-- Manifest-Provider: oracle");
                        writer.WriteLine("-- Manifest-Database: " + databaseName);
                        foreach (var pair in result.ManifestTables) writer.WriteLine("-- Manifest-Table: " + pair.Key + " | Rows: " + pair.Value);
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (snapshotActive) { EndSnapshot(); snapshotActive = false; }
                result.Sha256Checksum = ComputeSha256(stagingPath);
                if (options.IncludeManifest)
                    using (var writer = new StreamWriter(stagingPath, true, new UTF8Encoding(false))) writer.WriteLine("-- Manifest-Checksum-SHA256: " + result.Sha256Checksum);
                result.FileSizeBytes = new FileInfo(stagingPath).Length;
                cancellationToken.ThrowIfCancellationRequested();
                publicationAttempted = true;
                SqlExportPublication.Publish(stagingPath, options.TargetPath);
                result.Success = true;
                result.Elapsed = clock.Elapsed;
                progress?.Report(new OracleExportProgress { Stage = OracleExportStage.Completed, TablesCompleted = items.Count, TotalTables = items.Count, RowsExported = result.TotalRowsExported, BytesExported = result.FileSizeBytes, Message = "Oracle export completed." });
                return result;
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
                progress?.Report(new OracleExportProgress { Stage = ex is OperationCanceledException ? OracleExportStage.Cancelled : OracleExportStage.Failed, Message = "Oracle export failed: " + ex.Message });
                throw;
            }
            finally
            {
                try { if (snapshotActive) EndSnapshot(); }
                finally
                {
                    sourceTransaction?.Dispose(); sourceTransaction = null;
                    if (!publicationAttempted && File.Exists(stagingPath)) File.Delete(stagingPath);
                }
            }
        }

        private List<OracleTableExportItem> OrderSchemaDependencies(List<OracleTableExportItem> items, OracleExportMode mode, CancellationToken cancellationToken)
        {
            var selected = new Dictionary<string, OracleTableExportItem>(StringComparer.Ordinal);
            var dependencies = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var item in items)
                if (!item.IsView) { selected[item.Name] = item; dependencies[item.Name] = new HashSet<string>(StringComparer.Ordinal); }
            var metadata = QueryMetadata("SELECT c.TABLE_NAME AS CHILD_TABLE, p.OWNER AS PARENT_OWNER, p.TABLE_NAME AS PARENT_TABLE FROM ALL_CONSTRAINTS c JOIN ALL_CONSTRAINTS p ON p.OWNER=c.R_OWNER AND p.CONSTRAINT_NAME=c.R_CONSTRAINT_NAME WHERE c.OWNER=:owner AND c.CONSTRAINT_TYPE='R'" + (mode == OracleExportMode.DataOnly ? " AND c.STATUS='ENABLED'" : ""), new Dictionary<string, object> { { "owner", databaseName } }, cancellationToken);
            foreach (DataRow row in metadata.Rows)
            {
                string child = MetadataValue(row, "CHILD_TABLE"), parent = MetadataValue(row, "PARENT_TABLE"), owner = MetadataValue(row, "PARENT_OWNER");
                if (!selected.ContainsKey(child)) continue;
                if (owner != databaseName) throw new NotSupportedException("Cross-owner FK dependency cannot be restored under this owner's enforced scope: " + child + " -> " + owner + "." + parent);
                if (!selected.ContainsKey(parent)) throw new NotSupportedException("Selected table has an unselected FK parent. Include all required parent tables: " + child + " -> " + parent);
                dependencies[child].Add(parent);
            }
            if (mode != OracleExportMode.DataOnly && db is my_oracle)
            {
                foreach (var item in selected.Values)
                {
                    var references = QueryMetadata("SELECT REFERENCED_OWNER, REFERENCED_NAME, REFERENCED_TYPE FROM ALL_DEPENDENCIES WHERE OWNER=:owner AND NAME=:tableName AND TYPE='TABLE' AND REFERENCED_TYPE IN ('SEQUENCE','FUNCTION','PROCEDURE','PACKAGE','TYPE')", new Dictionary<string, object> { { "owner", item.Owner }, { "tableName", item.Name } }, cancellationToken);
                    foreach (DataRow reference in references.Rows)
                    {
                        string owner = MetadataValue(reference, "REFERENCED_OWNER");
                        if (owner == "SYS" || owner == "MDSYS") continue;
                        throw new NotSupportedException("External table dependency is not bundled by this wizard: " + item.DisplayName + " -> " + owner + "." + MetadataValue(reference, "REFERENCED_NAME") + " (" + MetadataValue(reference, "REFERENCED_TYPE") + ").");
                    }
                }
            }
            var ordered = new List<OracleTableExportItem>();
            var emitted = new HashSet<string>(StringComparer.Ordinal);
            while (emitted.Count < selected.Count)
            {
                bool progress = false;
                foreach (var item in items)
                {
                    if (item.IsView || emitted.Contains(item.Name)) continue;
                    bool ready = true;
                    foreach (string parent in dependencies[item.Name]) if (!emitted.Contains(parent)) { ready = false; break; }
                    if (!ready) continue;
                    emitted.Add(item.Name); ordered.Add(item); progress = true;
                }
                if (!progress) throw new NotSupportedException("Selected cyclic/self-referencing foreign keys require a deferred-constraint restoration phase; this export is rejected before publication.");
            }
            foreach (var item in items) if (item.IsView) ordered.Add(item);
            return ordered;
        }

        private DataTable QueryMetadata(string sql, Dictionary<string, object> parameters, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var native = db as my_oracle;
            if (native == null) return db.SelectSQL(sql, parameters);
            using (var command = new OracleCommand(sql, native.MCT))
            {
                command.BindByName = true;
                command.Transaction = sourceTransaction;
                if (parameters != null) foreach (var parameter in parameters) command.Parameters.Add(new OracleParameter(parameter.Key, parameter.Value ?? DBNull.Value));
                using (cancellationToken.Register(() => { try { command.Cancel(); } catch { } }))
                using (var reader = command.ExecuteReader(CommandBehavior.SequentialAccess))
                {
                    // Dictionary/catalog metadata is bounded by the selected owner; table data uses WriteRows exclusively.
                    var table = new DataTable(); table.Load(reader); return table;
                }
            }
        }

        private void ExecuteSourceChecked(string sql, CancellationToken cancellationToken)
        {
            var native = db as my_oracle;
            if (native != null)
            {
                using (var command = new OracleCommand(sql, native.MCT))
                {
                    command.Transaction = sourceTransaction;
                    using (cancellationToken.Register(() => { try { command.Cancel(); } catch { } })) { cancellationToken.ThrowIfCancellationRequested(); command.ExecuteNonQuery(); }
                }
                return;
            }
            var response = db.ExecSQL(sql);
            if (response == null || !response.ContainsKey("status") || !string.Equals(response["status"], "OK", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(DatabaseExecutionResultService.GetFailureReason(response));
        }
        private void EndSnapshot()
        {
            if (sourceTransaction != null) { sourceTransaction.Rollback(); sourceTransaction.Dispose(); sourceTransaction = null; }
            else ExecuteSourceChecked("ROLLBACK", CancellationToken.None);
        }

        private sealed class ExportColumn
        {
            public string Name, DataType;
            public bool Spatial;
        }

        private List<ExportColumn> GetWritableColumns(OracleTableExportItem item, CancellationToken cancellationToken)
        {
            DataTable metadata;
            if (db is my_oracle)
            {
                var parameters = new Dictionary<string, object> { { "owner", item.Owner }, { "tableName", item.Name } };
                try { metadata = QueryMetadata("SELECT COLUMN_NAME, DATA_TYPE, VIRTUAL_COLUMN, HIDDEN_COLUMN, IDENTITY_COLUMN FROM ALL_TAB_COLS WHERE OWNER=:owner AND TABLE_NAME=:tableName ORDER BY COLUMN_ID", parameters, cancellationToken); }
                catch (OracleException ex)
                {
                    if (ex.Number != 904) throw;
                    metadata = QueryMetadata("SELECT COLUMN_NAME, DATA_TYPE, VIRTUAL_COLUMN, HIDDEN_COLUMN FROM ALL_TAB_COLS WHERE OWNER=:owner AND TABLE_NAME=:tableName ORDER BY COLUMN_ID", parameters, cancellationToken);
                }
            }
            else metadata = db.GetColumns(item.Owner, item.Name);
            if (metadata == null || metadata.Rows.Count == 0) throw new InvalidOperationException("Column metadata unavailable: " + item.DisplayName + ". SELECT * fallback is disabled.");
            var columns = new List<ExportColumn>();
            foreach (DataRow row in metadata.Rows)
            {
                string name = MetadataValue(row, "COLUMN_NAME");
                string type = MetadataValue(row, "DATA_TYPE").ToUpperInvariant();
                if (MetadataValue(row, "VIRTUAL_COLUMN").Equals("YES", StringComparison.OrdinalIgnoreCase) || MetadataValue(row, "HIDDEN_COLUMN").Equals("YES", StringComparison.OrdinalIgnoreCase)) continue;
                var timestamp = System.Text.RegularExpressions.Regex.Match(type, @"^TIMESTAMP\((\d+)\)$");
                if (timestamp.Success)
                {
                    if (int.Parse(timestamp.Groups[1].Value, CultureInfo.InvariantCulture) > 7) throw new NotSupportedException("Oracle timestamp precision above .NET ticks requires a native timestamp codec.");
                    type = "TIMESTAMP";
                }
                if (MetadataValue(row, "IDENTITY_COLUMN").Equals("YES", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(MetadataValue(row, "IDENTITY_GENERATION")))
                    throw new NotSupportedException("Identity data export requires a verified identity sequence-state restore policy: " + item.DisplayName + "." + name + ". SchemaOnly remains available.");
                bool spatial = item.SpatialColumnNames.Contains(name) || type == "SDO_GEOMETRY";
                if (!spatial && Array.IndexOf(new[] { "NUMBER", "FLOAT", "BINARY_FLOAT", "BINARY_DOUBLE", "VARCHAR2", "NVARCHAR2", "CHAR", "NCHAR", "CLOB", "RAW", "BLOB", "DATE", "TIMESTAMP" }, type) < 0)
                    throw new NotSupportedException("Unsupported Oracle data type (no implicit string conversion): " + item.DisplayName + "." + name + " " + type);
                columns.Add(new ExportColumn { Name = name, DataType = type, Spatial = spatial });
            }
            if (columns.Count == 0) throw new NotSupportedException("No writable Oracle columns: " + item.DisplayName);
            return columns;
        }
        private static string MetadataValue(DataRow row, string name) => row.Table.Columns.Contains(name) && row[name] != DBNull.Value ? Convert.ToString(row[name], CultureInfo.InvariantCulture) : "";

        private long ExportTableDataStreaming(OracleTableExportItem item, List<ExportColumn> columns, StreamWriter writer, OracleExportWizardOptions options, CancellationToken cancellationToken, Action<long> onRow)
        {
            var projection = new List<string>();
            var names = new List<string>();
            foreach (var column in columns)
            {
                string quoted = "t." + OracleImportRunner.QuoteIdentifier(column.Name);
                if (column.Spatial && db is my_oracle) ValidateNativeGeometry(item, column, cancellationToken);
                names.Add(OracleImportRunner.QuoteIdentifier(column.Name));
                if (column.Spatial)
                {
                    projection.Add("CASE WHEN " + quoted + " IS NOT NULL THEN SDO_UTIL.TO_WKBGEOMETRY(" + quoted + ") ELSE NULL END");
                    projection.Add(quoted + ".SDO_SRID");
                    projection.Add(quoted + ".SDO_GTYPE");
                }
                else projection.Add(quoted);
            }
            string sql = "SELECT " + string.Join(", ", projection) + " FROM " + item.FullQualifiedName + " t";
            string prefix = "INSERT INTO " + item.FullQualifiedName + " (" + string.Join(", ", names) + ") VALUES (";
            var native = db as my_oracle;
            if (native != null)
            {
                using (var command = new OracleCommand(sql, native.MCT))
                {
                    command.Transaction = sourceTransaction;
                    using (cancellationToken.Register(() => { try { command.Cancel(); } catch { } }))
                    using (var reader = command.ExecuteReader(CommandBehavior.SequentialAccess))
                        return WriteRows(reader, columns, writer, prefix, options.BatchSize, cancellationToken, onRow);
                }
            }
            var source = db as IOracleExportReaderSource;
            if (source == null) throw new NotSupportedException("Oracle export requires a forward-only reader source; full-table DataTable fallback is disabled.");
            using (var reader = source.OpenExportReader(sql, cancellationToken))
                return WriteRows(reader, columns, writer, prefix, options.BatchSize, cancellationToken, onRow);
        }

        private void ValidateNativeGeometry(OracleTableExportItem item, ExportColumn column, CancellationToken cancellationToken)
        {
            string geometry = "t." + OracleImportRunner.QuoteIdentifier(column.Name);
            string sql = "SELECT COUNT(*) FROM " + item.FullQualifiedName + " t WHERE " + geometry + " IS NOT NULL AND (" +
                "MOD(TRUNC(" + geometry + ".SDO_GTYPE/100),10) != 0 OR TRUNC(" + geometry + ".SDO_GTYPE/1000) NOT IN (2,3) OR " +
                "MOD(" + geometry + ".SDO_GTYPE,1000) NOT BETWEEN 1 AND 7 OR EXISTS (SELECT 1 FROM " +
                "(SELECT COLUMN_VALUE AS V, ROWNUM AS N FROM TABLE(" + geometry + ".SDO_ELEM_INFO)) e " +
                "WHERE (MOD(e.N,3)=2 AND e.V NOT IN (1,2,1003,2003)) OR (MOD(e.N,3)=0 AND e.V != 1)))";
            var native = (my_oracle)db;
            using (var command = new OracleCommand(sql, native.MCT))
            {
                command.Transaction = sourceTransaction;
                using (cancellationToken.Register(() => { try { command.Cancel(); } catch { } }))
                    if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                        throw new NotSupportedException("Curved, compound, non-linear interpretation, LRS or unsupported geometry is not representable by this bounded WKB codec: " + item.DisplayName + "." + column.Name);
            }
        }

        private static long WriteRows(IDataReader reader, List<ExportColumn> columns, StreamWriter writer, string prefix, int batchSize, CancellationToken cancellationToken, Action<long> onRow)
        {
            long rows = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!reader.Read()) break;
                cancellationToken.ThrowIfCancellationRequested();
                var insert = new StringBuilder(prefix);
                var declarations = new StringBuilder();
                var initialize = new StringBuilder();
                var cleanup = new StringBuilder();
                int ordinal = 0, valueBytes = 0;
                foreach (var column in columns)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (ordinal > 0) insert.Append(", ");
                    object value = ReadBoundedValue(reader, ordinal++, column);
                    valueBytes += value is byte[] ? ((byte[])value).Length : value is string ? Encoding.UTF8.GetByteCount((string)value) : 32;
                    if (valueBytes > MaxRowValueBytes) throw new NotSupportedException("Oracle row values exceed the 8 MiB budget.");
                    string expression = ValueExpression(value, column, ordinal, declarations, initialize, cleanup);
                    if (column.Spatial)
                    {
                        object srid = reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal); ordinal++;
                        object gtype = reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal); ordinal++;
                        if (value == null || value == DBNull.Value) insert.Append("NULL");
                        else
                        {
                            if (gtype == null) throw new NotSupportedException("Geometry GTYPE is required for WKB export.");
                            int type = Convert.ToInt32(gtype, CultureInfo.InvariantCulture);
                            if ((type / 100) % 10 != 0 || (type / 1000 != 2 && type / 1000 != 3) || type % 1000 < 1 || type % 1000 > 7)
                                throw new NotSupportedException("LRS/unsupported geometry requires a native codec; no geometry is approximated.");
                            string sridSql = FormatOracleLiteral(srid, false);
                            insert.Append("SDO_UTIL.FROM_WKBGEOMETRY(").Append(expression).Append(", ").Append(sridSql).Append(")");
                        }
                    }
                    else insert.Append(expression);
                }
                insert.Append(");");
                string sql = declarations.Length == 0 ? insert.ToString() : "DECLARE\n" + declarations + "BEGIN\n" + initialize + insert + "\n" + cleanup + "EXCEPTION WHEN OTHERS THEN\n" + cleanup + "RAISE;\nEND;\n/";
                if (Encoding.UTF8.GetByteCount(sql) > MaxRowSqlBytes) throw new NotSupportedException("Oracle row SQL exceeds the 28 MiB statement budget.");
                writer.WriteLine(sql); rows++;
                if (rows % batchSize == 0) { writer.WriteLine("COMMIT;"); writer.Flush(); }
                onRow?.Invoke(rows);
            }
            return rows;
        }

        private static string ValueExpression(object value, ExportColumn column, int ordinal, StringBuilder declarations, StringBuilder initialize, StringBuilder cleanup)
        {
            bool blob = value is byte[] && ((byte[])value).Length > MaxBinaryLiteralBytes;
            bool clob = value is string && Encoding.UTF8.GetByteCount(((string)value).Replace("'", "''")) > MaxTextLiteralBytes;
            if (value is byte[] && ((byte[])value).Length == 0)
            {
                if (column.Spatial) throw new NotSupportedException("Non-null geometry has empty WKB.");
                if (column.DataType == "BLOB") return "EMPTY_BLOB()";
            }
            if (value is string && ((string)value).Length == 0 && column.DataType == "CLOB") return "EMPTY_CLOB()";
            if (!blob && !clob)
            {
                string literal = FormatOracleLiteral(value, false);
                return value is byte[] && (column.Spatial || column.DataType == "BLOB") ? "TO_BLOB(" + literal + ")" : literal;
            }
            if (blob && !column.Spatial && column.DataType != "BLOB") throw new NotSupportedException("RAW values over 2000 bytes require a verified extended-RAW codec.");
            if (clob && column.DataType != "CLOB") throw new NotSupportedException("Oversized non-CLOB text cannot use a temporary CLOB without changing the type.");
            string variable = "mp_lob_" + ordinal;
            declarations.Append("  ").Append(variable).Append(blob ? " BLOB;\n" : " CLOB;\n");
            initialize.Append("  DBMS_LOB.CREATETEMPORARY(").Append(variable).Append(", TRUE);\n");
            if (blob)
            {
                byte[] bytes = (byte[])value;
                for (int offset = 0; offset < bytes.Length; offset += MaxBinaryLiteralBytes)
                {
                    int count = Math.Min(MaxBinaryLiteralBytes, bytes.Length - offset);
                    var chunk = new byte[count]; Array.Copy(bytes, offset, chunk, 0, count);
                    initialize.Append("  DBMS_LOB.WRITEAPPEND(").Append(variable).Append(", ").Append(count).Append(", ").Append(FormatOracleLiteral(chunk, false)).Append(");\n");
                }
            }
            else
            {
                string text = (string)value;
                if (text.IndexOf('\0') >= 0) throw new NotSupportedException("NUL CLOB text requires a native character codec.");
                for (int offset = 0; offset < text.Length;)
                {
                    int count = Math.Min(500, text.Length - offset); // <= 3000 UTF-8 bytes even when every quote doubles.
                    if (offset + count < text.Length && char.IsHighSurrogate(text[offset + count - 1])) count--;
                    string literal = FormatOracleLiteral(text.Substring(offset, count), false);
                    initialize.Append("  DBMS_LOB.WRITEAPPEND(").Append(variable).Append(", LENGTH(").Append(literal).Append("), ").Append(literal).Append(");\n");
                    offset += count;
                }
            }
            cleanup.Append("  IF DBMS_LOB.ISTEMPORARY(").Append(variable).Append(") = 1 THEN DBMS_LOB.FREETEMPORARY(").Append(variable).Append("); END IF;\n");
            return variable;
        }

        private static object ReadBoundedValue(IDataReader reader, int ordinal, ExportColumn column)
        {
            if (reader.IsDBNull(ordinal)) return DBNull.Value;
            if (column.Spatial || column.DataType == "BLOB" || column.DataType == "RAW")
            {
                long length = reader.GetBytes(ordinal, 0, null, 0, 0);
                if (length > MaxRowValueBytes) throw new NotSupportedException("Oracle BLOB/RAW/WKB exceeds the 8 MiB row value budget.");
                var buffer = new byte[(int)length];
                int position = 0;
                while (position < buffer.Length) { int count = (int)reader.GetBytes(ordinal, position, buffer, position, buffer.Length - position); if (count == 0) throw new EndOfStreamException("Incomplete Oracle binary value."); position += count; }
                return buffer;
            }
            if (column.DataType.Contains("CHAR") || column.DataType == "CLOB" || column.DataType == "NCLOB")
            {
                long length = reader.GetChars(ordinal, 0, null, 0, 0);
                if (length > MaxRowValueBytes / 2) throw new NotSupportedException("Oracle CLOB/text exceeds the 8 MiB row value budget.");
                var buffer = new char[(int)length];
                int position = 0;
                while (position < buffer.Length) { int count = (int)reader.GetChars(ordinal, position, buffer, position, buffer.Length - position); if (count == 0) throw new EndOfStreamException("Incomplete Oracle text value."); position += count; }
                return new string(buffer);
            }
            return reader.GetValue(ordinal);
        }

        public static string FormatOracleLiteral(object value, bool isSpatial)
        {
            if (value == null || value == DBNull.Value) return "NULL";
            if (isSpatial) throw new NotSupportedException("Geometry export requires per-value SRID/GTYPE; use the native reader projection.");
            if (value is byte[])
            {
                var bytes = (byte[])value;
                if (bytes.Length > MaxBinaryLiteralBytes) throw new NotSupportedException("Oracle binary literal exceeds 2000 bytes.");
                return "HEXTORAW('" + BitConverter.ToString(bytes).Replace("-", "") + "')";
            }
            if (value is DateTime) return "TO_TIMESTAMP('" + ((DateTime)value).ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "', 'YYYY-MM-DD HH24:MI:SS.FF7')";
            if (value is bool) return (bool)value ? "1" : "0";
            if (value is double && (double.IsNaN((double)value) || double.IsInfinity((double)value)) || value is float && (float.IsNaN((float)value) || float.IsInfinity((float)value)))
                throw new NotSupportedException("Non-finite Oracle floating values require a native codec.");
            if (value is double)
            {
                double number = (double)value;
                if (number == 0d && BitConverter.DoubleToInt64Bits(number) < 0) throw new NotSupportedException("Signed-zero BINARY_DOUBLE requires a verified native bit-preserving codec.");
                return number.ToString("G17", CultureInfo.InvariantCulture) + "d";
            }
            if (value is float)
            {
                float number = (float)value;
                if (number == 0f && BitConverter.ToInt32(BitConverter.GetBytes(number), 0) < 0) throw new NotSupportedException("Signed-zero BINARY_FLOAT requires a verified native bit-preserving codec.");
                return number.ToString("G9", CultureInfo.InvariantCulture) + "f";
            }
            if (value is byte || value is sbyte || value is short || value is ushort || value is int || value is uint || value is long || value is ulong || value is decimal)
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            if (!(value is string)) throw new NotSupportedException("Unsupported Oracle provider value: " + value.GetType().FullName);
            string text = (string)value;
            if (text.IndexOf('\0') >= 0) throw new NotSupportedException("Oracle NUL text requires a verified native codec.");
            string escaped = text.Replace("'", "''");
            if (Encoding.UTF8.GetByteCount(escaped) > MaxTextLiteralBytes) throw new NotSupportedException("Escaped Oracle UTF-8 text literal exceeds 4000 bytes.");
            return "'" + escaped + "'";
        }

        private void WriteTableDdl(OracleTableExportItem item, OracleExportWizardOptions options, StreamWriter writer, List<string> deferred, CancellationToken cancellationToken)
        {
            string ddl = db is my_oracle ? GetNativeDdl("TABLE", item.Owner, item.Name, cancellationToken) : db.GetTableCreateStatement(item.Owner, item.Name);
            if (string.IsNullOrWhiteSpace(ddl)) throw new InvalidOperationException("Native table DDL unavailable: " + item.DisplayName);
            using (var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(ddl)), Encoding.UTF8))
            {
                foreach (var statement in OracleImportRunner.EnumerateStatements(reader, cancellationToken))
                {
                    string sql = statement.Sql.Trim();
                    bool index = System.Text.RegularExpressions.Regex.IsMatch(sql, @"^CREATE\s+(UNIQUE\s+|BITMAP\s+)?INDEX\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    bool spatial = sql.IndexOf("MDSYS.SPATIAL_INDEX", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool comment = sql.StartsWith("COMMENT ", StringComparison.OrdinalIgnoreCase);
                    if (index) { if (spatial ? options.IncludeSpatialIndexes : options.IncludeIndexes) deferred.Add(sql); }
                    else if (!comment || options.IncludeComments) WriteStatement(writer, sql);
                }
            }
            if (!(db is my_oracle)) return;
            var parameters = new Dictionary<string, object> { { "owner", item.Owner }, { "tableName", item.Name } };
            if (options.IncludeComments)
            {
                var comments = QueryMetadata("SELECT COLUMN_NAME, COMMENTS FROM ALL_COL_COMMENTS WHERE OWNER=:owner AND TABLE_NAME=:tableName AND COMMENTS IS NOT NULL UNION ALL SELECT NULL, COMMENTS FROM ALL_TAB_COMMENTS WHERE OWNER=:owner AND TABLE_NAME=:tableName AND COMMENTS IS NOT NULL", parameters, cancellationToken);
                foreach (DataRow row in comments.Rows)
                {
                    string column = MetadataValue(row, "COLUMN_NAME");
                    writer.WriteLine("COMMENT ON " + (column.Length == 0 ? "TABLE " + item.FullQualifiedName : "COLUMN " + item.FullQualifiedName + "." + OracleImportRunner.QuoteIdentifier(column)) + " IS " + FormatOracleLiteral(row["COMMENTS"], false) + ";");
                }
            }
            if (options.IncludeIndexes || options.IncludeSpatialIndexes)
            {
                var indexes = QueryMetadata("SELECT i.OWNER, i.INDEX_NAME, i.INDEX_TYPE, i.ITYP_OWNER, i.ITYP_NAME FROM ALL_INDEXES i WHERE i.TABLE_OWNER=:owner AND i.TABLE_NAME=:tableName AND NOT EXISTS (SELECT 1 FROM ALL_CONSTRAINTS c WHERE c.OWNER=i.OWNER AND c.INDEX_NAME=i.INDEX_NAME AND c.CONSTRAINT_TYPE IN ('P','U')) ORDER BY i.INDEX_NAME", parameters, cancellationToken);
                foreach (DataRow row in indexes.Rows)
                {
                    bool spatial = MetadataValue(row, "ITYP_OWNER") == "MDSYS" && MetadataValue(row, "ITYP_NAME").StartsWith("SPATIAL_INDEX", StringComparison.OrdinalIgnoreCase);
                    if (spatial ? options.IncludeSpatialIndexes : options.IncludeIndexes)
                        deferred.Add(GetNativeDdl("INDEX", MetadataValue(row, "OWNER"), MetadataValue(row, "INDEX_NAME"), cancellationToken));
                }
            }
        }

        private string GetNativeDdl(string kind, string owner, string name, CancellationToken cancellationToken)
        {
            var native = (my_oracle)db;
            using (var command = new OracleCommand("SELECT DBMS_METADATA.GET_DDL(:kind,:name,:owner) FROM DUAL", native.MCT))
            {
                command.BindByName = true; command.Transaction = sourceTransaction;
                command.Parameters.Add("kind", OracleDbType.Varchar2).Value = kind;
                command.Parameters.Add("name", OracleDbType.Varchar2).Value = name;
                command.Parameters.Add("owner", OracleDbType.Varchar2).Value = owner;
                using (cancellationToken.Register(() => { try { command.Cancel(); } catch { } }))
                using (var reader = command.ExecuteReader(CommandBehavior.SequentialAccess))
                {
                    if (!reader.Read() || reader.IsDBNull(0)) throw new InvalidOperationException("DBMS_METADATA returned no DDL: " + owner + "." + name);
                    long count = reader.GetChars(0, 0, null, 0, 0);
                    if (count > MaxMetadataChars) throw new NotSupportedException("Oracle DDL exceeds 1 MiB metadata bound.");
                    var chars = new char[(int)count]; int position = 0;
                    while (position < chars.Length) { int fetched = (int)reader.GetChars(0, position, chars, position, chars.Length - position); if (fetched == 0) throw new EndOfStreamException(); position += fetched; }
                    return new string(chars);
                }
            }
        }

        private void WriteSpatialMetadata(OracleTableExportItem item, StreamWriter writer, CancellationToken cancellationToken)
        {
            foreach (string column in item.SpatialColumnNames)
            {
                var parameters = new Dictionary<string, object> { { "owner", item.Owner }, { "tableName", item.Name }, { "columnName", column } };
                var metadata = QueryMetadata("SELECT SRID, CASE WHEN DIMINFO IS NULL THEN 1 ELSE 0 END AS DIMINFO_NULL FROM ALL_SDO_GEOM_METADATA WHERE OWNER=:owner AND TABLE_NAME=:tableName AND COLUMN_NAME=:columnName", parameters, cancellationToken);
                if (metadata == null || metadata.Rows.Count > 1) throw new InvalidOperationException("Invalid Oracle spatial metadata: " + item.DisplayName + "." + column);
                writer.WriteLine("DELETE FROM USER_SDO_GEOM_METADATA WHERE TABLE_NAME=" + FormatOracleLiteral(item.Name, false) + " AND COLUMN_NAME=" + FormatOracleLiteral(column, false) + ";");
                if (metadata.Rows.Count == 0) continue;
                var row = metadata.Rows[0];
                string dimensionsSql = "NULL";
                if (Convert.ToInt32(row["DIMINFO_NULL"], CultureInfo.InvariantCulture) == 0)
                {
                    var dimensions = QueryMetadata("SELECT ROWNUM AS DIM_ORD, d.SDO_DIMNAME AS DIM_NAME, d.SDO_LB AS LOWER_BOUND, d.SDO_UB AS UPPER_BOUND, d.SDO_TOLERANCE AS TOLERANCE FROM ALL_SDO_GEOM_METADATA m, TABLE(m.DIMINFO) d WHERE m.OWNER=:owner AND m.TABLE_NAME=:tableName AND m.COLUMN_NAME=:columnName ORDER BY DIM_ORD", parameters, cancellationToken);
                    if (dimensions == null || dimensions.Rows.Count > 4) throw new InvalidOperationException("Incomplete spatial DIMINFO metadata.");
                    var parts = new List<string>();
                    foreach (DataRow dimension in dimensions.Rows)
                        parts.Add("MDSYS.SDO_DIM_ELEMENT(" + FormatOracleLiteral(dimension["DIM_NAME"], false) + ", " + FormatOracleLiteral(dimension["LOWER_BOUND"], false) + ", " + FormatOracleLiteral(dimension["UPPER_BOUND"], false) + ", " + FormatOracleLiteral(dimension["TOLERANCE"], false) + ")");
                    dimensionsSql = "MDSYS.SDO_DIM_ARRAY(" + string.Join(", ", parts) + ")";
                }
                writer.WriteLine("INSERT INTO USER_SDO_GEOM_METADATA (TABLE_NAME,COLUMN_NAME,DIMINFO,SRID) VALUES (" + FormatOracleLiteral(item.Name, false) + ", " + FormatOracleLiteral(column, false) + ", " + dimensionsSql + ", " + FormatOracleLiteral(row["SRID"], false) + ");");
            }
        }
        private static void WriteStatement(StreamWriter writer, string sql)
        {
            writer.WriteLine(sql.Trim().TrimEnd(';') + ";");
            if (System.Text.RegularExpressions.Regex.IsMatch(sql.Trim(), @"^(BEGIN|DECLARE|CREATE\s+(OR\s+REPLACE\s+)?(TRIGGER|PROCEDURE|FUNCTION|PACKAGE|TYPE))\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) writer.WriteLine("/");
        }
        private static string ComputeSha256(string filePath)
        {
            using (var hash = SHA256.Create())
            using (var stream = File.OpenRead(filePath)) return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
    }
}
