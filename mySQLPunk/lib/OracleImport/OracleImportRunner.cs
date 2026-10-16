using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Oracle.ManagedDataAccess.Client;

namespace mySQLPunk.lib.OracleImport
{
    public sealed class OracleImportRunner
    {
        private const int BufferSize = 65536;
        private const int MaxStatementBytes = 32 * 1024 * 1024;
        private readonly IDatabase db;
        private readonly OracleImportRequest request;
        private readonly Action<string> terminalSink;
        private OracleTransaction transaction;
        private bool transactionActive;
        private CancellationToken currentToken;
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

        public Task<OracleImportResult> RunAsync(IProgress<OracleImportProgress> progress = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            return Task.Run(() => RunInternal(progress, cancellationToken), cancellationToken);
        }

        private OracleImportResult RunInternal(IProgress<OracleImportProgress> progress, CancellationToken cancellationToken)
        {
            var clock = Stopwatch.StartNew();
            var result = new OracleImportResult();
            currentToken = cancellationToken;
            using (var log = new OracleImportLog(request.DatabaseName))
            {
                result.LogFilePath = log.LogPath;
                Action<string> info = message =>
                {
                    log.LogInfo(message);
                    terminalSink?.Invoke(message);
                };
                int attempted = 0, failed = 0;
                long affected = 0, bytes = 0;
                var truncated = new HashSet<string>(StringComparer.Ordinal);
                try
                {
                    var preflight = new OracleImportPreflightScanner().Scan(request.FilePath, cancellationToken);
                    if (!preflight.CanProceed) throw new InvalidOperationException(string.Join("; ", preflight.Issues.ToArray()));
                    string owner = request.DatabaseName;
                    if (string.IsNullOrWhiteSpace(owner))
                        throw new InvalidOperationException("An explicit target Oracle owner is required.");
                    // Validate the complete script before changing a session or executing any statement.
                    bool spatialMetadata = false;
                    using (var reader = new StreamReader(request.FilePath, preflight.DetectedEncoding, true, BufferSize))
                        foreach (var item in EnumerateStatements(reader, cancellationToken))
                        {
                            ValidateTarget(item.Sql, owner);
                            if (IsSpatialMetadataMutation(item.Sql)) spatialMetadata = true;
                        }
                    if (spatialMetadata && request.Options.AutoRegisterSpatialMetadata)
                    {
                        var session = db.SelectSQL("SELECT SYS_CONTEXT('USERENV','SESSION_USER') FROM DUAL");
                        if (session == null || session.Rows.Count != 1 || !string.Equals(Convert.ToString(session.Rows[0][0]), owner, StringComparison.Ordinal))
                            throw new NotSupportedException("USER_SDO_GEOM_METADATA requires the connected session user to equal the selected owner. Cross-owner spatial metadata restore is unsupported.");
                    }
                    foreach (var pair in preflight.ManifestDeclaredTables) ResolveManifestTarget(pair.Key, owner);
                    ExecuteChecked("ALTER SESSION SET CURRENT_SCHEMA = " + QuoteIdentifier(owner));
                    info("Target owner pinned to " + QuoteIdentifier(owner) + ". Source-qualified objects in another owner are rejected.");
                    info("Oracle DDL and TRUNCATE implicitly commit. Rollback protection applies to pending DML only.");
                    if (request.Options.UseTransaction) BeginTransaction();
                    using (var reader = new StreamReader(request.FilePath, preflight.DetectedEncoding, true, BufferSize))
                    {
                        foreach (var item in EnumerateStatements(reader, cancellationToken))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            bytes += item.ByteLength;
                            string sql = item.Sql;
                            string normalized = RemoveComments(sql).Trim();
                            if (!request.Options.AutoRegisterSpatialMetadata && IsSpatialMetadataMutation(normalized))
                            {
                                info("[SKIP] Spatial metadata registration disabled.");
                                continue;
                            }
                            attempted++;
                            string preview = sql.Replace("\r", " ").Replace("\n", " ");
                            if (preview.Length > 120) preview = preview.Substring(0, 120);
                            try
                            {
                                if (Regex.IsMatch(normalized, @"^(COMMIT|ROLLBACK)\b", RegexOptions.IgnoreCase))
                                {
                                    if (!Regex.IsMatch(normalized, @"^(COMMIT|ROLLBACK)(\s+WORK)?$", RegexOptions.IgnoreCase))
                                        throw new InvalidOperationException("Only plain COMMIT/ROLLBACK transaction statements are supported.");
                                    EndTransaction(normalized.StartsWith("COMMIT", StringComparison.OrdinalIgnoreCase));
                                }
                                else if (Regex.IsMatch(normalized, @"^SET\s+TRANSACTION\b", RegexOptions.IgnoreCase))
                                {
                                    if (!Regex.IsMatch(normalized, @"^SET\s+TRANSACTION\s+READ\s+WRITE$", RegexOptions.IgnoreCase))
                                        throw new InvalidOperationException("Import supports only SET TRANSACTION READ WRITE.");
                                    if (!transactionActive) BeginTransaction();
                                }
                                else
                                {
                                    ApplyConflictStrategy(normalized, truncated, info);
                                    if (request.Options.UseTransaction && !transactionActive) BeginTransaction();
                                    long rows = ExecuteChecked(sql);
                                    if (rows > 0) affected += rows;
                                    if (IsDdl(normalized)) ForgetImplicitlyCommittedTransaction();
                                }
                                log.LogStatement(item.LineNumber, preview, true);
                                terminalSink?.Invoke("[OK] Line " + item.LineNumber + ": " + preview);
                            }
                            catch (Exception ex)
                            {
                                if (request.Options.ConflictStrategy == OracleConflictStrategy.KeepExisting &&
                                    Regex.IsMatch(normalized, @"^(CREATE|ALTER)\b", RegexOptions.IgnoreCase) && IsObjectExistsError(ex.Message))
                                {
                                    ForgetImplicitlyCommittedTransaction();
                                    info("[SKIP] " + ex.Message);
                                }
                                else
                                {
                                    failed++;
                                    log.LogStatement(item.LineNumber, preview, false, ex.Message);
                                    terminalSink?.Invoke("[ERROR] Line " + item.LineNumber + ": " + ex.Message);
                                    result.ErrorMessage = ex.Message;
                                    if (request.Options.StopOnError || ex is OperationCanceledException) throw;
                                }
                            }
                            progress?.Report(new OracleImportProgress
                            {
                                Stage = "Executing", CurrentStatement = attempted, TotalStatements = preflight.StatementCount,
                                BytesRead = bytes, TotalBytes = preflight.FileSizeBytes, RowsAffected = affected,
                                Message = "Executing Oracle statement " + attempted + "/" + preflight.StatementCount
                            });
                        }
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    if (failed != 0) EndTransaction(false);
                    else if (transactionActive) EndTransaction(true);
                    result.Success = failed == 0;
                    if (result.Success)
                    {
                        foreach (var pair in preflight.ManifestDeclaredTables)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            string target = ResolveManifestTarget(pair.Key, owner);
                            var count = db.SelectSQL("SELECT COUNT(*) FROM " + target);
                            if (count == null || count.Rows.Count != 1) throw new InvalidOperationException("Manifest count query did not return one row: " + target);
                            long actual = Convert.ToInt64(count.Rows[0][0]);
                            result.RestoredTables[pair.Key] = actual;
                            if (actual != pair.Value) result.ManifestMatched = false;
                        }
                        if (!result.ManifestMatched)
                        {
                            result.Success = false;
                            result.ErrorMessage = "Restored table counts do not match the native manifest.";
                        }
                        if (preflight.ManifestDeclaredTables.Count > 0)
                            log.LogManifest(preflight.ManifestDeclaredTables, result.RestoredTables, result.ManifestMatched);
                    }
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.ErrorMessage = ex.Message;
                    if (failed == 0) failed++;
                    info("Import failed: " + ex.Message);
                }
                finally
                {
                    if (transactionActive)
                    {
                        try { EndTransaction(false); info("Pending Oracle transaction rolled back."); }
                        catch (Exception ex)
                        {
                            result.Success = false;
                            result.ErrorMessage += "; rollback failed: " + ex.Message;
                            info("Rollback failed: " + ex.Message);
                        }
                    }
                    transaction?.Dispose();
                    transaction = null;
                    transactionActive = false;
                    clock.Stop();
                    result.Elapsed = clock.Elapsed;
                    result.StatementsExecuted = attempted;
                    result.StatementsFailed = failed;
                    result.TotalRowsAffected = affected;
                    log.LogSummary(attempted, failed, affected, result.Elapsed, result.Success);
                    log.Flush();
                }
            }
            return result;
        }

