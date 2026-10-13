using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Data.SqlClient;
using System.Globalization;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using mySQLPunk.lib.MssqlExport;

namespace mySQLPunk.lib.MssqlImport
{
    internal interface IMssqlImportSession : IDisposable
    {
        MssqlImportTarget ReadTarget(CancellationToken cancellation);
        MssqlImportSessionState ReadState(CancellationToken cancellation);
        MssqlImportLobState ReadLobState(Guid jobId, CancellationToken cancellation);
        void ExecuteBatch(MssqlImportBatch batch, CancellationToken cancellation);
        MssqlImportCounts CountTargetRows(Action<long, long> progress, CancellationToken cancellation);
        void CancelActiveCommand();
        MssqlImportCleanup Cleanup();
    }

    internal sealed class MssqlImportSqlSession : IMssqlImportSession
    {
        private readonly object gate = new object();
        private readonly Func<DbCommand> createCommand;
        private readonly Func<bool> isOpen;
        private readonly int cancelMilliseconds, cleanupReleaseWaitMilliseconds;
        private IDisposable owner;
        private ActiveCommand active;
        private bool releaseStarted, releaseCompleted, sessionReleased, cleanupRunning;
        private MssqlImportCleanup cleanup;
        private IReadOnlyList<MssqlImportSqlError> releaseErrors;

        private sealed class ActiveCommand
        {
            internal readonly DbCommand Command;
            internal CancellationTokenRegistration Registration;
            internal Timer Watchdog;
            internal bool CancellationRequested;
            internal ActiveCommand(DbCommand command) { Command = command; }
        }

        internal MssqlImportSqlSession(MssqlOwnedSession owner)
            : this(owner, () => owner.Connection.CreateCommand(), () => owner.Connection.State == ConnectionState.Open) { }

        // The test seam substitutes only this connection's command factory and lifetime, never a shared provider.
        internal MssqlImportSqlSession(IDisposable owner, Func<DbCommand> createCommand, Func<bool> isOpen, int cancelMilliseconds = 10000, int cleanupReleaseWaitMilliseconds = 10000)
        {
            try
            {
                if (owner == null) throw new ArgumentNullException(nameof(owner));
                if (createCommand == null) throw new ArgumentNullException(nameof(createCommand));
                if (isOpen == null) throw new ArgumentNullException(nameof(isOpen));
                if (cancelMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(cancelMilliseconds));
                if (cleanupReleaseWaitMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(cleanupReleaseWaitMilliseconds));
                this.owner = owner; this.createCommand = createCommand; this.isOpen = isOpen; this.cancelMilliseconds = cancelMilliseconds; this.cleanupReleaseWaitMilliseconds = cleanupReleaseWaitMilliseconds;
            }
            catch { try { if (owner != null) owner.Dispose(); } catch { } throw; }
        }

