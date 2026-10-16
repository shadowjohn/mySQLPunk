using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
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

        private const long MaxRowBytes = 8 * 1024 * 1024;

        public List<SqliteTableExportItem> LoadTableCatalog()
        {
            var list = new List<SqliteTableExportItem>();
            var virtuals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            DataTable tables = Query("SELECT name, sql FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name ASC;");
            foreach (DataRow row in tables.Rows)
                if (System.Text.RegularExpressions.Regex.IsMatch(SqliteImport.SqliteStatementReader.PolicySql(Convert.ToString(row["sql"])), @"^CREATE\s+VIRTUAL\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) virtuals.Add(Convert.ToString(row["name"]));
            var spatial = GetSpatialTableDetails();
            foreach (DataRow row in tables.Rows)
            {
                string name = Convert.ToString(row["name"]);
                if (name.Equals(my_sqlite.ColumnCommentTableName, StringComparison.OrdinalIgnoreCase) || IsShadowTable(name, virtuals)) continue;
                bool metadata = KnownSpatialMetadataTables.Contains(name), geometry = spatial.ContainsKey(name), virt = virtuals.Contains(name);
                list.Add(new SqliteTableExportItem(name, CountChecked(name), !virt && !(metadata && name.Equals("spatial_ref_sys", StringComparison.OrdinalIgnoreCase)), geometry, metadata, virt,
                    geometry ? spatial[name] : virt ? "Virtual table (native data export unsupported)" : metadata ? "Spatial Metadata" : ""));
            }
            return list;
        }
        public List<string> LoadViews() { return LoadNames("view"); }
        public List<string> LoadTriggers() { return LoadNames("trigger"); }
        private List<string> LoadNames(string type)
        {
            return Query("SELECT name FROM sqlite_master WHERE type='" + type + "' AND sql IS NOT NULL AND name NOT LIKE 'sqlite_%' ORDER BY name ASC;")
                .Rows.Cast<DataRow>().Select(r => Convert.ToString(r[0])).ToList();
        }
        public Task<SqliteExportResult> ExportAsync(SqliteExportWizardOptions options, IProgress<SqliteExportProgress> progress, CancellationToken cancellation)
        {
            return Task.Run(() => ExportInternal(options, progress, cancellation));
        }
        private SqliteExportResult ExportInternal(SqliteExportWizardOptions options, IProgress<SqliteExportProgress> progress, CancellationToken cancellation)
        {
            var clock = Stopwatch.StartNew();
            string target = options.TargetPath;
            if (string.IsNullOrWhiteSpace(target)) throw new ArgumentException("Target path is required", nameof(options.TargetPath));
            target = Path.GetFullPath(target);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            string staging = target + ".staging." + Guid.NewGuid().ToString("N") + ".tmp";
            bool publicationAttempted = false, snapshot = false;
            int tableCount = 0, viewCount = 0, triggerCount = 0, indexCount = 0;
            long rows = 0, estimated = 0;
            var selected = (options.SelectedTables ?? new List<string>()).Where(t => !t.Equals(my_sqlite.ColumnCommentTableName, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (options.ExcludeSpatialMetadata) selected.RemoveAll(t => KnownSpatialMetadataTables.Contains(t));
            var views = options.IncludeViews ? (options.SelectedViews ?? new List<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToList() : new List<string>();
            var triggers = new List<string>();
            var sourceCancellation = db is my_sqlite nativeSource && nativeSource.MCT != null
                ? cancellation.Register(() => { try { nativeSource.MCT.Cancel(); } catch { } })
                : default(CancellationTokenRegistration);
            try
            {
                cancellation.ThrowIfCancellationRequested();
                // This snapshot belongs to the dedicated wizard session; never reuse
                // the editor connection or silently continue when BEGIN fails.
                ExecChecked("BEGIN;"); snapshot = true;
                var ddls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string table in selected)
                {
                    cancellation.ThrowIfCancellationRequested();
                    string ddl = GetDdl("table", table);
                    if (System.Text.RegularExpressions.Regex.IsMatch(SqliteImport.SqliteStatementReader.PolicySql(ddl), @"^CREATE\s+VIRTUAL\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        throw new NotSupportedException("Virtual table native export is not yet supported: " + table + ". Use a module-specific backup.");
                    ddls[table] = ddl;
                    if (options.Mode != SqliteExportMode.SchemaOnly) estimated += CountChecked(table);
                }
                if (options.Mode != SqliteExportMode.SchemaOnly)
                {
                    var encoding = Query("PRAGMA encoding;");
                    if (encoding.Rows.Count != 1 || !Convert.ToString(encoding.Rows[0][0]).Equals("UTF-8", StringComparison.OrdinalIgnoreCase))
                        throw new NotSupportedException("Native text byte export currently requires a UTF-8 SQLite database.");
                }
                foreach (string view in views) GetDdl("view", view);
                var selectedSet = new HashSet<string>(selected.Concat(views), StringComparer.OrdinalIgnoreCase);
                if (options.IncludeTriggers)
                {
                    foreach (string name in (options.SelectedTriggers ?? new List<string>()).Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        var trigger = Query("SELECT tbl_name, sql FROM sqlite_master WHERE type='trigger' AND name=" + TextLiteral(name) + ";");
                        if (trigger.Rows.Count != 1 || trigger.Rows[0]["sql"] == DBNull.Value) throw new InvalidOperationException("Missing selected trigger DDL: " + name);
                        if (selectedSet.Contains(Convert.ToString(trigger.Rows[0]["tbl_name"]))) triggers.Add(name);
                    }
                }
                var manifest = new List<Tuple<string, long>>();
                using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false, true), 65536))
                {
                    writer.WriteLine("-- mySQLPunk SQLite Logical Dump");
                    writer.WriteLine("-- Created: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                    writer.WriteLine("-- Native payload checksum covers bytes preceding the checksum footer.");
                    writer.WriteLine("-- Data fidelity: ordinary UTF-8 SQLite tables; virtual modules require separate backup.");
                    if (options.PragmaForeignKeysOff) writer.WriteLine("PRAGMA foreign_keys = OFF;");
                    if (options.PragmaSynchronousOff) writer.WriteLine("PRAGMA synchronous = OFF;");
                    if (options.SingleTransaction) writer.WriteLine("BEGIN TRANSACTION;");
                    if (options.Mode != SqliteExportMode.DataOnly)
                    {
                        if (options.IncludeDropStatements)
                        {
                            foreach (string tr in triggers) writer.WriteLine("DROP TRIGGER IF EXISTS " + QuoteIdentifier(tr) + ";");
                            foreach (string view in views) writer.WriteLine("DROP VIEW IF EXISTS " + QuoteIdentifier(view) + ";");
                            foreach (string table in selected) writer.WriteLine("DROP TABLE IF EXISTS " + QuoteIdentifier(table) + ";");
                        }
                        foreach (string table in selected) { cancellation.ThrowIfCancellationRequested(); WriteDdl(writer, ddls[table]); }
                    }
                    foreach (string table in selected)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        long tableRows = 0;
                        if (options.Mode != SqliteExportMode.SchemaOnly)
                        {
                            tableRows = WriteTableData(writer, table, ddls[table], cancellation);
                            rows += tableRows;
                            manifest.Add(Tuple.Create(table, tableRows));
                        }
                        tableCount++;
                        ReportProgress(progress, SqliteExportStage.WritingData, table, tableCount, selected.Count, rows, estimated, stream.Position, clock);
                    }
                    if (options.Mode != SqliteExportMode.SchemaOnly) WriteSequence(writer, selected, cancellation);
                    if (options.Mode != SqliteExportMode.DataOnly)
                    {
                        foreach (string view in views) { cancellation.ThrowIfCancellationRequested(); WriteDdl(writer, GetDdl("view", view)); viewCount++; }
                        if (options.IncludeIndexes)
                        {
                            DataTable indexes = Query("SELECT sql, tbl_name FROM sqlite_master WHERE type='index' AND sql IS NOT NULL AND name NOT LIKE 'sqlite_%';");
                            foreach (DataRow index in indexes.Rows)
                                if (selected.Contains(Convert.ToString(index["tbl_name"]), StringComparer.OrdinalIgnoreCase))
                                {
                                    cancellation.ThrowIfCancellationRequested();
                                    string ddl = Convert.ToString(index["sql"]);
                                    if (string.IsNullOrWhiteSpace(ddl)) throw new InvalidOperationException("Missing selected index DDL.");
                                    WriteDdl(writer, ddl); indexCount++;
                                }
                        }
                        foreach (string trigger in triggers) { cancellation.ThrowIfCancellationRequested(); WriteDdl(writer, GetDdl("trigger", trigger)); triggerCount++; }
                    }
                    if (options.IncludeComments) WriteComments(writer, selected, cancellation);
                    if (options.SingleTransaction) writer.WriteLine("COMMIT;");
                    // The runner restores its actual original PRAGMA values in finally.
                    // Standalone SQL explicitly re-enables FK checks after restoration.
                    if (options.PragmaForeignKeysOff) writer.WriteLine("PRAGMA foreign_keys = ON;");
                    writer.WriteLine("-- mySQLPunk Native Manifest");
                    writer.WriteLine("-- Tables: " + tableCount);
                    writer.WriteLine("-- TotalRows: " + rows);
                    foreach (var pair in manifest)
                        writer.WriteLine("-- Manifest-Table64: " + Convert.ToBase64String(Encoding.UTF8.GetBytes(pair.Item1)) + " | Rows: " + pair.Item2);
                }
                ExecChecked("COMMIT;"); snapshot = false;
                sourceCancellation.Dispose();
                cancellation.ThrowIfCancellationRequested();
                string payloadHash = HashFile(staging, cancellation);
                using (var writer = new StreamWriter(staging, true, new UTF8Encoding(false, true))) writer.WriteLine("-- Manifest-Payload-SHA256: " + payloadHash);
                string deliveredHash = HashFile(staging, cancellation);
                long deliveredBytes = new FileInfo(staging).Length;
                cancellation.ThrowIfCancellationRequested();
                publicationAttempted = true;
                SqlExportPublication.Publish(staging, target);
                clock.Stop();
                ReportProgress(progress, SqliteExportStage.Completed, "", tableCount, selected.Count, rows, estimated, deliveredBytes, clock);
                return new SqliteExportResult(SqliteExportStage.Completed, target, tableCount, viewCount, triggerCount, indexCount, rows, deliveredBytes, deliveredHash, clock.Elapsed);
            }
            catch (Exception ex)
            {
                sourceCancellation.Dispose();
                bool cancelled = ex is OperationCanceledException || cancellation.IsCancellationRequested;
                string error = cancelled ? "Export cancelled by user." : ex.Message;
                if (snapshot)
                {
                    try { if (!(db is my_sqlite nativeCleanup) || nativeCleanup.MCT == null || !nativeCleanup.MCT.AutoCommit) ExecChecked("ROLLBACK;"); snapshot = false; }
                    catch (Exception cleanup) { error += " Source ROLLBACK failed: " + cleanup.Message; cancelled = false; }
                }
                if (!publicationAttempted) TryDeleteFile(staging);
                else error += " Recoverable staging file: " + staging;
                clock.Stop();
                var stage = cancelled ? SqliteExportStage.Cancelled : SqliteExportStage.Failed;
                ReportProgress(progress, stage, "", tableCount, selected.Count, rows, estimated, 0, clock, error);
                return new SqliteExportResult(stage, target, tableCount, viewCount, triggerCount, indexCount, rows, 0, null, clock.Elapsed, error);
            }
            finally { sourceCancellation.Dispose(); }
        }
        private long WriteTableData(StreamWriter writer, string table, string ddl, CancellationToken cancellation)
        {
            DataTable metadata = Query("PRAGMA table_xinfo(" + QuoteIdentifier(table) + ");");
            if (metadata.Rows.Count == 0 || !metadata.Columns.Contains("hidden")) throw new NotSupportedException("Writable column metadata unavailable: " + table);
            var all = metadata.Rows.Cast<DataRow>().Select(r => Convert.ToString(r["name"])).ToList();
            var columns = metadata.Rows.Cast<DataRow>().Where(r => Convert.ToInt32(r["hidden"]) == 0).Select(r => Convert.ToString(r["name"])).ToList();
            if (metadata.Rows.Cast<DataRow>().Any(r => Convert.ToInt32(r["hidden"]) == 1)) throw new NotSupportedException("Hidden virtual columns are not supported: " + table);
            DataTable tableInfo = Query("PRAGMA table_list(" + QuoteIdentifier(table) + ");");
            if (tableInfo.Rows.Count != 1 || !tableInfo.Columns.Contains("wr")) throw new NotSupportedException("Rowid metadata unavailable: " + table);
            bool withoutRowid = Convert.ToInt32(tableInfo.Rows[0]["wr"]) != 0;
            string stableOrder = null;
            if (!withoutRowid)
            {
                string alias = new[] { "rowid", "_rowid_", "oid" }.FirstOrDefault(n => !all.Contains(n, StringComparer.OrdinalIgnoreCase));
                if (alias != null) { columns.Insert(0, alias); stableOrder = QuoteIdentifier(alias); }
                else
                {
                    // An accessible INTEGER PRIMARY KEY aliases rowid only when no
                    // separate PK index exists (INTEGER PRIMARY KEY DESC is different).
                    DataTable indexes = Query("PRAGMA index_list(" + QuoteIdentifier(table) + ");");
                    bool integerPk = metadata.Rows.Cast<DataRow>().Any(r => Convert.ToInt32(r["pk"]) == 1 && Convert.ToString(r["type"]).Equals("INTEGER", StringComparison.OrdinalIgnoreCase));
                    bool pkIndex = indexes.Columns.Contains("origin") && indexes.Rows.Cast<DataRow>().Any(r => Convert.ToString(r["origin"]) == "pk");
                    stableOrder = string.Join(", ", metadata.Rows.Cast<DataRow>().Where(r => Convert.ToInt32(r["pk"]) > 0).OrderBy(r => Convert.ToInt32(r["pk"])).Select(r => QuoteIdentifier(Convert.ToString(r["name"]))));
                    if (!integerPk || pkIndex) throw new NotSupportedException("All implicit rowid aliases are shadowed; cannot safely preserve row identities: " + table);
                }
            }
            if (columns.Count == 0) throw new NotSupportedException("No writable columns found: " + table);
            long expected = CountChecked(table);
            string names = string.Join(", ", columns.Select(QuoteIdentifier));
            string insert = "INSERT INTO " + QuoteIdentifier(table) + " (" + names + ") VALUES (";
            int insertBytes = Encoding.UTF8.GetByteCount(insert) + columns.Count * 2 + 3;
            string from = " FROM " + QuoteIdentifier(table);
            if (stableOrder == null) stableOrder = string.Join(", ", metadata.Rows.Cast<DataRow>().Where(r => Convert.ToInt32(r["pk"]) > 0).OrderBy(r => Convert.ToInt32(r["pk"])).Select(r => QuoteIdentifier(Convert.ToString(r["name"]))));
            if (string.IsNullOrEmpty(stableOrder)) throw new NotSupportedException("Stable row identity unavailable: " + table);
            string order = " ORDER BY " + stableOrder;
            string size = string.Join(" + ", columns.Select(c => "coalesce(length(CAST(" + QuoteIdentifier(c) + " AS BLOB)),0)"));
            string literals = string.Join(", ", columns.Select(c => SqlValueExpression(QuoteIdentifier(c))));
            if (db is my_sqlite native && native.MCT != null)
            {
                // One sequential reader for the native provider. Gating the SQL
                // expressions avoids computing a huge hex literal before size is checked.
                string guarded = string.Join(", ", columns.Select(c => "CASE WHEN (" + size + ") <= " + MaxRowBytes + " THEN " + SqlValueExpression(QuoteIdentifier(c)) + " ELSE NULL END"));
                using (var command = new SQLiteCommand("SELECT " + size + ", " + guarded + from + order + ";", native.MCT))
                using (var reader = command.ExecuteReader(CommandBehavior.SequentialAccess))
                {
                    long actual = 0;
                    while (reader.Read())
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (reader.GetInt64(0) > MaxRowBytes) throw new NotSupportedException("Row exceeds safe 8 MiB native export limit: " + table);
                        long statementBytes = insertBytes;
                        writer.Write(insert);
                        for (int c = 0; c < columns.Count; c++)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            if (c > 0) writer.Write(", ");
                            if (reader.IsDBNull(c + 1)) throw new InvalidOperationException("Source literal rendering failed: " + table);
                            string literal = reader.GetString(c + 1);
                            statementBytes += Encoding.UTF8.GetByteCount(literal);
                            if (statementBytes > SqliteImport.SqliteStatementReader.MaxStatementBytes) throw new NotSupportedException("Serialized row exceeds 32 MiB statement limit: " + table);
                            writer.Write(literal);
                        }
                        writer.WriteLine(");"); actual++;
                    }
                    if (actual != expected) throw new InvalidOperationException("Source snapshot row count differs for " + table);
                    return actual;
                }
            }
            for (long offset = 0; offset < expected; offset++)
            {
                cancellation.ThrowIfCancellationRequested();
                string page = from + order + " LIMIT 1 OFFSET " + offset.ToString(CultureInfo.InvariantCulture) + ";";
                DataTable sizes = Query("SELECT " + size + page);
                if (sizes.Rows.Count != 1 || Convert.ToInt64(sizes.Rows[0][0]) > MaxRowBytes)
                    throw new NotSupportedException("Missing row or row exceeds the safe 8 MiB native export limit: " + table + ". Existing output was preserved.");
                DataTable values = Query("SELECT " + literals + page);
                if (values.Rows.Count != 1 || values.Columns.Count != columns.Count) throw new InvalidOperationException("Incomplete source page for " + table);
                long statementBytes = insertBytes;
                writer.Write(insert);
                for (int c = 0; c < columns.Count; c++)
                {
                    if (c > 0) writer.Write(", ");
                    string literal = Convert.ToString(values.Rows[0][c], CultureInfo.InvariantCulture);
                    statementBytes += Encoding.UTF8.GetByteCount(literal);
                    if (statementBytes > SqliteImport.SqliteStatementReader.MaxStatementBytes) throw new NotSupportedException("Serialized row exceeds 32 MiB statement limit: " + table);
                    writer.Write(literal);
                }
                writer.WriteLine(");");
            }
            if (CountChecked(table) != expected) throw new InvalidOperationException("Source row count changed during snapshot: " + table);
            return expected;
        }
        private void WriteSequence(StreamWriter writer, List<string> selected, CancellationToken cancellation)
        {
            if (!ObjectExists("table", "sqlite_sequence")) return;
            foreach (string table in selected)
            {
                cancellation.ThrowIfCancellationRequested();
                DataTable sequence = Query("SELECT seq FROM sqlite_sequence WHERE name=" + TextLiteral(table) + ";");
                if (sequence.Rows.Count == 0) continue;
                if (sequence.Rows.Count != 1) throw new InvalidOperationException("Ambiguous sqlite_sequence row for " + table);
                writer.WriteLine("DELETE FROM sqlite_sequence WHERE name=" + TextLiteral(table) + ";");
                writer.WriteLine("INSERT INTO sqlite_sequence(name,seq) VALUES (" + TextLiteral(table) + "," + Convert.ToInt64(sequence.Rows[0][0]).ToString(CultureInfo.InvariantCulture) + ");");
            }
        }
        private void WriteComments(StreamWriter writer, List<string> selected, CancellationToken cancellation)
        {
            if (!ObjectExists("table", my_sqlite.ColumnCommentTableName)) return;
            bool created = false;
            foreach (string table in selected)
            {
                cancellation.ThrowIfCancellationRequested();
                string size = "coalesce(length(CAST(comment AS BLOB)),0)+coalesce(length(CAST(column_name AS BLOB)),0)";
                string query = "SELECT " + size + ", CASE WHEN (" + size + ") <= " + MaxRowBytes + " THEN column_name ELSE NULL END, CASE WHEN (" + size + ") <= " + MaxRowBytes + " THEN " + SqlValueExpression("comment") + " ELSE NULL END FROM " + QuoteIdentifier(my_sqlite.ColumnCommentTableName) + " WHERE table_name=" + TextLiteral(table);
                if (db is my_sqlite native && native.MCT != null)
                {
                    using (var command = new SQLiteCommand(query + ";", native.MCT))
                        using (var reader = command.ExecuteReader(CommandBehavior.SequentialAccess))
                        while (reader.Read())
                        {
                            cancellation.ThrowIfCancellationRequested();
                            if (reader.GetInt64(0) > MaxRowBytes) throw new NotSupportedException("Column comment exceeds safe 8 MiB export limit: " + table);
                            WriteComment(writer, table, reader.GetString(1), reader.GetString(2), ref created);
                        }
                }
                else
                {
                    for (long offset = 0; ; offset++)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        DataTable data = Query(query + " ORDER BY column_name LIMIT 1 OFFSET " + offset + ";");
                        if (data.Rows.Count == 0) break;
                        if (Convert.ToInt64(data.Rows[0][0]) > MaxRowBytes) throw new NotSupportedException("Column comment exceeds safe 8 MiB export limit: " + table);
                        WriteComment(writer, table, Convert.ToString(data.Rows[0][1]), Convert.ToString(data.Rows[0][2]), ref created);
                    }
                }
            }
        }
        private static void WriteComment(StreamWriter writer, string table, string column, string literal, ref bool created)
        {
            if (!created)
            {
                writer.WriteLine("CREATE TABLE IF NOT EXISTS " + QuoteIdentifier(my_sqlite.ColumnCommentTableName) + " (table_name TEXT NOT NULL,column_name TEXT NOT NULL,comment TEXT NOT NULL,PRIMARY KEY(table_name,column_name));");
                created = true;
            }
            string sql = "INSERT OR REPLACE INTO " + QuoteIdentifier(my_sqlite.ColumnCommentTableName) + " (table_name,column_name,comment) VALUES (" + TextLiteral(table) + "," + TextLiteral(column) + "," + literal + ");";
            if (Encoding.UTF8.GetByteCount(sql) > SqliteImport.SqliteStatementReader.MaxStatementBytes) throw new NotSupportedException("Column comment SQL exceeds native statement size limit.");
            writer.WriteLine(sql);
        }
        private static string SqlValueExpression(string column)
        {
            return "CASE typeof(" + column + ") WHEN 'text' THEN 'CAST(X'''||hex(CAST(" + column + " AS BLOB))||''' AS TEXT)' WHEN 'blob' THEN 'X'''||hex(" + column + ")||'''' ELSE quote(" + column + ") END";
        }
        private DataTable Query(string sql)
        {
            DataTable result = db.SelectSQL(sql, new Dictionary<string, object>());
            if (result == null || result.ExtendedProperties.ContainsKey("QueryError")) throw new InvalidOperationException("SQLite source query failed: " + (result == null ? "no result" : Convert.ToString(result.ExtendedProperties["QueryError"])));
            return result;
        }
        private long CountChecked(string table)
        {
            long count = db.CountRows(databaseName, table);
            if (count < 0) throw new InvalidOperationException("SQLite row count failed: " + table);
            return count;
        }
        private bool ObjectExists(string type, string name)
        {
            return Query("SELECT sql FROM sqlite_master WHERE type='" + type + "' AND name=" + TextLiteral(name) + ";").Rows.Count == 1;
        }
        private string GetDdl(string type, string name)
        {
            DataTable ddl = Query("SELECT sql FROM sqlite_master WHERE type='" + type + "' AND name=" + TextLiteral(name) + ";");
            if (ddl.Rows.Count != 1 || ddl.Rows[0][0] == DBNull.Value || string.IsNullOrWhiteSpace(Convert.ToString(ddl.Rows[0][0]))) throw new InvalidOperationException("Missing selected " + type + " DDL: " + name);
            return Convert.ToString(ddl.Rows[0][0]);
        }
        private void ExecChecked(string sql)
        {
            var result = db.ExecSQL(sql);
            if (result == null || !result.TryGetValue("status", out string status) || !status.Equals("OK", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(sql + " " + DatabaseExecutionResultService.GetFailureReason(result));
        }
        private static string HashFile(string path, CancellationToken cancellation)
        {
            using (var stream = File.OpenRead(path)) using (var hash = SHA256.Create())
            {
                var buffer = new byte[65536]; int n;
                while ((n = stream.Read(buffer, 0, buffer.Length)) > 0) { cancellation.ThrowIfCancellationRequested(); hash.TransformBlock(buffer, 0, n, null, 0); }
                hash.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant();
            }
        }
        private static void WriteDdl(StreamWriter writer, string ddl)
        {
            if (Encoding.UTF8.GetByteCount(ddl) > SqliteImport.SqliteStatementReader.MaxStatementBytes - 2) throw new NotSupportedException("DDL exceeds native statement size limit.");
            writer.WriteLine(ddl.TrimEnd().TrimEnd(';') + ";");
        }
        private static string TextLiteral(string text) { return "'" + text.Replace("'", "''") + "'"; }
        public static string QuoteIdentifier(string name) { return "\"" + (name ?? "").Replace("\"", "\"\"") + "\""; }
        private static bool IsShadowTable(string tableName, HashSet<string> virtualNames)
        {
            int i = tableName.LastIndexOf('_');
            return i > 0 && virtualNames.Contains(tableName.Substring(0, i)) && ShadowTableSuffixes.Contains(tableName.Substring(i + 1), StringComparer.OrdinalIgnoreCase);
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
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
