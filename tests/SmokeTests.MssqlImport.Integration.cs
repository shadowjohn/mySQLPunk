using System;
using System.Collections.Generic;
using System.Collections;
using System.Data;
using System.Data.Common;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using mySQLPunk.lib;
using mySQLPunk.lib.MssqlImport;
using mySQLPunk.lib.MssqlExport;

public static partial class SmokeTests
{
    private static void MssqlImportConnectionOptions()
    {
        var settings = new Dictionary<string, object> {
            { "db_kind", "mssql" }, { "host", "fixture-server" }, { "port", "1433" },
            { "initial_database", "fixture-target" }, { "trusted_connection", "T" }, { "tls_mode", "Required" }
        };
        var builder = new SqlConnectionStringBuilder(ConnectionConfigurationService.BuildMssqlImportConnectionString(settings));
        Assert(!builder.MultipleActiveResultSets && builder.ConnectRetryCount == 0,
            "Import must disable MARS and reconnect before opening its owned connection.");
        Assert(builder.InitialCatalog == "fixture-target" && builder.IntegratedSecurity && builder.Encrypt,
            "Import keeps explicit target and existing authentication/TLS options.");
        var old = new SqlConnectionStringBuilder(ConnectionConfigurationService.BuildSqlServerConnectionString(settings));
        Assert(old.MultipleActiveResultSets && old.ConnectRetryCount == 1, "Existing callers keep their original connection defaults.");
        Assert((string)settings["initial_database"] == "fixture-target" && settings.Count == 6, "Builder does not mutate supplied settings.");
        settings["initial_database"] = " ";
        AssertThrows<ArgumentException>(() => ConnectionConfigurationService.BuildMssqlImportConnectionString(settings), "Missing target is refused before Open.");
        AssertThrows<ArgumentException>(() => ConnectionOpenService.OpenMssqlImportSession(settings), "Invalid target does not open a provider or SSH tunnel.");
        settings["db_kind"] = "mysql";
        AssertThrows<ArgumentException>(() => ConnectionOpenService.OpenMssqlImportSession(settings), "Importer refuses other engines.");
    }

    private static readonly MssqlImportBatch ImportSessionBatch = new MssqlImportBatch(1, 0, 20, 1, 2, "SELECT 'original; GO';\r\n-- preserve\r\n");

    private static void MssqlImportSessionDrainsResults()
    {
        var owner = new ImportSessionOwner();
        var reader = new ImportSessionReader(new[] { 200000, 0, 3 }, rejectValues: true);
        var command = new ImportSessionCommand(reader);
        using (var session = new MssqlImportSqlSession(owner, () => command, () => true)) {
            session.ExecuteBatch(ImportSessionBatch, CancellationToken.None);
            Assert(command.CommandText == ImportSessionBatch.Text && command.CommandTimeout == 0, "Original batch reaches SQL unchanged with timeout0.");
            Assert(command.Behavior == CommandBehavior.SequentialAccess && reader.RowsRead == 200003 && reader.NextCalls == 3,
                "Every row and result is drained sequentially without reading/materializing field values.");
            Assert(reader.Disposes == 1 && command.Disposes == 1 && command.LastToken == CancellationToken.None,
                "Completion includes reader and command disposal.");
        }
        Assert(owner.Disposes == 1, "Only the owned session is released.");

        foreach (string point in new[] { "start", "read", "next", "dispose" }) {
            var failedReader = new ImportSessionReader(new[] { 1 }, rejectValues: true);
            var failedCommand = new ImportSessionCommand(failedReader);
            var primary = DiagnosticSql(547, 2627);
            if (point == "start") failedCommand.StartError = primary;
            if (point == "read") failedReader.ReadError = primary;
            if (point == "next") failedReader.NextError = primary;
            failedReader.DisposeError = point == "dispose" ? primary : DiagnosticSql(50001);
            var failedOwner = new ImportSessionOwner();
            using (var session = new MssqlImportSqlSession(failedOwner, () => failedCommand, () => true)) {
                try { session.ExecuteBatch(ImportSessionBatch, CancellationToken.None); throw new Exception("Expected SQL failure " + point); }
                catch (MssqlImportBatchException e) {
                    Assert(e.Errors.Count == (point == "start" || point == "dispose" ? 2 : 3), "All SQL details survive " + point + " and reader disposal.");
                    Assert(e.Errors[0].Number == 547 && e.Errors[1].Number == 2627 && !e.OutcomeUncertain,
                        "Ordinary provider details preserve number/state/class/line without inventing uncertainty: " + point);
                }
                Assert(failedCommand.Disposes == 1, "Failed command releases once: " + point);
            }
            Assert(failedOwner.Disposes == 1, "Failure owns only its session: " + point);
        }
        var transport = new ImportSessionCommand(null) { StartError = new IOException("fixture transport failure") };
        using (var session = new MssqlImportSqlSession(new ImportSessionOwner(), () => transport, () => true)) {
            try { session.ExecuteBatch(ImportSessionBatch, CancellationToken.None); throw new Exception("Expected transport failure"); }
            catch (MssqlImportBatchException e) { Assert(e.OutcomeUncertain && e.Errors.Count == 0, "Transport failure is uncertain and cannot look like a skippable SQL error."); }
        }
        foreach (int code in new[] { -2, 0, 64, 233, 10053, 10054, 10060 }) {
            var network = new ImportSessionCommand(null) { StartError = DiagnosticSql(code) };
            using (var session = new MssqlImportSqlSession(new ImportSessionOwner(), () => network, () => true)) {
                try { session.ExecuteBatch(ImportSessionBatch, CancellationToken.None); throw new Exception("Expected network/timeout failure"); }
                catch (MssqlImportBatchException e) { Assert(e.OutcomeUncertain && e.Errors.Count == 1, "Known timeout/transport codes cannot become ordinary skippable SQL errors: " + code); }
            }
        }
        var unexpectedCancel = new ImportSessionCommand(null) { StartError = new OperationCanceledException() };
        using (var session = new MssqlImportSqlSession(new ImportSessionOwner(), () => unexpectedCancel, () => true))
            AssertThrows<MssqlImportBatchException>(() => session.ExecuteBatch(ImportSessionBatch, CancellationToken.None), "Provider cancellation without a caller request is uncertain, not user cancellation.");
    }

