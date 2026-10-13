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
}
