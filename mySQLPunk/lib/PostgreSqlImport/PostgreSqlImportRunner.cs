using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace mySQLPunk.lib.PostgreSqlImport
{
    public sealed class PostgreSqlImportRunner
    {
        private const int BufferSize = 65536; // 64KB
        private readonly IDatabase db;
        private readonly PostgreSqlImportRequest request;

        public PostgreSqlImportRunner(IDatabase db, PostgreSqlImportRequest request)
        {
            this.db = db ?? throw new ArgumentNullException(nameof(db));
            this.request = request ?? throw new ArgumentNullException(nameof(request));
        }

        public async Task<PostgreSqlImportResult> RunAsync(
            IProgress<PostgreSqlImportProgress> progress = null,
            Action<string> terminalSink = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return await Task.Run(() => Run(progress, terminalSink, cancellationToken), cancellationToken).ConfigureAwait(false);
        }

        public PostgreSqlImportResult Run(
            IProgress<PostgreSqlImportProgress> progress = null,
            Action<string> terminalSink = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var result = new PostgreSqlImportResult();
            var clock = Stopwatch.StartNew();
            Guid jobId = Guid.NewGuid();

            var log = new PostgreSqlImportLog(jobId);
            result.LogFilePath = log.Path;

            void LogAndTerminal(string message)
            {
                log.LogInfo(message);
                terminalSink?.Invoke(string.Format("[{0:HH:mm:ss}] {1}", DateTime.Now, message));
            }

            LogAndTerminal(string.Format("開始執行 PostgreSQL 匯入作業 (Job ID: {0})", jobId));
            LogAndTerminal(string.Format("目標檔案: {0}", request.FilePath));
            LogAndTerminal(string.Format("目標資料庫: {0}", request.TargetDatabase));
            LogAndTerminal(string.Format("衝突處理策略: {0}", request.Options.ConflictStrategy));

            var scanner = new PostgreSqlImportPreflightScanner();
            var preflight = scanner.Scan(request.FilePath, cancellationToken);
            int totalStatements = preflight.StatementCount;
            long totalBytes = preflight.FileSize;

            if (preflight.HasPostgisFeatures)
            {
                LogAndTerminal(string.Format("🌐 偵測到 PostGIS 空間特徵 (關鍵字: {0})", string.Join(", ", preflight.DetectedSpatialKeywords)));
                if (request.Options.AutoInstallPostgis)
                {
                    try
                    {
                        LogAndTerminal("正在確保目標資料庫已載入 PostGIS 擴充套件 (CREATE EXTENSION IF NOT EXISTS postgis)...");
                        db.ExecSQL("CREATE EXTENSION IF NOT EXISTS postgis;");
                        LogAndTerminal("PostGIS 擴充套件已就緒。");
                    }
                    catch (Exception ex)
                    {
                        LogAndTerminal("PostGIS 擴充套件檢查注意: " + ex.Message);
                    }
                }
            }

            if (preflight.HasDollarQuotes)
            {
                LogAndTerminal(string.Format("偵測到 {0} 個 Dollar-Quoted 區塊 ($$...$$)，將使用狀態機精準解析。", preflight.DollarQuoteBlocksCount));
            }

            // 效能優化旗標
            try
            {
                db.ExecSQL("SET synchronous_commit = off;");
            }
            catch { }

            // 判斷是否需要外部 Transaction
            bool outerTransaction = request.Options.UseTransaction;
            bool transactionStarted = false;

            if (outerTransaction)
            {
                try
                {
                    db.ExecSQL("BEGIN;");
                    transactionStarted = true;
                    LogAndTerminal("已開啟單一交易包覆 (BEGIN)。");
                }
                catch (Exception ex)
                {
                    LogAndTerminal("開啟交易時發生警告: " + ex.Message);
                }
            }

            int currentStmtIndex = 0;
            int failedStatements = 0;
            long totalRowsAffected = 0;
            long bytesRead = 0;
            var truncatedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var expectedManifestTables = new Dictionary<string, long>(preflight.ManifestDeclaredTables, StringComparer.OrdinalIgnoreCase);
            bool readingManifest = false;

            Action<string> checkManifestLine = comment =>
            {
                if (comment.Contains("mySQLPunk Native Manifest") && !readingManifest)
                {
                    readingManifest = true;
                    log.LogInfo("Detected Native Manifest in SQL script footer.");
                }

                var match = Regex.Match(comment, @"^--\s*Manifest-Table:\s*([^\s|]+)\s*\|\s*Rows:\s*(\d+)", RegexOptions.IgnoreCase);
                if (match.Success && long.TryParse(match.Groups[2].Value, out long r))
                {
                    expectedManifestTables[match.Groups[1].Value] = r;
                }
            };

            try
            {
                using (var fs = new FileStream(request.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize))
                using (var reader = new StreamReader(fs, preflight.DetectedEncoding, true, BufferSize))
                {
                    string statement;
                    int lineNum = 0;

                    foreach (var stmtInfo in EnumerateStatements(reader, cancellationToken, checkManifestLine))
                    {
                        statement = stmtInfo.Sql.Trim();
                        lineNum = stmtInfo.LineNumber;
                        bytesRead += stmtInfo.ByteLength;

                        if (string.IsNullOrWhiteSpace(statement)) continue;

                        currentStmtIndex++;

                        // 套用衝突處理策略
                        string effectiveSql = ApplyConflictStrategy(statement, request.Options.ConflictStrategy, truncatedTables, db, LogAndTerminal);

                        // 判斷當前語句是否為交易控制
                        string upperSql = effectiveSql.ToUpperInvariant().Trim();
                        if (upperSql.StartsWith("BEGIN") || upperSql.StartsWith("START TRANSACTION"))
                        {
                            if (transactionStarted)
                            {
                                // 避免巢狀 BEGIN
                                continue;
                            }
                            transactionStarted = true;
                        }
                        else if (upperSql.StartsWith("COMMIT"))
                        {
                            transactionStarted = false;
                        }
                        else if (upperSql.StartsWith("ROLLBACK"))
                        {
                            transactionStarted = false;
                        }

                        string preview = effectiveSql.Length > 80 ? effectiveSql.Substring(0, 80).Replace("\r", " ").Replace("\n", " ") + "..." : effectiveSql.Replace("\r", " ").Replace("\n", " ");

                        try
                        {
                            var execRes = db.ExecSQL(effectiveSql);
                            long affected = 0;
                            if (execRes != null && execRes.ContainsKey("rowsAffected"))
                            {
                                long.TryParse(execRes["rowsAffected"], out affected);
                            }

                            if (affected > 0) totalRowsAffected += affected;

                            log.LogStatement(lineNum, preview, true);
                            terminalSink?.Invoke(string.Format("[{0:HH:mm:ss}] [OK] Line {1}: {2} (Rows: {3})", DateTime.Now, lineNum, preview, affected));
                        }
                        catch (Exception ex)
                        {
                            failedStatements++;
                            log.LogStatement(lineNum, preview, false, ex.Message);
                            terminalSink?.Invoke(string.Format("[{0:HH:mm:ss}] [ERROR] Line {1}: {2} -> {3}", DateTime.Now, lineNum, preview, ex.Message));

                            if (request.Options.StopOnError)
                            {
                                if (transactionStarted)
                                {
                                    try { db.ExecSQL("ROLLBACK;"); LogAndTerminal("遇錯停止，已執行 ROLLBACK。"); } catch { }
                                }
                                throw new InvalidOperationException(string.Format("第 {0} 行執行失敗：{1}", lineNum, ex.Message), ex);
                            }
                        }

                        if (currentStmtIndex % 50 == 0 || currentStmtIndex == totalStatements)
                        {
                            progress?.Report(new PostgreSqlImportProgress
                            {
                                Stage = "Executing",
                                CurrentStatement = currentStmtIndex,
                                TotalStatements = totalStatements,
                                BytesRead = bytesRead,
                                TotalBytes = totalBytes,
                                RowsAffected = totalRowsAffected,
                                Message = string.Format("正在執行語句: {0}/{1} (已影響 {2:N0} 列)", currentStmtIndex, totalStatements, totalRowsAffected)
                            });
                        }
                    }
                }

                // 正常結束提交
                if (transactionStarted)
                {
                    try
                    {
                        db.ExecSQL("COMMIT;");
                        LogAndTerminal("交易已成功提交 (COMMIT)。");
                    }
                    catch (Exception ex)
                    {
                        LogAndTerminal("提交交易時發生警告: " + ex.Message);
                    }
                }

                result.Success = (failedStatements == 0);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                LogAndTerminal("匯入過程中斷: " + ex.Message);
            }

            clock.Stop();
            result.Elapsed = clock.Elapsed;
            result.StatementsExecuted = currentStmtIndex;
            result.StatementsFailed = failedStatements;
            result.TotalRowsAffected = totalRowsAffected;

            // 核對 Native Manifest
            if (expectedManifestTables.Count > 0)
            {
                LogAndTerminal(string.Format("正在核對腳本結尾 Manifest 清單 (共 {0} 張資料表)...", expectedManifestTables.Count));
                bool allMatch = true;

                foreach (var kvp in expectedManifestTables)
                {
                    string tableName = kvp.Key;
                    long expectedRows = kvp.Value;
                    long actualRows = -1;

                    try
                    {
                        string countSql = tableName.Contains(".")
                            ? string.Format("SELECT COUNT(*) FROM \"{0}\".\"{1}\";", tableName.Split('.')[0], tableName.Split('.')[1])
                            : string.Format("SELECT COUNT(*) FROM \"{0}\";", tableName);

                        DataTable dt = db.SelectSQL(countSql);
                        if (dt != null && dt.Rows.Count > 0)
                        {
                            actualRows = Convert.ToInt64(dt.Rows[0][0]);
                        }
                    }
                    catch
                    {
                        actualRows = -1;
                    }

                    result.RestoredTables[tableName] = actualRows;
                    if (actualRows != expectedRows)
                    {
                        allMatch = false;
                        LogAndTerminal(string.Format("⚠️ 資料表 [{0}] 筆數不符：Manifest 宣告 {1} 列，還原後實際 {2} 列。", tableName, expectedRows, actualRows));
                    }
                    else
                    {
                        LogAndTerminal(string.Format("✅ 資料表 [{0}] 筆數相符：{1} 列。", tableName, actualRows));
                    }
                }

                result.ManifestMatched = allMatch;
                log.LogManifest(expectedManifestTables, result.RestoredTables, allMatch);
            }

            log.LogSummary(currentStmtIndex, failedStatements, totalRowsAffected, result.Elapsed, result.Success);
            log.Flush();
            log.Dispose();

            LogAndTerminal(string.Format("PostgreSQL 匯入作業完成，耗時 {0:hh\\:mm\\:ss\\.fff}，詳細日誌已寫入: {1}", result.Elapsed, result.LogFilePath));
            return result;
        }

        private static string ApplyConflictStrategy(
            string sql,
            PostgreSqlConflictStrategy strategy,
            HashSet<string> truncatedTables,
            IDatabase db,
            Action<string> logAction)
        {
            if (strategy == PostgreSqlConflictStrategy.KeepExisting)
            {
                // CREATE TABLE -> CREATE TABLE IF NOT EXISTS
                var match = Regex.Match(sql, @"^CREATE\s+TABLE\s+(?!IF\s+NOT\s+EXISTS)([""\w\.]+)", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    return Regex.Replace(sql, @"^CREATE\s+TABLE\s+", "CREATE TABLE IF NOT EXISTS ", RegexOptions.IgnoreCase);
                }
            }
            else if (strategy == PostgreSqlConflictStrategy.DropAndRecreate || strategy == PostgreSqlConflictStrategy.ReplaceAll)
            {
                // 在 CREATE TABLE 前執行 DROP TABLE IF EXISTS ... CASCADE
                var match = Regex.Match(sql, @"^CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?([""\w\.]+)", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    string target = match.Groups[1].Value;
                    string dropSql = string.Format("DROP TABLE IF EXISTS {0} CASCADE;", target);
                    try
                    {
                        db.ExecSQL(dropSql);
                        logAction(string.Format("[衝突處理] 已預先清除資料表: {0}", target));
                    }
                    catch { }
                }
            }
            else if (strategy == PostgreSqlConflictStrategy.TruncateAndInsert)
            {
                // 當第一次遇到該表的 INSERT 時，先執行 TRUNCATE
                var match = Regex.Match(sql, @"^INSERT\s+INTO\s+([""\w\.]+)", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    string target = match.Groups[1].Value;
                    if (truncatedTables.Add(target))
                    {
                        string truncSql = string.Format("TRUNCATE TABLE {0} CASCADE;", target);
                        try
                        {
                            db.ExecSQL(truncSql);
                            logAction(string.Format("[衝突處理] 已清空資料表內容: {0}", target));
                        }
                        catch { }
                    }
                }
            }

            return sql;
        }

        private struct StatementInfo
        {
            public string Sql;
            public int LineNumber;
            public int ByteLength;
        }

        private static IEnumerable<StatementInfo> EnumerateStatements(StreamReader reader, CancellationToken cancellationToken, Action<string> onCommentLine = null)
        {
            var sb = new StringBuilder();
            int startLine = 1;
            int currentLine = 1;
            int byteLen = 0;

            bool inSingleQuote = false;
            bool inDoubleQuote = false;
            bool inLineComment = false;
            int blockCommentDepth = 0;
            string dollarTag = null;

            string line;
            while ((line = reader.ReadLine()) != null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byteLen += line.Length + 1;

                string trimmed = line.Trim();

                // 獨立行註解不作為執行語句，僅觸發註解回呼 (如 Manifest 解析)
                if (sb.Length == 0 && trimmed.StartsWith("--", StringComparison.Ordinal))
                {
                    onCommentLine?.Invoke(trimmed);
                    currentLine++;
                    startLine = currentLine;
                    continue;
                }

                if (sb.Length == 0) startLine = currentLine;

                int len = line.Length;
                for (int i = 0; i < len; i++)
                {
                    char c = line[i];

                    if (inLineComment) continue;

                    if (blockCommentDepth > 0)
                    {
                        if (c == '/' && i + 1 < len && line[i + 1] == '*') { blockCommentDepth++; i++; }
                        else if (c == '*' && i + 1 < len && line[i + 1] == '/') { blockCommentDepth--; i++; }
                        sb.Append(c);
                        continue;
                    }

                    if (inSingleQuote)
                    {
                        sb.Append(c);
                        if (c == '\'')
                        {
                            if (i + 1 < len && line[i + 1] == '\'')
                            {
                                sb.Append(line[i + 1]);
                                i++;
                            }
                            else
                            {
                                inSingleQuote = false;
                            }
                        }
                        continue;
                    }

                    if (inDoubleQuote)
                    {
                        sb.Append(c);
                        if (c == '"')
                        {
                            if (i + 1 < len && line[i + 1] == '"')
                            {
                                sb.Append(line[i + 1]);
                                i++;
                            }
                            else
                            {
                                inDoubleQuote = false;
                            }
                        }
                        continue;
                    }

                    if (dollarTag != null)
                    {
                        sb.Append(c);
                        if (c == '$')
                        {
                            int tagLen = dollarTag.Length;
                            if (i + tagLen <= len && string.Compare(line, i, dollarTag, 0, tagLen, StringComparison.Ordinal) == 0)
                            {
                                // 附加剩餘 tag 字元
                                for (int k = 1; k < tagLen; k++) sb.Append(line[i + k]);
                                dollarTag = null;
                                i += tagLen - 1;
                            }
                        }
                        continue;
                    }

                    // 一般符號檢查
                    if (c == '-' && i + 1 < len && line[i + 1] == '-')
                    {
                        inLineComment = true;
                        i++;
                        continue;
                    }

                    if (c == '/' && i + 1 < len && line[i + 1] == '*')
                    {
                        blockCommentDepth = 1;
                        sb.Append("/*");
                        i++;
                        continue;
                    }

                    if (c == '\'')
                    {
                        inSingleQuote = true;
                        sb.Append(c);
                        continue;
                    }

                    if (c == '"')
                    {
                        inDoubleQuote = true;
                        sb.Append(c);
                        continue;
                    }

                    if (c == '$')
                    {
                        string potentialTag = TryMatchDollarTag(line, i);
                        if (potentialTag != null)
                        {
                            dollarTag = potentialTag;
                            sb.Append(potentialTag);
                            i += potentialTag.Length - 1;
                            continue;
                        }
                    }

                    if (c == ';')
                    {
                        sb.Append(';');
                        yield return new StatementInfo
                        {
                            Sql = sb.ToString(),
                            LineNumber = startLine,
                            ByteLength = byteLen
                        };
                        sb.Clear();
                        byteLen = 0;
                        startLine = currentLine;
                        continue;
                    }

                    sb.Append(c);
                }

                inLineComment = false;
                if (sb.Length > 0) sb.Append("\r\n");
                currentLine++;
            }

            if (sb.Length > 0)
            {
                string rem = sb.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(rem) && !rem.StartsWith("--", StringComparison.Ordinal))
                {
                    yield return new StatementInfo
                    {
                        Sql = rem,
                        LineNumber = startLine,
                        ByteLength = byteLen
                    };
                }
            }
        }

        private static string TryMatchDollarTag(string line, int startIndex)
        {
            if (startIndex >= line.Length || line[startIndex] != '$') return null;

            int end = -1;
            for (int i = startIndex + 1; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '$')
                {
                    end = i;
                    break;
                }
                if (!char.IsLetterOrDigit(c) && c != '_')
                {
                    return null;
                }
            }

            if (end > startIndex)
            {
                return line.Substring(startIndex, end - startIndex + 1);
            }

            return null;
        }
    }
}