    private static void MssqlImportSessionCancellationOwnership()
    {
        var owner = new ImportSessionOwner(); var tree = new ImportSessionOwner();
        var entered = new ManualResetEventSlim(); var release = new ManualResetEventSlim();
        var command = new ImportSessionCommand(new ImportSessionReader(new[] { 0 }, true));
        command.Start = (behavior, token) => { entered.Set(); release.Wait(); return Task.FromResult<DbDataReader>(command.Reader); };
        using (var cancel = new CancellationTokenSource()) using (var session = new MssqlImportSqlSession(owner, () => command, () => true, 40)) {
            var task = Task.Run(() => { try { session.ExecuteBatch(ImportSessionBatch, cancel.Token); } catch (OperationCanceledException) { } });
            Assert(entered.Wait(2000), "Command startup enters active ownership.");
            AssertThrows<InvalidOperationException>(() => session.ReadState(CancellationToken.None), "No state query can overlap active reader/startup.");
            cancel.Cancel();
            Assert(command.Cancels > 0 && command.LastToken == cancel.Token, "Cancellation reaches the exact startup command and token.");
            Assert(owner.Released.Wait(2000), "Watchdog attempts owned close after cancellation deadline.");
            var pending = ImportWaitForPublishedReleaseWhileActive(session);
            Assert(!pending.CommandEnded && pending.SessionReleased && !pending.TransactionCleanupConfirmed,
                "Owned close is not command acknowledgement or rollback proof.");
            release.Set(); Assert(task.Wait(2000), "Worker unwinds only after provider completion.");
            var final = session.Cleanup();
            Assert(final.CommandEnded && final.SessionReleased && !final.TransactionCleanupConfirmed, "Unwound closed session still cannot prove rollback.");
            Parallel.For(0, 20, i => session.Dispose());
            Assert(owner.Disposes == 1 && tree.Disposes == 0, "Cancel/cleanup/Dispose race releases this owner once and never the independent tree owner.");
        }
        entered.Dispose(); release.Dispose();

        foreach (string phase in new[] { "read", "next", "read-sql", "dispose" }) {
            using (var cancel = new CancellationTokenSource()) {
                var readOwner = new ImportSessionOwner(); var reader = new ImportSessionReader(new[] { 1 }, true);
                var reading = new ImportSessionCommand(reader);
                if (phase == "dispose") reader.BeforeDispose = () => cancel.Cancel();
                else if (phase == "next") reader.BeforeNext = () => cancel.Cancel();
                else reader.BeforeRead = () => cancel.Cancel();
                if (phase == "read-sql") reader.ReadError = DiagnosticSql(547, 2627);
                using (var session = new MssqlImportSqlSession(readOwner, () => reading, () => true)) {
                    try { session.ExecuteBatch(ImportSessionBatch, cancel.Token); throw new Exception("Expected cancellation during " + phase); }
                    catch (OperationCanceledException) { Assert(phase != "read-sql", "SQL details must not disappear behind cancellation."); }
                    catch (MssqlImportBatchException error) { Assert(phase == "read-sql" && error.OutcomeUncertain && error.Errors.Count == 2, "Cancellation retains all delivered SQL details before stopping."); }
                    Assert(reading.Cancels == 1 && reader.Disposes == 1 && reading.Disposes == 1, "Read/NextResult cancellation retains ownership until disposal: " + phase);
                }
                Assert(readOwner.Disposes == 1, "Read cancellation releases exact owner: " + phase);
            }
        }
        var disposingOwner = new ImportSessionOwner();
        using (var disposeEntered = new ManualResetEventSlim()) using (var disposeRelease = new ManualResetEventSlim()) {
            var reader = new ImportSessionReader(new[] { 0 }, true) { BeforeDispose = () => { disposeEntered.Set(); disposeRelease.Wait(); } };
            var cmd = new ImportSessionCommand(reader);
            using (var session = new MssqlImportSqlSession(disposingOwner, () => cmd, () => true)) {
                var task = Task.Run(() => session.ExecuteBatch(ImportSessionBatch, CancellationToken.None));
                Assert(disposeEntered.Wait(2000) && !task.IsCompleted, "Reader Dispose remains inside batch acknowledgement.");
                AssertThrows<InvalidOperationException>(() => session.ReadState(CancellationToken.None), "A blocked reader Dispose still owns the command slot.");
                Assert(!session.Cleanup().CommandEnded, "Cleanup cannot acknowledge an unfinished reader Dispose.");
                disposeRelease.Set(); Assert(task.Wait(2000), "Batch acknowledgement follows actual Dispose completion.");
            }
        }

        var lateOwner = new ImportSessionOwner(); var commands = new Queue<ImportSessionCommand>();
        var first = new ImportSessionCommand(new ImportSessionReader(new[] { 0 }, true));
        first.Start = (behavior, token) => { token.ThrowIfCancellationRequested(); return Task.FromResult<DbDataReader>(first.Reader); };
        var second = new ImportSessionCommand(new ImportSessionReader(new[] { 0 }, true));
        var secondEntered = new ManualResetEventSlim(); var secondRelease = new ManualResetEventSlim();
        second.Start = (behavior, token) => { secondEntered.Set(); secondRelease.Wait(); return Task.FromResult<DbDataReader>(second.Reader); };
        commands.Enqueue(first); commands.Enqueue(second);
        using (var session = new MssqlImportSqlSession(lateOwner, () => commands.Dequeue(), () => true, 50)) {
            first.Reader.BeforeRead = () => session.CancelActiveCommand();
            session.ExecuteBatch(ImportSessionBatch, CancellationToken.None);
            var later = Task.Run(() => session.ExecuteBatch(ImportSessionBatch, CancellationToken.None));
            Assert(secondEntered.Wait(2000) && !lateOwner.Released.Wait(100), "A completed command's cancellation timer cannot close an active later generation.");
            secondRelease.Set(); Assert(later.Wait(2000), "Later generation completes normally.");
        }
        secondEntered.Dispose(); secondRelease.Dispose();
        Assert(lateOwner.Disposes == 1, "Normal disposal releases the surviving owner.");
        var invalidOwner = new ImportSessionOwner();
        AssertThrows<ArgumentNullException>(() => new MssqlImportSqlSession(invalidOwner, null, () => true), "Initialization failure releases transferred lifetime ownership.");
        Assert(invalidOwner.Disposes == 1, "Failed initialization releases exactly once.");

        var closedConnection = new SqlConnection();
        var provider = new FakeExecDatabase("mssql", "0");
        using (var real = new MssqlImportSqlSession(new MssqlOwnedSession(provider, closedConnection))) {
            AssertThrows<MssqlImportBatchException>(() => real.ExecuteBatch(ImportSessionBatch, CancellationToken.None), "Real closed SQL connection startup fails, without opening a database.");
        }
        closedConnection.Dispose();
    }