        public MssqlImportTarget ReadTarget(CancellationToken cancellation)
        {
            object[] identity = ReadValues(@"SELECT CONVERT(nvarchar(128),SERVERPROPERTY('ServerName')),DB_NAME(),DB_ID(),
(SELECT create_date FROM sys.databases WHERE database_id=DB_ID()),
CONVERT(nvarchar(128),SERVERPROPERTY('ProductVersion')),
CONVERT(nvarchar(60),DATABASEPROPERTYEX(DB_NAME(),'Updateability')),USER_ID(),
HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','VIEW DEFINITION'),
HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','CREATE TABLE'),HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','CREATE VIEW');", 5, cancellation);
            if (identity == null || identity.Length != 10 || Missing(identity[0]) || Missing(identity[1]) || Missing(identity[2]) || Missing(identity[3]) || Missing(identity[4]))
                throw new InvalidOperationException("TargetIdentityUnavailable");
            string server = Convert.ToString(identity[0], CultureInfo.InvariantCulture);
            string database = Convert.ToString(identity[1], CultureInfo.InvariantCulture);
            string version = Convert.ToString(identity[4], CultureInfo.InvariantCulture);
            int databaseId = Convert.ToInt32(identity[2], CultureInfo.InvariantCulture), major;
            if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(database) || databaseId <= 0 ||
                !int.TryParse(version.Split('.')[0], NumberStyles.None, CultureInfo.InvariantCulture, out major) || major <= 0 || !(identity[3] is DateTime))
                throw new InvalidOperationException("TargetIdentityUnavailable");
            bool complete = !Missing(identity[6]) && Convert.ToInt32(identity[6], CultureInfo.InvariantCulture) == 1;
            long? count = null;
            try
            {
                if (!complete && Permission(identity[7]) == true)
                {
                    bool securityVisible = major < 16 || Permission(Scalar("SELECT HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','VIEW SECURITY DEFINITION');", 5, cancellation)) == true;
                    if (securityVisible)
                    {
                        // Scan raw permission rows for the whole effective token, including roles/public/Windows groups.
                        // Joining objects here could hide the very DENY that makes an apparent empty catalog unsafe.
                        object denied = Scalar(@"SELECT COUNT_BIG(*) FROM sys.database_permissions AS p
WHERE p.state='D' AND p.grantee_principal_id IN (SELECT principal_id FROM sys.user_token);", 5, cancellation);
                        complete = !Missing(denied) && Convert.ToInt64(denied, CultureInfo.InvariantCulture) == 0;
                    }
                }
                if (complete)
                {
                    object objects = Scalar("SELECT COUNT_BIG(*) FROM sys.objects WHERE is_ms_shipped=0;", 5, cancellation);
                    if (!Missing(objects) && Convert.ToInt64(objects, CultureInfo.InvariantCulture) >= 0) count = Convert.ToInt64(objects, CultureInfo.InvariantCulture);
                    else complete = false;
                }
            }
            catch (MssqlImportBatchException error)
            {
                // Lack of metadata is acceptable for Generic/DataOnly, but a broken/cancelled session is not.
                if (error.OutcomeUncertain || cancellation.IsCancellationRequested || !ConnectionUsable()) throw;
                complete = false; count = null;
            }
            return new MssqlImportTarget(server, database, databaseId, (DateTime)identity[3], version,
                string.Equals(Convert.ToString(identity[5], CultureInfo.InvariantCulture), "READ_WRITE", StringComparison.OrdinalIgnoreCase),
                complete, complete ? count : null, Permission(identity[8]), Permission(identity[9]));
        }

        public MssqlImportSessionState ReadState(CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            EnsureNoActiveCommand();
            if (!ConnectionUsable()) return new MssqlImportSessionState(false, 0, 0, 0);
            // Observed on SQL17.0.4025.3: DB_ID metadata work exposed XACT_STATE=1 in the same SELECT at @@TRANCOUNT=0.
            // Complete that statement first, then observe the actual caller transaction without normalizing its state.
            object[] state = ReadValues("DECLARE @databaseId int=DB_ID(); SELECT @databaseId,@@TRANCOUNT,XACT_STATE();", 5, cancellation);
            if (state == null || state.Length != 3 || Missing(state[0]) || Missing(state[1]) || Missing(state[2]) || Convert.ToInt32(state[0], CultureInfo.InvariantCulture) <= 0)
                throw new InvalidOperationException("SessionStateUnavailable");
            return new MssqlImportSessionState(true, Convert.ToInt32(state[0], CultureInfo.InvariantCulture),
                Convert.ToInt32(state[1], CultureInfo.InvariantCulture), Convert.ToInt32(state[2], CultureInfo.InvariantCulture));
        }

