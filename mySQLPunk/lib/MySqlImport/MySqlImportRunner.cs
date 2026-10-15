using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace mySQLPunk.lib.MySqlImport
{
    public sealed class MySqlImportRunner
    {
        private readonly IDatabase db;
        private readonly string databaseName;

        public MySqlImportRunner(IDatabase db, string databaseName)
        {
            this.db = db ?? throw new ArgumentNullException(nameof(db));
            this.databaseName = !string.IsNullOrWhiteSpace(databaseName) ? databaseName : throw new ArgumentException("Database name is required", nameof(databaseName));
        }

        public Task<MySqlImportResult> RunAsync(
            MySqlImportRequest request,
            MySqlImportOptions options,
            IProgress<MySqlImportProgress> progress,
            CancellationToken cancellation)
        {
            return Task.Run(() => RunInternal(request, options, progress, cancellation), cancellation);
        }

        private MySqlImportResult RunInternal(
            MySqlImportRequest request,
            MySqlImportOptions options,
            IProgress<MySqlImportProgress> progress,
            CancellationToken cancellation)
        {
            var clock = Stopwatch.StartNew();
            var log = new MySqlImportLog(request.JobId);
            long fileBytes = 0;
            if (File.Exists(request.SourcePath)) fileBytes = new FileInfo(request.SourcePath).Length;

            log.LogInfo($"Target Database: {databaseName}");
            log.LogInfo($"Source SQL File: {request.SourcePath} ({fileBytes:N0} bytes)");
            log.LogInfo($"Options: ContinueOnError={options.ContinueOnError}, Strategy={options.ExistingObjectStrategy}");

            int executed = 0;
            int failed = 0;
            int skipped = 0;
            var errors = new List<string>();
            var manifestTables = new List<Tuple<string, long>>();
            bool readingManifest = false;

            try
            {
                using (var fs = new FileStream(request.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536))
                using (var reader = new StreamReader(fs, Encoding.UTF8, true))
                {
                    string delimiter = ";";
                    var currentBatch = new StringBuilder();
                    int batchStartLine = 1;
                    int currentLine = 0;
                    string line;

                    while ((line = reader.ReadLine()) != null)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        currentLine++;

                        string trimmed = line.Trim();

                        // 檢查 Manifest
                        if (trimmed.StartsWith("--"))
                        {
                            if (trimmed.Contains("mySQLPunk Native Manifest")) readingManifest = true;
                            else if (readingManifest)
                            {
                                var match = Regex.Match(trimmed, @"^--\s*Manifest-Table:\s*([^\s|]+)\s*\|\s*Rows:\s*(\d+)", RegexOptions.IgnoreCase);
                                if (match.Success && long.TryParse(match.Groups[2].Value, out long r))
                                {
                                    manifestTables.Add(Tuple.Create(match.Groups[1].Value, r));
                                }
                            }
                            continue;
                        }

                        // DELIMITER 切換
                        if (trimmed.StartsWith("DELIMITER", StringComparison.OrdinalIgnoreCase))
                        {
                            string newDelim = trimmed.Substring(9).Trim();
                            if (!string.IsNullOrWhiteSpace(newDelim))
                            {
                                delimiter = newDelim;
                                currentBatch.Clear();
                                batchStartLine = currentLine + 1;
                                continue;
                            }
                        }

                        if (currentBatch.Length == 0) batchStartLine = currentLine;
                        currentBatch.AppendLine(line);

                        // 判斷是否為批次結尾
                        if (trimmed.EndsWith(delimiter, StringComparison.Ordinal))
                        {
                            string sql = currentBatch.ToString().TrimEnd();
                            if (sql.EndsWith(delimiter, StringComparison.Ordinal))
                                sql = sql.Substring(0, sql.Length - delimiter.Length).Trim();

                            if (!string.IsNullOrWhiteSpace(sql))
                            {
                                bool ok = ExecuteBatchStatement(sql, batchStartLine, options, log, ref executed, ref failed, ref skipped, errors);
                                if (!ok && !options.ContinueOnError)
                                {
                                    log.LogInfo("Execution stopped on error as requested by options.");
                                    break;
                                }
                            }

                            currentBatch.Clear();
                            batchStartLine = currentLine + 1;
                            ReportProgress(progress, request.JobId, MySqlImportStage.Executing, fileBytes, fs.Position, executed, failed, skipped, clock, sql);
                        }
                    }

                    // 處理尾端剩餘語句
                    if (currentBatch.Length > 0 && (failed == 0 || options.ContinueOnError))
                    {
                        string remainingSql = currentBatch.ToString().Trim();
                        if (!string.IsNullOrWhiteSpace(remainingSql))
                        {
                            ExecuteBatchStatement(remainingSql, batchStartLine, options, log, ref executed, ref failed, ref skipped, errors);
                        }
                    }
                }

                // 若有 Native Manifest，自動驗證目標資料庫實際列數
                if (manifestTables.Count > 0)
                {
                    log.LogInfo("Verifying restored database tables against Native Manifest...");
                    foreach (var pair in manifestTables)
                    {
                        long actual = -1;
                        try { actual = db.CountRows(databaseName, pair.Item1); } catch { }
                        bool match = actual == pair.Item2;
                        log.LogManifestVerification(pair.Item1, pair.Item2, actual, match);
                    }
                }

                clock.Stop();
                MySqlImportStatus status = failed == 0
                    ? MySqlImportStatus.Completed
                    : (executed > 0 ? MySqlImportStatus.CompletedWithErrors : MySqlImportStatus.Failed);

                string summary = string.Format(
                    "Execution finished in {0:F1}s: {1} executed, {2} failed, {3} skipped.",
                    clock.Elapsed.TotalSeconds, executed, failed, skipped);

                var finalResult = new MySqlImportResult(
                    request.JobId,
                    status,
                    fileBytes,
                    executed,
                    failed,
                    skipped,
                    log.Path,
                    clock.Elapsed,
                    summary,
                    errors);

                log.WriteEnd(finalResult);
                log.Dispose();
                ReportProgress(progress, request.JobId, MySqlImportStage.Finished, fileBytes, fileBytes, executed, failed, skipped, clock, summary);

                return finalResult;
            }
            catch (OperationCanceledException)
            {
                clock.Stop();
                log.LogInfo("Execution cancelled by user.");
                var cancelResult = new MySqlImportResult(
                    request.JobId,
                    MySqlImportStatus.Cancelled,
                    fileBytes,
                    executed,
                    failed,
                    skipped,
                    log.Path,
                    clock.Elapsed,
                    "Import was cancelled by user.",
                    errors);
                log.WriteEnd(cancelResult);
                log.Dispose();
                return cancelResult;
            }
            catch (Exception ex)
            {
                clock.Stop();
                log.LogInfo("Fatal error: " + ex.Message);
                errors.Add(ex.Message);
                var failResult = new MySqlImportResult(
                    request.JobId,
                    MySqlImportStatus.Failed,
                    fileBytes,
                    executed,
                    failed,
                    skipped,
                    log.Path,
                    clock.Elapsed,
                    ex.Message,
                    errors);
                log.WriteEnd(failResult);
                log.Dispose();
                return failResult;
            }
        }

        private bool ExecuteBatchStatement(
            string sql,
            int line,
            MySqlImportOptions options,
            MySqlImportLog log,
            ref int executed,
            ref int failed,
            ref int skipped,
            List<string> errors)
        {
            string preview = sql.Length > 120 ? sql.Substring(0, 120).Replace("\r", " ").Replace("\n", " ") + "..." : sql.Replace("\r", " ").Replace("\n", " ");

            try
            {
                db.ExecSQL(sql);
                executed++;
                log.LogStatement(line, preview, true);
                return true;
            }
            catch (Exception ex)
            {
                failed++;
                string err = ex.Message;
                errors.Add($"Line {line}: {err}");
                string action = options.ContinueOnError ? "SkipBatch" : "HaltExecution";
                log.LogStatement(line, preview, false, err, action);
                return false;
            }
        }

        private static void ReportProgress(
            IProgress<MySqlImportProgress> progress,
            Guid jobId,
            MySqlImportStage stage,
            long totalBytes,
            long processedBytes,
            int executed,
            int failed,
            int skipped,
            Stopwatch clock,
            string currentPreview)
        {
            if (progress == null) return;
            double elapsedSec = clock.Elapsed.TotalSeconds;
            double bytesPerSec = elapsedSec > 0.1 ? (processedBytes / elapsedSec) : 0;
            TimeSpan? remaining = null;
            if (bytesPerSec > 1024 && totalBytes > processedBytes)
            {
                double remainingSec = (totalBytes - processedBytes) / bytesPerSec;
                if (remainingSec >= 0 && remainingSec < 86400 * 7)
                    remaining = TimeSpan.FromSeconds(remainingSec);
            }

            string msg = string.Format("Importing... {0} statements ({1:N0} KB / {2:N0} KB)", executed, processedBytes / 1024, totalBytes / 1024);
            progress.Report(new MySqlImportProgress(
                jobId,
                stage,
                totalBytes,
                processedBytes,
                executed,
                failed,
                skipped,
                bytesPerSec,
                clock.Elapsed,
                remaining,
                currentPreview,
                msg));
        }
    }
}