    private static ImportSessionReader ImportValues(params object[] row) { return new ImportSessionReader(new[] { new[] { row } }); }
    private static object[] ImportIdentity(int user = 5, object viewDefinition = null, string version = "15.0.2000.5")
    { return new object[] { "fixture-server", "fixture", 7, new DateTime(2026, 10, 13), version, "READ_WRITE", user, viewDefinition ?? 1, 0, 0 }; }

    private static void MssqlImportSessionMetadataProof()
    {
        foreach (string mode in new[] { "dbo", "no-deny", "deny", "low", "null", "2022-no-security", "2022-security", "denied-scan", "count-failure" }) {
            var owner = new ImportSessionOwner(); var issued = new List<ImportSessionCommand>();
            var identity = ImportIdentity(mode == "dbo" ? 1 : 5, mode == "low" ? (object)0 : mode == "null" ? DBNull.Value : 1, mode.StartsWith("2022") ? "16.0.1" : "15.0.1");
            var queue = new Queue<ImportSessionReader>(); queue.Enqueue(ImportValues(identity));
            if (mode.StartsWith("2022")) queue.Enqueue(ImportValues(mode == "2022-security" ? 1 : 0));
            if (mode != "dbo" && mode != "low" && mode != "null" && mode != "2022-no-security") queue.Enqueue(ImportValues(mode == "deny" ? 1L : 0L));
            bool complete = mode == "dbo" || mode == "no-deny" || mode == "2022-security";
            if (complete || mode == "count-failure") queue.Enqueue(ImportValues(0L));
            using (var session = new MssqlImportSqlSession(owner, () => {
                var cmd = new ImportSessionCommand(queue.Dequeue()); issued.Add(cmd);
                if (mode == "denied-scan" && issued.Count == 2 || mode == "count-failure" && issued.Count == 3) cmd.StartError = DiagnosticSql(229);
                return cmd;
            }, () => true)) {
                var target = session.ReadTarget(CancellationToken.None);
                Assert(target.DatabaseId == 7 && target.DatabaseName == "fixture" && target.Writable && !target.CanCreateTable.Value,
                    "Actual identity/writeability/create capability are independent: " + mode);
                Assert(target.MetadataVisible == complete && target.UserObjectCount == (complete ? (long?)0 : null),
                    "Only proven complete metadata yields empty0; unknown remains null: " + mode);
                Assert(issued.All(c => c.CommandTimeout == 5 && c.Disposes == 1), "Read-only metadata probes use5sec and dispose before another: " + mode);
                var denyQuery = issued.Select(c => c.CommandText).FirstOrDefault(sql => sql.Contains("sys.database_permissions"));
                if (denyQuery != null) Assert(denyQuery.Contains("sys.user_token") && !denyQuery.Contains("sys.objects"), "Effective-token DENY scan cannot hide rows via object-catalog join.");
            }
        }
        foreach (int column in new[] { 0, 1, 2, 3, 4 }) {
            var identity = ImportIdentity(); identity[column] = DBNull.Value;
            using (var session = new MssqlImportSqlSession(new ImportSessionOwner(), () => new ImportSessionCommand(ImportValues(identity)), () => true))
                AssertThrows<InvalidOperationException>(() => session.ReadTarget(CancellationToken.None), "Missing target identity is a failed probe, never synthesized.");
        }
    }

