using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace mySQLPunk.lib.SqliteImport
{
    public sealed class SqliteImportRunner
    {
        private const string IdentifierPattern = @"""(?:""""|[^""])+""|`(?:``|[^`])+`|\[[^\]]+\]|'[^']+'|[A-Za-z0-9_$]+";

        private static readonly Regex CreateObjectRegex = new Regex(
            @"^\s*CREATE\s+(?:(?:TEMP|TEMPORARY|UNIQUE|VIRTUAL)\s+)?(?<type>TABLE|VIEW|INDEX|TRIGGER)\s+(?:IF\s+NOT\s+EXISTS\s+)?(?<name>" + IdentifierPattern + @")(?:\s+USING\s+|\s+ON\s+(?<target>" + IdentifierPattern + @"))?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex DropObjectRegex = new Regex(
            @"^\s*DROP\s+(?<type>TABLE|VIEW|INDEX|TRIGGER)\s+(?:IF\s+EXISTS\s+)?(?<name>" + IdentifierPattern + @")",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex InsertTargetRegex = new Regex(
            @"^\s*INSERT(?:\s+OR\s+(?:REPLACE|IGNORE|ROLLBACK|ABORT|FAIL))?\s+INTO\s+(?<name>" + IdentifierPattern + @")",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex BeginTxRegex = new Regex(
            @"^\s*BEGIN(?:\s+(?:IMMEDIATE|DEFERRED|EXCLUSIVE))?(?:\s+TRANSACTION)?\s*;?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex CommitTxRegex = new Regex(
            @"^\s*(?:COMMIT|END)(?:\s+TRANSACTION)?\s*;?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex RollbackTxRegex = new Regex(
            @"^\s*ROLLBACK(?:\s+TRANSACTION)?\s*;?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ManifestTableRegex = new Regex(
            @"^--\s*Manifest-Table:\s*([^\s|]+)\s*\|\s*Rows:\s*(\d+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ManifestSha256Regex = new Regex(
            @"^--\s*Manifest-SHA256:\s*([a-fA-F0-9]{64})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly IDatabase db;
        private readonly string databaseName;

        public SqliteImportRunner(IDatabase db, string databaseName)
        {
            this.db = db ?? throw new ArgumentNullException(nameof(db));
            this.databaseName = !string.IsNullOrWhiteSpace(databaseName) ? databaseName : throw new ArgumentException("Database name is required", nameof(databaseName));
        }

        public Task<SqliteImportResult> RunAsync(
            SqliteImportRequest request,
            SqliteImportOptions options,
            IProgress<SqliteImportProgress> progress,
            CancellationToken cancellation)
        {
            return Task.Run(() => RunInternal(request, options, progress, cancellation), cancellation);
        }

        private SqliteImportResult RunInternal(
            SqliteImportRequest request,
            SqliteImportOptions options,
            IProgress<SqliteImportProgress> progress,
            CancellationToken cancellation)
        {
            var clock = Stopwatch.StartNew();
            var log = new SqliteImportLog(request.JobId);
            long fileBytes = 0;
            if (File.Exists(request.SourcePath)) fileBytes = new FileInfo(request.SourcePath).Length;

            log.LogInfo($"Target SQLite Database: {databaseName}");
            log.LogInfo($"Source SQL File: {request.SourcePath} ({fileBytes:N0} bytes)");
            log.LogInfo($"Options: ContinueOnError={options.ContinueOnError}, Strategy={options.ExistingObjectStrategy}, WrapInTransaction={options.WrapInTransaction}");

            int executed = 0;
            int failed = 0;
            int skipped = 0;
            long totalRowsAffected = 0;
            var errors = new List<string>();
            var manifestTables = new List<Tuple<string, long>>();
            bool readingManifest = false;
            bool haltedOnError = false;

            var existingTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var existingViews = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var skippedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            bool runnerManagedTx = false;

            try
            {
                // 1. 若啟用 SpatiaLite 自動加載，嘗試載入模組
                if (options.AutoLoadSpatiaLite && db is my_sqlite sqliteDb)
                {
                    try
                    {
                        sqliteDb.RetryLoadSpatiaLite();
                        if (sqliteDb.SpatiaLiteEnabled)
                        {
                            log.LogInfo("SpatiaLite runtime dynamic extension (mod_spatialite) loaded successfully.");
                        }
                        else if (!string.IsNullOrWhiteSpace(sqliteDb.SpatiaLiteLoadError))
                        {
                            log.LogInfo("SpatiaLite runtime extension note: " + sqliteDb.SpatiaLiteLoadError);
                        }
                    }
                    catch (Exception ex)
                    {
                        log.LogInfo("SpatiaLite runtime extension attempt: " + ex.Message);
                    }
                }

                // 2. 設定 PRAGMA 環境變數以加快寫入
                if (options.PragmaForeignKeysOff)
                {
                    try { db.ExecSQL("PRAGMA foreign_keys = OFF;"); } catch { }
                }
                if (options.PragmaSynchronousOff)
                {
                    try { db.ExecSQL("PRAGMA synchronous = OFF;"); } catch { }
                }

                // 3. 快取目標資料庫中既有物件
                try
                {
                    var tList = db.GetTables(databaseName);
                    if (tList != null) existingTables.UnionWith(tList);
                }
                catch { }

                try
                {
                    var vList = db.GetViews(databaseName);
                    if (vList != null) existingViews.UnionWith(vList);
                }
                catch { }

                // 4. 預先檢查腳本是否自帶 BEGIN TRANSACTION
                bool scriptHasExplicitTx = CheckIfScriptContainsExplicitTx(request.SourcePath);
                if (options.WrapInTransaction && !scriptHasExplicitTx)
                {
                    var beginRes = db.ExecSQL("BEGIN IMMEDIATE;");
                    if (IsExecutionSuccess(beginRes))
                    {
                        runnerManagedTx = true;
                        log.LogInfo("Transaction started by runner: BEGIN IMMEDIATE;");
                    }
                }

                using (var fs = new FileStream(request.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536))
                using (var reader = new StreamReader(fs, Encoding.UTF8, true, 65536))
                {
                    var currentBatch = new StringBuilder();
                    bool inSingleQuote = false;
                    bool inDoubleQuote = false;
                    bool inBracket = false;
                    bool inBlockComment = false;
                    int batchStartLine = 1;
                    int currentLine = 0;
                    string line;
                    long lastReportMs = 0;

                    while ((line = reader.ReadLine()) != null)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        currentLine++;

                        string trimmed = line.Trim();

                        // 檢查 Native Manifest
                        if (trimmed.StartsWith("--"))
                        {
                            if (trimmed.Contains("mySQLPunk Native Manifest") && !readingManifest)
                            {
                                readingManifest = true;
                                log.LogInfo("Detected Native Manifest in SQL script footer.");
                            }

                            var match = ManifestTableRegex.Match(trimmed);
                            if (match.Success && long.TryParse(match.Groups[2].Value, out long r))
                            {
                                manifestTables.Add(Tuple.Create(match.Groups[1].Value, r));
                            }

                            if (!inSingleQuote && !inDoubleQuote && !inBlockComment)
                            {
                                continue;
                            }
                        }

                        if (currentBatch.Length == 0) batchStartLine = currentLine;

                        // 逐字元狀態機解析語句結束分號
                        int len = line.Length;
                        int lastSegmentStart = 0;

                        for (int i = 0; i < len; i++)
                        {
                            char c = line[i];

                            if (inBlockComment)
                            {
                                if (c == '*' && i + 1 < len && line[i + 1] == '/')
                                {
                                    inBlockComment = false;
                                    i++;
                                }
                                continue;
                            }

                            if (!inSingleQuote && !inDoubleQuote && !inBracket)
                            {
                                if (c == '-' && i + 1 < len && line[i + 1] == '-')
                                {
                                    // 遇到單行註解，本行此處後續為註解
                                    currentBatch.Append(line.Substring(lastSegmentStart, i - lastSegmentStart));
                                    lastSegmentStart = len;
                                    break;
                                }
                                if (c == '/' && i + 1 < len && line[i + 1] == '*')
                                {
                                    inBlockComment = true;
                                    i++;
                                    continue;
                                }
                            }

                            if (c == '\'' && !inDoubleQuote && !inBracket)
                            {
                                inSingleQuote = !inSingleQuote;
                                continue;
                            }

                            if (c == '"' && !inSingleQuote && !inBracket)
                            {
                                inDoubleQuote = !inDoubleQuote;
                                continue;
                            }

                            if (c == '[' && !inSingleQuote && !inDoubleQuote)
                            {
                                inBracket = true;
                                continue;
                            }

                            if (c == ']' && inBracket)
                            {
                                inBracket = false;
                                continue;
                            }

                            // 偵測到語句結束分號
                            if (c == ';' && !inSingleQuote && !inDoubleQuote && !inBracket)
                            {
                                currentBatch.Append(line.Substring(lastSegmentStart, i - lastSegmentStart + 1));
                                lastSegmentStart = i + 1;

                                string statementSql = currentBatch.ToString().Trim();
                                currentBatch.Clear();

                                if (!string.IsNullOrWhiteSpace(statementSql))
                                {
                                    bool continueRunning = ExecuteStatement(
                                        statementSql,
                                        batchStartLine,
                                        options,
                                        log,
                                        existingTables,
                                        existingViews,
                                        skippedTables,
                                        ref executed,
                                        ref failed,
                                        ref skipped,
                                        ref totalRowsAffected,
                                        errors);

                                    if (!continueRunning)
                                    {
                                        haltedOnError = true;
                                        break;
                                    }

                                    // 每 150ms 節流回報進度至 UI
                                    if (clock.ElapsedMilliseconds - lastReportMs > 150)
                                    {
                                        lastReportMs = clock.ElapsedMilliseconds;
                                        ReportProgress(progress, fs.Position, fileBytes, executed, failed, skipped, totalRowsAffected, clock, statementSql);
                                    }
                                }

                                batchStartLine = currentLine;
                            }
                        }

                        if (haltedOnError) break;

                        if (lastSegmentStart < len)
                        {
                            currentBatch.AppendLine(line.Substring(lastSegmentStart));
                        }
                    }

                    // 處理檔尾若有未以分號結尾的最後一條語句
                    if (!haltedOnError && currentBatch.Length > 0)
                    {
                        string remainingSql = currentBatch.ToString().Trim();
                        if (!string.IsNullOrWhiteSpace(remainingSql) && !remainingSql.StartsWith("--"))
                        {
                            ExecuteStatement(
                                remainingSql,
                                batchStartLine,
                                options,
                                log,
                                existingTables,
                                existingViews,
                                skippedTables,
                                ref executed,
                                ref failed,
                                ref skipped,
                                ref totalRowsAffected,
                                errors);
                        }
                    }
                }

                // 5. 交易提交或回滾
                if (runnerManagedTx)
                {
                    if (haltedOnError && !options.ContinueOnError)
                    {
                        try { db.ExecSQL("ROLLBACK;"); } catch { }
                        log.LogInfo("Runner managed transaction rolled back due to error: ROLLBACK;");
                    }
                    else
                    {
                        try { db.ExecSQL("COMMIT;"); } catch { }
                        log.LogInfo("Runner managed transaction committed successfully: COMMIT;");
                    }
                }

                // 恢復 foreign_keys
                if (options.PragmaForeignKeysOff)
                {
                    try { db.ExecSQL("PRAGMA foreign_keys = ON;"); } catch { }
                }

                clock.Stop();

                // 6. Native Manifest 自動比對
                bool manifestVerified = false;
                string manifestReport = null;
                if (manifestTables.Count > 0)
                {
                    int matchCount = 0;
                    var reportBuilder = new StringBuilder();
                    foreach (var pair in manifestTables)
                    {
                        string tName = pair.Item1;
                        long expected = pair.Item2;
                        long actual = 0;
                        try { actual = db.CountRows(databaseName, tName); } catch { }
                        bool matched = (actual == expected);
                        if (matched) matchCount++;
                        log.LogManifestVerification(tName, expected, actual, matched);
                        reportBuilder.Append($"{tName}: {actual}/{expected} {(matched ? "OK" : "MISMATCH")}; ");
                    }
                    manifestVerified = (matchCount == manifestTables.Count);
                    manifestReport = reportBuilder.ToString().Trim();
                    log.LogInfo($"Manifest Verification: {matchCount}/{manifestTables.Count} tables matched row counts exactly. Verified={manifestVerified}");
                }

                var status = haltedOnError
                    ? SqliteImportStatus.Failed
                    : (failed > 0 && !options.ContinueOnError ? SqliteImportStatus.Failed : SqliteImportStatus.Completed);

                var finalResult = new SqliteImportResult(
                    status,
                    executed,
                    failed,
                    skipped,
                    totalRowsAffected,
                    log.Path,
                    clock.Elapsed,
                    errors,
                    manifestVerified,
                    manifestReport);

                log.WriteEnd(finalResult);
                log.Dispose();

                ReportProgress(progress, fileBytes, fileBytes, executed, failed, skipped, totalRowsAffected, clock, null, true);

                return finalResult;
            }
            catch (OperationCanceledException)
            {
                clock.Stop();
                if (runnerManagedTx)
                {
                    try { db.ExecSQL("ROLLBACK;"); } catch { }
                }
                log.LogInfo("Import cancelled by user. Rolled back any runner-managed transaction.");
                var cancelResult = new SqliteImportResult(
                    SqliteImportStatus.Cancelled,
                    executed,
                    failed,
                    skipped,
                    totalRowsAffected,
                    log.Path,
                    clock.Elapsed,
                    new[] { "Import cancelled by user." });
                log.WriteEnd(cancelResult);
                log.Dispose();
                return cancelResult;
            }
            catch (Exception ex)
            {
                clock.Stop();
                if (runnerManagedTx)
                {
                    try { db.ExecSQL("ROLLBACK;"); } catch { }
                }
                log.LogInfo("Import terminated with unhandled exception: " + ex.Message);
                errors.Add(ex.Message);
                var errResult = new SqliteImportResult(
                    SqliteImportStatus.Failed,
                    executed,
                    failed,
                    skipped,
                    totalRowsAffected,
                    log.Path,
                    clock.Elapsed,
                    errors);
                log.WriteEnd(errResult);
                log.Dispose();
                return errResult;
            }
        }

        private bool ExecuteStatement(
            string sql,
            int lineNumber,
            SqliteImportOptions options,
            SqliteImportLog log,
            HashSet<string> existingTables,
            HashSet<string> existingViews,
            HashSet<string> skippedTables,
            ref int executed,
            ref int failed,
            ref int skipped,
            ref long totalRowsAffected,
            List<string> errors)
        {
            string trimmed = sql.Trim();
            string preview = MakeStatementPreview(trimmed);

            // 1. 處理 CREATE 物件策略
            var createMatch = CreateObjectRegex.Match(trimmed);
            if (createMatch.Success)
            {
                string objType = createMatch.Groups["type"].Value.ToUpperInvariant();
                string objName = Unquote(createMatch.Groups["name"].Value);

                bool isExisting = false;
                if (objType == "TABLE") isExisting = existingTables.Contains(objName);
                else if (objType == "VIEW") isExisting = existingViews.Contains(objName);

                if (isExisting)
                {
                    switch (options.ExistingObjectStrategy)
                    {
                        case SqliteExistingObjectStrategy.AbortIfExists:
                            string abortMsg = $"Object {objType} \"{objName}\" already exists in database (AbortIfExists policy).";
                            log.LogStatement(lineNumber, preview, false, abortMsg, "ABORT");
                            failed++;
                            errors.Add(abortMsg);
                            return false; // 中斷匯入

                        case SqliteExistingObjectStrategy.SkipExisting:
                            log.LogStatement(lineNumber, preview, true, null, "SKIP_EXISTING");
                            skipped++;
                            if (objType == "TABLE") skippedTables.Add(objName);
                            return true; // 略過本句，繼續

                        case SqliteExistingObjectStrategy.DropAndRecreate:
                            string dropSql = $"DROP {objType} IF EXISTS {QuoteIdentifier(objName)};";
                            db.ExecSQL(dropSql);
                            log.LogStatement(lineNumber, $"DROP {objType} \"{objName}\" for recreation", true, null, "DROP_EXISTING");
                            if (objType == "TABLE") existingTables.Remove(objName);
                            else if (objType == "VIEW") existingViews.Remove(objName);
                            break;

                        case SqliteExistingObjectStrategy.OverwriteKeepExisting:
                            log.LogStatement(lineNumber, preview, true, null, "KEEP_STRUCTURE");
                            skipped++;
                            return true;
                    }
                }
            }

            // 2. 處理 INSERT 資料策略 (若該表於前面被標記為 SkipExisting，則一併略過 INSERT)
            var insertMatch = InsertTargetRegex.Match(trimmed);
            if (insertMatch.Success)
            {
                string targetTable = Unquote(insertMatch.Groups["name"].Value);
                if (skippedTables.Contains(targetTable))
                {
                    skipped++;
                    log.LogStatement(lineNumber, preview, true, null, "SKIP_DATA_FOR_SKIPPED_TABLE");
                    return true;
                }
            }

            // 3. 實際執行 SQL
            var res = db.ExecSQL(trimmed);
            bool success = IsExecutionSuccess(res);

            if (success)
            {
                executed++;
                long affected = 0;
                if (res != null && res.ContainsKey("rowsAffected") && long.TryParse(res["rowsAffected"], out long a))
                {
                    affected = a;
                    totalRowsAffected += a;
                }

                // 若為 CREATE TABLE / VIEW 成功，更新快取
                if (createMatch.Success)
                {
                    string objType = createMatch.Groups["type"].Value.ToUpperInvariant();
                    string objName = Unquote(createMatch.Groups["name"].Value);
                    if (objType == "TABLE") existingTables.Add(objName);
                    else if (objType == "VIEW") existingViews.Add(objName);
                }

                log.LogStatement(lineNumber, preview, true, null, affected > 0 ? $"OK (+{affected} rows)" : "OK");
                return true;
            }
            else
            {
                failed++;
                string reason = DatabaseExecutionResultService.GetFailureReason(res);
                string errText = $"Line {lineNumber}: {reason}";
                errors.Add(errText);
                log.LogStatement(lineNumber, preview, false, reason, "FAIL");

                if (!options.ContinueOnError)
                {
                    return false; // 停止匯入
                }
                return true; // 繼續略過執行
            }
        }

        private static bool IsExecutionSuccess(Dictionary<string, string> result)
        {
            if (result == null) return false;
            if (result.TryGetValue("status", out string status))
            {
                return string.Equals(status, "OK", StringComparison.OrdinalIgnoreCase);
            }
            return true;
        }

        private static string Unquote(string identifier)
        {
            if (string.IsNullOrWhiteSpace(identifier)) return string.Empty;
            string s = identifier.Trim();
            if ((s.StartsWith("\"") && s.EndsWith("\"")) ||
                (s.StartsWith("`") && s.EndsWith("`")) ||
                (s.StartsWith("'") && s.EndsWith("'")))
            {
                return s.Substring(1, s.Length - 2).Replace("\"\"", "\"").Replace("``", "`").Replace("''", "'");
            }
            if (s.StartsWith("[") && s.EndsWith("]"))
            {
                return s.Substring(1, s.Length - 2);
            }
            return s;
        }

        private static string QuoteIdentifier(string name)
        {
            name = name ?? string.Empty;
            return "\"" + name.Replace("\"", "\"\"") + "\"";
        }

        private static string MakeStatementPreview(string sql)
        {
            if (string.IsNullOrWhiteSpace(sql)) return string.Empty;
            string singleLine = Regex.Replace(sql, @"\s+", " ").Trim();
            if (singleLine.Length > 120) return singleLine.Substring(0, 117) + "...";
            return singleLine;
        }

        private static bool CheckIfScriptContainsExplicitTx(string filePath)
        {
            if (!File.Exists(filePath)) return false;
            try
            {
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096))
                using (var reader = new StreamReader(fs, Encoding.UTF8, true, 4096))
                {
                    string line;
                    int count = 0;
                    while ((line = reader.ReadLine()) != null && count < 200)
                    {
                        count++;
                        string trimmed = line.Trim();
                        if (BeginTxRegex.IsMatch(trimmed)) return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private static void ReportProgress(
            IProgress<SqliteImportProgress> progress,
            long currentBytes,
            long totalBytes,
            int executed,
            int failed,
            int skipped,
            long totalRowsAffected,
            Stopwatch clock,
            string currentPreview,
            bool isComplete = false)
        {
            if (progress == null) return;

            double elapsedSec = clock.Elapsed.TotalSeconds;
            double bytesPerSec = elapsedSec > 0.05 ? currentBytes / elapsedSec : 0;
            TimeSpan? remaining = null;
            if (bytesPerSec > 0 && totalBytes > currentBytes)
            {
                double remainingSec = (totalBytes - currentBytes) / bytesPerSec;
                if (remainingSec < 86400) remaining = TimeSpan.FromSeconds(remainingSec);
            }

            string msg;
            if (isComplete)
            {
                msg = $"Import execution finished: {executed} executed, {failed} failed, {skipped} skipped, {totalRowsAffected:N0} rows affected.";
            }
            else
            {
                msg = $"Executing: {executed} executed, {failed} failed, {skipped} skipped...";
            }

            progress.Report(new SqliteImportProgress(
                currentBytes,
                totalBytes,
                executed,
                failed,
                skipped,
                totalRowsAffected,
                bytesPerSec,
                clock.Elapsed,
                remaining,
                msg,
                currentPreview));
        }
    }
}
