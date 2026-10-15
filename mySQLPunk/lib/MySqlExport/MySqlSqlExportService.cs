using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
            foreach (var table in tables)
            {
                if (string.IsNullOrWhiteSpace(table)) continue;
                long count = 0;
                try { count = db.CountRows(databaseName, table); } catch { }
                list.Add(new MySqlTableExportItem(table, count, true));
            }
            return list;
        }

        public List<string> LoadViews()
        {
            try { return db.GetViews(databaseName) ?? new List<string>(); }
            catch { return new List<string>(); }
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
            return Task.Run(() => ExportInternal(options, progress, cancellation), cancellation);
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

            // 預估總列數
            if (options.Mode != MySqlExportMode.SchemaOnly)
            {
                foreach (var table in selectedTables)
                {
                    try { estimatedTotalRows += db.CountRows(databaseName, table); } catch { }
                }
            }

            bool inTransaction = false;
            try
            {
                cancellation.ThrowIfCancellationRequested();

                // 啟動可選一致性快照 (single-transaction)
                if (options.SingleTransaction && options.Mode != MySqlExportMode.SchemaOnly)
                {
                    try
                    {
                        db.ExecSQL("SET TRANSACTION ISOLATION LEVEL REPEATABLE READ;");
                        db.ExecSQL("START TRANSACTION WITH CONSISTENT SNAPSHOT;");
                        inTransaction = true;
                    }
                    catch
                    {
                        // 若無足夠權限或引擎不支援一致性快照，安全降級為普通讀取
                        inTransaction = false;
                    }
                }

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
                    if (options.DisableForeignKeyChecks)
                        writer.WriteLine("SET FOREIGN_KEY_CHECKS = 0;");
                    if (options.IncludeCreateDatabase)
                    {
                        var info = db.GetDatabaseInfo(databaseName) ?? new Dictionary<string, string>();
                        string charset = info.ContainsKey("character_set") ? info["character_set"] : "utf8mb4";
                        string collation = info.ContainsKey("collation") ? info["collation"] : "utf8mb4_general_ci";
                        writer.WriteLine($"CREATE DATABASE IF NOT EXISTS `{databaseName}` DEFAULT CHARACTER SET {charset} COLLATE {collation};");
                    }
                    if (options.IncludeUseDatabase)
                        writer.WriteLine($"USE `{databaseName}`;");
                    writer.WriteLine();

                    // 1. DROP 語句
                    if (options.IncludeDropStatements && options.Mode != MySqlExportMode.DataOnly)
                    {
                        foreach (var tr in selectedTriggers) writer.WriteLine($"DROP TRIGGER IF EXISTS `{tr}`;");
                        foreach (var r in selectedRoutines)
                        {
                            string type = r.StartsWith("PROCEDURE:", StringComparison.OrdinalIgnoreCase) ? "PROCEDURE" : "FUNCTION";
                            string name = r.Contains(":") ? r.Substring(r.IndexOf(':') + 1) : r;
                            writer.WriteLine($"DROP {type} IF EXISTS `{name}`;");
                        }
                        foreach (var v in selectedViews) writer.WriteLine($"DROP VIEW IF EXISTS `{v}`;");
                        foreach (var t in selectedTables) writer.WriteLine($"DROP TABLE IF EXISTS `{t}`;");
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
                        int batchSize = options.InsertBatchSize > 0 ? options.InsertBatchSize : 1000;
                        for (int i = 0; i < selectedTables.Count; i++)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            string table = selectedTables[i];
                            long tableRows = 0;
                            long totalTableRows = 0;
                            try { totalTableRows = db.CountRows(databaseName, table); } catch { }

                            ReportProgress(progress, MySqlExportStage.WritingData, table, i, selectedTables.Count, totalProcessedRows, estimatedTotalRows, fileStream.Position, clock);

                            while (tableRows < totalTableRows || totalTableRows == 0)
                            {
                                cancellation.ThrowIfCancellationRequested();
                                DataTable page = db.SelectTablePage(databaseName, table, tableRows, batchSize);
                                if (page == null || page.Rows.Count == 0) break;

                                writer.Write("INSERT INTO `");
                                writer.Write(table.Replace("`", "``"));
                                writer.Write("` (");
                                for (int c = 0; c < page.Columns.Count; c++)
                                {
                                    if (c > 0) writer.Write(", ");
                                    writer.Write("`");
                                    writer.Write(page.Columns[c].ColumnName.Replace("`", "``"));
                                    writer.Write("`");
                                }
                                writer.WriteLine(") VALUES");

                                for (int r = 0; r < page.Rows.Count; r++)
                                {
                                    writer.Write("(");
                                    for (int c = 0; c < page.Columns.Count; c++)
                                    {
                                        if (c > 0) writer.Write(", ");
                                        writer.Write(MySqlExportService.ToMySqlLiteral(page.Rows[r][c]));
                                    }
                                    writer.Write(r == page.Rows.Count - 1 ? ");" : "),");
                                    writer.WriteLine();
                                }

                                tableRows += page.Rows.Count;
                                totalProcessedRows += page.Rows.Count;
                                ReportProgress(progress, MySqlExportStage.WritingData, table, i, selectedTables.Count, totalProcessedRows, estimatedTotalRows, fileStream.Position, clock);
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
                    if (options.Mode != MySqlExportMode.DataOnly && selectedViews.Count > 0)
                    {
                        ReportProgress(progress, MySqlExportStage.WritingSchema, "Views", tableCount, selectedTables.Count, totalProcessedRows, estimatedTotalRows, fileStream.Position, clock);
                        foreach (var view in selectedViews)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            string ddl = db.GetViewCreateStatement(databaseName, view);
                            if (!string.IsNullOrWhiteSpace(ddl))
                            {
                                ddl = MySqlExportService.NormalizeObjectCreateSql(ddl, options.RemoveDefiner);
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
                                string sql = $"SHOW CREATE {routine.Type} `{databaseName}`.`{routine.Name}`;";
                                var dt = db.SelectSQL(sql, new Dictionary<string, object>());
                                if (dt != null && dt.Rows.Count > 0)
                                {
                                    string createKey = "Create " + (routine.Type.Equals("PROCEDURE", StringComparison.OrdinalIgnoreCase) ? "Procedure" : "Function");
                                    string ddl = dt.Columns.Contains(createKey) ? dt.Rows[0][createKey]?.ToString() : dt.Rows[0][2]?.ToString();
                                    if (!string.IsNullOrWhiteSpace(ddl))
                                    {
                                        ddl = MySqlExportService.NormalizeObjectCreateSql(ddl, options.RemoveDefiner);
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
                            string sql = $"SHOW CREATE TRIGGER `{databaseName}`.`{triggerName}`;";
                            var dt = db.SelectSQL(sql, new Dictionary<string, object>());
                            if (dt != null && dt.Rows.Count > 0)
                            {
                                string ddl = dt.Columns.Contains("SQL Original Statement") ? dt.Rows[0]["SQL Original Statement"]?.ToString() : dt.Rows[0][2]?.ToString();
                                if (!string.IsNullOrWhiteSpace(ddl))
                                {
                                    ddl = MySqlExportService.NormalizeObjectCreateSql(ddl, options.RemoveDefiner);
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

                // 將 SHA-256 附加至 Staging 檔案尾端
                File.AppendAllText(stagingPath, $"-- Manifest-SHA256: {sha256Hex}{Environment.NewLine}", Encoding.UTF8);

                // 原子替換到目標檔案
                if (File.Exists(targetPath)) File.Delete(targetPath);
                File.Move(stagingPath, targetPath);

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
                CleanStaging(stagingPath);
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
                CleanStaging(stagingPath);
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
                    ex.Message);
            }
            finally
            {
                if (inTransaction)
                {
                    try { db.ExecSQL("COMMIT;"); } catch { }
                }
            }
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