    private static void MssqlImportSessionProbesAndCleanup()
    {
        var owner = new ImportSessionOwner(); var commands = new List<ImportSessionCommand>();
        var readers = new Queue<ImportSessionReader>(new[] { ImportValues(7, 0, 0), ImportValues(true, 1L, 9L, 9L, true, false), ImportValues(0, 0) });
        using (var session = new MssqlImportSqlSession(owner, () => { var cmd = new ImportSessionCommand(readers.Dequeue()); commands.Add(cmd); return cmd; }, () => true)) {
            var state = session.ReadState(CancellationToken.None); Assert(state.IsOpen && state.DatabaseId == 7 && state.TransactionCount == 0 && state.XactState == 0, "State keeps DB and transaction facts.");
            Assert(commands[0].CommandText.StartsWith("DECLARE @databaseId int=DB_ID(); SELECT @databaseId,@@TRANCOUNT,XACT_STATE();", StringComparison.Ordinal),
                "Capture metadata DB_ID in its own statement so its SQL17 internal transaction cannot pollute XACT_STATE in the state SELECT.");
            var job = new Guid("10000000-0000-0000-0000-000000000001"); var lob = session.ReadLobState(job, CancellationToken.None);
            Assert(lob.Exists && lob.RowCount == 1 && lob.Sequence == 9 && lob.ConsumedThrough == 9 && lob.Consumed && !lob.Failed, "LOB aggregate preserves unique row and protocol counters.");
            Assert(commands[1].CommandText.Contains("#mp_" + job.ToString("N") + "_s") && commands[1].CommandTimeout == 5, "LOB lookup uses only GUID-derived identifier and bounded timeout.");
            var result = session.Cleanup();
            Assert(result.CommandEnded && result.SessionReleased && result.TransactionCleanupConfirmed && result.IssueCode == null, "Only returned zero transaction state and released owner confirm cleanup.");
            Assert(commands[2].CommandTimeout == 10 && commands[2].LastToken == CancellationToken.None && commands[2].CommandText.Contains("ROLLBACK"), "Cleanup is independent10sec with uncancelled token.");
            Assert(object.ReferenceEquals(result, session.Cleanup()) && commands.Count == 3 && owner.Disposes == 1, "Repeated cleanup returns actual cached receipt without another query or release.");
        }
        foreach (string failure in new[] { "closed", "rollback", "release" }) {
            var failedOwner = new ImportSessionOwner { Error = failure == "release" ? new IOException("fixture release") : null };
            var cmd = new ImportSessionCommand(ImportValues(0, 0)) { StartError = failure == "rollback" ? DiagnosticSql(-2) : null };
            using (var session = new MssqlImportSqlSession(failedOwner, () => cmd, () => failure != "closed")) {
                var result = session.Cleanup();
                Assert(result.CommandEnded && result.SessionReleased == (failure != "release") && result.TransactionCleanupConfirmed == (failure == "release") && result.IssueCode != null,
                    "Cleanup distinguishes actual rollback and resource-release evidence: " + failure);
            }
        }

        var countCommands = new List<ImportSessionCommand>();
        var rows = new Queue<ImportSessionReader>(new[] { ImportValues(ImportIdentity(1)), ImportValues(2L), ImportValues(10, "schema]x", "table]x"), ImportValues(3000000000L), ImportValues(11, "dbo", "t2"), ImportValues(4L), new ImportSessionReader(new object[][][] { new object[0][] }) });
        using (var session = new MssqlImportSqlSession(new ImportSessionOwner(), () => { var cmd = new ImportSessionCommand(rows.Dequeue()); countCommands.Add(cmd); return cmd; }, () => true)) {
            long progressTables = 0, progressRows = 0;
            var counts = session.CountTargetRows((tables, total) => { progressTables = tables; progressRows = total; }, CancellationToken.None);
            Assert(counts.Tables == 2 && counts.Rows == 3000000004L && progressTables == 2 && progressRows == counts.Rows, "COUNT_BIG totals and callbacks retain Int64 actual results.");
            Assert(countCommands.Any(c => c.CommandText.Contains("[schema]]x].[table]]x]")), "Every catalog identifier is bracket quoted.");
            Assert(countCommands.Where(c => c.CommandText.Contains("COUNT_BIG(*) FROM [")).All(c => c.CommandTimeout == 0), "Long row counts remain cancellable without a default timeout.");
        }
    }

