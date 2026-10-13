using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading;
using mySQLPunk.lib.MssqlExport;
public static partial class SmokeTests
{
    private static void Worker_RequiresService()
    {
        Assert(typeof(MssqlObjectKey).Assembly.GetType("mySQLPunk.lib.MssqlExport.MssqlSqlExportService") != null, "Worker must orchestrate owned source and verified publication");
    }
    private static MssqlExportSchema WorkerSchema(bool estimates = true)
    {
        return new MssqlExportSchema(new[] { 1, 2 }.Select(i => new MssqlTableDefinition(new MssqlObjectKey(i, "dbo", "t" + i),
            estimates ? new Dictionary<string, object> { { "estimated_rows", 100L }, { "estimated_bytes", i == 1 ? 1000L : 100000L } } : null,
            new[] { new MssqlColumnDefinition(new Dictionary<string, object> { { "column_id", 1 }, { "name", "id" }, { "type_name", "int" }, { "type_schema", "sys" } }) }, null, null)), null, null, 1, null);
    }
    private static void Worker_Progress()
    {
        var schema = WorkerSchema(); var id = Guid.NewGuid(); var e = new MssqlExportProgressEstimator(id, schema, true);
        var initial = e.Sample(MssqlExportStage.Data, schema.Tables[0].Key, 0, 0, 0, TimeSpan.Zero, false);
        Assert(initial.Eta == null, "No invented initial ETA");
        Assert(e.Sample(MssqlExportStage.Data, schema.Tables[0].Key, 1, 10, 10, TimeSpan.FromMilliseconds(50), false) == null, "200ms throttle");
        e.Sample(MssqlExportStage.Data, schema.Tables[0].Key, 10, 100, 100, TimeSpan.FromSeconds(1), false);
        var ready = e.Sample(MssqlExportStage.Data, schema.Tables[0].Key, 20, 200, 200, TimeSpan.FromSeconds(2), false);
        Assert(ready.BytesPerSecond == 100 && ready.LastProgressElapsed == TimeSpan.FromSeconds(2) && ready.Eta.HasValue && ready.Eta > TimeSpan.Zero, "Three samples and two seconds enable measured ETA");
        var stalled = e.Sample(MssqlExportStage.Data, schema.Tables[0].Key, 20, 200, 200, TimeSpan.FromSeconds(7), false);
        Assert(stalled.BytesPerSecond == null && stalled.LastProgressElapsed == TimeSpan.FromSeconds(2) && stalled.Eta == null && stalled.EstimationState == MssqlExportEstimationState.Waiting, "Stall clears countdown");
        var over = e.Sample(MssqlExportStage.Data, schema.Tables[0].Key, 150, 1500, 3000, TimeSpan.FromSeconds(8), true);
        Assert(over.Eta == null && over.Percent <= 99, "Underestimate cannot promise completion");
        var next = e.Sample(MssqlExportStage.Data, schema.Tables[1].Key, 1, 1000, 4000, TimeSpan.FromSeconds(9), false);
        Assert(next.CompletedTables == 1 && next.Rows == 151 && next.SourceBytes == 2500, "Table switch accumulates actual values");
        Assert(next.Percent >= ready.Percent && next.Percent < 50, "Wide table dominates work and percent is monotonic");
        var phase = e.SamplePhase(MssqlExportStage.Verifying, 500, 1000, 4000, TimeSpan.FromSeconds(10), false);
        Assert(phase.WrittenBytes == 4000 && phase.PhaseProcessedBytes == 500 && phase.PhaseTotalBytes == 1000, "Local read bytes are not SQL write bytes");
        var final = e.Sample(MssqlExportStage.Completed, null, 0, 0, 4000, TimeSpan.FromSeconds(10), false);
        Assert(final.Percent == 100 && final.JobId == id, "Terminal bypasses throttle and carries job identity");
        var emptyValues = new MssqlExportProgressEstimator(id, schema, false);
        emptyValues.Sample(MssqlExportStage.Data, schema.Tables[0].Key, 1, 0, 50, TimeSpan.Zero, true);
        var moreEmpty = emptyValues.Sample(MssqlExportStage.Data, schema.Tables[0].Key, 2, 0, 100, TimeSpan.FromSeconds(6), true);
        Assert(moreEmpty.EstimationState != MssqlExportEstimationState.Waiting, "Rows with no source payload still make real progress");
        var unknown = new MssqlExportProgressEstimator(Guid.NewGuid(), WorkerSchema(false), false);
        Assert(unknown.Sample(MssqlExportStage.Data, schema.Tables[0].Key, 20, 200, 200, TimeSpan.FromSeconds(3), true).Eta == null, "Unknown metadata remains unknown");
        var fast = new MssqlExportProgressEstimator(id, schema, false);
        fast.Sample(MssqlExportStage.Data, schema.Tables[0].Key, 0, 0, 0, TimeSpan.Zero, true);
        fast.Sample(MssqlExportStage.Data, schema.Tables[0].Key, 1, 10, 10, TimeSpan.FromSeconds(1), true);
        Assert(fast.Sample(MssqlExportStage.Data, schema.Tables[0].Key, 2, 900, 900, TimeSpan.FromSeconds(2), true).Eta == null, "Abrupt rate change is unknown");
    }
    private static void Worker_Lifecycle()
    {
        string root = Path.Combine(Path.GetTempPath(), "mssql-worker-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            foreach (var mode in new[] { MssqlExportMode.DataOnly, MssqlExportMode.SchemaOnly, MssqlExportMode.SchemaAndData })
            {
                var calls = new List<string>(); var schema = WorkerSchema(); var source = new WorkerSource(schema, calls);
                var service = new MssqlSqlExportService(() => source, new MssqlArtifactFiles(), s => calls.Add("artifact." + s.ToString().ToLowerInvariant()));
                var target = Path.Combine(root, mode + ".sql"); var request = WorkerRequest(target, schema, mode, null);
                var samples = new List<MssqlExportProgress>(); var result = service.Export(request, new WorkerProgress(samples.Add), CancellationToken.None);
                Assert(result.Stage == MssqlExportStage.Completed && result.PublishedPath == target, "Export publishes complete result");
                Assert(calls.IndexOf("source.dispose") < calls.IndexOf("artifact.verifying"), "Release snapshot before local work");
                Assert(calls.IndexOf("source.inspect") < calls.IndexOf("source.begin"), "Worker rechecks preflight");
                Assert(calls.LastIndexOf("command.dispose") < calls.IndexOf("source.complete"), "Close command before completing snapshot");
                Assert(calls.Count(x => x == "source.rows") == (mode == MssqlExportMode.SchemaOnly ? 0 : 2), "SchemaOnly never opens data");
                Assert(result.RowCounts.All(x => x.Rows == (mode == MssqlExportMode.SchemaOnly ? 0 : 3)), "Fresh reader includes first row and all rows");
                var sql = File.ReadAllText(target);
                Assert(sql.Contains("CREATE TABLE") == (mode != MssqlExportMode.DataOnly), "DataOnly omits DDL");
                Assert(samples.Where(x => x.Stage != MssqlExportStage.Completed).All(x => x.Percent == null || x.Percent <= 99), "No early success");
                Assert(samples.All(x => x.JobId == request.JobId), "Reports keep operation identity");
                Assert(samples.Last().Rows == result.RowCounts.Sum(x => x.Rows), "Final progress preserves actual totals");
            }
            var mirrorSchema = WorkerSchema(); var mirrorSamples = new List<MssqlExportProgress>();
            var mirrorRequest = WorkerRequest(Path.Combine(root, "mirror_backup_ok.sql"), mirrorSchema, MssqlExportMode.DataOnly, new MssqlMirrorOptions(Path.Combine(root, "mirror"), 2));
            var mirrorResult = new MssqlSqlExportService(() => new WorkerSource(mirrorSchema, new List<string>())).Export(mirrorRequest, new WorkerProgress(mirrorSamples.Add), CancellationToken.None);
            Assert(mirrorResult.Stage == MssqlExportStage.Completed && File.Exists(mirrorResult.MirrorPath), "Verified mirror success");
            var mirrorDone = mirrorSamples.Last(x => x.Stage == MssqlExportStage.Mirroring);
            Assert(mirrorDone.PhaseProcessedBytes == 2 * mirrorDone.WrittenBytes && mirrorDone.PhaseTotalBytes == 2 * mirrorDone.WrittenBytes, "Mirror accounts for copy and verify without double-counting SQL writes");
            for (int i = 1; i < mirrorSamples.Count; i++) Assert(!mirrorSamples[i].Percent.HasValue || !mirrorSamples[i-1].Percent.HasValue || mirrorSamples[i].Percent >= mirrorSamples[i-1].Percent, "Overall progress never resets");
            int disposed = 0; var catalogService = new MssqlSqlExportService(() => new WorkerSource(WorkerSchema(), new List<string>(), () => disposed++));
            catalogService.LoadCatalog("fixture", CancellationToken.None);
            catalogService.Preflight(WorkerRequest(Path.Combine(root, "preflight.sql"), WorkerSchema(), MssqlExportMode.DataOnly, null), CancellationToken.None);
            Assert(disposed == 2, "Catalog and preflight own separate short sources");
            var failingCatalog = new MssqlSqlExportService(() => { throw new IOException("private connection-string payload"); });
            try { failingCatalog.LoadCatalog("fixture", CancellationToken.None); Assert(false, "Catalog failure required"); }
            catch (Exception ex) { Assert(!ex.ToString().Contains("private connection-string"), "Catalog does not leak provider exception details"); }
            try { failingCatalog.Preflight(mirrorRequest, CancellationToken.None); Assert(false, "Preflight failure required"); }
            catch (Exception ex) { Assert(!ex.ToString().Contains("private connection-string"), "Preflight does not leak provider exception details"); }

        }
        finally { Directory.Delete(root, true); }
    }
    private static void Worker_Failures()
    {
        string root = Path.Combine(Path.GetTempPath(), "mssql-worker-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            foreach (string fault in new[] { "inspect", "begin", "rows", "complete", "write", "verify", "mirror", "cancel-read", "cancel-write", "cancel-verify", "cancel-verify-read", "cancel-publish", "cancel-commit", "cancel-mirror", "cancel-mirror-read", "cleanup" })
            using (var cancel = new CancellationTokenSource())
            {
                var schema = WorkerSchema(); var calls = new List<string>(); var source = new WorkerSource(schema, calls);
                source.Fault = fault; source.Cancel = cancel;
                string target = Path.Combine(root, fault + "_backup_out.sql"); File.WriteAllText(target, "original");
                var mirror = fault.Contains("mirror") ? new MssqlMirrorOptions(Path.Combine(root, "mirror-" + fault), 2) : null;
                var request = WorkerRequest(target, schema, MssqlExportMode.DataOnly, mirror);
                var files = new WorkerFiles(fault, cancel); var samples = new List<MssqlExportProgress>();
                var service = new MssqlSqlExportService(() => source, files, stage => {
                    calls.Add("artifact." + stage.ToString().ToLowerInvariant());
                    if ((fault == "verify" || fault == "cleanup") && stage == MssqlExportStage.Verifying) throw new IOException("private connection-string payload");
                    if (fault == "cancel-verify" && stage == MssqlExportStage.Verifying || fault == "cancel-publish" && stage == MssqlExportStage.Publishing || fault == "cancel-mirror" && stage == MssqlExportStage.Mirroring) cancel.Cancel();
                });
                var result = service.Export(request, new WorkerProgress(p => { samples.Add(p); if (fault == "cancel-verify-read" && p.Stage == MssqlExportStage.Verifying && p.PhaseProcessedBytes > 0 || fault == "cancel-mirror-read" && p.Stage == MssqlExportStage.Mirroring && p.PhaseProcessedBytes == p.PhaseTotalBytes) cancel.Cancel(); }), cancel.Token);
                Assert(calls.Contains("source.dispose"), "Dispose source after " + fault);
                if (mirror != null) Assert(result.PublishedPath == target && result.Stage == MssqlExportStage.LocalCompletedMirrorIncomplete, "Keep local outcome after " + fault);
                else if (fault == "cancel-commit") Assert(result.PublishedPath == target && result.Stage == MssqlExportStage.Completed, "Late commit cancellation keeps committed success");
                else {
                    Assert(result.PublishedPath == null && File.ReadAllText(target) == "original", "Failure preserves approved old output: " + fault);
                    Assert(result.Stage == (fault.StartsWith("cancel") ? MssqlExportStage.Canceled : MssqlExportStage.Failed), "Correct terminal: " + fault);
                }
                Assert(!string.Join(" ", result.Warnings).Contains("private connection-string"), "Do not expose source exception payloads");
                if (fault == "inspect" || fault == "begin") Assert(files.Creates == 0, "Preflight failure creates no partial");
                if (fault == "cleanup") Assert(result.ResidualPaths.Any() && result.Warnings.Any(), "Collect cleanup evidence after Dispose");
                else Assert(!Directory.GetFiles(root, "*.partial", SearchOption.AllDirectories).Any(x => x.Contains(request.JobId.ToString("N"))), "Owned partial cleaned");
                Assert(samples.Last().Stage == result.Stage, "Terminal after cleanup");
            }
        }
        finally { Directory.Delete(root, true); }
    }
    private static MssqlExportRequest WorkerRequest(string target, MssqlExportSchema schema, MssqlExportMode mode, MssqlMirrorOptions mirror)
    { return new MssqlExportRequest(Guid.NewGuid(), "fixture", schema.Tables.Select(x => x.Key).ToArray(), mode, false, target, MssqlExportArtifact.CaptureDestination(target, CancellationToken.None), mirror); }
    private sealed class WorkerProgress : IProgress<MssqlExportProgress>
    { private readonly Action<MssqlExportProgress> action; internal WorkerProgress(Action<MssqlExportProgress> action) { this.action = action; } public void Report(MssqlExportProgress p) { action(p); } }
    private sealed class WorkerDispose : IDisposable
    { private readonly Action action; internal WorkerDispose(Action action) { this.action = action; } public void Dispose() { action(); } }
    private sealed class WorkerSource : IMssqlExportSource
    {
        private readonly MssqlExportSchema schema; private readonly List<string> calls; private readonly Action disposed; private bool closed;
        internal string Fault; internal CancellationTokenSource Cancel;
        internal WorkerSource(MssqlExportSchema schema, List<string> calls, Action disposed = null) { this.schema = schema; this.calls = calls; this.disposed = disposed; calls.Add("source.open"); }
        public MssqlExportCatalog ReadCatalog(string db, CancellationToken c) { c.ThrowIfCancellationRequested(); return new MssqlExportCatalog(schema.Tables, 1, null); }
        public MssqlExportPreflight Inspect(MssqlExportRequest r, CancellationToken c) { calls.Add("source.inspect"); return new MssqlExportPreflight(schema, Fault == "inspect" ? new[] { new MssqlExportIssue("Denied", null, null, "denied") } : null); }
        public MssqlExportSchema BeginRead(MssqlExportRequest r, CancellationToken c) { calls.Add("source.begin"); if (Fault == "begin") throw new IOException("snapshot failed"); return schema; }
        public MssqlRowReader OpenRows(MssqlTableDefinition t, CancellationToken c) {
            calls.Add("source.rows"); if (Fault == "rows") throw new IOException("DB disconnected private connection-string");
            if (Fault == "cancel-read") { Cancel.Cancel(); c.ThrowIfCancellationRequested(); }
            var data = new DataTable(); data.Columns.Add("id", typeof(int)); data.Rows.Add(1); data.Rows.Add(2); data.Rows.Add(3);
            var reader = data.CreateDataReader(); return new MssqlRowReader(reader, new WorkerDispose(() => { Assert(reader.IsClosed, "Reader closes before command"); calls.Add("command.dispose"); }));
        }
        public void CompleteRead(MssqlExportSchema s, CancellationToken c) { calls.Add("source.complete"); if (Fault == "complete") throw new InvalidOperationException("schema fingerprint changed"); }
        public void Dispose() { if (closed) return; closed = true; calls.Add("source.dispose"); disposed?.Invoke(); }
    }
    private sealed class WorkerFiles : MssqlArtifactFiles
    {
        private readonly string fault; private readonly CancellationTokenSource cancel; internal int Creates;
        internal WorkerFiles(string fault, CancellationTokenSource cancel) { this.fault = fault; this.cancel = cancel; }
        internal override Stream Create(string path) { Creates++; if (fault == "mirror" && Creates == 2) throw new IOException("mirror unavailable"); return new WorkerStream(base.Create(path), fault, cancel); }
        internal override void Replace(string source, string target, string recovery) { if (fault == "cancel-commit") cancel.Cancel(); base.Replace(source, target, recovery); }
        internal override void Delete(string path) { if (fault == "cleanup") throw new IOException("cleanup failed"); base.Delete(path); }
    }
    private sealed class WorkerStream : Stream
    {
        private readonly Stream inner; private readonly string fault; private readonly CancellationTokenSource cancel;
        internal WorkerStream(Stream inner, string fault, CancellationTokenSource cancel) { this.inner = inner; this.fault = fault; this.cancel = cancel; }
        public override void Write(byte[] b, int o, int n) { if (fault == "write") throw new IOException("disk full"); if (fault == "cancel-write") { cancel.Cancel(); cancel.Token.ThrowIfCancellationRequested(); } inner.Write(b,o,n); }
        public override void Flush() { inner.Flush(); } protected override void Dispose(bool d) { if(d) inner.Dispose(); base.Dispose(d); }
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => inner.Length; public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] b,int o,int n) { throw new NotSupportedException(); } public override long Seek(long o,SeekOrigin s) { throw new NotSupportedException(); } public override void SetLength(long n) { throw new NotSupportedException(); }
    }
}