        public MssqlImportLobState ReadLobState(Guid jobId, CancellationToken cancellation)
        {
            if (jobId == Guid.Empty) throw new ArgumentException("A native LOB job identity is required.", nameof(jobId));
            string name = "#mp_" + jobId.ToString("N") + "_s";
            object[] state = ReadValues("IF OBJECT_ID(N'tempdb.." + name + "') IS NULL " +
                "SELECT CAST(0 AS bit),CAST(0 AS bigint),CAST(0 AS bigint),CAST(0 AS bigint),CAST(0 AS bit),CAST(0 AS bit); " +
                "ELSE SELECT CAST(1 AS bit),COUNT_BIG(*),COALESCE(MAX([Sequence]),0),COALESCE(MAX([ConsumedThrough]),0)," +
                "CAST(COALESCE(MIN(CAST([Consumed] AS int)),0) AS bit),CAST(COALESCE(MAX(CAST([Failed] AS int)),0) AS bit) FROM " + Quote(name) + ";", 5, cancellation);
            if (state == null || state.Length != 6) throw new InvalidOperationException("LobStateUnavailable");
            for (int i = 0; i < state.Length; i++) if (Missing(state[i])) throw new InvalidOperationException("LobStateUnavailable");
            return new MssqlImportLobState(Convert.ToBoolean(state[0], CultureInfo.InvariantCulture), Convert.ToInt64(state[1], CultureInfo.InvariantCulture),
                Convert.ToInt64(state[2], CultureInfo.InvariantCulture), Convert.ToInt64(state[3], CultureInfo.InvariantCulture),
                Convert.ToBoolean(state[4], CultureInfo.InvariantCulture), Convert.ToBoolean(state[5], CultureInfo.InvariantCulture));
        }

        public void ExecuteBatch(MssqlImportBatch batch, CancellationToken cancellation)
        {
            if (batch == null) throw new ArgumentNullException(nameof(batch));
            WithReader(batch.Text, 0, cancellation, reader => { Drain(reader, cancellation); return true; });
        }

        public MssqlImportCounts CountTargetRows(Action<long, long> progress, CancellationToken cancellation)
        {
            var target = ReadTarget(cancellation);
            if (!target.MetadataVisible) throw new InvalidOperationException("TargetMetadataIncomplete");
            int after = 0; long tables = 0, rows = 0;
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                // Keyset lookup closes before COUNT_BIG, so MARS is unnecessary and no catalog list grows in memory.
                object[] table = ReadValues("SELECT TOP(1) t.object_id,s.name,t.name FROM sys.tables AS t JOIN sys.schemas AS s ON s.schema_id=t.schema_id " +
                    "WHERE t.is_ms_shipped=0 AND t.object_id>" + after.ToString(CultureInfo.InvariantCulture) + " ORDER BY t.object_id;", 5, cancellation);
                if (table == null) break;
                if (table.Length != 3 || Missing(table[0]) || Missing(table[1]) || Missing(table[2])) throw new InvalidOperationException("TargetTableUnavailable");
                int next = Convert.ToInt32(table[0], CultureInfo.InvariantCulture);
                if (next <= after) throw new InvalidOperationException("TargetTableOrderInvalid");
                object actual = Scalar("SELECT COUNT_BIG(*) FROM " + Quote(Convert.ToString(table[1], CultureInfo.InvariantCulture)) + "." +
                    Quote(Convert.ToString(table[2], CultureInfo.InvariantCulture)) + ";", 0, cancellation);
                if (Missing(actual)) throw new InvalidOperationException("TargetCountUnavailable");
                long count = Convert.ToInt64(actual, CultureInfo.InvariantCulture);
                if (count < 0) throw new InvalidOperationException("TargetCountUnavailable");
                rows = checked(rows + count); tables = checked(tables + 1); after = next;
                if (progress != null) progress(tables, rows);
            }
            return new MssqlImportCounts(tables, rows);
        }

        public void CancelActiveCommand()
        {
            ActiveCommand command;
            lock (gate) command = active;
            if (command != null) RequestCancellation(command);
        }

        private void RequestCancellation(ActiveCommand command)
        {
            lock (gate)
            {
                if (active != command || command.CancellationRequested) return;
                command.CancellationRequested = true;
                command.Watchdog = new Timer(_ => ReleaseOwner(command), null, cancelMilliseconds, Timeout.Infinite);
            }
            try { command.Command.Cancel(); } catch { /* Completion/disposal can win this exact command's race. */ }
        }