    private sealed class ImportSessionOwner : IDisposable
    {
        internal int Disposes; internal Exception Error; internal Action BeforeReturn; internal ManualResetEventSlim Released = new ManualResetEventSlim();
        public void Dispose() { Interlocked.Increment(ref Disposes); Released.Set(); if (BeforeReturn != null) BeforeReturn(); if (Error != null) throw Error; }
    }
    private sealed class ImportSessionCommand : DbCommand
    {
        internal ImportSessionReader Reader; internal Exception StartError; internal Func<CommandBehavior, CancellationToken, Task<DbDataReader>> Start;
        internal CommandBehavior Behavior; internal CancellationToken LastToken; internal int Cancels, Disposes;
        internal ImportSessionCommand(ImportSessionReader reader) { Reader = reader; }
        public override string CommandText { get; set; }
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        public override bool DesignTimeVisible { get; set; }
        protected override DbConnection DbConnection { get; set; }
        protected override DbTransaction DbTransaction { get; set; }
        protected override DbParameterCollection DbParameterCollection { get { throw new NotSupportedException(); } }
        protected override DbParameter CreateDbParameter() { throw new NotSupportedException(); }
        public override void Cancel() { Interlocked.Increment(ref Cancels); }
        public override void Prepare() { throw new NotSupportedException(); }
        public override int ExecuteNonQuery() { throw new Exception("ExecuteNonQuery would skip result-drain acknowledgement"); }
        public override object ExecuteScalar() { throw new NotSupportedException(); }
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) { throw new Exception("Startup must use async cancellation token"); }
        protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken token)
        { Behavior = behavior; LastToken = token; if (StartError != null) throw StartError; return Start != null ? Start(behavior, token) : Task.FromResult<DbDataReader>(Reader); }
        protected override void Dispose(bool disposing) { if (disposing) Disposes++; base.Dispose(disposing); }
    }
    private sealed class ImportSessionReader : DbDataReader
    {
        private readonly int[] rowCounts; private readonly object[][][] values; private readonly bool rejectValues;
        private int result, row = -1;
        internal long RowsRead; internal int NextCalls, Disposes; internal Exception ReadError, NextError, DisposeError; internal Action BeforeRead, BeforeNext, BeforeDispose;
        internal ImportSessionReader(int[] rowCounts, bool rejectValues) { this.rowCounts = rowCounts; this.rejectValues = rejectValues; }
        internal ImportSessionReader(object[][][] values) { this.values = values; rowCounts = values.Select(x => x.Length).ToArray(); }
        public override bool Read() { if (BeforeRead != null) { var action = BeforeRead; BeforeRead = null; action(); } if (ReadError != null) throw ReadError; bool found = ++row < rowCounts[result]; if (found) RowsRead++; return found; }
        public override bool NextResult() { NextCalls++; if (BeforeNext != null) { var action = BeforeNext; BeforeNext = null; action(); } if (NextError != null) throw NextError; if (result + 1 >= rowCounts.Length) return false; result++; row = -1; return true; }
        public override Task<bool> ReadAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(Read()); }
        public override Task<bool> NextResultAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(NextResult()); }
        public override int FieldCount { get { return values == null || values[result].Length == 0 ? 0 : values[result][0].Length; } }
        public override object GetValue(int ordinal) { if (rejectValues) throw new Exception("Drain must not materialize LOB or field values"); return values[result][row][ordinal]; }
        public override bool IsDBNull(int ordinal) { return GetValue(ordinal) == DBNull.Value; }
        public override string GetString(int ordinal) { return (string)GetValue(ordinal); }
        public override int GetInt32(int ordinal) { return Convert.ToInt32(GetValue(ordinal)); }
        public override long GetInt64(int ordinal) { return Convert.ToInt64(GetValue(ordinal)); }
        public override bool GetBoolean(int ordinal) { return Convert.ToBoolean(GetValue(ordinal)); }
        public override DateTime GetDateTime(int ordinal) { return (DateTime)GetValue(ordinal); }
        public override object this[int ordinal] { get { return GetValue(ordinal); } }
        public override object this[string name] { get { throw new NotSupportedException(); } }
        public override int Depth { get { return 0; } }
        public override bool HasRows { get { return rowCounts[result] > 0; } }
        public override bool IsClosed { get { return Disposes > 0; } }
        public override int RecordsAffected { get { throw new Exception("Affected rows are not imported rows"); } }
        public override void Close() { }
        protected override void Dispose(bool disposing) { if (disposing) { if (BeforeDispose != null) BeforeDispose(); Disposes++; if (DisposeError != null) throw DisposeError; } }
        public override string GetName(int ordinal) { return ordinal.ToString(); }
        public override int GetOrdinal(string name) { throw new NotSupportedException(); }
        public override string GetDataTypeName(int ordinal) { throw new NotSupportedException(); }
        public override Type GetFieldType(int ordinal) { throw new NotSupportedException(); }
        public override int GetValues(object[] buffer) { throw new NotSupportedException(); }
        public override byte GetByte(int ordinal) { throw new NotSupportedException(); }
        public override long GetBytes(int ordinal, long offset, byte[] buffer, int bufferOffset, int length) { throw new Exception("LOB getter forbidden in drain"); }
        public override char GetChar(int ordinal) { throw new NotSupportedException(); }
        public override long GetChars(int ordinal, long offset, char[] buffer, int bufferOffset, int length) { throw new Exception("LOB getter forbidden in drain"); }
        public override Guid GetGuid(int ordinal) { throw new NotSupportedException(); }
        public override short GetInt16(int ordinal) { throw new NotSupportedException(); }
        public override float GetFloat(int ordinal) { throw new NotSupportedException(); }
        public override double GetDouble(int ordinal) { throw new NotSupportedException(); }
        public override decimal GetDecimal(int ordinal) { throw new NotSupportedException(); }
        public override IEnumerator GetEnumerator() { throw new NotSupportedException(); }
    }
}
