using System;
using System.IO;
using mySQLPunk.lib.MssqlExport;
public static partial class SmokeTests
{
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
