using System;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using mySQLPunk.lib.MssqlExport;
public static partial class SmokeTests
{
    private static void SnapshotLifecycle_PreserveOnAndRestoreOwnedOff()
    {
        foreach (int state in new[] { 0, 1 })
        {
            var events = new List<string>(); var backend = new SnapshotFakeBackend(events) { State = state };
            var store = new SnapshotFakeStore(events); var lifecycle = new MssqlSnapshotLifecycle(backend, store, Guid.NewGuid());
            lifecycle.Prepare(null, CancellationToken.None);
            var result = lifecycle.Restore(null); lifecycle.Release();
            Assert(result.State == (state == 0 ? MssqlSnapshotCleanupState.RestoredOff : MssqlSnapshotCleanupState.PreservedOn), "Original state determines cleanup");
            Assert(backend.AlterCalls.Count == (state == 0 ? 2 : 0), "Originally ON never altered");
            Assert(!store.Exists && backend.ReleaseCalls == 1, "Journal removed and lock released once");
            if (state == 0) Assert(string.Join(",", events).Contains("lock,read,create,update,read,ON,read"), "Durable intent precedes fresh capability and ON");
            Assert(object.ReferenceEquals(result, lifecycle.Restore(null)), "Restore is idempotent");
        }
    }
    private static void SnapshotLifecycle_UnknownEnableAndIdentity()
    {
        var events = new List<string>(); var backend = new SnapshotFakeBackend(events) { ThrowAfterOn = true };
        var store = new SnapshotFakeStore(events); var lifecycle = new MssqlSnapshotLifecycle(backend, store, Guid.NewGuid());
        bool failed = false; try { lifecycle.Prepare(null, CancellationToken.None); } catch (InvalidOperationException) { failed = true; }
        Assert(failed && backend.State == 1 && store.Exists, "Lost ON response retains recovery controller");
        Assert(lifecycle.Restore(null).State == MssqlSnapshotCleanupState.RestoredOff && backend.State == 0, "Unknown enable is read back and restored");
        backend = new SnapshotFakeBackend(new List<string>()); store = new SnapshotFakeStore(backend.Events);
        lifecycle = new MssqlSnapshotLifecycle(backend, store, Guid.NewGuid()); lifecycle.Prepare(null, CancellationToken.None);
        backend.Identity = new MssqlSnapshotIdentity("server", "fixture", 3, new DateTime(2026, 2, 1));
        Assert(lifecycle.Restore(null).State == MssqlSnapshotCleanupState.Unconfirmed && backend.AlterCalls.Count == 1 && store.Exists, "Recreated database never altered");
        backend = new SnapshotFakeBackend(new List<string>()) { NotAttemptedOn = true }; store = new SnapshotFakeStore(backend.Events);
        lifecycle = new MssqlSnapshotLifecycle(backend, store, Guid.NewGuid());
        failed = false; try { lifecycle.Prepare(null, CancellationToken.None); } catch (MssqlSnapshotSettingNotAttemptedException) { failed = true; }
        Assert(failed && lifecycle.Restore(null).State == MssqlSnapshotCleanupState.NotRequired && backend.State == 1 && backend.AlterCalls.Count == 0, "Definite pre-send rejection never owns external ON");
        backend = new SnapshotFakeBackend(new List<string>()) { ThrowAfterOff = true }; store = new SnapshotFakeStore(backend.Events);
        lifecycle = new MssqlSnapshotLifecycle(backend, store, Guid.NewGuid()); lifecycle.Prepare(null, CancellationToken.None);
        Assert(lifecycle.Restore(null).State == MssqlSnapshotCleanupState.RestoredOff && !store.Exists, "Lost OFF response resolved by actual readback");
    }
    private static void SnapshotLifecycle_CancelDeadlineAndResidual()
    {
        var backend = new SnapshotFakeBackend(new List<string>()); var store = new SnapshotFakeStore(backend.Events);
        var canceled = new CancellationTokenSource(); var lifecycle = new MssqlSnapshotLifecycle(backend, store, Guid.NewGuid());
        lifecycle.Prepare(null, canceled.Token); canceled.Cancel();
        Assert(lifecycle.Restore(null).State == MssqlSnapshotCleanupState.RestoredOff && !backend.OffTokenCanceled, "Cleanup does not inherit job cancellation");
        backend = new SnapshotFakeBackend(new List<string>()) { ThrowOff = true }; store = new SnapshotFakeStore(backend.Events);
        lifecycle = new MssqlSnapshotLifecycle(backend, store, Guid.NewGuid()); lifecycle.Prepare(null, CancellationToken.None);
        Assert(lifecycle.Restore(null).State == MssqlSnapshotCleanupState.Unconfirmed && store.Exists, "Failed OFF retains journal");
    }
    private static void SnapshotLifecycle_NoJournalNoAlter()
    {
        foreach (int failure in new[] { 0, 1, 2 })
        {
            var backend = new SnapshotFakeBackend(new List<string>());
            var store = new SnapshotFakeStore(backend.Events) { FailCreate = failure == 0, FailUpdate = failure == 1, MissingReceipt = failure == 2 };
            var lifecycle = new MssqlSnapshotLifecycle(backend, store, Guid.NewGuid());
            bool failed = false; try { lifecycle.Prepare(null, CancellationToken.None); } catch (Exception) { failed = true; }
            var result = lifecycle.Restore(null);
            Assert(failed && backend.AlterCalls.Count == 0, "No durable receipt/intent means no ALTER");
            if (failure == 1) Assert(result.WarningCode == "JournalUpdateFailed", "Journal failure retained as fixed notice");
        }
    }
    private static void SnapshotLifecycle_CapabilityAndCooperation()
    {
        foreach (bool? permission in new bool?[] { null, false })
        foreach (int state in new[] { 0, 1 })
        {
            var backend = new SnapshotFakeBackend(new List<string>()) { CanAlter = permission, State = state };
            var lifecycle = new MssqlSnapshotLifecycle(backend, new SnapshotFakeStore(backend.Events), Guid.NewGuid());
            bool failed = false; try { lifecycle.Prepare(null, CancellationToken.None); } catch (InvalidOperationException) { failed = true; }
            lifecycle.Restore(null); Assert(failed && backend.AlterCalls.Count == 0, "Unknown or denied capability blocks even originally ON");
        }
        var locks = new HashSet<string>(); var a = new SnapshotFakeBackend(new List<string>()) { Locks = locks };
        var b = new SnapshotFakeBackend(new List<string>()) { Locks = locks };
        var c = new SnapshotFakeBackend(new List<string>()) { Locks = locks, Database = "other" };
        var first = new MssqlSnapshotLifecycle(a, new SnapshotFakeStore(a.Events), Guid.NewGuid()); first.Prepare(null, CancellationToken.None);
        var second = new MssqlSnapshotLifecycle(b, new SnapshotFakeStore(b.Events), Guid.NewGuid());
        bool busy = false; try { second.Prepare(null, CancellationToken.None); } catch (InvalidOperationException) { busy = true; }
        Assert(busy && b.Events.Count == 1 && b.AlterCalls.Count == 0, "Busy same database stops before read/ALTER");
        var third = new MssqlSnapshotLifecycle(c, new SnapshotFakeStore(c.Events), Guid.NewGuid()); third.Prepare(null, CancellationToken.None);
        third.Restore(null); first.Restore(null); second.Restore(null);
        var withdrawn = new SnapshotFakeBackend(new List<string>()); var store = new SnapshotFakeStore(withdrawn.Events);
        int reads = 0; withdrawn.OnRead = () => { if (++reads == 2) withdrawn.CanAlter = false; };
        var job = new MssqlSnapshotLifecycle(withdrawn, store, Guid.NewGuid());
        bool rejected = false; try { job.Prepare(null, CancellationToken.None); } catch (InvalidOperationException) { rejected = true; }
        job.Restore(null); Assert(rejected && withdrawn.AlterCalls.Count == 0, "Withdrawn permission rechecked before ON");
    }
    private static void SnapshotLifecycle_ResidualAndObservers()
    {
        foreach (int fault in new[] { 0, 1, 2, 3, 4, 5 })
        {
            var backend = new SnapshotFakeBackend(new List<string>()); var store = new SnapshotFakeStore(backend.Events);
            var job = new MssqlSnapshotLifecycle(backend, store, Guid.NewGuid());
            Action<MssqlExportStage> report = stage => { if (fault == 4) throw new InvalidOperationException("observer-secret"); };
            job.Prepare(report, CancellationToken.None);
            backend.Closed = fault == 0; backend.CanAlter = fault == 1 ? false : true;
            store.FailRemove = fault == 2; backend.FailRelease = fault == 3; store.FailUpdate = fault == 5;
            var result = job.Restore(report); job.Release(); job.Restore(report);
            Assert(result.WarningCode != null && !result.WarningCode.Contains("secret"), "Cleanup/observer faults expose fixed notice");
            Assert(result.State == (fault <= 1 ? MssqlSnapshotCleanupState.Unconfirmed : MssqlSnapshotCleanupState.RestoredOff), "Known OFF retained despite local warning");
            Assert(store.Exists == (fault <= 3), "Residual recovery kept for incomplete cleanup");
            if (fault == 3) Assert(result.ObservedState == 0 && backend.ReleaseCalls == 1, "Failed release retains known OFF and does not repeat");
        }
    }
    private static void SnapshotLifecycle_WholeDeadline()
    {
        var deadlines = new List<SnapshotFakeDeadline>();
        Func<int, CancellationToken, IMssqlSnapshotDeadline> factory = (seconds, token) =>
        { var d = new SnapshotFakeDeadline(seconds, token); deadlines.Add(d); return d; };
        var backend = new SnapshotFakeBackend(new List<string>()) { TransitionAfterOn = true };
        var store = new SnapshotFakeStore(backend.Events); var job = new MssqlSnapshotLifecycle(backend, store, Guid.NewGuid(), factory);
        bool failed = false; try { job.Prepare(null, CancellationToken.None); } catch (TimeoutException) { failed = true; }
        Assert(failed && deadlines[0].Seconds == 60 && deadlines[0].Polls == 60, "Enable whole deadline never resets between polls");
        Assert(job.Restore(null).State == MssqlSnapshotCleanupState.Unconfirmed && deadlines[1].Seconds == 30 && deadlines[1].Polls == 30 && store.Exists, "Independent cleanup deadline bounds transition wait");
        Assert(object.ReferenceEquals(backend.SeenDeadlines[0], backend.SeenDeadlines[1]), "Backend receives same deadline across operation commands");
        backend = new SnapshotFakeBackend(new List<string>()); store = new SnapshotFakeStore(backend.Events); deadlines.Clear();
        job = new MssqlSnapshotLifecycle(backend, store, Guid.NewGuid(), factory); job.Prepare(null, CancellationToken.None);
        backend.State = 3; int reads = 0; backend.OnRead = () => { if (++reads == 2) backend.State = 1; };
        Assert(job.Restore(null).State == MssqlSnapshotCleanupState.RestoredOff, "Transition settling ON still attempts owned OFF");
        var cancel = new CancellationTokenSource(); backend = new SnapshotFakeBackend(new List<string>()) { OnEnabled = () => cancel.Cancel(), ThrowAfterOn = true };
        store = new SnapshotFakeStore(backend.Events); job = new MssqlSnapshotLifecycle(backend, store, Guid.NewGuid(), factory);
        try { job.Prepare(null, cancel.Token); } catch (InvalidOperationException) { }
        Assert(job.Restore(null).State == MssqlSnapshotCleanupState.RestoredOff && !backend.OffTokenCanceled, "Canceled uncertain ON still restored using fresh token");
    }
    private sealed class SnapshotFakeDeadline : IMssqlSnapshotDeadline
    {
        internal readonly int Seconds; internal int Polls; private readonly CancellationToken token;
        internal SnapshotFakeDeadline(int seconds, CancellationToken token) { Seconds = seconds; this.token = token; }
        public CancellationToken Token => token;
        public int RemainingSeconds { get { Check(); return Seconds - Polls; } }
        public void Check() { token.ThrowIfCancellationRequested(); if (Polls >= Seconds) throw new TimeoutException(); }
        public void WaitForPoll() { Polls++; Check(); }
        public void Dispose() { }
    }
    private sealed class SnapshotFakeBackend : IMssqlSnapshotBackend, IMssqlSnapshotDeadlineBackend
    {
        internal readonly List<string> Events; internal readonly List<bool> AlterCalls = new List<bool>();
        internal int State, ReleaseCalls; internal bool ThrowAfterOn, ThrowOff, ThrowAfterOff, NotAttemptedOn, OffTokenCanceled, Closed, FailRelease, TransitionAfterOn;
        internal HashSet<string> Locks; internal string Database = "fixture";
        internal Action OnEnabled;
        private IMssqlSnapshotDeadline deadline;
        internal readonly List<IMssqlSnapshotDeadline> SeenDeadlines = new List<IMssqlSnapshotDeadline>();
        public IMssqlSnapshotDeadline Deadline { set { deadline = value; } }
        internal bool? CanAlter = true, Writable = true;
        internal Action OnRead;
        internal MssqlSnapshotIdentity Identity = new MssqlSnapshotIdentity("server", "fixture", 3, new DateTime(2026, 1, 1));
        internal SnapshotFakeBackend(List<string> events) { Events = events; }
        public bool IsUsable => !Closed;
        public bool TryAcquireLock(CancellationToken token) { token.ThrowIfCancellationRequested(); Events.Add("lock"); SeenDeadlines.Add(deadline); return Locks == null || Locks.Add(Database); }
        public MssqlSnapshotCapability ReadCapability(CancellationToken token) { token.ThrowIfCancellationRequested(); Events.Add("read"); SeenDeadlines.Add(deadline); OnRead?.Invoke(); return new MssqlSnapshotCapability(Identity, State, CanAlter, Writable); }
        public void SetEnabled(bool enabled, CancellationToken token)
        {
            if (enabled && NotAttemptedOn) { State = 1; throw new MssqlSnapshotSettingNotAttemptedException(); }
            if (!enabled) OffTokenCanceled = token.IsCancellationRequested;
            token.ThrowIfCancellationRequested(); Events.Add(enabled ? "ON" : "OFF"); AlterCalls.Add(enabled);
            if (!enabled && ThrowOff) throw new InvalidOperationException("unsafe-provider-message");
            State = enabled ? (TransitionAfterOn ? 3 : 1) : 0;
            if (enabled) OnEnabled?.Invoke();
            if (enabled && ThrowAfterOn) throw new InvalidOperationException("unsafe-provider-message");
            if (!enabled && ThrowAfterOff) throw new InvalidOperationException("unsafe-provider-message");
        }
        public void ReleaseLock(CancellationToken token) { token.ThrowIfCancellationRequested(); Events.Add("unlock"); ReleaseCalls++; if (FailRelease) throw new InvalidOperationException(); Locks?.Remove(Database); }
    }
    private sealed class SnapshotFakeStore : IMssqlSnapshotRecoveryStore
    {
        private readonly List<string> events; internal bool Exists, FailCreate, FailUpdate, FailRemove, MissingReceipt;
        internal SnapshotFakeStore(List<string> events) { this.events = events; }
        public string Create(MssqlSnapshotRecoveryRecord record) { events.Add("create"); if (FailCreate) throw new IOException(); Exists = true; return MissingReceipt ? null : "fixture-journal"; }
        public void Update(MssqlSnapshotRecoveryRecord record) { events.Add("update"); if (FailUpdate) throw new IOException(); }
        public void RemoveConfirmed(MssqlSnapshotRecoveryRecord record) { events.Add("remove"); if (FailRemove) throw new IOException(); Exists = false; }
        public IReadOnlyList<MssqlSnapshotRecoveryNotice> ReadPending() => new MssqlSnapshotRecoveryNotice[0];
    }
    private static MssqlExportRequest SnapshotRequest(MssqlExportMode mode, bool snapshot = false) => new MssqlExportRequest(Guid.NewGuid(), "fixture", new[] { new MssqlObjectKey(1, "dbo", "t") }, mode, false, Path.Combine(Path.GetTempPath(), "snapshot.sql"), null, null, snapshot);
    private static void SnapshotOptions_DefaultAndSchemaOnly()
    {
        Assert(!SnapshotRequest(MssqlExportMode.SchemaAndData).UseSnapshot, "Snapshot defaults off");
        bool rejected = false;
        try { SnapshotRequest(MssqlExportMode.SchemaOnly, true); } catch (ArgumentException) { rejected = true; }
        Assert(rejected, "Schema only rejects snapshot");
        Assert(!SnapshotRequest(MssqlExportMode.SchemaOnly).UseSnapshot && SnapshotRequest(MssqlExportMode.DataOnly, true).UseSnapshot, "Modes retain explicit selection");
        var result = new MssqlExportResult(Guid.NewGuid(), MssqlExportStage.Completed, null, null, null, TimeSpan.Zero, null, null, null);
        Assert(!result.UseSnapshot && result.SnapshotCleanup == null, "Existing results retain unknown cleanup default");
    }
    private static void SnapshotOptions_CapabilityMatrix()
    {
        var identity = new MssqlSnapshotIdentity("server", "fixture", 1, new DateTime(2026, 1, 1));
        foreach (int state in new[] { -1, 0, 1, 2, 3, 9 })
        foreach (bool? alter in new bool?[] { null, false, true })
        foreach (bool? writable in new bool?[] { null, false, true })
        foreach (var id in new[] { null, identity, new MssqlSnapshotIdentity(null, "fixture", 1, identity.DatabaseCreateDate) })
        {
            var capability = new MssqlSnapshotCapability(id, state, alter, writable);
            Assert(capability.CanManage == (id == identity && (state == 0 || state == 1) && alter == true && writable == true), "Capability requires complete identity, stable state and explicit permissions");
        }
    }
    private static void SnapshotOptions_Fingerprint()
    {
        var a = new MssqlExportSchema(null, null, null, 0, null, snapshotCapability: new MssqlSnapshotCapability(null, 0, null, null));
        var b = new MssqlExportSchema(null, null, null, 1, null, snapshotCapability: new MssqlSnapshotCapability(new MssqlSnapshotIdentity("server", "fixture", 1, DateTime.Today), 1, true, true));
        bool rejected = false;
        try { new MssqlExportCatalog(null, 0, null, b.SnapshotCapability); } catch (ArgumentException) { rejected = true; }
        Assert(rejected, "Catalog rejects inconsistent capability state");
        rejected = false;
        try { new MssqlExportSchema(null, null, null, 0, null, snapshotCapability: b.SnapshotCapability); } catch (ArgumentException) { rejected = true; }
        Assert(rejected, "Schema rejects inconsistent capability state");
        Assert(new MssqlExportCatalog(null, 0, null).SnapshotCapability == null, "Missing capability stays unknown");
        Assert(a.Fingerprint == b.Fingerprint, "Capability is not schema drift");
        Assert(new MssqlExportPreflight(b, null).SnapshotCapability == b.SnapshotCapability, "Preflight exposes capability");
    }
    private static void SnapshotRecovery_DurableCreateAndUpdate()
    {
        string root = Path.Combine(Path.GetTempPath(), "snapshot-recovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new MssqlSnapshotRecoveryStore(root);
            var record = new MssqlSnapshotRecoveryRecord(Guid.NewGuid(), new MssqlSnapshotIdentity("server", "fixture", 3, new DateTime(2026, 1, 1)), 0, MssqlSnapshotRecoveryPhase.Prepared, null, DateTime.UtcNow, MssqlSnapshotRecoveryCode.None);
            string path = store.Create(record);
            var pending = new MssqlSnapshotRecoveryStore(root).ReadPending();
            Assert(pending.Count == 1 && pending[0].Record.JobId == record.JobId && pending[0].Record.Identity.DatabaseCreateDate == record.Identity.DatabaseCreateDate, "Durable identity roundtrip");
            bool rejected = false;
            try { store.Create(record); } catch (IOException) { rejected = true; }
            Assert(rejected, "Create never overwrites existing job");
            var update = new MssqlSnapshotRecoveryRecord(record.JobId, record.Identity, 0, MssqlSnapshotRecoveryPhase.Enabled, 1, DateTime.UtcNow, MssqlSnapshotRecoveryCode.None);
            store.Update(update);
            Assert(store.ReadPending()[0].Record.Phase == MssqlSnapshotRecoveryPhase.Enabled, "Atomic update persists phase");
            store.RemoveConfirmed(update);
            Assert(!File.Exists(path), "Matching record removed");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static void SnapshotRecovery_UntrustedFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), "snapshot-untrusted-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new MssqlSnapshotRecoveryStore(root);
            string path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, "{");
            Assert(store.ReadPending()[0].ReadError == MssqlSnapshotRecoveryReadError.InvalidRecord, "Bad JSON safe category");
            File.WriteAllText(path, new string('x', 65537));
            Assert(store.ReadPending()[0].ReadError == MssqlSnapshotRecoveryReadError.TooLarge, "Bounded read");
            File.WriteAllText(path, "{\"version\":2}");
            Assert(store.ReadPending()[0].ReadError == MssqlSnapshotRecoveryReadError.UnsupportedVersion, "Unknown version");
            File.Delete(path);
            var record = RecoveryFixture(); string owned = store.Create(record);
            using (var locked = new FileStream(owned, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert(store.ReadPending()[0].ReadError == MssqlSnapshotRecoveryReadError.ReadFailed, "Locked journal yields safe notice");
            File.Move(owned, path);
            Assert(store.ReadPending()[0].Record == null, "Filename must match job");
            File.Delete(path);
            Directory.CreateDirectory(Path.Combine(root, "nested"));
            File.WriteAllText(Path.Combine(root, "nested", "ignored.json"), "{}");
            for (int n = 0; n < 101; n++) File.WriteAllText(Path.Combine(root, Guid.NewGuid().ToString("N") + ".json"), "{}");
            var notices = store.ReadPending();
            Assert(notices.Count == 101 && notices[100].ReadError == MssqlSnapshotRecoveryReadError.Overflow, "Top-level cap plus overflow");
            // A junction is a real reparse path, not a mocked attribute.
            string link = root + "-link";
            var start = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + root + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var process = System.Diagnostics.Process.Start(start)) { process.WaitForExit(); Assert(process.ExitCode == 0, "Create test junction"); }
            try
            {
                Assert(new MssqlSnapshotRecoveryStore(link).ReadPending()[0].ReadError == MssqlSnapshotRecoveryReadError.UnsafePath, "Reparse root rejected");
                bool unsafeWrite = false;
                try { new MssqlSnapshotRecoveryStore(Path.Combine(link, "child")).Create(RecoveryFixture()); } catch (IOException) { unsafeWrite = true; }
                Assert(unsafeWrite && !Directory.Exists(Path.Combine(root, "child")), "Reparse ancestor prevents writes");
            }
            finally { Directory.Delete(link); }
        }
        finally { Directory.Delete(root, true); }
    }
    private static MssqlSnapshotRecoveryRecord RecoveryFixture() => new MssqlSnapshotRecoveryRecord(Guid.NewGuid(), new MssqlSnapshotIdentity("server", "fixture", 3, new DateTime(2026, 1, 1)), 0, MssqlSnapshotRecoveryPhase.Prepared, null, DateTime.UtcNow, MssqlSnapshotRecoveryCode.None);
    private static void SnapshotRecovery_NoSecretsOrUnsafeDelete()
    {
        string root = Path.Combine(Path.GetTempPath(), "snapshot-faults-" + Guid.NewGuid().ToString("N"));
        try
        {
            var record = RecoveryFixture(); var store = new MssqlSnapshotRecoveryStore(root); string path = store.Create(record);
            string json = File.ReadAllText(path);
            Assert(!json.Contains("connectionString") && !json.Contains("password") && !json.Contains("providerMessage"), "Allowlisted metadata only");
            var wrong = new MssqlSnapshotRecoveryRecord(record.JobId, new MssqlSnapshotIdentity("other", "fixture", 3, record.Identity.DatabaseCreateDate), 0, record.Phase, null, record.UpdatedUtc, record.NoticeCode);
            bool rejected = false; try { store.RemoveConfirmed(wrong); } catch (IOException) { rejected = true; }
            Assert(rejected && File.Exists(path), "Wrong identity cannot delete");
            wrong = new MssqlSnapshotRecoveryRecord(record.JobId, record.Identity, 1, record.Phase, null, record.UpdatedUtc, record.NoticeCode);
            rejected = false; try { store.RemoveConfirmed(wrong); } catch (IOException) { rejected = true; }
            Assert(rejected && File.Exists(path), "Wrong original state cannot delete");
            foreach (string boundary in new[] { "BeforeFlush", "BeforePublish" })
            {
                var failing = new MssqlSnapshotRecoveryStore(root, point => { if (point == boundary) throw new IOException("secret provider text"); });
                string receipt = null; var fresh = RecoveryFixture();
                try { receipt = failing.Create(fresh); } catch (IOException) { }
                Assert(receipt == null && !File.Exists(Path.Combine(root, fresh.JobId.ToString("N") + ".json")), "Failed create grants no receipt");
                rejected = false; try { failing.Update(new MssqlSnapshotRecoveryRecord(record.JobId, record.Identity, 0, MssqlSnapshotRecoveryPhase.Enabled, 1, DateTime.UtcNow, record.NoticeCode)); } catch (IOException) { rejected = true; }
                Assert(rejected && File.ReadAllText(path) == json, "Failed update retains prior durable journal");
            }
            rejected = false;
            try { new MssqlSnapshotRecoveryStore(root, point => { throw new IOException(); }).RemoveConfirmed(record); } catch (IOException) { rejected = true; }
            Assert(rejected && File.Exists(path), "Delete fault retains journal");
            Assert(Directory.GetFiles(root, "*.tmp").Length == 0, "No temporary residue");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
