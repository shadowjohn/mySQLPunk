using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
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
        private const string IdentifierPattern = @"""(?:""""|[^""])+""|`(?:``|[^`])+`|\[[^\]]+\]|'(?:''|[^'])+'|(?:[A-Za-z0-9_$]|[^\x00-\x7F])+";
        private static readonly Regex CreateObjectRegex = new Regex(@"^CREATE\s+(?:(?:TEMP|TEMPORARY|UNIQUE|VIRTUAL)\s+)?(?<type>TABLE|VIEW|INDEX|TRIGGER)\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:(?<schema>" + IdentifierPattern + @")\s*\.\s*)?(?<name>" + IdentifierPattern + @")", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex DropObjectRegex = new Regex(@"^DROP\s+(?<type>TABLE|VIEW|INDEX|TRIGGER)\s+(?:IF\s+EXISTS\s+)?(?:(?<schema>" + IdentifierPattern + @")\s*\.\s*)?(?<name>" + IdentifierPattern + @")", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex DataTargetRegex = new Regex(@"^(?:INSERT(?:\s+OR\s+(?:REPLACE|IGNORE|ROLLBACK|ABORT|FAIL))?\s+INTO|REPLACE\s+INTO|UPDATE(?:\s+OR\s+(?:REPLACE|IGNORE|ROLLBACK|ABORT|FAIL))?|DELETE\s+FROM|ALTER\s+TABLE)\s+(?:(?<schema>" + IdentifierPattern + @")\s*\.\s*)?(?<name>" + IdentifierPattern + @")", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex OnTargetRegex = new Regex(@"\bON\s+(?:(?<schema>" + IdentifierPattern + @")\s*\.\s*)?(?<name>" + IdentifierPattern + @")", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex BeginTxRegex = new Regex(@"^BEGIN(?:\s+(?:IMMEDIATE|DEFERRED|EXCLUSIVE))?(?:\s+TRANSACTION)?\s*;?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex CommitTxRegex = new Regex(@"^(?:COMMIT|END)(?:\s+TRANSACTION)?\s*;?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RollbackTxRegex = new Regex(@"^ROLLBACK(?:\s+TRANSACTION)?\s*;?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private readonly IDatabase db;
        private readonly string databaseName;

        public SqliteImportRunner(IDatabase db, string databaseName)
        {
            this.db = db ?? throw new ArgumentNullException(nameof(db));
            this.databaseName = !string.IsNullOrWhiteSpace(databaseName) ? databaseName : throw new ArgumentException("Database name is required", nameof(databaseName));
        }
        public Task<SqliteImportResult> RunAsync(SqliteImportRequest request, SqliteImportOptions options, IProgress<SqliteImportProgress> progress, CancellationToken cancellation)
        {
            return Task.Run(() => RunInternal(request, options, progress, cancellation));
        }
        private SqliteImportResult RunInternal(SqliteImportRequest request, SqliteImportOptions options, IProgress<SqliteImportProgress> progress, CancellationToken cancellation)
        {
            var clock = Stopwatch.StartNew();
            using (var log = new SqliteImportLog(request.JobId))
            {
                int executed = 0, failed = 0, skipped = 0;
                long rows = 0, fileBytes = File.Exists(request.SourcePath) ? new FileInfo(request.SourcePath).Length : 0;
                bool txOpen = false, managed = false, cancelled = false, manifestVerified = false;
                int? originalFk = null, originalSync = null;
                string manifestReport = null;
                var errors = new List<string>();
                var manifests = new List<Tuple<string, long>>();
                var initial = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var current = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var initialTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                log.LogInfo("Target SQLite Database: " + databaseName);
                log.LogInfo("Source SQL File: " + request.SourcePath);
                try
                {
                    // Retain a read-only file handle through preflight and execution so a
                    // writer cannot replace the verified bytes on Windows in between.
                    using (var sourceLock = new FileStream(request.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var preflight = SqliteImportPreflightScanner.Scan(request.SourcePath, cancellation);
                        cancellation.ThrowIfCancellationRequested();
                        if (!preflight.Passed) throw new InvalidDataException(string.Join("; ", preflight.Issues));
                        if (db is my_sqlite session && session.MCT != null && !session.MCT.AutoCommit)
                            throw new InvalidOperationException("Import requires an owned SQLite session with no existing transaction.");
                        DataTable catalog = SelectChecked("SELECT type, name, tbl_name FROM sqlite_master WHERE type IN ('table','view','index','trigger');");
                        foreach (DataRow row in catalog.Rows)
                        {
                            string type = Convert.ToString(row["type"]).ToUpperInvariant(), name = Convert.ToString(row["name"]);
                            initial.Add(type + ":" + name); current.Add(type + ":" + name);
                            owners[type + ":" + name] = Convert.ToString(row["tbl_name"]);
                            if (type == "TABLE" || type == "VIEW") initialTables.Add(name);
                        }
                        bool explicitTx = false;
                        using (var scan = new SqliteStatementReader(request.SourcePath, cancellation, text =>
                        {
                            var pair = SqliteImportPreflightScanner.ParseTableManifest(text);
                            if (pair != null) manifests.Add(pair);
                        }))
                        {
                            string statement;
                            while ((statement = scan.Read()) != null)
                            {
                                string sql = SqliteStatementReader.PolicySql(statement);
                                if (BeginTxRegex.IsMatch(sql)) explicitTx = true;
                                if (Regex.IsMatch(sql, @"^(ATTACH|DETACH|SAVEPOINT|RELEASE)\b|^ROLLBACK\s+TO\b", RegexOptions.IgnoreCase))
                                    throw new NotSupportedException("Attached databases and savepoint scripts are not supported by this import wizard.");
                                if (options.ExistingObjectStrategy == SqliteExistingObjectStrategy.SkipExisting && initialTables.Count > 0 && Regex.IsMatch(sql, @"^WITH\b", RegexOptions.IgnoreCase))
                                    throw new NotSupportedException("CTE statements cannot be safely classified under SkipExisting with existing objects.");
                                var change = CreateObjectRegex.Match(sql);
                                if (!change.Success) change = DropObjectRegex.Match(sql);
                                if (Regex.IsMatch(sql, @"^CREATE\s+(TEMP|TEMPORARY)\b", RegexOptions.IgnoreCase))
                                    throw new NotSupportedException("Temporary schema import is not supported by this wizard.");
                                var target = change.Success ? change : DataTargetRegex.Match(sql);
                                if (target.Success && target.Groups["schema"].Success && !Unquote(target.Groups["schema"].Value).Equals("main", StringComparison.OrdinalIgnoreCase))
                                    throw new NotSupportedException("Import may only modify the main SQLite schema.");
                                if (change.Success && options.ExistingObjectStrategy == SqliteExistingObjectStrategy.AbortIfExists && IsInitiallyProtected(change, sql, initial, initialTables, owners))
                                    throw new InvalidOperationException("Object " + Unquote(change.Groups["name"].Value) + " already exists in database (AbortIfExists policy).");
                            }
                        }
                        originalFk = ReadPragma("foreign_keys"); originalSync = ReadPragma("synchronous");
                        if (options.AutoLoadSpatiaLite && db is my_sqlite sqlite) sqlite.RetryLoadSpatiaLite();
                        if (options.PragmaForeignKeysOff) ExecChecked("PRAGMA foreign_keys = OFF;");
                        if (options.PragmaSynchronousOff) ExecChecked("PRAGMA synchronous = OFF;");
                        managed = options.WrapInTransaction && !explicitTx;
                        using (var reader = new SqliteStatementReader(request.SourcePath, cancellation))
                        {
                            string statement;
                            while ((statement = reader.Read()) != null)
                            {
                                cancellation.ThrowIfCancellationRequested();
                                string sql = SqliteStatementReader.PolicySql(statement);
                                if (txOpen && db is my_sqlite nativeState && nativeState.MCT != null && nativeState.MCT.AutoCommit)
                                {
                                    txOpen = false;
                                    throw new InvalidOperationException("SQLite ended the import transaction unexpectedly; further statements were not executed.");
                                }
                                if (ShouldSkip(sql, options.ExistingObjectStrategy, initial, initialTables, owners))
                                {
                                    skipped++; log.LogStatement(reader.StatementLine, Preview(sql), true, null, "SKIP_PROTECTED_OBJECT"); continue;
                                }
                                if (managed && !txOpen && !Regex.IsMatch(sql, @"^PRAGMA\b", RegexOptions.IgnoreCase))
                                {
                                    ExecChecked("BEGIN IMMEDIATE;"); txOpen = true;
                                }
                                var create = CreateObjectRegex.Match(sql);
                                if (create.Success && options.ExistingObjectStrategy == SqliteExistingObjectStrategy.DropAndRecreate)
                                {
                                    string key = Key(create);
                                    if (current.Contains(key) && !(key.Equals("TABLE:" + my_sqlite.ColumnCommentTableName, StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(sql, @"^CREATE\s+TABLE\s+IF\s+NOT\s+EXISTS\b", RegexOptions.IgnoreCase)))
                                    {
                                        ExecChecked("DROP " + create.Groups["type"].Value + " IF EXISTS " + QuoteIdentifier(Unquote(create.Groups["name"].Value)) + ";");
                                        current.Remove(key);
                                    }
                                }
                                if (failed > 0 && CommitTxRegex.IsMatch(sql))
                                    throw new InvalidOperationException("Transaction contains failed SQL statements; COMMIT was withheld.");
                                var result = ExecuteSql(statement, cancellation);
                                cancellation.ThrowIfCancellationRequested();
                                if (!IsExecutionSuccess(result))
                                {
                                    failed++;
                                    string reason = "Line " + reader.StatementLine + ": " + DatabaseExecutionResultService.GetFailureReason(result);
                                    errors.Add(reason); log.LogStatement(reader.StatementLine, Preview(sql), false, reason, "FAIL");
                                    if (txOpen && db is my_sqlite errorState && errorState.MCT != null && errorState.MCT.AutoCommit)
                                    {
                                        txOpen = false;
                                        throw new InvalidOperationException("SQLite rolled back the import transaction after the statement error; further statements were not executed.");
                                    }
                                    // A failed transaction command always stops: a failed
                                    // COMMIT can leave the transaction and locks open.
                                    if (!options.ContinueOnError || BeginTxRegex.IsMatch(sql) || CommitTxRegex.IsMatch(sql) || RollbackTxRegex.IsMatch(sql)) throw new InvalidOperationException(reason);
                                    continue;
                                }
                                executed++;
                                if (result.TryGetValue("rowsAffected", out string affected) && long.TryParse(affected, out long n)) rows += Math.Max(0, n);
                                if (BeginTxRegex.IsMatch(sql)) txOpen = true;
                                if (CommitTxRegex.IsMatch(sql) || RollbackTxRegex.IsMatch(sql)) txOpen = false;
                                if (create.Success) current.Add(Key(create));
                                var drop = DropObjectRegex.Match(sql);
                                if (drop.Success) current.Remove(Key(drop));
                                log.LogStatement(reader.StatementLine, Preview(sql), true, null, "OK"); log.Flush();
                                ReportProgress(progress, fileBytes, executed, failed, skipped, rows, clock, Preview(sql));
                            }
                        }
                        cancellation.ThrowIfCancellationRequested();
                        if (failed > 0) throw new InvalidOperationException("Import contains failed SQL statements; transaction was not committed.");
                        if (managed && txOpen) { ExecChecked("COMMIT;"); txOpen = false; }
                        if (txOpen || (db is my_sqlite completedSession && completedSession.MCT != null && !completedSession.MCT.AutoCommit)) throw new InvalidOperationException("Script ended with an active transaction; rolling back instead of reporting completion.");
                        if (manifests.Count > 0)
                        {
                            var report = new StringBuilder(); int matches = 0;
                            foreach (var pair in manifests)
                            {
                                long actual = db.CountRows(databaseName, pair.Item1);
                                if (actual < 0) throw new InvalidOperationException("Manifest count failed for " + pair.Item1);
                                bool equal = actual == pair.Item2; if (equal) matches++;
                                log.LogManifestVerification(pair.Item1, pair.Item2, actual, equal);
                                report.Append(pair.Item1 + ": " + actual + "/" + pair.Item2 + "; ");
                            }
                            manifestVerified = matches == manifests.Count; manifestReport = report.ToString();
                            // Preserving existing data intentionally changes row counts.
                            // ManifestVerified is row-count evidence, not data equivalence.
                        }
                    }
                }
                catch (OperationCanceledException) { cancelled = true; errors.Add("Import cancelled by user."); }
                catch (Exception ex) { if (!errors.Contains(ex.Message)) errors.Add(ex.Message); failed = Math.Max(1, failed); }
                finally
                {
                    if (txOpen)
                    {
                        try
                        {
                            if (!(db is my_sqlite cleanSession) || cleanSession.MCT == null || !cleanSession.MCT.AutoCommit) ExecChecked("ROLLBACK;");
                            if (db is my_sqlite rolledBack && rolledBack.MCT != null && !rolledBack.MCT.AutoCommit) throw new InvalidOperationException("SQLite transaction is still active.");
                            txOpen = false; log.LogInfo("Active import transaction rolled back.");
                        }
                        catch (Exception ex) { errors.Add("ROLLBACK cleanup failed: " + ex.Message); failed++; cancelled = false; }
                    }
                    foreach (var pragma in new[] { Tuple.Create("foreign_keys", originalFk), Tuple.Create("synchronous", originalSync) })
                    {
                        if (!pragma.Item2.HasValue) continue;
                        try
                        {
                            ExecChecked("PRAGMA " + pragma.Item1 + " = " + pragma.Item2.Value + ";");
                            if (ReadPragma(pragma.Item1) != pragma.Item2.Value) throw new InvalidOperationException(pragma.Item1 + " was not restored.");
                        }
                        catch (Exception ex) { errors.Add("PRAGMA cleanup failed: " + ex.Message); failed++; cancelled = false; }
                    }
                }
                clock.Stop();
                var status = cancelled ? SqliteImportStatus.Cancelled : failed > 0 ? SqliteImportStatus.Failed : SqliteImportStatus.Completed;
                var final = new SqliteImportResult(status, executed, failed, skipped, rows, log.Path, clock.Elapsed, errors, manifestVerified, manifestReport);
                log.WriteEnd(final);
                return final;
            }
        }
        private bool ShouldSkip(string sql, SqliteExistingObjectStrategy strategy, HashSet<string> initial, HashSet<string> tables, Dictionary<string, string> owners)
        {
            if (strategy != SqliteExistingObjectStrategy.SkipExisting && strategy != SqliteExistingObjectStrategy.OverwriteKeepExisting) return false;
            var change = CreateObjectRegex.Match(sql);
            if (!change.Success) change = DropObjectRegex.Match(sql);
            if (change.Success) return IsInitiallyProtected(change, sql, initial, tables, owners);
            var data = DataTargetRegex.Match(sql);
            if (!data.Success) return false;
            string name = Unquote(data.Groups["name"].Value);
            if (strategy == SqliteExistingObjectStrategy.SkipExisting && tables.Contains(name) && !name.Equals(my_sqlite.ColumnCommentTableName, StringComparison.OrdinalIgnoreCase) && !name.Equals("sqlite_sequence", StringComparison.OrdinalIgnoreCase)) return true;
            if (Regex.IsMatch(sql, @"^ALTER\s+TABLE\b", RegexOptions.IgnoreCase) && tables.Contains(name)) return true;
            // Comments and sequence records belong to the table named in their
            // literal, rather than to the sidecar/system table itself.
            if (name.Equals(my_sqlite.ColumnCommentTableName, StringComparison.OrdinalIgnoreCase) || name.Equals("sqlite_sequence", StringComparison.OrdinalIgnoreCase))
            {
                if (tables.Count > 0)
                {
                    string owner;
                    // A broad DELETE/UPDATE or multi-owner VALUES list cannot be
                    // applied safely while preserving existing owners. Only the
                    // native exporter's single-owner forms are permitted here.
                    if (!TryScopedMetadataOwner(sql, data, name, out owner)) return true;
                    if (tables.Contains(owner)) return true;
                }
            }
            return false;
        }
        private static bool TryScopedMetadataOwner(string sql, Match target, string name, out string owner)
        {
            owner = null;
            const string text = @"'(?:''|[^'])*'";
            string suffix = sql.Substring(target.Length);
            string pattern;
            bool insert = Regex.IsMatch(sql, @"^(?:INSERT|REPLACE)\b", RegexOptions.IgnoreCase);
            if (name.Equals("sqlite_sequence", StringComparison.OrdinalIgnoreCase))
            {
                pattern = insert
                    ? @"^\s*\(\s*name\s*,\s*seq\s*\)\s*VALUES\s*\(\s*'(?<owner>(?:''|[^'])*)'\s*,\s*[+-]?\d+\s*\)\s*;?\s*$"
                    : Regex.IsMatch(sql, @"^DELETE\b", RegexOptions.IgnoreCase)
                        ? @"^\s+WHERE\s+name\s*=\s*'(?<owner>(?:''|[^'])*)'\s*;?\s*$" : null;
            }
            else
            {
                string comment = @"(?:" + text + @"|CAST\s*\(\s*X'[0-9a-f]*'\s+AS\s+TEXT\s*\))";
                pattern = insert ? @"^\s*\(\s*table_name\s*,\s*column_name\s*,\s*comment\s*\)\s*VALUES\s*\(\s*'(?<owner>(?:''|[^'])*)'\s*,\s*" + text + @"\s*,\s*" + comment + @"\s*\)\s*;?\s*$" : null;
            }
            if (pattern == null) return false;
            Match match = Regex.Match(suffix, pattern, RegexOptions.IgnoreCase);
            if (!match.Success) return false;
            owner = match.Groups["owner"].Value.Replace("''", "'");
            return true;
        }
        private static bool IsInitiallyProtected(Match change, string sql, HashSet<string> initial, HashSet<string> tables, Dictionary<string, string> owners)
        {
            string key = Key(change), type = change.Groups["type"].Value.ToUpperInvariant();
            if (key.Equals("TABLE:" + my_sqlite.ColumnCommentTableName, StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(sql, @"^CREATE\s+TABLE\s+IF\s+NOT\s+EXISTS\b", RegexOptions.IgnoreCase)) return false;
            if (initial.Contains(key)) return true;
            if (type == "INDEX" || type == "TRIGGER")
            {
                if (owners.TryGetValue(key, out string owner) && tables.Contains(owner)) return true;
                var on = OnTargetRegex.Match(sql.Substring(change.Length));
                if (on.Success && tables.Contains(Unquote(on.Groups["name"].Value))) return true;
            }
            return false;
        }
        private DataTable SelectChecked(string sql)
        {
            DataTable result = db.SelectSQL(sql, new Dictionary<string, object>());
            if (result == null || result.ExtendedProperties.ContainsKey("QueryError"))
                throw new InvalidOperationException("SQLite query failed: " + (result == null ? "no result" : Convert.ToString(result.ExtendedProperties["QueryError"])));
            return result;
        }
        private int ReadPragma(string name)
        {
            DataTable result = SelectChecked("PRAGMA " + name + ";");
            if (result.Rows.Count != 1 || result.Columns.Count == 0) throw new InvalidOperationException("Cannot read PRAGMA " + name);
            return Convert.ToInt32(result.Rows[0][0]);
        }
        private Dictionary<string, string> ExecuteSql(string sql, CancellationToken cancellation)
        {
            if (!(db is my_sqlite native) || native.MCT == null) return db.ExecSQL(sql);
            using (var command = new SQLiteCommand(sql, native.MCT))
            using (cancellation.Register(() => { try { native.MCT.Cancel(); } catch { } }))
            {
                try { return new Dictionary<string, string> { { "status", "OK" }, { "rowsAffected", command.ExecuteNonQuery().ToString(System.Globalization.CultureInfo.InvariantCulture) } }; }
                catch (SQLiteException ex) { return new Dictionary<string, string> { { "status", "NO" }, { "reason", ex.Message } }; }
            }
        }
        private void ExecChecked(string sql)
        {
            var result = db.ExecSQL(sql);
            if (!IsExecutionSuccess(result)) throw new InvalidOperationException(sql + " " + DatabaseExecutionResultService.GetFailureReason(result));
        }
        private static bool IsExecutionSuccess(Dictionary<string, string> result)
        {
            return result != null && result.TryGetValue("status", out string status) && string.Equals(status, "OK", StringComparison.OrdinalIgnoreCase);
        }
        private static string Key(Match match) { return match.Groups["type"].Value.ToUpperInvariant() + ":" + Unquote(match.Groups["name"].Value); }
        private static string Unquote(string name)
        {
            if (name.Length > 1 && (name[0] == '"' || name[0] == '`' || name[0] == '\'' || name[0] == '['))
            {
                char quote = name[0]; name = name.Substring(1, name.Length - 2);
                if (quote != '[') name = name.Replace(new string(quote, 2), quote.ToString());
            }
            return name;
        }
        private static string QuoteIdentifier(string name) { return "\"" + name.Replace("\"", "\"\"") + "\""; }
        private static string Preview(string sql) { return Regex.Replace(sql.Substring(0, Math.Min(120, sql.Length)), @"\s+", " ").Trim(); }
        private static void ReportProgress(IProgress<SqliteImportProgress> progress, long bytes, int executed, int failed, int skipped, long rows, Stopwatch clock, string preview)
        {
            progress?.Report(new SqliteImportProgress(0, bytes, executed, failed, skipped, rows, 0, clock.Elapsed, null,
                "Executing: " + executed + " executed, " + failed + " failed, " + skipped + " skipped...", preview));
        }
    }
}