        private long ExecuteChecked(string sql)
        {
            var native = db as my_oracle;
            if (native != null)
            {
                using (var command = new OracleCommand(sql, native.MCT))
                {
                    command.Transaction = transaction;
                    using (currentToken.Register(() => { try { command.Cancel(); } catch { } }))
                    {
                        currentToken.ThrowIfCancellationRequested();
                        return command.ExecuteNonQuery();
                    }
                }
            }
            var response = db.ExecSQL(sql);
            if (response == null || !response.ContainsKey("status") || !string.Equals(response["status"], "OK", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(DatabaseExecutionResultService.GetFailureReason(response));
            long rows;
            return response.ContainsKey("rowsAffected") && long.TryParse(response["rowsAffected"], out rows) ? rows : 0;
        }

        private void BeginTransaction()
        {
            var native = db as my_oracle;
            if (native != null) transaction = native.MCT.BeginTransaction();
            else ExecuteChecked("SET TRANSACTION READ WRITE");
            transactionActive = true;
        }

        private void EndTransaction(bool commit)
        {
            // Cleanup must still be possible after cancellation. It is checked for failures, but not cancelled.
            CancellationToken old = currentToken;
            currentToken = CancellationToken.None;
            try
            {
                if (transaction != null)
                {
                    if (commit) transaction.Commit(); else transaction.Rollback();
                    transaction.Dispose();
                    transaction = null;
                }
                else ExecuteChecked(commit ? "COMMIT" : "ROLLBACK");
                transactionActive = false;
            }
            finally { currentToken = old; }
        }

        private void ForgetImplicitlyCommittedTransaction()
        {
            transaction?.Dispose();
            transaction = null;
            transactionActive = false;
        }

        private void ApplyConflictStrategy(string sql, HashSet<string> truncated, Action<string> info)
        {
            string identifier = @"(?:""(?:[^""]|"""")+""|[A-Za-z_][\w$#]*)(?:\s*\.\s*(?:""(?:[^""]|"""")+""|[A-Za-z_][\w$#]*))?";
            if (request.Options.ConflictStrategy == OracleConflictStrategy.DropAndRecreate || request.Options.ConflictStrategy == OracleConflictStrategy.ReplaceAll)
            {
                var match = Regex.Match(sql, @"^CREATE\s+TABLE\s+(" + identifier + ")", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    try { ExecuteChecked("DROP TABLE " + match.Groups[1].Value + " CASCADE CONSTRAINTS"); }
                    catch (Exception ex) { if (ex.Message.IndexOf("ORA-00942", StringComparison.OrdinalIgnoreCase) < 0) throw; }
                    ForgetImplicitlyCommittedTransaction();
                    info("[Conflict] DROP checked: " + match.Groups[1].Value);
                }
            }
            else if (request.Options.ConflictStrategy == OracleConflictStrategy.TruncateAndInsert)
            {
                var target = GetMutationTarget(Tokens(sql));
                if (target != null && target.Operation == "INSERT")
                {
                    string identity = target.QualifiedName(request.DatabaseName);
                    if (truncated.Add(identity))
                    {
                        ExecuteChecked("TRUNCATE TABLE " + identity);
                        ForgetImplicitlyCommittedTransaction();
                        info("[Conflict] TRUNCATE checked: " + identity);
                    }
                }
            }
        }

        private static bool IsDdl(string sql) => Regex.IsMatch(sql, @"^(CREATE|ALTER|DROP|TRUNCATE|COMMENT|GRANT|REVOKE)\b", RegexOptions.IgnoreCase) || Regex.IsMatch(sql, @"^BEGIN\s+EXECUTE\s+IMMEDIATE", RegexOptions.IgnoreCase);
        private static bool IsObjectExistsError(string text) => text.IndexOf("ORA-00955", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("ORA-02260", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("ORA-01430", StringComparison.OrdinalIgnoreCase) >= 0;
        private static bool IsSpatialMetadataMutation(string sql)
        {
            var target = GetMutationTarget(Tokens(sql));
            return target != null && target.Name == "USER_SDO_GEOM_METADATA";
        }

        private sealed class ObjectTarget
        {
            public string Operation, Owner, Name;
            public string QualifiedName(string selectedOwner) => QuoteIdentifier(Owner ?? selectedOwner) + "." + QuoteIdentifier(Name);
        }

        private static bool Word(List<string> tokens, int index, string expected) => index >= 0 && index < tokens.Count && string.Equals(tokens[index], expected, StringComparison.OrdinalIgnoreCase);

        private static ObjectTarget ReadObjectTarget(List<string> tokens, int index, string operation)
        {
            if (Word(tokens, index, "ONLY")) throw new NotSupportedException("Oracle ONLY target wrappers are unsupported under the direct local-owner target contract.");
            if (index >= tokens.Count || !IsIdentifier(tokens[index])) throw new NotSupportedException("Oracle object target could not be parsed.");
            var target = new ObjectTarget { Operation = operation, Name = IdentifierValue(tokens[index]) };
            if (index + 1 < tokens.Count && tokens[index + 1] == ".")
            {
                if (index + 2 >= tokens.Count || !IsIdentifier(tokens[index + 2])) throw new NotSupportedException("Oracle qualified object target could not be parsed.");
                target.Owner = target.Name;
                target.Name = IdentifierValue(tokens[index + 2]);
            }
            return target;
        }

        private static ObjectTarget GetMutationTarget(List<string> tokens)
        {
            bool block = Word(tokens, 0, "DECLARE");
            ObjectTarget target = null;
            for (int i = 0; i < tokens.Count; i++)
            {
                if (i != 0 && !block) break;
                string operation = tokens[i].ToUpperInvariant();
                int objectIndex;
                if (operation == "INSERT")
                {
                    if (!Word(tokens, i + 1, "INTO")) throw new NotSupportedException("Only INSERT INTO is supported by this Oracle target contract.");
                    objectIndex = i + 2;
                }
                else if (!block && operation == "DELETE") objectIndex = Word(tokens, i + 1, "FROM") ? i + 2 : i + 1;
                else if (!block && operation == "UPDATE") objectIndex = i + 1;
                else if (!block && operation == "MERGE")
                {
                    if (!Word(tokens, i + 1, "INTO")) throw new NotSupportedException("Only MERGE INTO is supported.");
                    objectIndex = i + 2;
                }
                else continue;
                if (target != null) throw new NotSupportedException("A generated LOB block must contain exactly one INSERT target.");
                target = ReadObjectTarget(tokens, objectIndex, operation);
            }
            return target;
        }

        private static ObjectTarget GetCreateTarget(List<string> tokens)
        {
            if (!Word(tokens, 0, "CREATE")) return null;
            int index = 1;
            if (Word(tokens, index, "OR") && Word(tokens, index + 1, "REPLACE")) index += 2;
            if (Word(tokens, index, "FORCE") || Word(tokens, index, "NOFORCE")) index++;
            if (Word(tokens, index, "EDITIONABLE") || Word(tokens, index, "NONEDITIONABLE")) index++;
            if (Word(tokens, index, "UNIQUE") || Word(tokens, index, "BITMAP")) index++;
            if (Word(tokens, index, "TABLE") || Word(tokens, index, "INDEX") || Word(tokens, index, "VIEW")) return ReadObjectTarget(tokens, index + 1, "CREATE " + tokens[index].ToUpperInvariant());
            return null;
        }
        internal static string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

        internal static string ResolveManifestTarget(string name, string owner)
        {
            var tokens = Tokens(name);
            if (tokens.Count == 1 && IsIdentifier(tokens[0])) return QuoteIdentifier(owner) + "." + QuoteIdentifier(IdentifierValue(tokens[0]));
            if (tokens.Count == 3 && tokens[1] == "." && IsIdentifier(tokens[0]) && IsIdentifier(tokens[2]) && string.Equals(IdentifierValue(tokens[0]), owner, StringComparison.Ordinal))
                return QuoteIdentifier(owner) + "." + QuoteIdentifier(IdentifierValue(tokens[2]));
            throw new InvalidOperationException("Manifest target is invalid or belongs to a different owner: " + name);
        }

        internal static void ValidateTarget(string sql, string owner)
        {
            var tokens = Tokens(sql);
            if (tokens.Count == 0) return;
            if (tokens.Contains("@")) throw new NotSupportedException("Oracle database-link targets are outside the local-owner import contract.");
            var mutationTarget = GetMutationTarget(tokens);
            var createTarget = GetCreateTarget(tokens);
            foreach (var target in new[] { mutationTarget, createTarget })
                if (target != null && target.Owner != null && target.Owner != owner)
                    throw new InvalidOperationException("Cross-owner object rejected before execution: " + target.QualifiedName(owner));
            string normalized = RemoveComments(sql).Trim();
            if (Regex.IsMatch(normalized, @"^SET\s+TRANSACTION\b", RegexOptions.IgnoreCase) && !Regex.IsMatch(normalized, @"^SET\s+TRANSACTION\s+READ\s+WRITE$", RegexOptions.IgnoreCase))
                throw new NotSupportedException("Import accepts only SET TRANSACTION READ WRITE.");
            string tokenText = string.Join(" ", tokens);
            // Dynamic SQL cannot be safely redirected. Permit only the exact bounded DROP wrapper emitted by this exporter.
            if (Regex.IsMatch(tokenText, @"\b(EXECUTE\s+IMMEDIATE|DBMS_SQL|DBMS_UTILITY)\b", RegexOptions.IgnoreCase))
            {
                var drop = Regex.Match(normalized, @"^BEGIN\s+EXECUTE\s+IMMEDIATE\s+'DROP TABLE ((?:[^']|'')+) CASCADE CONSTRAINTS';\s*EXCEPTION WHEN OTHERS THEN IF SQLCODE != -942 THEN RAISE; END IF;\s*END;$", RegexOptions.IgnoreCase);
                if (!drop.Success) throw new InvalidOperationException("Dynamic Oracle SQL is unsupported because target-owner enforcement cannot be guaranteed.");
                ResolveManifestTarget(drop.Groups[1].Value.Replace("''", "'"), owner);
                return;
            }
            if (Regex.IsMatch(normalized, @"^(ALTER\s+SESSION|CONNECT\b|DISCONNECT\b|GRANT\b|REVOKE\b|CREATE\s+(USER|DATABASE|SCHEMA|SYNONYM|DATABASE\s+LINK)\b|ALTER\s+USER|DROP\s+USER)", RegexOptions.IgnoreCase))
                throw new InvalidOperationException("The script attempts to change session/owner scope or unsupported privileges.");
            if (!Regex.IsMatch(tokenText, @"^(INSERT\b|UPDATE\b|DELETE\b|MERGE\b|CREATE ((OR REPLACE )?(FORCE |NOFORCE )?(EDITIONABLE |NONEDITIONABLE )?VIEW|TABLE|INDEX|UNIQUE INDEX|BITMAP INDEX)\b|ALTER (TABLE|INDEX)\b|DROP (TABLE|VIEW|INDEX)\b|TRUNCATE TABLE\b|COMMENT ON\b|COMMIT\b|ROLLBACK\b|SET TRANSACTION\b|BEGIN\b|DECLARE\b)", RegexOptions.IgnoreCase))
                throw new NotSupportedException("Unsupported Oracle statement. This wizard accepts bounded native dump DDL/DML and explicit transaction control.");
            bool block = Regex.IsMatch(normalized, @"^(BEGIN|DECLARE)\b", RegexOptions.IgnoreCase);
            if (Regex.IsMatch(normalized, @"^CREATE\s+(OR\s+REPLACE\s+)?(TRIGGER|PROCEDURE|FUNCTION|PACKAGE|TYPE)\b", RegexOptions.IgnoreCase))
                throw new NotSupportedException("Stored PL/SQL import requires an explicit owner/dependency policy and is not supported by this wizard.");
            if (block)
            {
                // Only the bounded temporary-LOB blocks generated by this exporter are admitted.
                if (!Regex.IsMatch(normalized, @"^DECLARE\s+(mp_lob_\d+\s+(BLOB|CLOB);\s*)+BEGIN\b", RegexOptions.IgnoreCase))
                    throw new NotSupportedException("Anonymous PL/SQL is unsupported except generated bounded temporary-LOB INSERT blocks.");
                if (mutationTarget == null || mutationTarget.Operation != "INSERT") throw new NotSupportedException("A generated LOB block must contain one INSERT target.");
                for (int i = 0; i + 2 < tokens.Count; i++)
                {
                    if (tokens[i + 1] != ".") continue;
                    string qualifier = IdentifierValue(tokens[i]);
                    if (qualifier != owner && qualifier != "DBMS_LOB" && qualifier != "SDO_UTIL")
                        throw new NotSupportedException("Unsupported qualified call in Oracle LOB block: " + qualifier);
                    if (qualifier == "DBMS_LOB" && Array.IndexOf(new[] { "CREATETEMPORARY", "WRITEAPPEND", "ISTEMPORARY", "FREETEMPORARY" }, tokens[i + 2].ToUpperInvariant()) < 0)
                        throw new NotSupportedException("Unsupported DBMS_LOB operation in Oracle import.");
                }
            }
            for (int i = 0; i < tokens.Count; i++)
            {
                string token = tokens[i].ToUpperInvariant();
                bool target = token == "INTO" || token == "UPDATE" || token == "FROM" || token == "JOIN" || token == "USING" || token == "REFERENCES" ||
                    (token == "ON" && createTarget != null && createTarget.Operation == "CREATE INDEX") || token == "COLUMN" || token == "TABLE" ||
                    ((token == "TABLE" || token == "VIEW" || token == "INDEX" || token == "TRIGGER" || token == "PROCEDURE" || token == "FUNCTION" || token == "SEQUENCE" || token == "PACKAGE" || token == "TYPE") && i > 0 &&
                    (tokens[i - 1].Equals("CREATE", StringComparison.OrdinalIgnoreCase) || tokens[i - 1].Equals("ALTER", StringComparison.OrdinalIgnoreCase) || tokens[i - 1].Equals("DROP", StringComparison.OrdinalIgnoreCase) || tokens[i - 1].Equals("TRUNCATE", StringComparison.OrdinalIgnoreCase) || tokens[i - 1].Equals("REPLACE", StringComparison.OrdinalIgnoreCase) || tokens[i - 1].Equals("ON", StringComparison.OrdinalIgnoreCase)));
                if (!target || i + 1 >= tokens.Count || !IsIdentifier(tokens[i + 1])) continue;
                if (i + 3 < tokens.Count && tokens[i + 2] == "." && IsIdentifier(tokens[i + 3]) &&
                    !string.Equals(IdentifierValue(tokens[i + 1]), owner, StringComparison.Ordinal))
                    throw new InvalidOperationException("Cross-owner object rejected before execution: " + tokens[i + 1] + "." + tokens[i + 3] + ". Selected owner: " + owner);
            }
        }

        private static bool IsIdentifier(string token) => token.Length > 0 && (token[0] == '"' || char.IsLetter(token[0]) || token[0] == '_');
        private static string IdentifierValue(string token) => token[0] == '"' ? token.Substring(1, token.Length - 2).Replace("\"\"", "\"") : token.ToUpperInvariant();
        private static List<string> Tokens(string sql)
        {
            var tokens = new List<string>();
            foreach (Match match in Regex.Matches(sql, @"--[^\r\n]*|/\*[\s\S]*?\*/|[qQ]'(?:\[[\s\S]*?\]|\{[\s\S]*?\}|\([\s\S]*?\)|<[\s\S]*?>)'|[qQ]'(?<qd>[^\s])[\s\S]*?\k<qd>'|'(?:[^']|'')*'|""(?:[^""]|"""")*""|[A-Za-z_][\w$#]*|[^\s]"))
            {
                string value = match.Value;
                if (value.StartsWith("--") || value.StartsWith("/*") || value[0] == '\'' || (value.Length > 1 && (value[0] == 'q' || value[0] == 'Q') && value[1] == '\'')) continue;
                tokens.Add(value);
            }
            return tokens;
        }
        private static string RemoveComments(string sql)
        {
            // Preserve literals and quoted identifiers while masking comments for classification only.
            return Regex.Replace(sql, @"'(?:[^']|'')*'|""(?:[^""]|"""")*""|--[^\r\n]*|/\*[\s\S]*?\*/", m => m.Value.StartsWith("--") || m.Value.StartsWith("/*") ? " " : m.Value);
        }

        internal struct StatementInfo
        {
            public string Sql;
            public int LineNumber;
            public int ByteLength;
        }

        internal static IEnumerable<StatementInfo> EnumerateStatements(StreamReader reader, CancellationToken cancellationToken, Action<string> onCommentLine = null)
        {
            var buffer = new StringBuilder();
            int lineNumber = 1, startLine = 1, sourceBytes = 0;
            bool single = false, quoted = false, blockComment = false, plsql = false, hasContent = false;
            char alternateEnd = '\0';
            string line;
            while ((line = ReadPhysicalLine(reader)) != null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (line.IndexOf('\0') >= 0) throw new InvalidOperationException("SQL script contains a NUL character at line " + lineNumber + ".");
                if (!single && !quoted && !blockComment && alternateEnd == '\0' && line.Trim() == "/")
                {
                    sourceBytes += reader.CurrentEncoding.GetByteCount(line);
                    if (hasContent)
                    {
                        yield return new StatementInfo { Sql = buffer.ToString().Trim(), LineNumber = startLine, ByteLength = sourceBytes };
                        buffer.Clear(); sourceBytes = 0; hasContent = false; plsql = false;
                    }
                    lineNumber++;
                    continue;
                }
                if (!single && !quoted && !blockComment && alternateEnd == '\0' && line.TrimStart().StartsWith("--")) onCommentLine?.Invoke(line.Trim());
                bool lineComment = false;
                for (int i = 0; i < line.Length; i++)
                {
                    char c = line[i];
                    int count = char.IsHighSurrogate(c) && i + 1 < line.Length && char.IsLowSurrogate(line[i + 1]) ? 2 : 1;
                    sourceBytes += reader.CurrentEncoding.GetByteCount(line.Substring(i, count));
                    if (sourceBytes > MaxStatementBytes) throw new InvalidOperationException("A SQL statement exceeds the 32 MiB safety limit.");
                    if (lineComment) { if (c == '\n') buffer.Append('\n'); continue; }
                    if (blockComment)
                    {
                        // Comments are intentionally replaced with whitespace; SQL delimiter bytes remain counted.
                        if (c == '*' && i + 1 < line.Length && line[i + 1] == '/') { blockComment = false; i++; sourceBytes += reader.CurrentEncoding.GetByteCount("/"); buffer.Append(' '); }
                        else if (c == '\n') buffer.Append('\n');
                        continue;
                    }
                    if (alternateEnd != '\0')
                    {
                        buffer.Append(c);
                        if (c == alternateEnd && i + 1 < line.Length && line[i + 1] == '\'') { buffer.Append('\''); i++; sourceBytes += reader.CurrentEncoding.GetByteCount("'"); alternateEnd = '\0'; }
                        else if (count == 2) { buffer.Append(line[++i]); }
                        continue;
                    }
                    if (single || quoted)
                    {
                        buffer.Append(c);
                        char end = single ? '\'' : '"';
                        if (c == end)
                        {
                            if (i + 1 < line.Length && line[i + 1] == end) { buffer.Append(line[++i]); sourceBytes += reader.CurrentEncoding.GetByteCount(end.ToString()); }
                            else { single = false; quoted = false; }
                        }
                        else if (count == 2) buffer.Append(line[++i]);
                        continue;
                    }
                    if (c == '-' && i + 1 < line.Length && line[i + 1] == '-') { lineComment = true; buffer.Append(' '); continue; }
                    if (c == '/' && i + 1 < line.Length && line[i + 1] == '*') { blockComment = true; buffer.Append(' '); i++; sourceBytes += reader.CurrentEncoding.GetByteCount("*"); continue; }
                    if (!hasContent && !char.IsWhiteSpace(c)) { hasContent = true; startLine = lineNumber; }
                    if ((c == 'q' || c == 'Q') && i + 2 < line.Length && line[i + 1] == '\'')
                    {
                        char opening = line[i + 2];
                        alternateEnd = opening == '[' ? ']' : opening == '{' ? '}' : opening == '(' ? ')' : opening == '<' ? '>' : opening;
                        buffer.Append(c).Append('\'').Append(opening); i += 2; sourceBytes += reader.CurrentEncoding.GetByteCount("'" + opening); continue;
                    }
                    if (c == '\'') single = true;
                    if (c == '"') quoted = true;
                    if (c == ';')
                    {
                        plsql = plsql || PlSqlStartRegex.IsMatch(buffer.ToString());
                        if (!plsql)
                        {
                            if (hasContent) yield return new StatementInfo { Sql = buffer.ToString().Trim(), LineNumber = startLine, ByteLength = sourceBytes };
                            buffer.Clear(); sourceBytes = 0; hasContent = false; continue;
                        }
                    }
                    buffer.Append(c);
                    if (count == 2) buffer.Append(line[++i]);
                }
                lineNumber++;
            }
            if (single || quoted || blockComment || alternateEnd != '\0') throw new InvalidOperationException("Unterminated Oracle literal, identifier or comment.");
            if (hasContent) yield return new StatementInfo { Sql = buffer.ToString().Trim(), LineNumber = startLine, ByteLength = sourceBytes };
        }

        private static string ReadPhysicalLine(StreamReader reader)
        {
            var line = new StringBuilder();
            int value;
            while ((value = reader.Read()) >= 0) { line.Append((char)value); if (line.Length > MaxStatementBytes) throw new InvalidOperationException("A SQL line exceeds the 32 MiB parser safety limit."); if (value == '\n') break; }
            return line.Length == 0 ? null : line.ToString();
        }
    }
}
