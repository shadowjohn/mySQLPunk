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
        MssqlExportSchema BeginRead(MssqlExportRequest request, CancellationToken cancellation);
        MssqlRowReader OpenRows(MssqlTableDefinition table, CancellationToken cancellation);
        void CompleteRead(MssqlExportSchema schema, CancellationToken cancellation);
    }
    internal sealed class MssqlRowReader : IDisposable
    {
        private IDisposable commandOwner;
        public DbDataReader Reader { get; }
        internal bool IsDisposed { get; private set; }
        internal MssqlRowReader(DbDataReader reader, IDisposable commandOwner)
        { Reader = reader ?? throw new ArgumentNullException(nameof(reader)); this.commandOwner = commandOwner ?? throw new ArgumentNullException(nameof(commandOwner)); }
        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            try { Reader.Dispose(); } finally { var owner = commandOwner; commandOwner = null; owner.Dispose(); }
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
        private bool disposed;
        internal MssqlExportSource(MssqlOwnedSession session) { this.session = session ?? throw new ArgumentNullException(nameof(session)); }
        public MssqlExportCatalog ReadCatalog(string databaseName, CancellationToken cancellation)
        { CheckIdle(); try { return metadata.ReadCatalog(session.Connection, databaseName, cancellation); } catch { Dispose(); throw; } }
        public MssqlExportPreflight Inspect(MssqlExportRequest value, CancellationToken cancellation)
        {
            CheckIdle();
            try { cancellation.ThrowIfCancellationRequested(); session.Connection.ChangeDatabase(value.DatabaseName); var schema = metadata.ReadSchema(session.Connection, null, value, cancellation); return new MssqlExportPreflight(schema, metadata.Validate(schema, value)); }
            catch { Dispose(); throw; }
        }
        public MssqlExportSchema BeginRead(MssqlExportRequest value, CancellationToken cancellation)
        {
            CheckIdle();
            try
            {
                var preflight = Inspect(value, cancellation);
                RequireValid(preflight);
                request = value;
                cancellation.ThrowIfCancellationRequested();
                if (value.Mode != MssqlExportMode.SchemaOnly) transaction = session.Connection.BeginTransaction(IsolationLevel.Snapshot);
                frozen = metadata.ReadSchema(session.Connection, transaction, value, cancellation);
                RequireValid(new MssqlExportPreflight(frozen, metadata.Validate(frozen, value)));
                return frozen;
            }
            catch { Dispose(); throw; }
        }
        public MssqlRowReader OpenRows(MssqlTableDefinition table, CancellationToken cancellation)
        {
            CheckAlive();
            try
            {
                if (frozen == null || transaction == null) throw new InvalidOperationException("A data snapshot must be started first.");
                if (activeReader != null && !activeReader.IsDisposed) throw new InvalidOperationException("Dispose the current row reader first.");
                if (!frozen.Tables.Any(t => ReferenceEquals(t, table))) throw new ArgumentException("Table must belong to this frozen schema.", nameof(table));
                cancellation.ThrowIfCancellationRequested();
                var columns = table.Columns.Where(c => !c.Computed && !c.RowVersion).OrderBy(c => c.Ordinal).ToList();
                // A rowversion/computed-only table still needs a row count for DEFAULT VALUES.
                string projection = columns.Count == 0 ? "CAST(1 AS bit) AS [__mysqlpunk_row]" : string.Join(",", columns.Select(c => MssqlExportMetadataReader.Quote(c.Name)));
                var command = new SqlCommand("SELECT " + projection + " FROM " + MssqlExportMetadataReader.Qualified(table.Key), session.Connection, transaction) { CommandTimeout = 0 };
                var owner = new MssqlCommandOwner(command, cancellation);
                try { activeReader = new MssqlRowReader(command.ExecuteReader(CommandBehavior.SequentialAccess), owner); return activeReader; }
                catch { owner.Dispose(); throw; }
            }
            catch { Dispose(); throw; }
        }
        public void CompleteRead(MssqlExportSchema schema, CancellationToken cancellation)
        {
            CheckAlive();
            try
            {
                if (frozen == null || !ReferenceEquals(schema, frozen)) throw new InvalidOperationException("Schema does not belong to this read.");
                if (activeReader != null && !activeReader.IsDisposed) throw new InvalidOperationException("Close the row reader before completing the read.");
                cancellation.ThrowIfCancellationRequested();
                var current = metadata.ReadSchema(session.Connection, transaction, request, cancellation);
                RequireValid(new MssqlExportPreflight(current, metadata.Validate(current, request)));
                if (current.Fingerprint != frozen.Fingerprint) throw new InvalidOperationException("Source definitions changed during export; discard this export.");
                cancellation.ThrowIfCancellationRequested();
                if (transaction != null) { transaction.Commit(); transaction.Dispose(); transaction = null; }
            }
            finally { Dispose(); }
        }
        private static void RequireValid(MssqlExportPreflight preflight)
        { if (!preflight.CanStart) throw new InvalidOperationException(string.Join(Environment.NewLine, preflight.Issues.Select(x => x.Code + ": " + x.Message))); }
        private void CheckAlive() { if (disposed) throw new ObjectDisposedException(nameof(MssqlExportSource)); }
        private void CheckIdle() { CheckAlive(); if (request != null) throw new InvalidOperationException("A read is already active."); }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            try { if (activeReader != null) activeReader.Dispose(); }
            finally
            {
                try { if (transaction != null) { try { transaction.Rollback(); } catch (InvalidOperationException) { } finally { transaction.Dispose(); transaction = null; } } }
                finally { session.Dispose(); }
            }
        }
    }
    internal sealed class MssqlCommandOwner : IDisposable
    {
        private DbCommand command;
        private CancellationTokenRegistration registration;
        internal MssqlCommandOwner(DbCommand command, CancellationToken cancellation)
        {
            this.command = command;
            registration = cancellation.Register(() => { try { command.Cancel(); } catch (ObjectDisposedException) { } });
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
