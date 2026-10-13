using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace mySQLPunk.lib.MssqlImport
{
    internal sealed class MssqlImportIssue
    {
        public string Code { get; }
        public string Message { get; }
        public bool IsWarning { get; }
        public long BatchNumber { get; }
        public long Line { get; }
        public long ByteOffset { get; }
        internal MssqlImportIssue(string code, bool warning = false, long batch = 0, long line = 0, long offset = 0)
        { Code = code; Message = code; IsWarning = warning; BatchNumber = batch; Line = line; ByteOffset = offset; }
    }

    // Resource ownership transfers exactly once; a stale wizard Dispose cannot close a running input/log.
    internal sealed class MssqlImportPreflight : IDisposable
    {
        private readonly object gate = new object();
        private readonly MssqlImportService service;
        private readonly MssqlImportRequest request;
        private readonly string source;
        private MssqlImportInput input;
        private IMssqlImportLog log;
        private bool claimed, disposed;
        public bool Passed { get; }
        public MssqlImportScan Scan { get; }
        public MssqlImportTarget Target { get; }
        public string LogPath { get; }
        public IReadOnlyList<MssqlImportIssue> Issues { get; }
        internal MssqlImportPreflight(MssqlImportService service, MssqlImportRequest request, string source, MssqlImportInput input,
            MssqlImportScan scan, MssqlImportTarget target, IMssqlImportLog log, IEnumerable<MssqlImportIssue> issues)
        {
            this.service = service; this.request = request; this.source = source; this.input = input; this.log = log;
            Scan = scan; Target = target; LogPath = log == null ? null : log.Path;
            Issues = Array.AsReadOnly(issues.ToArray()); Passed = !Issues.Any(i => !i.IsWarning);
        }
        internal bool Claim(MssqlImportService caller, MssqlImportRequest current, out MssqlImportInput ownedInput, out IMssqlImportLog ownedLog)
        {
            ownedInput = null; ownedLog = null;
            lock (gate)
            {
                if (disposed || claimed || !Passed || caller != service || current == null || request == null ||
                    current.JobId != request.JobId || current.SkipSqlErrors != request.SkipSqlErrors ||
                    !string.Equals(current.DatabaseName, request.DatabaseName, StringComparison.Ordinal)) return false;
                string path; try { path = Path.GetFullPath(current.SourcePath); } catch { return false; }
                if (!string.Equals(path, source, StringComparison.OrdinalIgnoreCase)) return false;
                claimed = true; ownedInput = input; ownedLog = log; input = null; log = null; return true;
            }
        }
        public void Dispose()
        {
            MssqlImportInput ownedInput; IMssqlImportLog ownedLog;
            lock (gate) { if (disposed) return; disposed = true; ownedInput = input; ownedLog = log; input = null; log = null; }
            try { if (ownedInput != null) ownedInput.Dispose(); } finally { if (ownedLog != null) ownedLog.Dispose(); }
        }
    }

    internal sealed class MssqlImportService
    {
        private readonly Func<IMssqlImportSession> openSession;
        private readonly Func<Guid, IMssqlImportLog> openLog;
        internal MssqlImportService(Func<IMssqlImportSession> openSession, Func<Guid, IMssqlImportLog> openLog)
        { this.openSession = openSession ?? throw new ArgumentNullException(nameof(openSession)); this.openLog = openLog ?? throw new ArgumentNullException(nameof(openLog)); }

        internal MssqlImportPreflight Preflight(MssqlImportRequest request, Action<MssqlImportProgress> progress, CancellationToken cancellation)
        {
            var issues = new List<MssqlImportIssue>();
            MssqlImportInput input = null; MssqlImportScan scan = null; MssqlImportTarget target = null; IMssqlImportLog log = null;
            string source = null;
            try
            {
                if (request == null || string.IsNullOrWhiteSpace(request.SourcePath)) throw new InvalidOperationException("SourceRequired");
                if (string.IsNullOrWhiteSpace(request.DatabaseName)) throw new InvalidOperationException("TargetRequired");
                cancellation.ThrowIfCancellationRequested(); source = Path.GetFullPath(request.SourcePath);
                input = MssqlImportInput.Open(source, request.DatabaseName);
                var clock = Stopwatch.StartNew(); var estimator = new MssqlImportProgressEstimator();
                scan = input.Scan(cancellation, bytes => {
                    var p = new MssqlImportProgress(request.JobId, 0, MssqlImportStage.Scanning, scannedBytes: bytes, elapsed: clock.Elapsed);
                    if (estimator.ShouldReport(p, DateTime.UtcNow)) Notify(progress, p);
                });
                cancellation.ThrowIfCancellationRequested();
                using (var session = openSession()) target = session.ReadTarget(cancellation);
                string gate = TargetGate(request, scan, target);
                if (gate != null) throw new InvalidOperationException(gate);
                if (scan.Script.Kind == MssqlImportScriptKind.Generic) issues.Add(new MssqlImportIssue("SessionIntrinsicsMayChange", true));
                if (scan.Script.Kind != MssqlImportScriptKind.NativeSchema && (!target.MetadataVisible || target.UserObjectCount != 0)) issues.Add(new MssqlImportIssue("TargetNotProvenEmpty", true));
                if (target.CanCreateView != true) issues.Add(new MssqlImportIssue("CreateViewNotProven", true));
                if (scan.Script.Kind != MssqlImportScriptKind.NativeSchema && target.CanCreateTable != true) issues.Add(new MssqlImportIssue("CreateTableNotProven", true));
                issues.Add(new MssqlImportIssue("PartialBatchWritesMayRemain", true));
                log = openLog(request.JobId); log.WriteStart(request, scan, target);
                cancellation.ThrowIfCancellationRequested();
            }
            catch (Exception error)
            {
                var parser = error as MssqlImportInputException;
                string code = parser != null ? parser.Code : cancellation.IsCancellationRequested ? "Cancelled" : SafeCode(error, "PreflightFailed");
                issues.Add(new MssqlImportIssue(code, false, parser == null ? 0 : parser.BatchNumber, parser == null ? 0 : parser.Line, parser == null ? 0 : parser.ByteOffset));
                try { if (input != null) input.Dispose(); } catch { issues.Add(new MssqlImportIssue("InputReleaseFailed")); }
                try { if (log != null) log.Dispose(); } catch { issues.Add(new MssqlImportIssue("LogIncomplete")); }
                input = null; log = null;
            }
            return new MssqlImportPreflight(this, request, source, input, scan, target, log, issues);
        }

        internal MssqlImportResult Run(MssqlImportRequest request, MssqlImportPreflight preflight, Action<MssqlImportProgress> progress, CancellationToken cancellation)
        {
            MssqlImportInput input; IMssqlImportLog log;
            if (preflight == null || !preflight.Claim(this, request, out input, out log))
                return new MssqlImportResult(request == null ? Guid.Empty : request.JobId, MssqlImportStatus.Failed,
                    preflight == null || preflight.Scan == null ? 0 : preflight.Scan.FileBytes, validationSummary: "PreflightInvalidOrConsumed");
            return new Worker(this, request, preflight, input, log, progress, cancellation).Run();
        }

        private static string TargetGate(MssqlImportRequest request, MssqlImportScan scan, MssqlImportTarget target)
        {
            if (target == null || string.IsNullOrWhiteSpace(target.ServerName) || string.IsNullOrWhiteSpace(target.Version) ||
                target.DatabaseId <= 0 || target.DatabaseCreateDate == default(DateTime) || !string.Equals(target.DatabaseName, request.DatabaseName, StringComparison.Ordinal)) return "TargetIdentityUnavailable";
            if (!target.Writable) return "TargetReadOnly";
            if (scan.Script.Kind == MssqlImportScriptKind.NativeSchema)
            {
                if (!target.MetadataVisible || !target.UserObjectCount.HasValue) return "TargetVisibilityRequired";
                if (target.UserObjectCount != 0) return "TargetNotEmpty";
                if (scan.Script.ExpectedTables > 0 && target.CanCreateTable != true) return "CreateTableRequired";
            }
            return null;
        }
        private static bool SameTarget(MssqlImportTarget a, MssqlImportTarget b)
        { return a != null && b != null && a.DatabaseId == b.DatabaseId && a.DatabaseCreateDate == b.DatabaseCreateDate && string.Equals(a.ServerName, b.ServerName, StringComparison.Ordinal) && string.Equals(a.DatabaseName, b.DatabaseName, StringComparison.Ordinal); }
        private static void Notify(Action<MssqlImportProgress> action, MssqlImportProgress progress)
        { if (action != null) { try { action(progress); } catch { /* An observer cannot change import truth. */ } } }
        private static string SafeCode(Exception error, string fallback)
        {
            // Only service-owned fixed codes may cross the UI boundary; provider/IO messages may contain secrets.
            if (error is InvalidOperationException)
                switch (error.Message)
                {
                    case "SourceRequired": case "TargetRequired": case "TargetIdentityUnavailable": case "TargetReadOnly":
                    case "TargetVisibilityRequired": case "TargetNotEmpty": case "CreateTableRequired":
                    case "TargetChanged": case "SessionStateInvalid": case "LobStateInvalid": case "LobInitializationMissing":
                    case "EofTransactionOpen": case "DigestMismatch": case "SummaryMismatch": return error.Message;
                }
            return fallback;
        }

        private sealed class Worker
        {
            private readonly MssqlImportService service;
            private readonly MssqlImportRequest request;
            private readonly MssqlImportPreflight preflight;
            private readonly MssqlImportInput input;
            private readonly IMssqlImportLog log;
            private readonly Action<MssqlImportProgress> observer;
            private readonly CancellationToken cancellation;
            private readonly Stopwatch clock = Stopwatch.StartNew();
            private readonly MssqlImportProgressEstimator estimator = new MssqlImportProgressEstimator();
            private readonly object reportGate = new object();
            private MssqlImportProgress latest;
            private IMssqlImportSession session;
            private MssqlImportBatch batch;
            private MssqlImportPhase phase;
            private long successfulBytes, skippedBytes, successes, failures, acknowledged, boundary;
            private long? uncertain;
            private bool lobInitialized, unacknowledgedExecution, finished, logIncomplete;
            private MssqlImportCleanup cleanup;
            private MssqlImportStatus status = MssqlImportStatus.Failed;
            private string summary = "ExecutionNotStarted";
            internal Worker(MssqlImportService service, MssqlImportRequest request, MssqlImportPreflight preflight, MssqlImportInput input,
                IMssqlImportLog log, Action<MssqlImportProgress> observer, CancellationToken cancellation)
            { this.service = service; this.request = request; this.preflight = preflight; this.input = input; this.log = log; this.observer = observer; this.cancellation = cancellation; }

            internal MssqlImportResult Run()
            {
                Publish(MssqlImportStage.Executing, true);
                using (cancellation.Register(Stopping))
                {
                    try
                    {
                        cancellation.ThrowIfCancellationRequested(); session = service.openSession();
                        var target = session.ReadTarget(cancellation);
                        if (!SameTarget(preflight.Target, target)) throw new InvalidOperationException("TargetChanged");
                        string targetGate = TargetGate(request, preflight.Scan, target);
                        if (targetGate != null) throw new InvalidOperationException(targetGate);
                        var inspector = new MssqlImportScriptInspector(request.DatabaseName);
                        foreach (var next in input.ReadBatches(cancellation))
                        {
                            cancellation.ThrowIfCancellationRequested(); batch = next; inspector.Observe(batch);
                            var previousPhase = phase;
                            phase = preflight.Scan.Script.Kind == MssqlImportScriptKind.Generic ? MssqlImportPhase.None : inspector.Phase;
                            Publish(MssqlImportStage.Executing, phase != previousPhase);
                            cancellation.ThrowIfCancellationRequested();
                            var before = session.ReadState(cancellation); RequireState(before);
                            if (inspector.UsesNativeLob && !lobInitialized && !inspector.InitializesNativeLob) throw new InvalidOperationException("LobInitializationMissing");
                            if (inspector.InitializesNativeLob) lobInitialized = true;
                            try
                            {
                                cancellation.ThrowIfCancellationRequested(); unacknowledgedExecution = true;
                                session.ExecuteBatch(batch, cancellation);
                            }
                            catch (MssqlImportBatchException error)
                            {
                                unacknowledgedExecution = false;
                                failures = checked(failures + 1); if (error.OutcomeUncertain) uncertain = batch.Number;
                                HandleSqlError(error, before, inspector.UsesNativeLob);
                                continue;
                            }
                            cancellation.ThrowIfCancellationRequested();
                            var after = session.ReadState(cancellation); RequireState(after);
                            if (lobInitialized) RequireLob(false);
                            successfulBytes = checked(successfulBytes + batch.EndByte - boundary); boundary = batch.EndByte;
                            successes = checked(successes + 1); acknowledged = batch.Number; unacknowledgedExecution = false;
                            Publish(MssqlImportStage.Executing);
                        }
                        batch = null; phase = MssqlImportPhase.None; Publish(MssqlImportStage.Verifying, true);
                        cancellation.ThrowIfCancellationRequested();
                        if (input.ExecutionSha256 == null || !string.Equals(input.ExecutionSha256, preflight.Scan.Sha256, StringComparison.Ordinal)) throw new InvalidOperationException("DigestMismatch");
                        var finalState = session.ReadState(cancellation); RequireState(finalState);
                        if (finalState.TransactionCount != 0 || finalState.XactState != 0) throw new InvalidOperationException("EofTransactionOpen");
                        if (!SameTarget(preflight.Target, session.ReadTarget(cancellation))) throw new InvalidOperationException("TargetChanged");
                        if (lobInitialized) RequireLob(true);
                        summary = failures == 0 ? "AllBatchesExecuted;NoDataContentValidation" : "AllBatchesProcessedWithErrors;FullRestoreNotValidated";
                        if (failures == 0 && preflight.Scan.Script.Kind == MssqlImportScriptKind.NativeSchema)
                        {
                            var counts = session.CountTargetRows((tables, rows) => { Publish(MssqlImportStage.Verifying); cancellation.ThrowIfCancellationRequested(); }, cancellation);
                            if (counts.Tables != preflight.Scan.Script.ExpectedTables || counts.Rows != preflight.Scan.Script.ExpectedRows) throw new InvalidOperationException("SummaryMismatch");
                            summary = "NativeSummaryCountsMatched;NotColumnContentValidation";
                        }
                        cancellation.ThrowIfCancellationRequested();
                        successfulBytes = checked(successfulBytes + preflight.Scan.FileBytes - boundary); boundary = preflight.Scan.FileBytes;
                        status = failures == 0 ? MssqlImportStatus.Completed : MssqlImportStatus.CompletedWithErrors;
                    }
                    catch (OperationCanceledException)
                    {
                        status = cancellation.IsCancellationRequested ? MssqlImportStatus.Cancelled : MssqlImportStatus.Failed;
                        summary = cancellation.IsCancellationRequested ? "Cancelled;PreviouslyCommittedWritesMayRemain" : "ExecutionOutcomeUnknown";
                        if (batch != null && unacknowledgedExecution) uncertain = batch.Number;
                    }
                    catch (Exception error)
                    {
                        status = MssqlImportStatus.Failed;
                        if (!summary.StartsWith("SqlBatchFailed", StringComparison.Ordinal) && !logIncomplete) summary = SafeCode(error, "ImportFailed");
                        if (batch != null && unacknowledgedExecution) uncertain = batch.Number;
                        var sql = error as MssqlImportBatchException;
                        if (sql != null)
                        {
                            try { log.WriteError(new MssqlImportBatch(0, 0, 0, 0, 0, string.Empty), sql, "Stop;Origin=Probe;DuringBatch=" + (batch == null ? "0" : batch.Number.ToString(CultureInfo.InvariantCulture))); log.Flush(); }
                            catch { MarkLogIncomplete(); }
                            if (cancellation.IsCancellationRequested && !logIncomplete)
                            { status = MssqlImportStatus.Cancelled; summary = "Cancelled;PreviouslyCommittedWritesMayRemain"; }
                        }
                    }
                    finally
                    {
                        try
                        {
                            if (session != null) cleanup = session.Cleanup() ?? new MssqlImportCleanup(false, false, false, "CleanupReceiptMissing");
                            else cleanup = new MssqlImportCleanup(true, true, true, null);
                        }
                        catch { cleanup = new MssqlImportCleanup(false, false, false, "CleanupFailed"); }
                        try { if (session != null) session.Dispose(); }
                        catch { cleanup = new MssqlImportCleanup(cleanup.CommandEnded, false, cleanup.TransactionCleanupConfirmed, "SessionReleaseFailed", cleanup.Errors); }
                        try { input.Dispose(); } catch { summary += ";InputReleaseFailed"; if (status != MssqlImportStatus.Failed) status = MssqlImportStatus.CleanupUncertain; }
                        if (cleanup == null || !cleanup.CommandEnded || !cleanup.SessionReleased || !cleanup.TransactionCleanupConfirmed)
                        {
                            summary += ";CleanupUnconfirmed";
                            if (status != MssqlImportStatus.Failed) status = MssqlImportStatus.CleanupUncertain;
                        }
                        if (cleanup != null && cleanup.Errors.Count != 0)
                        {
                            try
                            {
                                log.WriteError(new MssqlImportBatch(0, 0, 0, 0, 0, string.Empty), new MssqlImportBatchException(cleanup.Errors, true),
                                    "Stop;Origin=Cleanup;DuringBatch=" + (batch == null ? "0" : batch.Number.ToString(CultureInfo.InvariantCulture)));
                                log.Flush();
                            }
                            catch { MarkLogIncomplete(); }
                        }
                        try { log.WriteEnd(Result()); } catch { MarkLogIncomplete(); }
                        try { log.Dispose(); } catch { MarkLogIncomplete(); }
                        Publish(MssqlImportStage.Finished, true);
                    }
                }
                return Result();
            }

            private void HandleSqlError(MssqlImportBatchException error, MssqlImportSessionState before, bool protocol)
            {
                bool skip = false; string stateSummary = string.Empty; MssqlImportBatchException probeFailure = null;
                // Cancellation with SQL details must still log the original complete detail list.
                if (!cancellation.IsCancellationRequested)
                {
                    try
                    {
                        var after = session.ReadState(cancellation); RequireState(after);
                        if (lobInitialized) { RequireLob(true); }
                        skip = request.SkipSqlErrors && !protocol && !error.OutcomeUncertain && error.Errors.Count != 0 &&
                            error.Errors.All(e => e.Class >= 11 && e.Class <= 16 && !UnsafeErrorNumber(e.Number)) &&
                            before.TransactionCount == 0 && before.XactState == 0 && after.TransactionCount == 0 && after.XactState == 0;
                        stateSummary = ";TransactionCount=" + after.TransactionCount.ToString(CultureInfo.InvariantCulture) + ";XactState=" + after.XactState.ToString(CultureInfo.InvariantCulture) + ";DatabaseId=" + after.DatabaseId.ToString(CultureInfo.InvariantCulture);
                    }
                    catch (Exception stateError)
                    {
                        skip = false; stateSummary = ";StateCheck=" + SafeCode(stateError, "StateUnconfirmed");
                        probeFailure = stateError as MssqlImportBatchException;
                    }
                }
                if (protocol) stateSummary += ";NativeLobBatch";
                if (!skip) summary = "SqlBatchFailed";
                try
                {
                    log.WriteError(batch, error, (cancellation.IsCancellationRequested ? "Cancel" : skip ? "Skip" : "Stop") + ";Origin=Source" + stateSummary);
                    if (probeFailure != null) log.WriteError(new MssqlImportBatch(0, 0, 0, 0, 0, string.Empty), probeFailure, "Stop;Origin=Probe;DuringBatch=" + batch.Number.ToString(CultureInfo.InvariantCulture));
                    log.Flush();
                }
                catch { MarkLogIncomplete(); throw; }
                cancellation.ThrowIfCancellationRequested();
                if (!skip) { summary = "SqlBatchFailed"; throw new InvalidOperationException("SqlBatchFailed"); }
                skippedBytes = checked(skippedBytes + batch.EndByte - boundary); boundary = batch.EndByte; acknowledged = batch.Number;
                Publish(MssqlImportStage.Executing);
            }
            private static bool UnsafeErrorNumber(int number)
            { switch (number) { case -2: case 0: case 11: case 20: case 64: case 233: case 596: case 10053: case 10054: case 10060: return true; default: return false; } }
            private void RequireState(MssqlImportSessionState state)
            { if (state == null || !state.IsOpen || state.DatabaseId != preflight.Target.DatabaseId || state.TransactionCount < 0 || state.XactState < -1 || state.XactState > 1) throw new InvalidOperationException("SessionStateInvalid"); }
            private void RequireLob(bool terminal)
            {
                if (!preflight.Scan.Script.NativeJobId.HasValue) throw new InvalidOperationException("LobStateInvalid");
                var state = session.ReadLobState(preflight.Scan.Script.NativeJobId.Value, cancellation);
                if (state == null || !state.Exists || state.RowCount != 1 || state.Failed || state.Sequence < 0 || state.ConsumedThrough < 0 ||
                    (state.Consumed ? state.Sequence != state.ConsumedThrough : state.Sequence <= 0 || state.ConsumedThrough != state.Sequence - 1) || terminal && !state.Consumed)
                    throw new InvalidOperationException("LobStateInvalid");
            }
            private void MarkLogIncomplete()
            { logIncomplete = true; if (!summary.Contains("LogIncomplete")) summary += ";LogIncomplete"; if (status != MssqlImportStatus.Failed) status = MssqlImportStatus.Failed; }
            private MssqlImportResult Result()
            { return new MssqlImportResult(request.JobId, status, preflight.Scan.FileBytes, successfulBytes, skippedBytes, successes, failures, acknowledged, uncertain, log.Path, cleanup, summary, log.Tail.TotalErrors, clock.Elapsed); }
            private void Stopping()
            {
                lock (reportGate)
                {
                    if (finished || latest == null || latest.Stage == MssqlImportStage.Stopping) return;
                    latest = Snapshot(MssqlImportStage.Stopping, latest, null); Notify(observer, latest);
                }
            }
            private MssqlImportProgress Snapshot(MssqlImportStage stage, MssqlImportProgress previous, MssqlImportEstimate estimate)
            {
                // Cancellation thread only copies an already immutable worker snapshot.
                if (previous != null) return new MssqlImportProgress(request.JobId, previous.FileBytes, stage, previous.ScannedBytes, previous.SuccessfulBytes, previous.SkippedBytes,
                    previous.SuccessfulBatches, previous.FailedBatches, previous.BatchNumber, previous.StartLine, previous.EndLine, previous.StartByte, previous.EndByte,
                    previous.Phase, elapsed: clock.Elapsed, recentErrorText: previous.RecentErrorText, logPath: previous.LogPath, totalErrors: previous.TotalErrors, logTruncated: previous.LogTruncated);
                return new MssqlImportProgress(request.JobId, preflight.Scan.FileBytes, stage, successfulBytes: successfulBytes, skippedBytes: skippedBytes,
                    successfulBatches: successes, failedBatches: failures, batchNumber: batch == null ? 0 : batch.Number, startLine: batch == null ? 0 : batch.StartLine,
                    endLine: batch == null ? 0 : batch.EndLine, startByte: batch == null ? 0 : batch.StartByte, endByte: batch == null ? 0 : batch.EndByte, phase: phase,
                    elapsed: clock.Elapsed, remaining: estimate == null ? null : estimate.Remaining, estimatedFinish: estimate == null ? null : estimate.EstimatedFinish,
                    recentErrorText: log.Tail.Snapshot() + (logIncomplete ? "\r\nLogIncomplete" : ""), logPath: log.Path, totalErrors: log.Tail.TotalErrors, logTruncated: log.Tail.Truncated);
            }
            private void Publish(MssqlImportStage stage, bool force = false)
            {
                lock (reportGate)
                {
                    if (finished) return;
                    if (cancellation.IsCancellationRequested && stage != MssqlImportStage.Finished) stage = MssqlImportStage.Stopping;
                    var now = DateTime.UtcNow; var p = Snapshot(stage, null, null); estimator.Observe(p, now);
                    latest = Snapshot(stage, null, estimator.Estimate(now));
                    bool shouldReport = estimator.ShouldReport(latest, now);
                    if (stage == MssqlImportStage.Finished) finished = true;
                    if (force || shouldReport) Notify(observer, latest);
                }
            }
        }
    }
}