using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace mySQLPunk.lib.OracleImport
{
    public sealed class OracleImportRunner
    {
        private const int BufferSize = 65536;
        private readonly IDatabase db;
        private readonly OracleImportRequest request;
        private readonly Action<string> terminalSink;

        private static readonly Regex PlSqlStartRegex = new Regex(
            @"^\s*(DECLARE\b|BEGIN\b|CREATE\s+(OR\s+REPLACE\s+)?(PROCEDURE|FUNCTION|PACKAGE|TRIGGER|TYPE)\b)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public OracleImportRunner(IDatabase db, OracleImportRequest request, Action<string> terminalSink = null)
        {
            this.db = db ?? throw new ArgumentNullException(nameof(db));
            this.request = request ?? throw new ArgumentNullException(nameof(request));
            this.terminalSink = terminalSink;
        }

        public OracleImportResult Run(IProgress<OracleImportProgress> progress = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            return RunInternal(progress, cancellationToken);
        }

        public async Task<OracleImportResult> RunAsync(IProgress<OracleImportProgress> progress = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            return await Task.Run(() => RunInternal(progress, cancellationToken), cancellationToken).ConfigureAwait(false);
        }

        private OracleImportResult RunInternal(IProgress<OracleImportProgress> progress = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            var clock = Stopwatch.StartNew();
            var log = new OracleImportLog(request.DatabaseName);
            var result = new OracleImportResult { LogFilePath = log.LogPath };

            Action<string> LogAndTerminal = msg =>
            {
                log.LogInfo(msg);
                terminalSink?.Invoke(string.Format("[{0:HH:mm:ss}] {1}", DateTime.Now, msg));
            };

            LogAndTerminal(string.Format("正在初始化 Oracle 匯入任務: {0}", Path.GetFileName(request.FilePath)));
            LogAndTerminal(string.Format("目標綱要 (Owner): {0}", string.IsNullOrWhiteSpace(request.DatabaseName) ? "Current Schema" : request.DatabaseName));

            // 1. 預檢掃描
            var scanner = new OracleImportPreflightScanner();
            var preflight = scanner.Scan(request.FilePath, cancellationToken);
            if (!preflight.CanProceed)
            {
                clock.Stop();
                result.Success = false;
                result.ErrorMessage = string.Join("; ", preflight.Issues.ToArray());
                LogAndTerminal("❌ 預檢未通過：" + result.ErrorMessage);
                log.LogSummary(0, 0, 0, clock.Elapsed, false);
                log.Flush();
                log.Dispose();
                return result;
            }

            LogAndTerminal(string.Format("預檢通過：共探測到約 {0:N0} 條語句 (檔案大小: {1:N0} 位元組)。", preflight.StatementCount, preflight.FileSizeBytes));
            if (preflight.HasSpatialFeatures)
            {
                LogAndTerminal(string.Format("偵測到 Oracle Spatial (SDO_GEOMETRY) 空間特性關鍵字: {0}", string.Join(", ", preflight.DetectedSpatialKeywords.ToArray())));
            }
            if (preflight.HasPlSqlBlocks)
            {
                LogAndTerminal(string.Format("偵測到 PL/SQL 預存區塊或觸發程序 (約 {0} 個區塊)。", preflight.PlSqlBlocksCount));
            }

            long totalBytes = preflight.FileSizeBytes;
            int totalStatements = preflight.StatementCount;

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

                        // 去除獨立行結尾斜線（若有）
                        if (effectiveSql.EndsWith("/") && effectiveSql.Length > 1)
                        {
                            effectiveSql = effectiveSql.Substring(0, effectiveSql.Length - 1).TrimEnd();
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
                            // 若衝突策略為 KeepExisting 且為物件已存在錯誤 (ORA-00955 / ORA-01430 / ORA-02260 等)
                            if (request.Options.ConflictStrategy == OracleConflictStrategy.KeepExisting && IsObjectExistsError(ex.Message))
                            {
                                log.LogStatement(lineNum, preview, true, "物件已存在，依 KeepExisting 策略略過。");
                                terminalSink?.Invoke(string.Format("[{0:HH:mm:ss}] [SKIP] Line {1}: {2} (物件已存在)", DateTime.Now, lineNum, preview));
                            }
                            else
                            {
                                failedStatements++;
                                log.LogStatement(lineNum, preview, false, ex.Message);
                                terminalSink?.Invoke(string.Format("[{0:HH:mm:ss}] [ERROR] Line {1}: {2} -> {3}", DateTime.Now, lineNum, preview, ex.Message));

                                if (request.Options.StopOnError)
                                {
                                    try { db.ExecSQL("ROLLBACK;"); LogAndTerminal("遇錯停止，已執行 ROLLBACK。"); } catch { }
                                    throw new InvalidOperationException(string.Format("第 {0} 行執行失敗：{1}", lineNum, ex.Message), ex);
                                }
                            }
                        }

                        if (currentStmtIndex % 50 == 0 || currentStmtIndex == totalStatements)
                        {
                            progress?.Report(new OracleImportProgress
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

                // 結尾提交交易
                try
                {
                    db.ExecSQL("COMMIT;");
                    LogAndTerminal("交易已成功提交 (COMMIT)。");
                }
                catch (Exception ex)
                {
                    LogAndTerminal("提交交易時發生警告: " + ex.Message);
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

            // 7. 核對 Native Manifest
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
                            ? string.Format("SELECT COUNT(*) FROM \"{0}\".\"{1}\"", tableName.Split('.')[0], tableName.Split('.')[1])
                            : string.Format("SELECT COUNT(*) FROM \"{0}\"", tableName);

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

            LogAndTerminal(string.Format("Oracle 匯入作業完成，耗時 {0:hh\\:mm\\:ss\\.fff}，詳細日誌已寫入: {1}", result.Elapsed, result.LogFilePath));
            log.LogSummary(currentStmtIndex, failedStatements, totalRowsAffected, result.Elapsed, result.Success);
            log.Flush();
            log.Dispose();

            return result;
        }

        private static bool IsObjectExistsError(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return false;
            // ORA-00955: name is already used by an existing object
            // ORA-02260: table can have only one primary key
            // ORA-01430: column being added already exists in table
            return message.IndexOf("ORA-00955", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("ORA-02260", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("ORA-01430", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ApplyConflictStrategy(
            string sql,
            OracleConflictStrategy strategy,
            HashSet<string> truncatedTables,
            IDatabase db,
            Action<string> logAction)
        {
            if (strategy == OracleConflictStrategy.DropAndRecreate || strategy == OracleConflictStrategy.ReplaceAll)
            {
                // 在 CREATE TABLE 前執行 DROP TABLE ... CASCADE CONSTRAINTS
                var match = Regex.Match(sql, @"^CREATE\s+TABLE\s+([""\w\.]+)", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    string target = match.Groups[1].Value;
                    string dropSql = string.Format("BEGIN EXECUTE IMMEDIATE 'DROP TABLE {0} CASCADE CONSTRAINTS'; EXCEPTION WHEN OTHERS THEN NULL; END;", target);
                    try
                    {
                        db.ExecSQL(dropSql);
                        logAction(string.Format("[衝突處理] 已預先清除資料表: {0}", target));
                    }
                    catch { }
                }
            }
            else if (strategy == OracleConflictStrategy.TruncateAndInsert)
            {
                // 當第一次遇到該表的 INSERT 時，先執行 TRUNCATE
                var match = Regex.Match(sql, @"^INSERT\s+INTO\s+([""\w\.]+)", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    string target = match.Groups[1].Value;
                    if (truncatedTables.Add(target))
                    {
                        string truncSql = string.Format("TRUNCATE TABLE {0};", target);
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
            bool inPlSqlBlock = false;

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

                // 檢查是否開展 PL/SQL 區塊
                if (!inPlSqlBlock && !inSingleQuote && !inDoubleQuote && blockCommentDepth == 0)
                {
                    if (PlSqlStartRegex.IsMatch(line))
                    {
                        inPlSqlBlock = true;
                    }
                }

                // 檢查 PL/SQL 結束斜線 /
                if (inPlSqlBlock && !inSingleQuote && !inDoubleQuote && blockCommentDepth == 0)
                {
                    if (trimmed == "/")
                    {
                        inPlSqlBlock = false;
                        string plsql = sb.ToString().Trim();
                        sb.Clear();
                        byteLen = 0;
                        startLine = currentLine + 1;

                        if (!string.IsNullOrWhiteSpace(plsql))
                        {
                            yield return new StatementInfo
                            {
                                Sql = plsql,
                                LineNumber = startLine,
                                ByteLength = byteLen
                            };
                        }
                        currentLine++;
                        continue;
                    }
                }

                int len = line.Length;
                for (int i = 0; i < len; i++)
                {
                    char c = line[i];

                    if (inLineComment) continue;

                    if (blockCommentDepth > 0)
                    {
                        if (c == '*' && i + 1 < len && line[i + 1] == '/') { blockCommentDepth--; i++; }
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

                    // 符號檢查
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

                    // 非 PL/SQL 區塊下的分號終止符
                    if (c == ';' && !inPlSqlBlock)
                    {
                        sb.Append(';');
                        string sql = sb.ToString().Trim();
                        sb.Clear();
                        byteLen = 0;
                        startLine = currentLine;

                        if (!string.IsNullOrWhiteSpace(sql))
                        {
                            yield return new StatementInfo
                            {
                                Sql = sql,
                                LineNumber = startLine,
                                ByteLength = byteLen
                            };
                        }
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
    }
}
