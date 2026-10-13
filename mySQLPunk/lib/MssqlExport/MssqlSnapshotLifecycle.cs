using System;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
namespace mySQLPunk.lib.MssqlExport
{
    internal interface IMssqlSnapshotDeadline : IDisposable
    {
        CancellationToken Token { get; }
        int RemainingSeconds { get; }
        void Check();
        void WaitForPoll();
    }
    internal interface IMssqlSnapshotDeadlineBackend
    {
        IMssqlSnapshotDeadline Deadline { set; }
    }
    // One monotonic budget and linked token cover the entire operation, including every SQL command.
    internal sealed class MssqlSnapshotDeadline : IMssqlSnapshotDeadline
    {
        private readonly Stopwatch elapsed = Stopwatch.StartNew();
        private readonly CancellationTokenSource cancellation;
        private readonly int seconds;
        internal MssqlSnapshotDeadline(int seconds, CancellationToken token)
        { this.seconds = seconds; cancellation = CancellationTokenSource.CreateLinkedTokenSource(token); cancellation.CancelAfter(TimeSpan.FromSeconds(seconds)); }
        public CancellationToken Token => cancellation.Token;
        public int RemainingSeconds { get { Check(); return Math.Max(1, (int)Math.Ceiling(seconds - elapsed.Elapsed.TotalSeconds)); } }
        public void Check()
        { Token.ThrowIfCancellationRequested(); if (elapsed.Elapsed.TotalSeconds >= seconds) throw new TimeoutException("SnapshotDeadlineElapsed"); }
        public void WaitForPoll() { Check(); Token.WaitHandle.WaitOne(100); Check(); }
        public void Dispose() { cancellation.Dispose(); }
    }
    internal sealed class MssqlSnapshotLifecycle
    {
        internal const int EnableSeconds = 60;
        internal const int RestoreSeconds = 30;
        private readonly IMssqlSnapshotBackend backend;
        private readonly IMssqlSnapshotRecoveryStore store;
        private readonly Guid jobId;
        private readonly Func<int, CancellationToken, IMssqlSnapshotDeadline> deadlineFactory;
        private MssqlSnapshotIdentity identity;
        private MssqlSnapshotRecoveryRecord record;
        private string path;
        private bool started, locked, releaseAttempted, attemptedEnable;
        private int? original, observed;
        private MssqlSnapshotRecoveryCode notice;
        private MssqlSnapshotCleanupResult result;
        internal MssqlSnapshotLifecycle(IMssqlSnapshotBackend backend, IMssqlSnapshotRecoveryStore store, Guid jobId,
            Func<int, CancellationToken, IMssqlSnapshotDeadline> deadlineFactory = null)
        {
            this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            if (jobId == Guid.Empty) throw new ArgumentException("JobIdRequired", nameof(jobId));
            this.jobId = jobId; this.deadlineFactory = deadlineFactory ?? ((s, t) => new MssqlSnapshotDeadline(s, t));
        }
        internal void Prepare(Action<MssqlExportStage> report, CancellationToken cancellation)
        {
            if (started || result != null) throw new InvalidOperationException("SnapshotAlreadyPrepared");
            started = true;
            using (var deadline = deadlineFactory(EnableSeconds, cancellation))
            {
                SetDeadline(deadline);
                try
                {
                    deadline.Check();
                    if (!backend.TryAcquireLock(deadline.Token)) throw new MssqlSnapshotStartException(MssqlSnapshotStartRejection.SnapshotBusy);
                    locked = true;
                    var capability = Read(deadline);
                    if (!capability.CanManage) throw new MssqlSnapshotStartException(MssqlSnapshotStartRejection.SnapshotUnavailable, capability);
                    identity = capability.Identity; original = capability.State;
                    if (original == 1) return;
                    Report(report, MssqlExportStage.EnablingSnapshot);
                    record = Record(MssqlSnapshotRecoveryPhase.Prepared, MssqlSnapshotRecoveryCode.None);
                    path = store.Create(record);
                    if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("SnapshotJournalReceiptMissing");
                    record = Record(MssqlSnapshotRecoveryPhase.EnableAttempted, MssqlSnapshotRecoveryCode.EnableUnconfirmed);
                    try { store.Update(record); } // Durable intent is required BEFORE sending ON.
                    catch (Exception) { Warn(MssqlSnapshotRecoveryCode.JournalUpdateFailed); throw; }
                    var fresh = Read(deadline);
                    RequireIdentity(fresh);
                    if (!fresh.CanManage || fresh.State != 0) throw new MssqlSnapshotStartException(MssqlSnapshotStartRejection.SnapshotUnavailable, fresh);
                    deadline.Check(); attemptedEnable = true;
                    try { backend.SetEnabled(true, deadline.Token); }
                    catch (MssqlSnapshotSettingNotAttemptedException ex)
                    {
                        attemptedEnable = false;
                        // Preserve the caller's cancellation/error semantics after discarding mutation ownership.
                        if (ex.InnerException != null) ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                        throw;
                    }
                    Confirm(deadline, 1);
                    Update(MssqlSnapshotRecoveryPhase.Enabled, MssqlSnapshotRecoveryCode.None);
                    deadline.Check();
                }
                finally { SetDeadline(null); } // Keep the session and lock alive for explicit Restore, even on error.
            }
        }
        internal MssqlSnapshotCleanupResult Restore(Action<MssqlExportStage> report)
        {
            if (result != null) return result;
            var state = original == 1 ? MssqlSnapshotCleanupState.PreservedOn : MssqlSnapshotCleanupState.NotRequired;
            using (var deadline = deadlineFactory(RestoreSeconds, CancellationToken.None))
            {
                SetDeadline(deadline);
                try
                {
                    if (attemptedEnable)
                    {
                        state = MssqlSnapshotCleanupState.Unconfirmed;
                        Report(report, MssqlExportStage.RestoringSnapshot);
                        Update(MssqlSnapshotRecoveryPhase.Restoring, MssqlSnapshotRecoveryCode.None);
                        var capability = Read(deadline); RequireIdentity(capability);
                        while (capability.State == 2 || capability.State == 3)
                        {
                            deadline.WaitForPoll(); capability = Read(deadline); RequireIdentity(capability);
                        }
                        if (capability.State == 1)
                        {
                            if (!capability.CanManage) throw new InvalidOperationException("SnapshotCapabilityUnavailable");
                            // The SQL adapter checks live identity/lock again immediately before ALTER.
                            try { backend.SetEnabled(false, deadline.Token); }
                            catch (Exception) { /* Response can be lost after OFF. Actual readback decides. */ }
                            Confirm(deadline, 0);
                        }
                        else if (capability.State != 0) Confirm(deadline, 0);
                        state = MssqlSnapshotCleanupState.RestoredOff;
                        Update(MssqlSnapshotRecoveryPhase.Restoring, MssqlSnapshotRecoveryCode.None);
                    }
                    if (path != null)
                    {
                        // No ALTER was attempted, or matching live-session readback confirmed OFF.
                        deadline.Check();
                        try { store.RemoveConfirmed(record); path = null; }
                        catch (Exception) { Warn(MssqlSnapshotRecoveryCode.JournalRemoveFailed); Update(MssqlSnapshotRecoveryPhase.Unconfirmed, MssqlSnapshotRecoveryCode.JournalRemoveFailed); }
                    }
                }
                catch (Exception)
                {
                    Warn(MssqlSnapshotRecoveryCode.RestoreUnconfirmed);
                    Update(MssqlSnapshotRecoveryPhase.Unconfirmed, notice);
                }
                finally
                {
                    ReleaseCore(deadline);
                    SetDeadline(null);
                }
            }
            result = new MssqlSnapshotCleanupResult(state, observed, path,
                notice == MssqlSnapshotRecoveryCode.None ? null : notice.ToString());
            return result;
        }
        // Final fallback; Restore normally releases the lock so its outcome includes release failures.
        internal void Release() { if (!releaseAttempted && locked) Restore(null); }
        private void ReleaseCore(IMssqlSnapshotDeadline deadline)
        {
            if (!locked || releaseAttempted) return;
            releaseAttempted = true;
            try { deadline.Check(); backend.ReleaseLock(deadline.Token); locked = false; }
            catch (Exception)
            {
                Warn(MssqlSnapshotRecoveryCode.LockReleaseFailed);
                if (record != null && path == null)
                {
                    try { record = Record(MssqlSnapshotRecoveryPhase.Unconfirmed, MssqlSnapshotRecoveryCode.LockReleaseFailed); path = store.Create(record); }
                    catch (Exception) { /* Fixed notice survives a failed journal recreation. */ }
                }
                else Update(MssqlSnapshotRecoveryPhase.Unconfirmed, MssqlSnapshotRecoveryCode.LockReleaseFailed);
            }
        }
        private MssqlSnapshotCapability Read(IMssqlSnapshotDeadline deadline)
        {
            deadline.Check();
            if (!locked || !backend.IsUsable) { Warn(MssqlSnapshotRecoveryCode.SessionUnavailable); throw new InvalidOperationException("SnapshotSessionUnavailable"); }
            var capability = backend.ReadCapability(deadline.Token);
            deadline.Check();
            if (capability == null) throw new InvalidOperationException("SnapshotCapabilityUnavailable");
            observed = (identity == null || SameIdentity(identity, capability.Identity)) && capability.State >= 0 && capability.State <= 3 ? capability.State : (int?)null;
            return capability;
        }
        private void Confirm(IMssqlSnapshotDeadline deadline, int target)
        {
            while (true)
            {
                var capability = Read(deadline); RequireIdentity(capability);
                if (capability.State == target) return;
                if (capability.State != 2 && capability.State != 3) throw new InvalidOperationException("SnapshotStateUnconfirmed");
                deadline.WaitForPoll();
            }
        }
        private void RequireIdentity(MssqlSnapshotCapability capability)
        {
            if (!SameIdentity(identity, capability.Identity)) { Warn(MssqlSnapshotRecoveryCode.IdentityChanged); throw new InvalidOperationException("SnapshotIdentityChanged"); }
        }
        internal static bool SameIdentity(MssqlSnapshotIdentity a, MssqlSnapshotIdentity b) => a != null && b != null && a.IsComplete && b.IsComplete &&
            string.Equals(a.ServerName, b.ServerName, StringComparison.Ordinal) && string.Equals(a.DatabaseName, b.DatabaseName, StringComparison.Ordinal) &&
            a.DatabaseId == b.DatabaseId && a.DatabaseCreateDate == b.DatabaseCreateDate;
        private MssqlSnapshotRecoveryRecord Record(MssqlSnapshotRecoveryPhase phase, MssqlSnapshotRecoveryCode code) =>
            new MssqlSnapshotRecoveryRecord(jobId, identity, original ?? 0, phase, observed, DateTime.UtcNow, code);
        private void Update(MssqlSnapshotRecoveryPhase phase, MssqlSnapshotRecoveryCode code)
        {
            if (path == null) return;
            record = Record(phase, code);
            try { store.Update(record); } catch (Exception) { Warn(MssqlSnapshotRecoveryCode.JournalUpdateFailed); }
        }
        private void Warn(MssqlSnapshotRecoveryCode code) { if (notice == MssqlSnapshotRecoveryCode.None) notice = code; }
        private void Report(Action<MssqlExportStage> report, MssqlExportStage stage)
        { try { report?.Invoke(stage); } catch (Exception) { Warn(MssqlSnapshotRecoveryCode.ObserverFailed); } }
        private void SetDeadline(IMssqlSnapshotDeadline deadline)
        { var bounded = backend as IMssqlSnapshotDeadlineBackend; if (bounded != null) bounded.Deadline = deadline; }
    }
}