        public MssqlImportCleanup Cleanup()
        {
            lock (gate)
            {
                if (cleanup != null) return cleanup;
                if (active != null) return new MssqlImportCleanup(false, sessionReleased, false, "CommandStillActive");
                if (cleanupRunning) return new MssqlImportCleanup(false, sessionReleased, false, "CleanupStillActive");
                cleanupRunning = true;
            }
            bool transactionConfirmed = false;
            IReadOnlyList<MssqlImportSqlError> cleanupErrors = null;
            string issue = null;
            try
            {
                if (!ConnectionUsable()) issue = "SessionUnavailable";
                else
                {
                    object[] state = ReadValues("IF @@TRANCOUNT>0 ROLLBACK TRANSACTION; SELECT @@TRANCOUNT,XACT_STATE();", 10, CancellationToken.None, true);
                    transactionConfirmed = state != null && state.Length == 2 && !Missing(state[0]) && !Missing(state[1]) &&
                        Convert.ToInt32(state[0], CultureInfo.InvariantCulture) == 0 && Convert.ToInt32(state[1], CultureInfo.InvariantCulture) == 0;
                    if (!transactionConfirmed) issue = "TransactionCleanupUnconfirmed";
                }
            }
            catch (MssqlImportBatchException error) { issue = "TransactionCleanupFailed"; cleanupErrors = error.Errors; }
            catch { issue = "TransactionCleanupFailed"; }
            finally { ReleaseOwner(null); }
            lock (gate)
            {
                // A watchdog may still be inside Dispose after the provider has unwound.
                // Take the final diagnostic snapshot only after publication or this monotonic bound.
                var releaseWait = Stopwatch.StartNew();
                while (releaseStarted && !releaseCompleted)
                {
                    long remaining = cleanupReleaseWaitMilliseconds - releaseWait.ElapsedMilliseconds;
                    if (remaining <= 0) break;
                    Monitor.Wait(gate, (int)remaining);
                }
                if (!sessionReleased)
                {
                    string releaseIssue = releaseCompleted ? "SessionReleaseFailed" : "SessionReleasePending";
                    issue = issue == null ? releaseIssue : issue + ";" + releaseIssue;
                }
                var errors = new List<MssqlImportSqlError>();
                if (cleanupErrors != null) errors.AddRange(cleanupErrors);
                if (releaseErrors != null) errors.AddRange(releaseErrors);
                cleanup = new MssqlImportCleanup(active == null, sessionReleased, transactionConfirmed, issue, errors);
                cleanupRunning = false;
                return cleanup;
            }
        }

        public void Dispose() { ReleaseOwner(null); }

        private void ReleaseOwner(ActiveCommand requiredActive)
        {
            IDisposable value;
            lock (gate)
            {
                if (requiredActive != null && active != requiredActive || releaseStarted) return;
                // Latch release under the same gate as the generation check, before any later command can start.
                releaseStarted = true; value = owner; owner = null;
            }
            bool released = false;
            IReadOnlyList<MssqlImportSqlError> details = null;
            try { value.Dispose(); released = true; }
            catch (SqlException error)
            {
                var errors = new List<MssqlImportSqlError>();
                foreach (SqlError detail in error.Errors)
                    errors.Add(new MssqlImportSqlError(detail.Number, detail.State, detail.Class, detail.LineNumber, detail.Message));
                details = errors.AsReadOnly();
            }
            catch { /* Non-SQL release failures retain the fixed receipt code, never raw exception text. */ }
            finally { lock (gate) { sessionReleased = released; releaseErrors = details; releaseCompleted = true; Monitor.PulseAll(gate); } }
        }

        private bool ConnectionUsable()
        {
            lock (gate) { if (releaseStarted) return false; }
            try { return isOpen(); } catch { return false; }
        }

        private void EnsureNoActiveCommand()
        {
            lock (gate) { if (active != null || cleanupRunning) throw new InvalidOperationException("ImportCommandAlreadyActive"); }
        }

