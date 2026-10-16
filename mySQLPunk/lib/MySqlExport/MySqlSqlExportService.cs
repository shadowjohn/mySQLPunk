using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Globalization;
using mySQLPunk.lib.MySqlImport;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace mySQLPunk.lib.MySqlExport
{
    public sealed class MySqlSqlExportService
    {
        private readonly IDatabase db;
        private readonly string databaseName;

        public MySqlSqlExportService(IDatabase db, string databaseName)
        {
            this.db = db ?? throw new ArgumentNullException(nameof(db));
            this.databaseName = !string.IsNullOrWhiteSpace(databaseName) ? databaseName : throw new ArgumentException("Database name is required", nameof(databaseName));
        }

        public List<MySqlTableExportItem> LoadTableCatalog()
        {
            var list = new List<MySqlTableExportItem>();
            var tables = db.GetTables(databaseName);
            DataTable status = null;
            try { status = db.GetTableStatus(databaseName); } catch { }
            foreach (var table in tables)
            {
                if (string.IsNullOrWhiteSpace(table)) continue;
                long count = 0;
                try { count = db.CountRows(databaseName, table); } catch { }
                list.Add(new MySqlTableExportItem(table, count, true) { Engine = ReadTableEngine(status, table) });
            }
            return list;
        }

        public List<string> LoadViews()
        {
            try { return db.GetViews(databaseName) ?? new List<string>(); }
            catch { return new List<string>(); }
        }

        // The wizard may call this before confirmation. It must not scan data,
        // count rows, start a transaction, or cache metadata across attempts.
        public void ValidateTableDataCompatibility(IEnumerable<string> selectedTables, CancellationToken cancellation)
        {
            if (selectedTables == null) throw new ArgumentNullException(nameof(selectedTables));
            foreach (string table in selectedTables)
            {
                cancellation.ThrowIfCancellationRequested();
                LoadTableShape(table, cancellation);
            }
            cancellation.ThrowIfCancellationRequested();
        }

        public List<MySqlRoutineInfo> LoadRoutines()
        {
            try { return MySqlExportService.GetRoutines(db, databaseName) ?? new List<MySqlRoutineInfo>(); }
            catch { return new List<MySqlRoutineInfo>(); }
        }

        public List<MySqlTriggerInfo> LoadTriggers()
        {
            try { return MySqlExportService.GetTriggers(db, databaseName) ?? new List<MySqlTriggerInfo>(); }
            catch { return new List<MySqlTriggerInfo>(); }
        }

        public Task<MySqlExportResult> ExportAsync(
            MySqlExportWizardOptions options,
            IProgress<MySqlExportProgress> progress,
            CancellationToken cancellation)
        {
            return Task.Run(() => ExportInternal(options, progress, cancellation));
        }

        private MySqlExportResult ExportInternal(
            MySqlExportWizardOptions options,
            IProgress<MySqlExportProgress> progress,
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
            int routineCount = 0;
            int triggerCount = 0;
            var tableManifest = new List<Tuple<string, long>>();

            var selectedTables = options.SelectedTables?.ToList() ?? new List<string>();
            var selectedViews = options.IncludeViews ? (options.SelectedViews?.ToList() ?? new List<string>()) : new List<string>();
            var selectedRoutines = options.IncludeRoutines ? (options.SelectedRoutines?.ToList() ?? new List<string>()) : new List<string>();
            var selectedTriggers = options.IncludeTriggers ? (options.SelectedTriggers?.ToList() ?? new List<string>()) : new List<string>();
            var tableShapes = new Dictionary<string, TableShape>(StringComparer.Ordinal);
            var tableRowCounts = new Dictionary<string, long>(StringComparer.Ordinal);

            bool inTransaction = false;
            bool publicationAttempted = false;
            try
            {
                cancellation.ThrowIfCancellationRequested();
                RequireExecution("SET TIME_ZONE = '+00:00';");
                if (options.SingleTransaction && options.Mode != MySqlExportMode.SchemaOnly)
                {
                    RequireTransactionalTables(selectedTables);
                    RequireExecution("SET TRANSACTION ISOLATION LEVEL REPEATABLE READ;");
                    RequireExecution("START TRANSACTION WITH CONSISTENT SNAPSHOT;");
                    inTransaction = true;
                }
                if (options.Mode != MySqlExportMode.SchemaOnly)
                {
                    // Validate the whole selection before any potentially expensive
                    // row scan. All metadata is fresh and belongs to this attempt.
                    foreach (string table in selectedTables)
                    {
                        ReportProgress(progress, MySqlExportStage.ReadingCatalog, table, 0, selectedTables.Count, 0, 0, 0, clock);
                        tableShapes[table] = LoadTableShape(table, cancellation);
                    }
                    foreach (string table in selectedTables)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        ReportProgress(progress, MySqlExportStage.ReadingCatalog, table, 0, selectedTables.Count, 0, estimatedTotalRows, 0, clock);
                        CheckRowBound(table, tableShapes[table], cancellation);
                        long count = db.CountRows(databaseName, table);
                        if (count < 0) throw new InvalidOperationException("Invalid source row count: " + table);
                        tableRowCounts[table] = count;
                        estimatedTotalRows += count;
                        cancellation.ThrowIfCancellationRequested();
                    }
                }

                cancellation.ThrowIfCancellationRequested();

                using (var fileStream = new FileStream(stagingPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 65536))
                using (var writer = new StreamWriter(fileStream, new UTF8Encoding(false)))
                {
                    writer.WriteLine("-- ========================================================");
                    writer.WriteLine("-- mySQLPunk MySQL Logical Dump");
                    writer.WriteLine($"-- Database: {databaseName}");
                    writer.WriteLine($"-- Created: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    writer.WriteLine($"-- Mode: {options.Mode}");
                    writer.WriteLine($"-- SingleTransaction: {options.SingleTransaction && inTransaction}");
                    writer.WriteLine("-- ========================================================");
                    writer.WriteLine();
                    writer.WriteLine("SET NAMES utf8mb4;");
                    writer.WriteLine("SET TIME_ZONE = '+00:00';");
                    if (options.SetNoAutoValueOnZero)
                        writer.WriteLine("SET SQL_MODE = 'NO_AUTO_VALUE_ON_ZERO';");
                    if (options.DisableForeignKeyChecks)
                        writer.WriteLine("SET FOREIGN_KEY_CHECKS = 0;");
                    if (options.DisableUniqueChecks)
                        writer.WriteLine("SET UNIQUE_CHECKS = 0;");
                    if (options.IncludeCreateDatabase)
                    {
                        var info = db.GetDatabaseInfo(databaseName) ?? new Dictionary<string, string>();
                        string charset = info.ContainsKey("character_set") ? info["character_set"] : "utf8mb4";
                        string collation = info.ContainsKey("collation") ? info["collation"] : "utf8mb4_general_ci";
                        writer.WriteLine($"CREATE DATABASE IF NOT EXISTS {MySqlImportRunner.QuoteIdentifier(databaseName)} DEFAULT CHARACTER SET {charset} COLLATE {collation};");
                    }
                    if (options.IncludeUseDatabase)
                        writer.WriteLine($"USE {MySqlImportRunner.QuoteIdentifier(databaseName)};");
                    writer.WriteLine();

                    // 1. DROP 語句
                    if (options.IncludeDropStatements && options.Mode != MySqlExportMode.DataOnly)
                    {
                        foreach (var tr in selectedTriggers) writer.WriteLine($"DROP TRIGGER IF EXISTS {MySqlImportRunner.QuoteIdentifier(tr)};");
                        foreach (var r in selectedRoutines)
                        {
                            string type = r.StartsWith("PROCEDURE:", StringComparison.OrdinalIgnoreCase) ? "PROCEDURE" : "FUNCTION";
                            string name = r.Contains(":") ? r.Substring(r.IndexOf(':') + 1) : r;
                            writer.WriteLine($"DROP {type} IF EXISTS {MySqlImportRunner.QuoteIdentifier(name)};");
                        }
                        foreach (var v in selectedViews) writer.WriteLine($"DROP VIEW IF EXISTS {MySqlImportRunner.QuoteIdentifier(v)};");
                        foreach (var t in selectedTables) writer.WriteLine($"DROP TABLE IF EXISTS {MySqlImportRunner.QuoteIdentifier(t)};");
                        writer.WriteLine();
                    }

                    // 2. 結構 DDL (Tables)
                    if (options.Mode != MySqlExportMode.DataOnly)
                    {
                        ReportProgress(progress, MySqlExportStage.WritingSchema, "", 0, selectedTables.Count, 0, estimatedTotalRows, fileStream.Position, clock);

                        foreach (var table in selectedTables)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            string ddl = db.GetTableCreateStatement(databaseName, table);
                            if (string.IsNullOrWhiteSpace(ddl)) throw new InvalidOperationException("Missing table DDL: " + table);
                            if (!string.IsNullOrWhiteSpace(ddl))
                            {
                                writer.WriteLine(ddl.TrimEnd().TrimEnd(';') + ";");
                                writer.WriteLine();
                            }
                        }
                    }

                    // 3. 資料列 (Data)
                    if (options.Mode != MySqlExportMode.SchemaOnly)
                    {
                        // IDatabase materializes a page; bound bytes before fetching.
                        int batchSize = Math.Min(options.InsertBatchSize > 0 ? options.InsertBatchSize : 16, 16);
                        for (int i = 0; i < selectedTables.Count; i++)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            string table = selectedTables[i];
                            long tableRows = 0;
                            long totalTableRows = tableRowCounts[table];
                            var shape = tableShapes[table];

                            ReportProgress(progress, MySqlExportStage.WritingData, table, i, selectedTables.Count, totalProcessedRows, estimatedTotalRows, fileStream.Position, clock);

                            while (tableRows < totalTableRows || totalTableRows == 0)
                            {
                                cancellation.ThrowIfCancellationRequested();
                                DataTable page = ReadBoundedPage(table, shape, tableRows, batchSize);
                                if (page == null) throw new InvalidOperationException("Source row query returned no result: " + table);
                                cancellation.ThrowIfCancellationRequested();
                                if (page.Rows.Count == 0) break;

                                var writableColumns = shape.Writable;
                                string prefix = "INSERT INTO " + MySqlImportRunner.QuoteIdentifier(table) + " (" +
                                    string.Join(", ", writableColumns.Select(c => MySqlImportRunner.QuoteIdentifier(c.Name))) + ") VALUES\r\n";
                                var insert = new StringBuilder(prefix);
                                foreach (DataRow row in page.Rows)
                                {
                                    cancellation.ThrowIfCancellationRequested();
                                    var rowText = new StringBuilder("(");
                                    for (int c = 0; c < writableColumns.Count; c++)
                                    {
                                        if (c > 0) rowText.Append(", ");
                                        var column = writableColumns[c];
                                        rowText.Append(column.Spatial ? ToSpatialLiteral(row[column.Name], row[column.SridAlias], table + "." + column.Name) : ToBoundedLiteral(row[column.Name]));
                                        if (rowText.Length > MaxInsertBytes) throw new NotSupportedException("Single row exceeds safe logical-export limit: " + table);
                                    }
                                    rowText.Append(")");
                                    string rowSql = rowText.ToString();
                                    if (Encoding.UTF8.GetByteCount(prefix) + Encoding.UTF8.GetByteCount(rowSql) + 4 > MaxInsertBytes)
                                        throw new NotSupportedException("Single row exceeds 8 MiB SQL limit: " + table);
                                    if (Encoding.UTF8.GetByteCount(insert.ToString()) + Encoding.UTF8.GetByteCount(rowSql) + 4 > MaxInsertBytes)
                                    {
                                        writer.Write(insert.ToString());
                                        writer.WriteLine(";");
                                        insert.Clear().Append(prefix);
                                    }
                                    if (insert.Length > prefix.Length) insert.Append(",\r\n");
                                    insert.Append(rowSql);
                                }
                                if (insert.Length > prefix.Length) { writer.Write(insert.ToString()); writer.WriteLine(";"); }

                                tableRows += page.Rows.Count;
                                totalProcessedRows += page.Rows.Count;
                                ReportProgress(progress, MySqlExportStage.WritingData, table, i, selectedTables.Count, totalProcessedRows, estimatedTotalRows, fileStream.Position, clock);
                            }

                            if (tableRows != totalTableRows) throw new InvalidOperationException("Source row count changed or a page was lost: " + table);
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
                    if (options.Mode != MySqlExportMode.DataOnly && selectedViews.Count > 0)
                    {
                        ReportProgress(progress, MySqlExportStage.WritingSchema, "Views", tableCount, selectedTables.Count, totalProcessedRows, estimatedTotalRows, fileStream.Position, clock);
                        foreach (var view in selectedViews)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            string ddl = db.GetViewCreateStatement(databaseName, view);
                            if (!string.IsNullOrWhiteSpace(ddl))
                            {
                                ddl = NormalizeCreateSql(ddl, options.RemoveDefiner);
                                writer.WriteLine(ddl.TrimEnd().TrimEnd(';') + ";");
                                writer.WriteLine();
                            }
                            viewCount++;
                        }
                    }

                    // 5. 函數與預存程序 (Routines)
                    if (options.Mode != MySqlExportMode.DataOnly && selectedRoutines.Count > 0)
                    {
                        ReportProgress(progress, MySqlExportStage.WritingRoutines, "Routines", tableCount, selectedTables.Count, totalProcessedRows, estimatedTotalRows, fileStream.Position, clock);
                        var allRoutines = LoadRoutines();
                        foreach (var rName in selectedRoutines)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            var routine = allRoutines.FirstOrDefault(r => r.Name.Equals(rName, StringComparison.OrdinalIgnoreCase) ||
                                (r.Type + ":" + r.Name).Equals(rName, StringComparison.OrdinalIgnoreCase));
                            if (routine != null)
                            {
                                string sql = $"SHOW CREATE {routine.Type} {MySqlImportRunner.QuoteIdentifier(databaseName)}.{MySqlImportRunner.QuoteIdentifier(routine.Name)};";
                                var dt = db.SelectSQL(sql, new Dictionary<string, object>());
                                if (dt != null && dt.Rows.Count > 0)
                                {
                                    string createKey = "Create " + (routine.Type.Equals("PROCEDURE", StringComparison.OrdinalIgnoreCase) ? "Procedure" : "Function");
                                    string ddl = dt.Columns.Contains(createKey) ? dt.Rows[0][createKey]?.ToString() : dt.Rows[0][2]?.ToString();
                                    if (!string.IsNullOrWhiteSpace(ddl))
                                    {
                                        ddl = NormalizeCreateSql(ddl, options.RemoveDefiner);
                                        writer.WriteLine("DELIMITER ;;");
                                        writer.WriteLine(ddl.TrimEnd().TrimEnd(';'));
                                        writer.WriteLine(";;");
                                        writer.WriteLine("DELIMITER ;");
                                        writer.WriteLine();
                                    }
                                }
                                routineCount++;
                            }
                        }
                    }

                    // 6. 觸發器 (Triggers)
                    if (options.Mode != MySqlExportMode.DataOnly && selectedTriggers.Count > 0)
                    {
                        ReportProgress(progress, MySqlExportStage.WritingTriggers, "Triggers", tableCount, selectedTables.Count, totalProcessedRows, estimatedTotalRows, fileStream.Position, clock);
                        foreach (var triggerName in selectedTriggers)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            string sql = $"SHOW CREATE TRIGGER {MySqlImportRunner.QuoteIdentifier(databaseName)}.{MySqlImportRunner.QuoteIdentifier(triggerName)};";
                            var dt = db.SelectSQL(sql, new Dictionary<string, object>());
                            if (dt != null && dt.Rows.Count > 0)
                            {
                                string ddl = dt.Columns.Contains("SQL Original Statement") ? dt.Rows[0]["SQL Original Statement"]?.ToString() : dt.Rows[0][2]?.ToString();
                                if (!string.IsNullOrWhiteSpace(ddl))
                                {
                                    ddl = NormalizeCreateSql(ddl, options.RemoveDefiner);
                                    writer.WriteLine("DELIMITER ;;");
                                    writer.WriteLine(ddl.TrimEnd().TrimEnd(';'));
                                    writer.WriteLine(";;");
                                    writer.WriteLine("DELIMITER ;");
                                    writer.WriteLine();
                                }
                            }
                            triggerCount++;
                        }
                    }

                    if (options.DisableUniqueChecks)
                        writer.WriteLine("SET UNIQUE_CHECKS = 1;");
                    if (options.DisableForeignKeyChecks)
                        writer.WriteLine("SET FOREIGN_KEY_CHECKS = 1;");

                    writer.Flush();

                    // 7. 寫入 Native Manifest 摘要 (用註解格式附在結尾，供匯入時自動檢驗比對)
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

                cancellation.ThrowIfCancellationRequested();
                if (inTransaction)
                {
                    RequireExecution("ROLLBACK;");
                    inTransaction = false;
                }

                // 計算產出檔案的 SHA-256
                string sha256Hex;
                long finalLength;
                using (var fs = File.OpenRead(stagingPath))
                {
                    finalLength = fs.Length;
                    using (var sha = SHA256.Create())
                    {
                        var buffer = new byte[65536];
                        int read;
                        while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            sha.TransformBlock(buffer, 0, read, buffer, 0);
                        }
                        sha.TransformFinalBlock(new byte[0], 0, 0);
                        sha256Hex = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
                    }
                }

                // 將 SHA-256 附加至 Staging 檔案尾端
                File.AppendAllText(stagingPath, $"-- Manifest-SHA256: {sha256Hex}{Environment.NewLine}", Encoding.UTF8);

                // 原子替換到目標檔案
                cancellation.ThrowIfCancellationRequested();
                finalLength = new FileInfo(stagingPath).Length;
                publicationAttempted = true;
                SqlExportPublication.Publish(stagingPath, targetPath);

                clock.Stop();
                ReportProgress(progress, MySqlExportStage.Completed, "", tableCount, selectedTables.Count, totalProcessedRows, estimatedTotalRows, finalLength, clock);

                return new MySqlExportResult(
                    MySqlExportStage.Completed,
                    targetPath,
                    tableCount,
                    viewCount,
                    routineCount,
                    triggerCount,
                    totalProcessedRows,
                    finalLength,
                    sha256Hex,
                    clock.Elapsed);
            }
            catch (OperationCanceledException)
            {
                if (!publicationAttempted) CleanStaging(stagingPath);
                clock.Stop();
                return new MySqlExportResult(
                    MySqlExportStage.Cancelled,
                    targetPath,
                    tableCount,
                    viewCount,
                    routineCount,
                    triggerCount,
                    totalProcessedRows,
                    0,
                    null,
                    clock.Elapsed,
                    "Export was cancelled by user.");
            }
            catch (Exception ex)
            {
                if (!publicationAttempted) CleanStaging(stagingPath);
                clock.Stop();
                return new MySqlExportResult(
                    MySqlExportStage.Failed,
                    targetPath,
                    tableCount,
                    viewCount,
                    routineCount,
                    triggerCount,
                    totalProcessedRows,
                    0,
                    null,
                    clock.Elapsed,
                    ex.Message + (publicationAttempted ? " Completed staging file retained at: " + stagingPath : ""));
            }
            finally
            {
                if (inTransaction)
                {
                    try { db.ExecSQL("ROLLBACK;"); } catch { }
                }
            }
        }

        private static string NormalizeCreateSql(string sql, bool removeDefiner)
        {
            if (!removeDefiner) return sql;
            var tokens = MySqlScriptReader.Tokens(sql);
            // DEFINER is only a CREATE header clause. Never run replacement on
            // string constants or on the body of a stored routine.
            for (int i = 1; i < tokens.Count; i++)
            {
                string value = tokens[i].Value.ToUpperInvariant();
                if (!tokens[i].Quoted && (value == "VIEW" || value == "TRIGGER" || value == "PROCEDURE" || value == "FUNCTION")) break;
                if (!tokens[i].Quoted && value == "DEFINER" && i + 4 < tokens.Count && tokens[i + 1].Value == "=")
                {
                    int end = i + 3;
                    if (tokens[end].Value == "@" && end + 1 < tokens.Count) end += 2;
                    if (end < tokens.Count) sql = sql.Remove(tokens[i].Start, tokens[end].Start - tokens[i].Start);
                    break;
                }
            }
            tokens = MySqlScriptReader.Tokens(sql);
            for (int i = tokens.Count - 3; i >= 0; i--)
                if (!tokens[i].Quoted && !tokens[i + 1].Quoted && !tokens[i + 2].Quoted &&
                    tokens[i].Value.Equals("SQL", StringComparison.OrdinalIgnoreCase) &&
                    tokens[i + 1].Value.Equals("SECURITY", StringComparison.OrdinalIgnoreCase) &&
                    tokens[i + 2].Value.Equals("DEFINER", StringComparison.OrdinalIgnoreCase))
                    sql = MySqlScriptReader.ReplaceToken(sql, tokens[i + 2], "INVOKER");
            return sql;
        }

        private static string ReadTableEngine(DataTable status, string table)
        {
            if (status == null || !status.Columns.Contains("Name") || !status.Columns.Contains("Engine")) return null;
            // Match the catalog identity exactly; case-sensitive servers may contain both Foo and foo.
            DataRow entry = status.Rows.Cast<DataRow>().FirstOrDefault(r => string.Equals(Convert.ToString(r["Name"]), table, StringComparison.Ordinal));
            string engine = entry == null ? null : Convert.ToString(entry["Engine"]);
            return string.IsNullOrWhiteSpace(engine) ? null : engine.Trim();
        }

        private void RequireTransactionalTables(List<string> selectedTables)
        {
            DataTable status;
            try { status = db.GetTableStatus(databaseName); }
            catch (Exception ex)
            {
                throw new InvalidOperationException(ReportText.Get(
                    "無法確認資料表引擎，不能使用一致性快照。請返回輸出設定，取消一致性快照後使用一般匯出。",
                    "Cannot verify table engines for a consistent snapshot. Return to output settings and turn off the snapshot for an ordinary export."), ex);
            }
            foreach (string table in selectedTables)
            {
                var item = new MySqlTableExportItem(table, 0) { Engine = ReadTableEngine(status, table) };
                if (!item.SupportsConsistentSnapshot)
                    throw new NotSupportedException(string.Format(ReportText.Get(
                        "一致性快照只支援已確認的 InnoDB 資料表：{0}（引擎：{1}）。請返回輸出設定，取消一致性快照後使用一般匯出；跨表資料可能不一致。",
                        "Consistent snapshot requires verified InnoDB tables: {0} (engine: {1}). Return to output settings and turn off the snapshot for an ordinary export; cross-table data may be inconsistent."),
                        table, item.Engine ?? ReportText.Get("無法確認", "unknown")));
            }
        }

        private const int MaxSourceRowBytes = 1024 * 1024;
        private const int MaxInsertBytes = 8 * 1024 * 1024;
        private static readonly HashSet<string> SpatialTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "geometry", "point", "linestring", "polygon", "multipoint", "multilinestring", "multipolygon", "geometrycollection"
        };
        private static readonly HashSet<string> ScalarTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "tinyint", "smallint", "mediumint", "int", "integer", "bigint", "decimal", "dec", "numeric", "fixed",
            "float", "double", "real", "bit", "bool", "boolean", "char", "varchar", "binary", "varbinary",
            "tinyblob", "blob", "mediumblob", "longblob", "tinytext", "text", "mediumtext", "longtext",
            "enum", "set", "json", "date", "datetime", "timestamp", "time", "year"
        };

        private void RequireExecution(string sql)
        {
            var result = db.ExecSQL(sql);
            if (!MySqlImportRunner.IsExecutionSuccess(result))
                throw new InvalidOperationException(DatabaseExecutionResultService.GetFailureReason(result));
        }

        private TableShape LoadTableShape(string table, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            DataTable columns = db.GetColumns(databaseName, table);
            cancellation.ThrowIfCancellationRequested();
            if (columns == null || columns.Rows.Count == 0) throw new InvalidOperationException("Missing column metadata: " + table);
            string nameColumn = columns.Columns.Contains("Field") ? "Field" : "COLUMN_NAME";
            string extraColumn = columns.Columns.Contains("Extra") ? "Extra" : "EXTRA";
            if (!columns.Columns.Contains(nameColumn) || !columns.Columns.Contains(extraColumn))
                throw new NotSupportedException("Generated-column metadata unavailable: " + table);
            string typeColumn = columns.Columns.Contains("Type") ? "Type" : "DATA_TYPE";
            if (!columns.Columns.Contains(typeColumn))
                throw new NotSupportedException("Column type metadata unavailable; use a native MySQL export: " + table);
            var shape = new TableShape();
            var sizes = new List<string>();
            foreach (DataRow column in columns.Rows)
            {
                cancellation.ThrowIfCancellationRequested();
                string name = Convert.ToString(column[nameColumn]);
                if (string.IsNullOrWhiteSpace(name) || !shape.Names.Add(name))
                    throw new InvalidOperationException("Missing or duplicate column identity: " + table);
                string extra = Convert.ToString(column[extraColumn]);
                string expression = columns.Columns.Contains("GenerationExpression") ? Convert.ToString(column["GenerationExpression"]) : "";
                if (extra.IndexOf("VIRTUAL", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    extra.IndexOf("STORED", StringComparison.OrdinalIgnoreCase) >= 0 || !string.IsNullOrWhiteSpace(expression)) continue;
                string declaredType = Convert.ToString(column[typeColumn]).Trim();
                int suffix = declaredType.IndexOfAny(new[] { '(', ' ', '\t', '\r', '\n' });
                string type = suffix < 0 ? declaredType : declaredType.Substring(0, suffix);
                bool spatial = SpatialTypes.Contains(type);
                if (!spatial && !ScalarTypes.Contains(type))
                    throw new NotSupportedException("Unsupported MySQL column type '" + (declaredType.Length == 0 ? "unknown" : declaredType) + "'; use a native MySQL export: " + table + "." + name);
                string quoted = MySqlImportRunner.QuoteIdentifier(name);
                var writable = new ColumnShape { Name = name, Spatial = spatial, SourceExpression = spatial ? "ST_AsWKB(" + quoted + ")" : quoted };
                shape.Writable.Add(writable);
                sizes.Add(spatial ? "(COALESCE(OCTET_LENGTH(" + writable.SourceExpression + "),0) + CASE WHEN " + quoted + " IS NULL THEN 0 ELSE 10 END)" :
                    "COALESCE(OCTET_LENGTH(" + quoted + "),0)");
            }
            if (shape.Writable.Count == 0)
                throw new NotSupportedException("Tables containing only generated columns require a native MySQL export: " + table);
            shape.RowBytesAlias = AllocateHelperAlias(shape.Names, "__mysqlpunk_row_bytes");
            int spatialIndex = 0;
            foreach (ColumnShape column in shape.Writable.Where(c => c.Spatial))
                column.SridAlias = AllocateHelperAlias(shape.Names, "__mysqlpunk_srid_" + (spatialIndex++).ToString(CultureInfo.InvariantCulture));
            shape.ByteExpression = string.Join(" + ", sizes);
            return shape;
        }

        private void CheckRowBound(string table, TableShape shape, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            string sql = "SELECT COALESCE(MAX(" + shape.ByteExpression + "),0) AS MaxRowBytes FROM " +
                MySqlImportRunner.QuoteIdentifier(databaseName) + "." + MySqlImportRunner.QuoteIdentifier(table) + ";";
            DataTable bounds = db.SelectSQL(sql);
            cancellation.ThrowIfCancellationRequested();
            if (bounds == null || bounds.Rows.Count != 1 || !bounds.Columns.Contains("MaxRowBytes") || bounds.Rows[0]["MaxRowBytes"] == DBNull.Value)
                throw new InvalidOperationException("Cannot establish a safe source row-size bound: " + table);
            long maxBytes = Convert.ToInt64(bounds.Rows[0]["MaxRowBytes"], CultureInfo.InvariantCulture);
            if (maxBytes < 0 || maxBytes > MaxSourceRowBytes)
                throw new NotSupportedException("Source row exceeds 1 MiB materialization limit; use a native MySQL export: " + table);
        }

        private sealed class TableShape
        {
            public readonly HashSet<string> Names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly List<ColumnShape> Writable = new List<ColumnShape>();
            public string ByteExpression, RowBytesAlias;
        }

        private sealed class ColumnShape
        {
            public string Name, SourceExpression, SridAlias;
            public bool Spatial;
        }

        private static string AllocateHelperAlias(HashSet<string> names, string preferred)
        {
            string alias = preferred;
            while (!names.Add(alias)) alias += "_";
            return alias;
        }

        private DataTable ReadBoundedPage(string table, TableShape shape, long offset, int limit)
        {
            string marker = shape.RowBytesAlias;
            // Server-side CASE prevents a concurrently enlarged LOB from being
            // materialized even when the user disabled the snapshot option.
            string predicate = "(" + shape.ByteExpression + ") <= " + MaxSourceRowBytes;
            var expressions = new List<string>();
            foreach (ColumnShape column in shape.Writable)
            {
                expressions.Add("CASE WHEN " + predicate + " THEN " + column.SourceExpression + " ELSE NULL END AS " + MySqlImportRunner.QuoteIdentifier(column.Name));
                if (column.Spatial)
                    expressions.Add("CASE WHEN " + predicate + " THEN ST_SRID(" + MySqlImportRunner.QuoteIdentifier(column.Name) + ") ELSE NULL END AS " + MySqlImportRunner.QuoteIdentifier(column.SridAlias));
            }
            string projection = string.Join(", ", expressions);
            DataTable page = db.SelectSQL("SELECT " + projection + ", (" + shape.ByteExpression + ") AS " + marker +
                " FROM " + MySqlImportRunner.QuoteIdentifier(databaseName) + "." + MySqlImportRunner.QuoteIdentifier(table) +
                " LIMIT " + offset.ToString(CultureInfo.InvariantCulture) + ", " + limit.ToString(CultureInfo.InvariantCulture) + ";");
            if (page == null || !page.Columns.Contains(marker)) throw new InvalidOperationException("Bounded source page query failed: " + table);
            foreach (ColumnShape column in shape.Writable)
                if (!page.Columns.Contains(column.Name) || (column.Spatial && !page.Columns.Contains(column.SridAlias)))
                    throw new InvalidOperationException("Missing bounded source column: " + table + "." + column.Name);
            foreach (DataRow row in page.Rows)
                if (row[marker] == DBNull.Value || Convert.ToInt64(row[marker], CultureInfo.InvariantCulture) < 0 || Convert.ToInt64(row[marker], CultureInfo.InvariantCulture) > MaxSourceRowBytes)
                    throw new NotSupportedException("Source row exceeds 1 MiB materialization limit: " + table);
            page.Columns.Remove(marker);
            return page;
        }

        private static string ToSpatialLiteral(object wkb, object srid, string column)
        {
            bool noWkb = wkb == null || wkb == DBNull.Value;
            bool noSrid = srid == null || srid == DBNull.Value;
            if (noWkb && noSrid) return "NULL";
            byte[] bytes = wkb as byte[];
            if (noWkb || noSrid || bytes == null || bytes.Length == 0)
                throw new InvalidOperationException("Invalid spatial WKB/SRID value: " + column);
            if (!(srid is byte) && !(srid is sbyte) && !(srid is short) && !(srid is ushort) && !(srid is int) && !(srid is uint) &&
                !(srid is long) && !(srid is ulong) && !(srid is decimal))
                throw new InvalidOperationException("Spatial SRID must be an unsigned 32-bit integer: " + column);
            decimal value = Convert.ToDecimal(srid, CultureInfo.InvariantCulture);
            if (value < 0 || value > uint.MaxValue || decimal.Truncate(value) != value)
                throw new InvalidOperationException("Spatial SRID must be an unsigned 32-bit integer: " + column);
            // Use the matching source/target default SRS axis convention. The
            // two-argument form is shared by MySQL and MariaDB; this is not a
            // cross-vendor geographic SRS migration or coordinate transform.
            return "ST_GeomFromWKB(" + ToBoundedLiteral(bytes) + "," + ((uint)value).ToString(CultureInfo.InvariantCulture) + ")";
        }

        private static string ToBoundedLiteral(object value)
        {
            if (value is byte[] && ((byte[])value).Length > MaxSourceRowBytes ||
                value is string && Encoding.UTF8.GetByteCount((string)value) > MaxSourceRowBytes)
                throw new NotSupportedException("Source value exceeds 1 MiB logical-export limit.");
            if (value is TimeSpan)
            {
                TimeSpan span = (TimeSpan)value;
                if (span.Ticks % 10 != 0) throw new NotSupportedException("TIME precision exceeds MySQL microseconds.");
                bool negative = span < TimeSpan.Zero;
                TimeSpan abs = span.Duration();
                return "'" + (negative ? "-" : "") + ((long)abs.TotalHours).ToString("00", CultureInfo.InvariantCulture) +
                    ":" + abs.Minutes.ToString("00") + ":" + abs.Seconds.ToString("00") +
                    "." + ((abs.Ticks % TimeSpan.TicksPerSecond) / 10).ToString("000000") + "'";
            }
            if (value != null && value != DBNull.Value && !(value is string) && !(value is char) && !(value is byte[]) &&
                !(value is bool) && !(value is DateTime) && !(value is decimal) && !(value is double) && !(value is float) &&
                !(value is byte) && !(value is sbyte) && !(value is short) && !(value is ushort) && !(value is int) && !(value is uint) &&
                !(value is long) && !(value is ulong) && value.GetType().FullName != "MySqlConnector.MySqlDateTime")
                throw new NotSupportedException("Unsupported MySQL value type: " + value.GetType().FullName);
            return MySqlExportService.ToMySqlLiteral(value);
        }

        private static void CleanStaging(string path)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                try { File.Delete(path); } catch { }
            }
        }

        private static void ReportProgress(
            IProgress<MySqlExportProgress> progress,
            MySqlExportStage stage,
            string currentObject,
            int completedTables,
            int totalTables,
            long processedRows,
            long estimatedTotalRows,
            long writtenBytes,
            Stopwatch clock)
        {
            if (progress == null) return;
            double elapsedSec = clock.Elapsed.TotalSeconds;
            double rowsPerSec = elapsedSec > 0.1 ? (processedRows / elapsedSec) : 0;
            TimeSpan? remaining = null;
            if (rowsPerSec > 1 && estimatedTotalRows > processedRows)
            {
                double remainingSec = (estimatedTotalRows - processedRows) / rowsPerSec;
                if (remainingSec >= 0 && remainingSec < 86400 * 7)
                    remaining = TimeSpan.FromSeconds(remainingSec);
            }

            string message;
            switch (stage)
            {
                case MySqlExportStage.ReadingCatalog:
                    message = string.Format(ReportText.Get("正在檢查資料表型別、列大小與筆數：{0}", "Checking table types, row size and row count: {0}"), currentObject);
                    break;
                case MySqlExportStage.WritingSchema:
                    message = Localization.Format("MySqlExport.ProgressTableStructure", currentObject);
                    break;
                case MySqlExportStage.WritingData:
                    message = Localization.Format("MySqlExport.ProgressTableData", currentObject);
                    break;
                case MySqlExportStage.WritingRoutines:
                    message = Localization.Format("MySqlExport.ProgressRoutine", "Routine", currentObject);
                    break;
                case MySqlExportStage.WritingTriggers:
                    message = Localization.Format("MySqlExport.ProgressTrigger", currentObject);
                    break;
                case MySqlExportStage.Completed:
                    message = Localization.T("Common.Complete");
                    break;
                default:
                    message = currentObject;
                    break;
            }

            progress.Report(new MySqlExportProgress(
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
                message));
        }
    }
}
