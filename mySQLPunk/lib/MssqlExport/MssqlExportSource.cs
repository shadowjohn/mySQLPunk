using System;
using System.Data;
using System.Data.Common;
using System.Data.SqlClient;
using System.Linq;
using System.Threading;

namespace mySQLPunk.lib.MssqlExport
{
    internal interface IMssqlExportSource : IDisposable
    {
        MssqlExportCatalog ReadCatalog(string databaseName, CancellationToken cancellation);
        MssqlExportPreflight Inspect(MssqlExportRequest request, CancellationToken cancellation);
        MssqlExportSchema BeginRead(MssqlExportRequest request, Action<MssqlExportStage> report, CancellationToken cancellation);
        MssqlSnapshotCleanupResult FinishRead(Action<MssqlExportStage> report);
        MssqlRowReader OpenRows(MssqlTableDefinition table, CancellationToken cancellation);
        void CompleteRead(MssqlExportSchema schema, CancellationToken cancellation);
    }
    internal sealed class MssqlRowReader : IDisposable
    {
        private IDisposable commandOwner;
        public DbDataReader Reader { get; }
        internal bool IsDisposed { get; private set; }
        internal bool DisposeSucceeded { get; private set; }
        internal MssqlRowReader(DbDataReader reader, IDisposable commandOwner)
        { Reader = reader ?? throw new ArgumentNullException(nameof(reader)); this.commandOwner = commandOwner ?? throw new ArgumentNullException(nameof(commandOwner)); }
        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            try { Reader.Dispose(); } finally { var owner = commandOwner; commandOwner = null; owner.Dispose(); }
            DisposeSucceeded = true;
        }
    }
    internal sealed class MssqlExportSource : IMssqlExportSource
    {
        private readonly MssqlOwnedSession session;
        private readonly MssqlExportMetadataReader metadata = new MssqlExportMetadataReader();
        private SqlTransaction transaction;
        private MssqlExportRequest request;
        private MssqlExportSchema frozen;
        private MssqlRowReader activeReader;
        private bool disposed, ownerClosed;
        private readonly Action<SqlConnection> evictPool;
        private readonly IMssqlSnapshotRecoveryStore recoveryStore;
        private readonly Func<SqlConnection, IMssqlSnapshotBackend> backendFactory;
        private readonly Func<int, CancellationToken, IMssqlSnapshotDeadline> deadlineFactory;
        private SourceSnapshotBackend snapshotBackend;
        private MssqlSnapshotLifecycle lifecycle;
        private MssqlSnapshotCleanupResult cleanup;
        internal MssqlExportSource(MssqlOwnedSession session, IMssqlSnapshotRecoveryStore recoveryStore = null,
            Func<SqlConnection, IMssqlSnapshotBackend> backendFactory = null,
            Func<int, CancellationToken, IMssqlSnapshotDeadline> deadlineFactory = null, Action<SqlConnection> evictPool = null)
        {
            this.session = session ?? throw new ArgumentNullException(nameof(session));
            this.recoveryStore = recoveryStore ?? new MssqlSnapshotRecoveryStore();
            this.backendFactory = backendFactory ?? (c => new MssqlSnapshotBackend(c));
            this.deadlineFactory = deadlineFactory;
            this.evictPool = evictPool ?? SqlConnection.ClearPool;
        }
        internal static IsolationLevel? ReadIsolation(MssqlExportRequest value) => value.Mode == MssqlExportMode.SchemaOnly
            ? (IsolationLevel?)null : value.UseSnapshot ? IsolationLevel.Snapshot : IsolationLevel.ReadCommitted;
        public MssqlExportCatalog ReadCatalog(string databaseName, CancellationToken cancellation)
        { CheckIdle(); return metadata.ReadCatalog(session.Connection, databaseName, cancellation); }
        public MssqlExportPreflight Inspect(MssqlExportRequest value, CancellationToken cancellation)
        {
            CheckIdle();
            cancellation.ThrowIfCancellationRequested(); session.Connection.ChangeDatabase(value.DatabaseName);
            var schema = metadata.ReadSchema(session.Connection, null, value, cancellation);
            return new MssqlExportPreflight(schema, metadata.ValidateStart(schema, value));
        }
        public MssqlExportSchema BeginRead(MssqlExportRequest value, Action<MssqlExportStage> report, CancellationToken cancellation)
        {
            CheckIdle();
            var preflight = Inspect(value, cancellation);
            RequireValid(preflight);
            request = value;
            cancellation.ThrowIfCancellationRequested();
            if (value.UseSnapshot)
            {
                snapshotBackend = new SourceSnapshotBackend(backendFactory(session.Connection));
                lifecycle = new MssqlSnapshotLifecycle(snapshotBackend, recoveryStore, value.JobId, deadlineFactory);
                lifecycle.Prepare(report, cancellation);
                // Prepare confirms ON within its budget; recheck immediately before beginning the data transaction.
                if (snapshotBackend.ReadCapability(cancellation).State != 1) throw new InvalidOperationException("SnapshotStateUnconfirmed");
            }
            var isolation = ReadIsolation(value);
            if (isolation.HasValue) transaction = session.Connection.BeginTransaction(isolation.Value);
            frozen = metadata.ReadSchema(session.Connection, transaction, value, cancellation);
            RequireValid(new MssqlExportPreflight(frozen, metadata.Validate(frozen, value)));
            return frozen;
        }
        public MssqlRowReader OpenRows(MssqlTableDefinition table, CancellationToken cancellation)
        {
            CheckAlive();
            if (frozen == null || transaction == null) throw new InvalidOperationException("A data transaction must be started first.");
            if (activeReader != null && !activeReader.IsDisposed) throw new InvalidOperationException("Dispose the current row reader first.");
            if (!frozen.Tables.Any(t => ReferenceEquals(t, table))) throw new ArgumentException("Table must belong to this frozen schema.", nameof(table));
            cancellation.ThrowIfCancellationRequested();
            var columns = table.Columns.Where(c => !c.Computed && !c.RowVersion).OrderBy(c => c.Ordinal).ToList();
            // A rowversion/computed-only table still needs a row count for DEFAULT VALUES.
            string projection = columns.Count == 0 ? "CAST(1 AS bit) AS [__mysqlpunk_row]" : string.Join(",", columns.Select(MssqlValueCodec.SourceExpression));
            var command = new SqlCommand("SELECT " + projection + " FROM " + MssqlExportMetadataReader.Qualified(table.Key), session.Connection, transaction) { CommandTimeout = 0 };
            var owner = new MssqlCommandOwner(command, cancellation);
            try { activeReader = new MssqlRowReader(owner.ExecuteReader(CommandBehavior.SequentialAccess), owner); return activeReader; }
            catch { owner.Dispose(); throw; }
        }
        public void CompleteRead(MssqlExportSchema schema, CancellationToken cancellation)
        {
            CheckAlive();
            if (frozen == null || !ReferenceEquals(schema, frozen)) throw new InvalidOperationException("Schema does not belong to this read.");
            if (activeReader != null && !activeReader.IsDisposed) throw new InvalidOperationException("Close the row reader before completing the read.");
            cancellation.ThrowIfCancellationRequested();
            var current = metadata.ReadSchema(session.Connection, transaction, request, cancellation);
            RequireValid(new MssqlExportPreflight(current, metadata.Validate(current, request)));
            if (current.Fingerprint != frozen.Fingerprint) throw new InvalidOperationException("Source definitions changed during export; discard this export.");
            cancellation.ThrowIfCancellationRequested();
            if (transaction != null) { transaction.Commit(); transaction.Dispose(); transaction = null; }
        }
        private static void RequireValid(MssqlExportPreflight preflight)
        { if (!preflight.CanStart) throw new InvalidOperationException(string.Join(Environment.NewLine, preflight.Issues.Select(x => x.Code + ": " + x.Message))); }
        private void CheckAlive() { if (disposed) throw new ObjectDisposedException(nameof(MssqlExportSource)); }
        private void CheckIdle() { CheckAlive(); if (request != null) throw new InvalidOperationException("A read is already active."); }
        public MssqlSnapshotCleanupResult FinishRead(Action<MssqlExportStage> report)
        {
            if (cleanup != null) return cleanup;
            bool ended = CloseReadResources();
            bool ownerReleased = true;
            if (!ended)
            {
                // A reader/transaction with uncertain closure must never be followed by ALTER.
                if (snapshotBackend != null) snapshotBackend.Available = false;
                ownerReleased = CloseOwner(true);
            }
            try { cleanup = lifecycle == null ? new MssqlSnapshotCleanupResult(MssqlSnapshotCleanupState.NotRequired, null, null, ended ? null : "SourceCleanupUnconfirmed") : lifecycle.Restore(report); }
            catch (Exception) { cleanup = new MssqlSnapshotCleanupResult(MssqlSnapshotCleanupState.Unconfirmed, null, null, "SourceCleanupUnconfirmed"); }
            if (!ended && request != null && request.UseSnapshot)
                cleanup = new MssqlSnapshotCleanupResult(MssqlSnapshotCleanupState.Unconfirmed, cleanup.ObservedState, cleanup.RecoveryRecordPath, cleanup.WarningCode ?? "SourceCleanupUnconfirmed");
            if (!CloseOwner(snapshotBackend != null && snapshotBackend.LockUnconfirmed) || !ownerReleased)
                cleanup = new MssqlSnapshotCleanupResult(cleanup.State, cleanup.ObservedState, cleanup.RecoveryRecordPath, cleanup.WarningCode ?? "SourceReleaseUnconfirmed");
            disposed = true;
            return cleanup;
        }
        private bool CloseReadResources()
        {
            bool ended = true;
            try { activeReader?.Dispose(); } catch (Exception) { ended = false; }
            if (activeReader != null && !activeReader.DisposeSucceeded) ended = false;
            var value = transaction; transaction = null;
            if (value != null)
            {
                try { value.Rollback(); } catch (Exception) { ended = false; }
                try { value.Dispose(); } catch (Exception) { ended = false; }
            }
            return ended;
        }
        private bool CloseOwner(bool evict)
        {
            if (ownerClosed) return true;
            ownerClosed = true;
            bool closed = true;
            // ClearPool marks active connections for discard on close; it does not terminate other sessions.
            if (evict) { try { evictPool(session.Connection); } catch (Exception) { closed = false; } }
            try { session.Dispose(); } catch (Exception) { closed = false; }
            return closed;
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (snapshotBackend != null) snapshotBackend.Available = false;
            // Last-resort resource release only. Settings restoration belongs exclusively to FinishRead.
            bool ended = CloseReadResources();
            CloseOwner(!ended || snapshotBackend != null && snapshotBackend.LockUnconfirmed);
        }
        // Prevent lifecycle SQL after uncertain transaction closure and preserve the deadline through test adapters.
        internal sealed class SourceSnapshotBackend : IMssqlSnapshotBackend, IMssqlSnapshotDeadlineBackend
        {
            private readonly IMssqlSnapshotBackend inner;
            internal bool Available = true;
            internal bool LockUnconfirmed;
            internal SourceSnapshotBackend(IMssqlSnapshotBackend inner) { this.inner = inner ?? throw new ArgumentNullException(nameof(inner)); }
            public bool IsUsable => Available && inner.IsUsable;
            public IMssqlSnapshotDeadline Deadline { set { var bounded = inner as IMssqlSnapshotDeadlineBackend; if (bounded != null) bounded.Deadline = value; } }
            public MssqlSnapshotCapability ReadCapability(CancellationToken token) { if (!IsUsable) throw new InvalidOperationException("SnapshotSessionUnavailable"); return inner.ReadCapability(token); }
            public bool TryAcquireLock(CancellationToken token) { LockUnconfirmed = true; bool acquired = inner.TryAcquireLock(token); LockUnconfirmed = acquired; return acquired; }
            public void SetEnabled(bool enabled, CancellationToken token) { if (!IsUsable) throw new InvalidOperationException("SnapshotSessionUnavailable"); inner.SetEnabled(enabled, token); }
            public void ReleaseLock(CancellationToken token) { if (!IsUsable) throw new InvalidOperationException("SnapshotSessionUnavailable"); inner.ReleaseLock(token); LockUnconfirmed = false; }
        }
    }

    internal sealed class MssqlCommandOwner : IDisposable
    {
        private DbCommand command;
        private CancellationTokenRegistration registration;
        private readonly CancellationToken cancellation;
        internal MssqlCommandOwner(DbCommand command, CancellationToken cancellation)
        {
            this.command = command;
            this.cancellation = cancellation;
            registration = cancellation.Register(() => { try { command.Cancel(); } catch (ObjectDisposedException) { } });
        }
        internal DbDataReader ExecuteReader(CommandBehavior behavior)
        {
            // The provider must observe cancellation during startup, not just receive
            // an idle Cancel() before synchronous execution starts. Keep our separate
            // registration alive for subsequent synchronous reads of the returned reader.
            return command.ExecuteReaderAsync(behavior, cancellation).GetAwaiter().GetResult();
        }
        public void Dispose()
        {
            var value = command;
            if (value == null) return;
            command = null;
            try { registration.Dispose(); } finally { value.Dispose(); }
        }
    }
}