        private T WithReader<T>(string sql, int timeout, CancellationToken cancellation, Func<DbDataReader, T> action, bool cleaning = false)
        {
            cancellation.ThrowIfCancellationRequested();
            ActiveCommand owned;
            lock (gate)
            {
                if (active != null || cleanupRunning && !cleaning) throw new InvalidOperationException("ImportCommandAlreadyActive");
                if (releaseStarted) throw new ObjectDisposedException(nameof(MssqlImportSqlSession));
                DbCommand command = createCommand();
                try { command.CommandText = sql; command.CommandTimeout = timeout; owned = new ActiveCommand(command); active = owned; }
                catch { command.Dispose(); throw; }
            }
            DbDataReader reader = null;
            var errors = new List<MssqlImportSqlError>();
            Exception failure = null; bool uncertain = false;
            T result = default(T);
            try
            {
                owned.Registration = cancellation.Register(() => RequestCancellation(owned));
                reader = owned.Command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellation).GetAwaiter().GetResult();
                result = action(reader);
                cancellation.ThrowIfCancellationRequested();
            }
            catch (Exception error) { failure = error; AddErrors(error, errors, ref uncertain); }
            finally
            {
                try { if (reader != null) reader.Dispose(); }
                catch (Exception error) { if (failure == null) failure = error; AddErrors(error, errors, ref uncertain); }
                try { owned.Command.Dispose(); }
                catch (Exception error) { if (failure == null) failure = error; AddErrors(error, errors, ref uncertain); }
                try { owned.Registration.Dispose(); }
                finally
                {
                    lock (gate)
                    {
                        if (owned.Watchdog != null) owned.Watchdog.Dispose();
                        if (active == owned) active = null;
                    }
                }
            }
            if (failure != null)
            {
                if (errors.Count != 0 || !(failure is OperationCanceledException) || !cancellation.IsCancellationRequested)
                    throw new MssqlImportBatchException(errors, uncertain || failure is OperationCanceledException || cancellation.IsCancellationRequested || !ConnectionUsable());
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
            cancellation.ThrowIfCancellationRequested();
            return result;
        }

        private static void AddErrors(Exception error, List<MssqlImportSqlError> errors, ref bool uncertain)
        {
            var sql = error as SqlException;
            if (sql == null) { if (!(error is OperationCanceledException)) uncertain = true; return; }
            foreach (SqlError detail in sql.Errors)
            {
                errors.Add(new MssqlImportSqlError(detail.Number, detail.State, detail.Class, detail.LineNumber, detail.Message));
                if (detail.Number == -2 || detail.Number == 0 || detail.Number == 64 || detail.Number == 233 ||
                    detail.Number == 10053 || detail.Number == 10054 || detail.Number == 10060 || detail.Class >= 20) uncertain = true;
            }
        }

        private object Scalar(string sql, int timeout, CancellationToken cancellation)
        {
            object[] values = ReadValues(sql, timeout, cancellation);
            if (values == null || values.Length != 1) throw new InvalidOperationException("ProbeResultUnavailable");
            return values[0];
        }

        private object[] ReadValues(string sql, int timeout, CancellationToken cancellation, bool cleaning = false)
        {
            return WithReader(sql, timeout, cancellation, reader =>
            {
                object[] values = null;
                if (reader.ReadAsync(cancellation).GetAwaiter().GetResult())
                {
                    if (reader.FieldCount > 16) throw new InvalidOperationException("ProbeShapeInvalid");
                    values = new object[reader.FieldCount];
                    for (int i = 0; i < values.Length; i++) values[i] = reader.GetValue(i);
                    if (reader.ReadAsync(cancellation).GetAwaiter().GetResult()) throw new InvalidOperationException("ProbeRowCountInvalid");
                }
                if (reader.NextResultAsync(cancellation).GetAwaiter().GetResult()) throw new InvalidOperationException("ProbeResultCountInvalid");
                return values;
            }, cleaning);
        }

        private static void Drain(DbDataReader reader, CancellationToken cancellation)
        {
            do { while (reader.ReadAsync(cancellation).GetAwaiter().GetResult()) { cancellation.ThrowIfCancellationRequested(); } }
            while (reader.NextResultAsync(cancellation).GetAwaiter().GetResult());
        }

        private static bool Missing(object value) { return value == null || value == DBNull.Value; }
        private static bool? Permission(object value)
        {
            if (Missing(value)) return null;
            int permission = Convert.ToInt32(value, CultureInfo.InvariantCulture);
            return permission == 1 ? true : permission == 0 ? (bool?)false : null;
        }
        private static string Quote(string identifier) { return "[" + identifier.Replace("]", "]]") + "]"; }
    }
}
