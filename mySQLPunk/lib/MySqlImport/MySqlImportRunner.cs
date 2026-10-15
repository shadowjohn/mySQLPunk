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
        private const string IdentifierPattern = @"`(?:``|[^`])+`|[A-Za-z0-9_$]+";

        private static readonly Regex UseDatabaseRegex = new Regex(
            @"^\s*USE\s+(?<database>" + IdentifierPattern + @")\s*;?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex CreateDatabaseRegex = new Regex(
            @"^\s*CREATE\s+(?:DATABASE|SCHEMA)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex DropObjectRegex = new Regex(
            @"^\s*DROP\s+(?:TEMPORARY\s+)?(?<type>TABLE|VIEW|FUNCTION|PROCEDURE|TRIGGER)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex CreateObjectRegex = new Regex(
            @"^\s*CREATE\b[\s\S]*?\b(?<type>TABLE|VIEW|FUNCTION|PROCEDURE|TRIGGER)\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:(?<database>" + IdentifierPattern + @")\s*\.\s*)?(?<name>" + IdentifierPattern + @")",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex InsertTargetRegex = new Regex(
            @"^\s*(?:INSERT|REPLACE)\s+(?:IGNORE\s+)?INTO\s+(?:(?<database>" + IdentifierPattern + @")\s*\.\s*)?(?<name>" + IdentifierPattern + @")",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
            long totalRowsAffected = 0;
            var errors = new List<string>();
            var manifestTables = new List<Tuple<string, long>>();
            bool readingManifest = false;
            bool haltedOnError = false;

            string safeTargetDb = QuoteIdentifier(databaseName);
            string activeSourceDb = null;

            var existingTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var existingViews = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var skippedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                // 1. 切換至目標資料庫並鎖定 Session
                var useRes = db.ExecSQL($"USE {safeTargetDb};");
                if (!IsExecutionSuccess(useRes))
                {
                    string reason = DatabaseExecutionResultService.GetFailureReason(useRes);
                    throw new InvalidOperationException($"Failed to select target database {safeTargetDb}: {reason}");
                }

                // 2. 初始化 Session 環境變數以加快匯入並確保相容性
                try { db.ExecSQL("SET NAMES utf8mb4;"); } catch { }
                try { db.ExecSQL("SET FOREIGN_KEY_CHECKS = 0;"); } catch { }
                try { db.ExecSQL("SET UNIQUE_CHECKS = 0;"); } catch { }
                try { db.ExecSQL("SET SQL_MODE = 'NO_AUTO_VALUE_ON_ZERO';"); } catch { }

                // 3. 快取目標資料庫中既有物件，避免頻繁發送 SHOW TABLES
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

                using (var fs = new FileStream(request.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536))
                using (var reader = new StreamReader(fs, Encoding.UTF8, true))
                {
                    string delimiter = ";";
                    var currentBatch = new StringBuilder();
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

                            var match = Regex.Match(trimmed, @"^--\s*Manifest-Table:\s*([^\s|]+)\s*\|\s*Rows:\s*(\d+)", RegexOptions.IgnoreCase);
                            if (match.Success && long.TryParse(match.Groups[2].Value, out long r))
                            {
                                manifestTables.Add(Tuple.Create(match.Groups[1].Value, r));
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
                                string headline;
                                bool shouldContinue = ProcessAndExecuteStatement(
                                    sql,
                                    batchStartLine,
                                    options,
                                    log,
                                    safeTargetDb,
                                    ref activeSourceDb,
                                    existingTables,
                                    existingViews,
                                    skippedTables,
                                    ref executed,
                                    ref failed,
                                    ref skipped,
                                    ref totalRowsAffected,
                                    errors,
                                    out headline);

                                if (!shouldContinue && !options.ContinueOnError)
                                {
                                    haltedOnError = true;
                                    log.LogInfo("Execution stopped on error as requested by options.");
                                    ReportProgress(progress, request.JobId, MySqlImportStage.Executing, fileBytes, fs.Position, executed, failed, skipped, totalRowsAffected, clock, headline);
                                    break;
                                }

                                long now = clock.ElapsedMilliseconds;
                                bool isDdl = IsSignificantStatement(headline);
                                if (!shouldContinue || isDdl || (now - lastReportMs >= 60))
                                {
                                    lastReportMs = now;
                                    ReportProgress(progress, request.JobId, MySqlImportStage.Executing, fileBytes, fs.Position, executed, failed, skipped, totalRowsAffected, clock, headline);
                                }
                            }

                            currentBatch.Clear();
                            batchStartLine = currentLine + 1;
                        }
                    }

                    // 處理尾端剩餘語句
                    if (currentBatch.Length > 0 && !haltedOnError && (failed == 0 || options.ContinueOnError))
                    {
                        string remainingSql = currentBatch.ToString().Trim();
                        if (!string.IsNullOrWhiteSpace(remainingSql))
                        {
                            string headline;
                            bool ok = ProcessAndExecuteStatement(
                                remainingSql,
                                batchStartLine,
                                options,
                                log,
                                safeTargetDb,
                                ref activeSourceDb,
                                existingTables,
                                existingViews,
                                skippedTables,
                                ref executed,
                                ref failed,
                                ref skipped,
                                ref totalRowsAffected,
                                errors,
                                out headline);
                            if (!ok && !options.ContinueOnError)
                            {
                                haltedOnError = true;
                            }
                            ReportProgress(progress, request.JobId, MySqlImportStage.Executing, fileBytes, fs.Position, executed, failed, skipped, totalRowsAffected, clock, headline);
                        }
                    }
                }

                // 4. 若有 Native Manifest，自動驗證目標資料庫實際列數
                bool manifestMismatch = false;
                if (manifestTables.Count > 0 && !haltedOnError)
                {
                    log.LogInfo("Verifying restored database tables against Native Manifest...");
                    foreach (var pair in manifestTables)
                    {
                        long actual = -1;
                        try { actual = db.CountRows(databaseName, pair.Item1); } catch { }
                        bool match = actual == pair.Item2;
                        if (!match) manifestMismatch = true;
                        log.LogManifestVerification(pair.Item1, pair.Item2, actual, match);
                    }
                }

                clock.Stop();

                MySqlImportStatus status;
                if (haltedOnError)
                {
                    status = MySqlImportStatus.Failed;
                }
                else if (failed > 0 || manifestMismatch)
                {
                    status = MySqlImportStatus.CompletedWithErrors;
                }
                else
                {
                    status = MySqlImportStatus.Completed;
                }

                string summary;
                if (failed == 0 && !manifestMismatch)
                {
                    summary = string.Format(
                        "Execution finished successfully in {0:F1}s: {1} executed, {2} skipped, {3:N0} rows affected.",
                        clock.Elapsed.TotalSeconds, executed, skipped, totalRowsAffected);
                }
                else if (manifestMismatch && failed == 0)
                {
                    summary = string.Format(
                        "Execution finished with row discrepancy in {0:F1}s: {1} executed, {2} skipped, {3:N0} rows affected.",
                        clock.Elapsed.TotalSeconds, executed, skipped, totalRowsAffected);
                }
                else
                {
                    summary = string.Format(
                        "Execution finished with {0} errors in {1:F1}s: {2} executed, {3} skipped, {4:N0} rows affected.",
                        failed, clock.Elapsed.TotalSeconds, executed, skipped, totalRowsAffected);
                }

                if (manifestTables.Count > 0)
                {
                    if (manifestMismatch)
                    {
                        summary += " ⚠️ [Native Manifest 驗證發現列數不符]";
                    }
                    else
                    {
                        summary += string.Format(" [Native Manifest 驗證 {0} 張資料表列數全部相符 ✔]", manifestTables.Count);
                    }
                }

                var finalResult = new MySqlImportResult(
                    request.JobId,
                    status,
                    fileBytes,
                    executed,
                    failed,
                    skipped,
                    totalRowsAffected,
                    log.Path,
                    clock.Elapsed,
                    summary,
                    errors);

                log.WriteEnd(finalResult);
                log.Dispose();
                ReportProgress(progress, request.JobId, MySqlImportStage.Finished, fileBytes, fileBytes, executed, failed, skipped, totalRowsAffected, clock, summary);

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
                    totalRowsAffected,
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
                    totalRowsAffected,
                    log.Path,
                    clock.Elapsed,
                    ex.Message,
                    errors);
                log.WriteEnd(failResult);
                log.Dispose();
                return failResult;
            }
            finally
            {
                // 還原外鍵與唯一性約束檢查
                try { db.ExecSQL("SET FOREIGN_KEY_CHECKS = 1;"); } catch { }
                try { db.ExecSQL("SET UNIQUE_CHECKS = 1;"); } catch { }
            }
        }

        private bool ProcessAndExecuteStatement(
            string sql,
            int line,
            MySqlImportOptions options,
            MySqlImportLog log,
            string safeTargetDb,
            ref string activeSourceDb,
            HashSet<string> existingTables,
            HashSet<string> existingViews,
            HashSet<string> skippedTables,
            ref int executed,
            ref int failed,
            ref int skipped,
            ref long totalRowsAffected,
            List<string> errors,
            out string headline)
        {
            string cleanSql = SanitizeSqlHeadline(sql);

            // A. USE 語句重定向
            Match useMatch = UseDatabaseRegex.Match(sql);
            if (useMatch.Success)
            {
                string targetInSql = UnquoteIdentifier(useMatch.Groups["database"].Value);
                if (!string.Equals(targetInSql, databaseName, StringComparison.OrdinalIgnoreCase))
                {
                    activeSourceDb = targetInSql;
                    log.LogInfo($"Redirected statement from 'USE `{targetInSql}`' to 'USE {safeTargetDb}'");
                }
                sql = $"USE {safeTargetDb};";
                return ExecuteBatchStatement(sql, line, options, log, ref executed, ref failed, ref skipped, ref totalRowsAffected, existingTables, existingViews, errors, out headline);
            }

            // B. CREATE DATABASE / SCHEMA 重定向
            if (CreateDatabaseRegex.IsMatch(sql))
            {
                log.LogInfo($"Redirected CREATE DATABASE to ensure target database {safeTargetDb} exists");
                sql = $"CREATE DATABASE IF NOT EXISTS {safeTargetDb};";
                return ExecuteBatchStatement(sql, line, options, log, ref executed, ref failed, ref skipped, ref totalRowsAffected, existingTables, existingViews, errors, out headline);
            }

            // 若先前偵測到來源資料庫名稱，且與目標不同，將顯式資料庫限定名稱改寫至目標庫
            if (!string.IsNullOrEmpty(activeSourceDb) && !string.Equals(activeSourceDb, databaseName, StringComparison.OrdinalIgnoreCase))
            {
                sql = RewriteDatabaseQualifier(sql, activeSourceDb, databaseName);
            }

            // C. DROP 語句策略判定
            if ((options.ExistingObjectStrategy == MySqlExistingObjectStrategy.CreateIfMissing ||
                 options.ExistingObjectStrategy == MySqlExistingObjectStrategy.SkipExisting) &&
                DropObjectRegex.IsMatch(sql))
            {
                skipped++;
                log.LogStatement(line, cleanSql, true, null, "SKIP_DROP");
                headline = "⏭ [略過 DROP] " + cleanSql;
                return true;
            }

            // D. CREATE 物件策略判定
            Match createMatch = CreateObjectRegex.Match(sql);
            if (createMatch.Success)
            {
                string objType = createMatch.Groups["type"].Value.ToUpperInvariant();
                string objDb = UnquoteIdentifier(createMatch.Groups["database"].Value);
                string objName = UnquoteIdentifier(createMatch.Groups["name"].Value);

                if (string.IsNullOrWhiteSpace(objDb)) objDb = databaseName;

                bool exists = ObjectExists(db, objDb, objName, objType, existingTables, existingViews);

                if (exists && (options.ExistingObjectStrategy == MySqlExistingObjectStrategy.CreateIfMissing ||
                               options.ExistingObjectStrategy == MySqlExistingObjectStrategy.SkipExisting))
                {
                    skipped++;
                    if (options.ExistingObjectStrategy == MySqlExistingObjectStrategy.SkipExisting &&
                        string.Equals(objType, "TABLE", StringComparison.OrdinalIgnoreCase))
                    {
                        skippedTables.Add(objName);
                    }
                    log.LogStatement(line, cleanSql, true, null, "SKIP_EXISTING");
                    headline = $"⏭ [略過既有 {objType}] {objName}";
                    return true;
                }

                if (exists && options.ExistingObjectStrategy == MySqlExistingObjectStrategy.DropAndRecreate)
                {
                    string dropSql = $"DROP {objType} IF EXISTS {safeTargetDb}.{QuoteIdentifier(objName)};";
                    db.ExecSQL(dropSql);
                    existingTables.Remove(objName);
                    existingViews.Remove(objName);
                    log.LogStatement(line, $"DROP {objType} IF EXISTS `{objName}`", true, null, "DROP_BEFORE_CREATE");
                }
            }

            // E. INSERT / REPLACE 資料寫入策略判定
            if (options.ExistingObjectStrategy == MySqlExistingObjectStrategy.SkipExisting)
            {
                Match insertMatch = InsertTargetRegex.Match(sql);
                if (insertMatch.Success)
                {
                    string insTable = UnquoteIdentifier(insertMatch.Groups["name"].Value);
                    if (skippedTables.Contains(insTable) || existingTables.Contains(insTable))
                    {
                        skipped++;
                        log.LogStatement(line, cleanSql, true, null, "SKIP_DATA");
                        headline = $"⏭ [略過既有表資料] {insTable}";
                        return true;
                    }
                }
            }

            // F. 執行 SQL 批次
            return ExecuteBatchStatement(
                sql,
                line,
                options,
                log,
                ref executed,
                ref failed,
                ref skipped,
                ref totalRowsAffected,
                existingTables,
                existingViews,
                errors,
                out headline);
        }

        private bool ExecuteBatchStatement(
            string sql,
            int line,
            MySqlImportOptions options,
            MySqlImportLog log,
            ref int executed,
            ref int failed,
            ref int skipped,
            ref long totalRowsAffected,
            HashSet<string> existingTables,
            HashSet<string> existingViews,
            List<string> errors,
            out string headline)
        {
            string cleanSql = SanitizeSqlHeadline(sql);

            try
            {
                var result = db.ExecSQL(sql);
                bool isSuccess = IsExecutionSuccess(result);

                if (isSuccess)
                {
                    executed++;
                    if (result != null && result.TryGetValue("rowsAffected", out string rStr) &&
                        long.TryParse(rStr, out long rVal) && rVal > 0)
                    {
                        totalRowsAffected += rVal;
                    }

                    // 成功建立或刪除物件時動態維護記憶體快取
                    UpdateObjectCacheOnSuccess(sql, existingTables, existingViews);

                    log.LogStatement(line, cleanSql, true);
                    headline = "✔ " + cleanSql;
                    return true;
                }
                else
                {
                    failed++;
                    string err = DatabaseExecutionResultService.GetFailureReason(result);
                    errors.Add($"Line {line}: {err}");
                    string action = options.ContinueOnError ? "SkipBatch" : "HaltExecution";
                    log.LogStatement(line, cleanSql, false, err, action);
                    headline = $"❌ [行 {line}] {cleanSql} ➔ 錯誤: {err}";
                    return false;
                }
            }
            catch (Exception ex)
            {
                failed++;
                string err = ex.Message;
                errors.Add($"Line {line}: {err}");
                string action = options.ContinueOnError ? "SkipBatch" : "HaltExecution";
                log.LogStatement(line, cleanSql, false, err, action);
                headline = $"❌ [行 {line}] {cleanSql} ➔ 錯誤: {err}";
                return false;
            }
        }

        public static bool IsExecutionSuccess(Dictionary<string, string> result)
        {
            if (result == null) return false;
            if (result.TryGetValue("status", out string status))
            {
                return string.Equals(status, "OK", StringComparison.OrdinalIgnoreCase);
            }
            return !result.ContainsKey("reason");
        }

        private static void UpdateObjectCacheOnSuccess(string sql, HashSet<string> existingTables, HashSet<string> existingViews)
        {
            if (string.IsNullOrWhiteSpace(sql)) return;

            Match createMatch = CreateObjectRegex.Match(sql);
            if (createMatch.Success)
            {
                string objType = createMatch.Groups["type"].Value.ToUpperInvariant();
                string objName = UnquoteIdentifier(createMatch.Groups["name"].Value);
                if (string.Equals(objType, "TABLE", StringComparison.OrdinalIgnoreCase))
                {
                    existingTables.Add(objName);
                }
                else if (string.Equals(objType, "VIEW", StringComparison.OrdinalIgnoreCase))
                {
                    existingViews.Add(objName);
                }
                return;
            }

            Match dropMatch = DropObjectRegex.Match(sql);
            if (dropMatch.Success)
            {
                string objType = dropMatch.Groups["type"].Value.ToUpperInvariant();
                string objName = UnquoteIdentifier(dropMatch.Groups["name"].Value);
                if (string.Equals(objType, "TABLE", StringComparison.OrdinalIgnoreCase))
                {
                    existingTables.Remove(objName);
                }
                else if (string.Equals(objType, "VIEW", StringComparison.OrdinalIgnoreCase))
                {
                    existingViews.Remove(objName);
                }
            }
        }

        private static bool ObjectExists(
            IDatabase db,
            string dbName,
            string objName,
            string objType,
            HashSet<string> existingTables,
            HashSet<string> existingViews)
        {
            if (string.Equals(objType, "TABLE", StringComparison.OrdinalIgnoreCase))
            {
                if (existingTables != null && existingTables.Contains(objName)) return true;
                return db.TableExists(dbName, objName);
            }

            if (string.Equals(objType, "VIEW", StringComparison.OrdinalIgnoreCase))
            {
                if (existingViews != null && existingViews.Contains(objName)) return true;
                return db.ViewExists(dbName, objName);
            }

            var parameters = new Dictionary<string, object>
            {
                { "schema", dbName },
                { "name", objName }
            };

            DataTable table = null;
            if (string.Equals(objType, "FUNCTION", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(objType, "PROCEDURE", StringComparison.OrdinalIgnoreCase))
            {
                parameters.Add("type", objType);
                table = db.SelectSQL(
                    "SELECT 1 FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA = ?schema AND ROUTINE_NAME = ?name AND ROUTINE_TYPE = ?type LIMIT 1;",
                    parameters);
            }
            else if (string.Equals(objType, "TRIGGER", StringComparison.OrdinalIgnoreCase))
            {
                table = db.SelectSQL(
                    "SELECT 1 FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA = ?schema AND TRIGGER_NAME = ?name LIMIT 1;",
                    parameters);
            }

            return table != null && table.Rows.Count > 0;
        }

        public static string QuoteIdentifier(string value)
        {
            return "`" + (value ?? string.Empty).Replace("`", "``") + "`";
        }

        public static string UnquoteIdentifier(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length >= 2 && value[0] == '`' && value[value.Length - 1] == '`')
                return value.Substring(1, value.Length - 2).Replace("``", "`");
            return value;
        }

        public static string RewriteDatabaseQualifier(string sql, string fromDb, string toDb)
        {
            if (string.IsNullOrWhiteSpace(sql) || string.IsNullOrWhiteSpace(fromDb) || string.IsNullOrWhiteSpace(toDb))
                return sql;

            string unquotedFrom = UnquoteIdentifier(fromDb);
            string safeTo = QuoteIdentifier(toDb);

            string pattern = @"(`?" + Regex.Escape(unquotedFrom) + @"`?)\s*\.\s*(`?" + IdentifierPattern + @"`?)";
            return Regex.Replace(sql, pattern, safeTo + ".$2", RegexOptions.IgnoreCase);
        }

        public static string SanitizeSqlHeadline(string sql, int maxLength = 130)
        {
            if (string.IsNullOrWhiteSpace(sql)) return string.Empty;

            string[] lines = sql.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            string firstActionable = null;
            foreach (var l in lines)
            {
                string t = l.Trim();
                if (string.IsNullOrEmpty(t)) continue;
                if (t.StartsWith("--") || t.StartsWith("#") || t.StartsWith("/*")) continue;
                firstActionable = t;
                break;
            }

            string target = firstActionable ?? (lines.Length > 0 ? lines[0].Trim() : sql.Trim());

            if (target.StartsWith("INSERT INTO", StringComparison.OrdinalIgnoreCase))
            {
                int valIdx = target.IndexOf("VALUES", StringComparison.OrdinalIgnoreCase);
                if (valIdx > 0 && target.Length > 80)
                {
                    target = target.Substring(0, valIdx + 6) + " (...)";
                }
            }

            string collapsed = Regex.Replace(target, @"\s+", " ").Trim();
            if (collapsed.Length > maxLength)
                collapsed = collapsed.Substring(0, maxLength) + "…";

            return collapsed;
        }

        private static bool IsSignificantStatement(string headline)
        {
            if (string.IsNullOrEmpty(headline)) return false;
            string upper = headline.ToUpperInvariant();
            return upper.Contains("CREATE ")
                || upper.Contains("DROP ")
                || upper.Contains("ALTER ")
                || upper.Contains("LOCK ")
                || upper.Contains("UNLOCK ")
                || upper.Contains("USE ")
                || upper.Contains("TRUNCATE ");
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
            long totalRowsAffected,
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

            string msg = string.Format("Importing... {0} statements, {1:N0} rows ({2:N0} KB / {3:N0} KB)",
                executed, totalRowsAffected, processedBytes / 1024, totalBytes / 1024);

            progress.Report(new MySqlImportProgress(
                jobId,
                stage,
                totalBytes,
                processedBytes,
                executed,
                failed,
                skipped,
                totalRowsAffected,
                bytesPerSec,
                clock.Elapsed,
                remaining,
                currentPreview,
                msg));
        }
    }
}
