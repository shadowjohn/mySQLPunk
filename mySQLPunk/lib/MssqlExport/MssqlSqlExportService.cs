using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Data.SqlClient;
using System.Text;
using System.Threading;

namespace mySQLPunk.lib.MssqlExport
{
    internal class MssqlSqlExportService
    {
        internal const string OrdinaryReadWarning = "一般讀取（READ COMMITTED）：匯出期間可能讀到不同時間的資料；來源持續寫入時，關聯資料可能不一致，還原可能因外鍵限制失敗。檔案驗證不等同還原成功。";
        private readonly IMssqlSnapshotRecoveryStore recoveryStore;
        private readonly Func<IMssqlExportSource> sourceFactory;
        private readonly MssqlArtifactFiles files;
        private readonly Action<MssqlExportStage> localPhase;
        internal MssqlSqlExportService(Func<IMssqlExportSource> sourceFactory) : this(sourceFactory, new MssqlArtifactFiles(), null) { }
        internal MssqlSqlExportService(Dictionary<string, object> connection) : this(connection, new MssqlSnapshotRecoveryStore()) { }
        internal MssqlSqlExportService(Dictionary<string, object> connection, IMssqlSnapshotRecoveryStore recoveryStore,
            Func<SqlConnection, IMssqlSnapshotBackend> backendFactory = null)
            : this(OwnedFactory(connection, recoveryStore, backendFactory), new MssqlArtifactFiles(), null, recoveryStore) { }
        // Existing file operations plus a local phase boundary are sufficient for deterministic faults.
        internal MssqlSqlExportService(Func<IMssqlExportSource> sourceFactory, MssqlArtifactFiles files, Action<MssqlExportStage> localPhase, IMssqlSnapshotRecoveryStore recoveryStore = null)
        { this.sourceFactory = sourceFactory ?? throw new ArgumentNullException(nameof(sourceFactory)); this.files = files ?? throw new ArgumentNullException(nameof(files)); this.localPhase = localPhase; this.recoveryStore = recoveryStore ?? new MssqlSnapshotRecoveryStore(); }
        private static Func<IMssqlExportSource> OwnedFactory(Dictionary<string, object> connection, IMssqlSnapshotRecoveryStore recoveryStore, Func<SqlConnection, IMssqlSnapshotBackend> backendFactory)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            var settings = new Dictionary<string, object>(connection);
            return () => new MssqlExportSource(ConnectionOpenService.OpenMssqlSession(new Dictionary<string, object>(settings)), recoveryStore, backendFactory);
        }
        internal virtual IReadOnlyList<MssqlSnapshotRecoveryNotice> LoadRecoveryNotices(MssqlSnapshotIdentity identity)
        {
            // Display candidates are broader than live-session cleanup authorization: recreated databases
            // and unknown current identity still need an honest historical notice, never automatic repair.
            return recoveryStore.ReadPending().Where(n => n.Record == null || identity == null || !identity.IsComplete ||
                string.Equals(identity.ServerName, n.Record.Identity.ServerName, StringComparison.Ordinal) &&
                string.Equals(identity.DatabaseName, n.Record.Identity.DatabaseName, StringComparison.Ordinal)).ToList().AsReadOnly();
        }
        internal virtual MssqlExportCatalog LoadCatalog(string databaseName, CancellationToken cancellation)
        {
            try { cancellation.ThrowIfCancellationRequested(); using (var source = sourceFactory()) return source.ReadCatalog(databaseName, cancellation); }
            catch (Exception ex) { throw SafeInspectionFailure("catalog", ex, cancellation); }
        }
        internal virtual MssqlExportPreflight Preflight(MssqlExportRequest request, CancellationToken cancellation)
        {
            try { cancellation.ThrowIfCancellationRequested(); using (var source = sourceFactory()) return source.Inspect(request, cancellation); }
            catch (Exception ex) { throw SafeInspectionFailure("preflight", ex, cancellation); }
        }
        private static Exception SafeInspectionFailure(string operation, Exception error, CancellationToken cancellation)
        {
            if (cancellation.IsCancellationRequested) return new OperationCanceledException(cancellation);
            var prerequisite = MssqlExportPrerequisiteException.From(error);
            if (prerequisite != null) return prerequisite;
            // No original exception as InnerException: even ToString must remain safe for a UI log.
            return new InvalidOperationException("Export " + operation + " failed (" + error.GetType().Name + ").");
        }
        internal virtual MssqlExportResult Export(MssqlExportRequest request, IProgress<MssqlExportProgress> progress, CancellationToken cancellation)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var clock = Stopwatch.StartNew(); var counts = new List<MssqlTableRowCount>();
            var warnings = new List<string>(); var residuals = new List<string>();
            MssqlExportArtifact artifact = null; MssqlExportMirror mirror = null;
            var estimator = new MssqlExportProgressEstimator(request.JobId, new MssqlExportSchema(null, null, null, 0, null), request.Mirror != null);
            var stage = MssqlExportStage.Preflight; bool succeeded = false; bool canceled = false; string error = null;
            MssqlSnapshotCleanupResult cleanup = null;
            Exception sourceError = null; bool sourceCanceled = false;
            Action<Exception> captureSourceFailure = ex => {
                if (sourceError != null) return;
                sourceError = ex; sourceCanceled = cancellation.IsCancellationRequested;
            };
            if (request.Mode != MssqlExportMode.SchemaOnly && !request.UseSnapshot) warnings.Add(OrdinaryReadWarning);
            long written = 0;
            Action<MssqlExportProgress> report = p => { if (p != null && progress != null) { try { progress.Report(p); } catch { /* Observer failure must not change publication truth. */ } } };
            Action<MssqlExportStage> phase = s => {
                stage = s; report(estimator.Sample(s, null, 0, 0, written, clock.Elapsed, true));
                localPhase?.Invoke(s); cancellation.ThrowIfCancellationRequested();
            };
            // Cleanup reports are deliberately independent of job cancellation and fallible local phase hooks.
            Action<MssqlExportStage> cleanupPhase = s => report(estimator.Sample(s, null, 0, 0, written, clock.Elapsed, true));
            try
            {
                report(estimator.Sample(stage, null, 0, 0, 0, clock.Elapsed, true));
                cancellation.ThrowIfCancellationRequested();
                var source = sourceFactory();
                try
                {
                    var preflight = source.Inspect(request, cancellation);
                    if (!preflight.CanStart) throw new InvalidOperationException("Export preflight did not pass.");
                    var schema = source.BeginRead(request, s => { stage = s; report(estimator.Sample(s, null, 0, 0, written, clock.Elapsed, true)); }, cancellation);
                    estimator = new MssqlExportProgressEstimator(request.JobId, schema, request.Mirror != null);
                    cancellation.ThrowIfCancellationRequested();
                    artifact = MssqlExportArtifact.Create(request, files);
                    // Wrapper releases before Complete, underlying artifact remains owned by this job.
                    using (var writer = new StreamWriter(artifact.Output, new UTF8Encoding(false, true), 8192, true))
                    {
                        var ddl = new MssqlSchemaScriptWriter();
                        var data = new MssqlDataScriptWriter(new MssqlLobScriptWriter(request.JobId));
                        stage = MssqlExportStage.Schema;
                        report(estimator.Sample(stage, null, 0, 0, artifact.Output.Position, clock.Elapsed, true));
                        ddl.WriteBeforeData(schema, request, writer, cancellation);
                        foreach (var table in schema.Tables)
                        {
                            long rows = 0, sourceBytes = 0;
                            stage = request.Mode == MssqlExportMode.SchemaOnly ? MssqlExportStage.Schema : MssqlExportStage.Data;
                            report(estimator.Sample(stage, table.Key, 0, 0, artifact.Output.Position, clock.Elapsed, true));
                            if (request.Mode != MssqlExportMode.SchemaOnly)
                            {
                                var reader = source.OpenRows(table, cancellation);
                                bool readFailed = false;
                                try
                                {
                                    rows = data.WriteTable(reader.Reader, table, writer, (r, b) => {
                                        rows = r; sourceBytes = b; written = artifact.Output.Position;
                                        report(estimator.Sample(stage, table.Key, r, b, written, clock.Elapsed, false));
                                    }, cancellation);
                                }
                                catch (Exception ex) { readFailed = true; captureSourceFailure(ex); throw; }
                                finally
                                {
                                    try { reader.Dispose(); }
                                    catch (Exception) when (readFailed) { /* FinishRead still sees DisposeSucceeded=false; keep the read failure. */ }
                                }
                            }
                            counts.Add(new MssqlTableRowCount(table.Key, rows));
                            written = artifact.Output.Position;
                            report(estimator.Sample(stage, table.Key, rows, sourceBytes, written, clock.Elapsed, true));
                        }
                        stage = MssqlExportStage.Schema;
                        report(estimator.Sample(stage, null, 0, 0, artifact.Output.Position, clock.Elapsed, true));
                        ddl.WriteAfterData(schema, request, writer, cancellation);
                        writer.Flush(); written = artifact.Output.Position;
                    }
                    source.CompleteRead(schema, cancellation);
                }
                catch (Exception ex) { captureSourceFailure(ex); throw; }
                finally
                {
                    try { cleanup = source.FinishRead(cleanupPhase); }
                    catch (Exception) { cleanup = new MssqlSnapshotCleanupResult(MssqlSnapshotCleanupState.Unconfirmed, null, null, "SourceCleanupUnconfirmed"); }
                    finally { try { source.Dispose(); } catch (Exception) { warnings.Add("SourceReleaseUnconfirmed"); } }
                }
                // No snapshot/reader/command/connection is alive during these local phases.
                var receipt = artifact.Complete(counts.AsReadOnly(), cancellation); written = receipt.Bytes;
                phase(MssqlExportStage.Verifying);
                artifact.Verify(receipt, (done, total) => report(estimator.SamplePhase(stage, done, total, written, clock.Elapsed, false)), cancellation);
                phase(MssqlExportStage.Publishing); // Includes cancelable integrity/consent preparation.
                artifact.Publish(request.Destination, cancellation); // Only the artifact's actual commit defers cancellation.
                if (request.Mirror != null)
                {
                    mirror = new MssqlExportMirror(files);
                    phase(MssqlExportStage.Mirroring);
                    long last = 0; bool verifyPass = false;
                    mirror.CopyVerified(artifact.PublishedPath, receipt, request.Mirror, (done, total) => {
                        // CopyVerified reports a copy pass followed by a verification pass (which restarts at zero).
                        if (!verifyPass && last == total && done <= last) verifyPass = true;
                        last = done;
                        report(estimator.SamplePhase(stage, (verifyPass ? total : 0) + done, checked(total * 2), written, clock.Elapsed, false));
                    }, cancellation);
                }
                succeeded = true;
            }
            catch (Exception ex)
            {
                // Cleanup can observe a later cancellation or throw another exception. The original source
                // outcome is immutable; cancellation after successful reading still stops local publication.
                var primary = sourceError ?? ex;
                canceled = sourceError != null ? sourceCanceled : cancellation.IsCancellationRequested;
                // SQL/provider messages can contain credentials or row values. Return only a safe category.
                var prerequisite = canceled ? null : MssqlExportPrerequisiteException.From(primary);
                error = canceled ? "Export canceled." : prerequisite != null ? "Export failed during " + stage + ". " + prerequisite.Message : "Export failed during " + stage + " (" + primary.GetType().Name + ").";
            }
            finally
            {
                if (artifact != null)
                {
                    written = artifact.Output.Position;
                    artifact.Dispose();
                    warnings.AddRange(artifact.Warnings); residuals.AddRange(artifact.ResidualPaths);
                }
                // Mirror owns its temporary lifetime inside CopyVerified's finally.
                if (mirror != null) { warnings.AddRange(mirror.Warnings); residuals.AddRange(mirror.ResidualPaths); }
            }
            if (cleanup != null)
            {
                if (cleanup.WarningCode != null) warnings.Add(cleanup.WarningCode);
                else if (cleanup.State == MssqlSnapshotCleanupState.Unconfirmed) warnings.Add("SnapshotRestoreUnconfirmed");
                if (cleanup.RecoveryRecordPath != null) residuals.Add(cleanup.RecoveryRecordPath);
            }
            string published = artifact?.PublishedPath, mirrorPath = mirror?.MirrorPublishedPath;
            if (succeeded) stage = MssqlExportStage.Completed;
            else if (published != null) stage = MssqlExportStage.LocalCompletedMirrorIncomplete;
            else stage = canceled ? MssqlExportStage.Canceled : MssqlExportStage.Failed;
            report(estimator.Sample(stage, null, 0, 0, written, clock.Elapsed, true));
            return new MssqlExportResult(request.JobId, stage, published, mirrorPath, counts, clock.Elapsed, warnings, residuals, error, request.UseSnapshot, cleanup);
        }
    }
}
