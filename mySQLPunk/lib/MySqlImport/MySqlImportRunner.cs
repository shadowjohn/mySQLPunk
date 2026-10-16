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
            return Task.Run(() => RunInternal(request, options, progress, cancellation));
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
            bool haltedOnError = false;
            bool scriptTransaction = false;
            bool sessionTouched = false;
            bool sessionCleaned = false;

            string safeTargetDb = QuoteIdentifier(databaseName);
            string activeSourceDb = null;

            var existingTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var existingViews = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var skippedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var initiallyExistingTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                cancellation.ThrowIfCancellationRequested();
                var preflight = MySqlImportPreflightScanner.Scan(request.SourcePath, cancellation);
                cancellation.ThrowIfCancellationRequested();
                if (!preflight.Passed) throw new InvalidDataException(string.Join("; ", preflight.Issues));

                // 1. 切換至目標資料庫並鎖定 Session
                sessionTouched = true;
                var useRes = db.ExecSQL($"USE {safeTargetDb};");
                if (!IsExecutionSuccess(useRes))
                {
                    string reason = DatabaseExecutionResultService.GetFailureReason(useRes);
                    throw new InvalidOperationException($"Failed to select target database {safeTargetDb}: {reason}");
                }

                // 2. 初始化 Session 環境變數以加快匯入並確保相容性
                RequireExecution("SET NAMES utf8mb4;");
                RequireExecution("SET FOREIGN_KEY_CHECKS = 0;");
                RequireExecution("SET UNIQUE_CHECKS = 0;");
                RequireExecution("SET SQL_MODE = 'NO_AUTO_VALUE_ON_ZERO';");

                // 3. 快取目標資料庫中既有物件，避免頻繁發送 SHOW TABLES
                try
                {
                    var tList = db.GetTables(databaseName);
                    if (tList != null) existingTables.UnionWith(tList);
                }
                catch { throw new InvalidOperationException("Cannot read initial target table catalog safely."); }
                initiallyExistingTables.UnionWith(existingTables);

                try
                {
                    var vList = db.GetViews(databaseName);
                    if (vList != null) existingViews.UnionWith(vList);
                }
                catch { }

                using (var fs = new FileStream(request.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536))
                using (var reader = new StreamReader(fs, new UTF8Encoding(false, true), true))
                {
                    foreach (var statement in MySqlScriptReader.Read(reader, cancellation, comment =>
                    {
                        var match = Regex.Match(comment, @"^--\s*Manifest-Table:\s*([^\s|]+)\s*\|\s*Rows:\s*(\d+)", RegexOptions.IgnoreCase);
                        if (match.Success && long.TryParse(match.Groups[2].Value, out long rows))
                            manifestTables.Add(Tuple.Create(match.Groups[1].Value, rows));
                    }))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        string headline;
                        bool ok = ProcessAndExecuteStatement(statement.Sql, statement.Line, options, log,
                            safeTargetDb, ref activeSourceDb, existingTables, existingViews, skippedTables,
                            initiallyExistingTables, ref executed, ref failed, ref skipped, ref totalRowsAffected,
                            errors, out headline);
                        cancellation.ThrowIfCancellationRequested();
                        if (ok)
                        {
                            var tokens = MySqlScriptReader.Tokens(statement.Sql);
                            if (Keyword(tokens[0], "BEGIN") || Keyword(tokens[0], "START")) scriptTransaction = true;
                            if (Keyword(tokens[0], "COMMIT") || Keyword(tokens[0], "ROLLBACK")) scriptTransaction = false;
                        }
                        ReportProgress(progress, request.JobId, MySqlImportStage.Executing, fileBytes, fs.Position,
                            executed, failed, skipped, totalRowsAffected, clock, headline);
                        if (!ok && !options.ContinueOnError) { haltedOnError = true; break; }
                    }
                }
                cancellation.ThrowIfCancellationRequested();

                if (scriptTransaction) throw new InvalidDataException("Script ended with an uncommitted transaction; pending rows were rolled back.");
                sessionCleaned = true;
                if (!CleanupSession(log, errors)) throw new InvalidOperationException("Import session cleanup failed; completion cannot be confirmed.");
                cancellation.ThrowIfCancellationRequested();

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
                ReportProgress(progress, request.JobId, MySqlImportStage.Finished, fileBytes, fileBytes, executed, failed, skipped, totalRowsAffected, clock, summary);

                return finalResult;
            }
            catch (OperationCanceledException)
            {
                if (sessionTouched && !sessionCleaned) { sessionCleaned = true; CleanupSession(log, errors); }
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
                return cancelResult;
            }
            catch (Exception ex)
            {
                if (sessionTouched && !sessionCleaned) { sessionCleaned = true; CleanupSession(log, errors); }
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
                return failResult;
            }
            finally { log.Dispose(); }
        }

        private bool CleanupSession(MySqlImportLog log, List<string> errors)
        {
            bool success = true;
            foreach (string sql in new[] { "ROLLBACK;", "SET FOREIGN_KEY_CHECKS = 1;", "SET UNIQUE_CHECKS = 1;" })
            {
                try { RequireExecution(sql); }
                catch (Exception ex) { success = false; errors.Add("Session cleanup: " + ex.Message); log.LogInfo("Session cleanup failed: " + ex.Message); }
            }
            return success;
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
            HashSet<string> initiallyExistingTables,
            ref int executed,
            ref int failed,
            ref int skipped,
            ref long totalRowsAffected,
            List<string> errors,
            out string headline)
        {
            string cleanSql = SanitizeSqlHeadline(sql);
            string code = MySqlScriptReader.CodeOnly(sql);
            ValidateTargetSyntax(sql);

            // A. USE 語句重定向
            Match useMatch = UseDatabaseRegex.Match(code);
            if (useMatch.Success)
            {
                string targetInSql = UnquoteIdentifier(useMatch.Groups["database"].Value);
                if (!string.Equals(targetInSql, databaseName, StringComparison.OrdinalIgnoreCase))
                {
                    activeSourceDb = targetInSql;
                    log.LogInfo($"Redirected statement from 'USE `{targetInSql}`' to 'USE {safeTargetDb}'");
                }
                var useTokens = MySqlScriptReader.Tokens(sql);
                sql = MySqlScriptReader.ReplaceToken(sql, useTokens[1], safeTargetDb);
                return ExecuteBatchStatement(sql, line, options, log, ref executed, ref failed, ref skipped, ref totalRowsAffected, existingTables, existingViews, errors, out headline);
            }

            // B. CREATE DATABASE / SCHEMA 重定向
            if (CreateDatabaseRegex.IsMatch(code))
            {
                log.LogInfo($"Redirected CREATE DATABASE to ensure target database {safeTargetDb} exists");
                sql = $"CREATE DATABASE IF NOT EXISTS {safeTargetDb};";
                return ExecuteBatchStatement(sql, line, options, log, ref executed, ref failed, ref skipped, ref totalRowsAffected, existingTables, existingViews, errors, out headline);
            }

            sql = EnforceTargetQualifiers(sql, activeSourceDb, databaseName);
            code = MySqlScriptReader.CodeOnly(sql);

            // C. DROP 語句策略判定
            if ((options.ExistingObjectStrategy == MySqlExistingObjectStrategy.CreateIfMissing ||
                 options.ExistingObjectStrategy == MySqlExistingObjectStrategy.SkipExisting) &&
                DropObjectRegex.IsMatch(code))
            {
                skipped++;
                log.LogStatement(line, cleanSql, true, null, "SKIP_DROP");
                headline = "⏭ [略過 DROP] " + cleanSql;
                return true;
            }

            // D. CREATE 物件策略判定
            Match createMatch = CreateObjectRegex.Match(code);
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
                    if (!ExecuteBatchStatement(dropSql, line, options, log, ref executed, ref failed, ref skipped, ref totalRowsAffected, existingTables, existingViews, errors, out headline)) return false;
                    existingTables.Remove(objName);
                    existingViews.Remove(objName);
                    log.LogStatement(line, $"DROP {objType} IF EXISTS `{objName}`", true, null, "DROP_BEFORE_CREATE");
                }
            }

            // E. INSERT / REPLACE 資料寫入策略判定
            if (options.ExistingObjectStrategy == MySqlExistingObjectStrategy.SkipExisting)
            {
                var writeTokens = MySqlScriptReader.Tokens(sql);
                int insertTarget = FindInsertTargetIndex(writeTokens);
                if (insertTarget == -2) throw new InvalidDataException("Cannot identify INSERT/REPLACE target safely under SkipExisting.");
                if (insertTarget >= 0)
                {
                    int name = insertTarget;
                    if (name + 2 < writeTokens.Count && writeTokens[name + 1].Value == ".") name += 2;
                    string insTable = writeTokens[name].Value;
                    if (skippedTables.Contains(insTable) || initiallyExistingTables.Contains(insTable))
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

            string code = MySqlScriptReader.CodeOnly(sql);
            Match createMatch = CreateObjectRegex.Match(code);
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

            Match dropMatch = DropObjectRegex.Match(code);
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

            var tokens = MySqlScriptReader.Tokens(sql);
            for (int i = tokens.Count - 3; i >= 0; i--)
                if (tokens[i].Identifier && tokens[i + 1].Value == "." && tokens[i + 2].Identifier &&
                    string.Equals(tokens[i].Value, UnquoteIdentifier(fromDb), StringComparison.OrdinalIgnoreCase))
                    sql = MySqlScriptReader.ReplaceToken(sql, tokens[i], QuoteIdentifier(toDb));
            return sql;
        }

        private void RequireExecution(string sql)
        {
            var result = db.ExecSQL(sql);
            if (!IsExecutionSuccess(result)) throw new InvalidOperationException(DatabaseExecutionResultService.GetFailureReason(result));
        }

        private static bool Keyword(MySqlScriptReader.Token token, string value)
        { return !token.Quoted && string.Equals(token.Value, value, StringComparison.OrdinalIgnoreCase); }

        private static void ValidateTargetSyntax(string sql)
        {
            var tokens = MySqlScriptReader.Tokens(sql);
            if (tokens.Count > 1 && (Keyword(tokens[0], "COMMIT") || Keyword(tokens[0], "ROLLBACK") || Keyword(tokens[0], "BEGIN")))
                throw new InvalidDataException("Only plain BEGIN, COMMIT, and ROLLBACK are supported.");
            for (int i = 0; i < tokens.Count; i++)
            {
                if (Keyword(tokens[i], "AUTOCOMMIT")) throw new InvalidDataException("SET AUTOCOMMIT is unsupported; use explicit BEGIN/COMMIT.");
                if (Keyword(tokens[i], "PREPARE") || Keyword(tokens[i], "EXECUTE") || Keyword(tokens[i], "CALL") ||
                    Keyword(tokens[i], "SOURCE") || Keyword(tokens[i], "CONNECT"))
                    throw new InvalidDataException("Dynamic SQL, CALL, and client commands cannot be safely redirected to the selected target.");
                if (i > 0 && Keyword(tokens[i], "USE"))
                    throw new InvalidDataException("Nested USE cannot be safely redirected.");
                if ((Keyword(tokens[i], "DATABASE") || Keyword(tokens[i], "SCHEMA")) &&
                    (i == 0 || !Keyword(tokens[i - 1], "CREATE")))
                    throw new InvalidDataException("Only CREATE DATABASE is supported; database-wide destructive commands are rejected.");
                if (Keyword(tokens[i], "SQL_MODE"))
                {
                    if (tokens.Count != 4 || !Keyword(tokens[0], "SET") || i != 1 || tokens[2].Value != "=" ||
                        sql.Substring(tokens[3].Start, tokens[3].Length) != "'NO_AUTO_VALUE_ON_ZERO'")
                        throw new InvalidDataException("Changing SQL_MODE is unsupported by the logical import reader.");
                }
            }
        }

        // INSERT accepts priority modifiers, IGNORE and optional INTO; REPLACE
        // accepts the corresponding priority/optional-INTO forms. Share target
        // extraction between redirection and the SkipExisting policy.
        private static int FindInsertTargetIndex(List<MySqlScriptReader.Token> tokens)
        {
            if (tokens.Count == 0 || (!Keyword(tokens[0], "INSERT") && !Keyword(tokens[0], "REPLACE"))) return -1;
            int target = 1;
            while (target < tokens.Count && (Keyword(tokens[target], "LOW_PRIORITY") || Keyword(tokens[target], "HIGH_PRIORITY") ||
                Keyword(tokens[target], "DELAYED") || Keyword(tokens[target], "IGNORE"))) target++;
            if (target < tokens.Count && Keyword(tokens[target], "INTO")) target++;
            if (target >= tokens.Count || !tokens[target].Identifier) return -2;
            if (target + 1 < tokens.Count && tokens[target + 1].Value == "." &&
                (target + 2 >= tokens.Count || !tokens[target + 2].Identifier)) return -2;
            return target;
        }

        private static string EnforceTargetQualifiers(string sql, string source, string target)
        {
            var tokens = MySqlScriptReader.Tokens(sql);
            var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var objectPrefixes = new HashSet<int>();
            Match create = CreateObjectRegex.Match(MySqlScriptReader.CodeOnly(sql));
            bool triggerDefinition = create.Success && create.Groups["type"].Value.Equals("TRIGGER", StringComparison.OrdinalIgnoreCase);
            if (triggerDefinition) { aliases.Add("NEW"); aliases.Add("OLD"); }
            int insertTarget = FindInsertTargetIndex(tokens);
            if (insertTarget >= 0)
            {
                int last = insertTarget;
                if (last + 2 < tokens.Count && tokens[last + 1].Value == ".") { objectPrefixes.Add(last); last += 2; }
                aliases.Add(tokens[last].Value);
            }
            string[] objectWords = { "INTO", "UPDATE", "TABLE", "VIEW", "FROM", "JOIN", "REFERENCES", "TRIGGER", "PROCEDURE", "FUNCTION", "TRUNCATE", "TO" };
            for (int i = 0; i < tokens.Count - 1; i++)
            {
                bool objectWord = false;
                foreach (string word in objectWords) if (Keyword(tokens[i], word)) objectWord = true;
                // ON names a table in a trigger header; it is not a general
                // object keyword (JOIN predicates use ON for expressions).
                if (triggerDefinition && Keyword(tokens[i], "ON")) objectWord = true;
                if (!objectWord) continue;
                int name = i + 1;
                while (name < tokens.Count && (Keyword(tokens[name], "IF") || Keyword(tokens[name], "NOT") || Keyword(tokens[name], "EXISTS") || Keyword(tokens[name], "ONLY"))) name++;
                if (name >= tokens.Count || !tokens[name].Identifier) continue;
                int last = name;
                if (name + 2 < tokens.Count && tokens[name + 1].Value == "." && tokens[name + 2].Identifier)
                { objectPrefixes.Add(name); last = name + 2; }
                aliases.Add(tokens[last].Value);
                int alias = last + 1;
                if (alias < tokens.Count && Keyword(tokens[alias], "AS")) alias++;
                if (alias < tokens.Count && tokens[alias].Identifier) aliases.Add(tokens[alias].Value);
            }
            for (int i = tokens.Count - 3; i >= 0; i--)
            {
                if (!tokens[i].Identifier || tokens[i + 1].Value != "." || !tokens[i + 2].Identifier) continue;
                if (i >= 2 && tokens[i - 1].Value == ".") continue;
                bool threePart = i + 3 < tokens.Count && tokens[i + 3].Value == ".";
                bool sourcePrefix = !string.IsNullOrEmpty(source) && string.Equals(tokens[i].Value, source, StringComparison.OrdinalIgnoreCase);
                bool targetPrefix = string.Equals(tokens[i].Value, target, StringComparison.OrdinalIgnoreCase);
                if (objectPrefixes.Contains(i) || threePart || ((sourcePrefix || targetPrefix) && !aliases.Contains(tokens[i].Value)))
                    sql = MySqlScriptReader.ReplaceToken(sql, tokens[i], QuoteIdentifier(target));
                else if (!aliases.Contains(tokens[i].Value))
                    throw new InvalidDataException("Ambiguous database qualifier '" + tokens[i].Value + "'; cannot safely redirect SQL.");
            }
            return sql;
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
