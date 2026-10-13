using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading;
using mySQLPunk.lib.MssqlExport;
public static partial class SmokeTests
{
    // SQL provider is the external boundary; run the actual service sanitizer.
    private static System.Data.SqlClient.SqlException DiagnosticSql(params int[] numbers)
    {
        const string secret = "private-server;Password=private-secret;SELECT confidential";
        var errors = (System.Data.SqlClient.SqlErrorCollection)Activator.CreateInstance(typeof(System.Data.SqlClient.SqlErrorCollection), true);
        var constructor = typeof(System.Data.SqlClient.SqlError).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .OrderByDescending(c => c.GetParameters().Length).First();
        var add = typeof(System.Data.SqlClient.SqlErrorCollection).GetMethod("Add", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        foreach (int number in numbers) {
            var args = new object[] { number, (byte)1, (byte)16, secret, secret, secret, 1 }.Concat(constructor.GetParameters().Skip(7).Select(p => p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null)).ToArray();
            add.Invoke(errors, new[] { constructor.Invoke(args) });
        }
        var create = typeof(System.Data.SqlClient.SqlException).GetMethod("CreateException", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic, null,
            new[] { typeof(System.Data.SqlClient.SqlErrorCollection), typeof(string) }, null);
        return (System.Data.SqlClient.SqlException)create.Invoke(null, new object[] { errors, "test" });
    }
    private static Exception DiagnosticFailure(Exception input, bool catalog)
    {
        var service = new MssqlSqlExportService(() => { throw input; });
        try {
            if (catalog) service.LoadCatalog("fixture", CancellationToken.None);
            else service.Preflight(WorkerRequest(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sql"), WorkerSchema(), MssqlExportMode.SchemaOnly, null), CancellationToken.None);
        } catch (Exception safe) { return safe; }
        throw new Exception("Expected failed inspection");
    }
    private static void Diagnostic_Service()
    {
        foreach (bool catalog in new[] { true, false })
        foreach (var example in new[] {
            new { Number = 229, Code = "SqlPermissionDenied" }, new { Number = 230, Code = "SqlPermissionDenied" },
            new { Number = 297, Code = "SqlPermissionDenied" }, new { Number = 300, Code = "SqlPermissionDenied" },
            new { Number = 18456, Code = "LoginRejected" }, new { Number = 18452, Code = "LoginRejected" },
            new { Number = 916, Code = "DatabaseUnavailable" }, new { Number = 4060, Code = "DatabaseUnavailable" }, new { Number = 4064, Code = "DatabaseUnavailable" } }) {
            var safe = DiagnosticFailure(DiagnosticSql(50000, example.Number), catalog);
            AssertContains(safe.Message, example.Code, "Inspection keeps a safe actionable category even when a later SqlError carries it");
            Assert(safe.InnerException == null && !safe.ToString().Contains("private-") && !safe.ToString().Contains("confidential"), "No original provider payload or inner exception");
        }
        foreach (bool catalog in new[] { true, false }) {
            var safe = DiagnosticFailure(new MssqlExportPrerequisiteException(MssqlPrerequisite.ViewDefinitionRequired), catalog);
            Assert(safe is MssqlExportPrerequisiteException && ((MssqlExportPrerequisiteException)safe).Reason == MssqlPrerequisite.ViewDefinitionRequired && safe.InnerException == null, "VIEW DEFINITION diagnostic crosses either service inspection boundary without losing its type");
        }
        var workerService = new MssqlSqlExportService(() => { throw DiagnosticSql(229); });
        var target = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sql");
        var result = workerService.Export(WorkerRequest(target, WorkerSchema(), MssqlExportMode.DataOnly, null), null, CancellationToken.None);
        Assert(result.Stage == MssqlExportStage.Failed && result.PublishedPath == null && !File.Exists(target), "Start-time permission rejection cannot publish an artifact");
        AssertContains(result.ErrorMessage, "SqlPermissionDenied", "Permission revoked after confirmation retains safe reason at worker failure");
        Assert(!result.ErrorMessage.Contains("private-") && !result.ErrorMessage.Contains("confidential"), "Worker result keeps provider details redacted");
        foreach (Exception raw in new Exception[] { DiagnosticSql(50000), new IOException("Password=private-secret") }) {
            var safe = DiagnosticFailure(raw, true);
            Assert(!safe.ToString().Contains("private-secret") && safe.InnerException == null, "Unknown inspection remains redacted");
            Assert(!safe.Message.Contains("SqlPermissionDenied") && !safe.Message.Contains("LoginRejected"), "Unknown failure does not invent a permission cause");
        }
    }
    private static void SnapshotWorker_CancellationDuringCleanup()
    {
        string root = Path.Combine(Path.GetTempPath(), "snapshot-primary-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try {
            foreach (string fault in new[] { "rows", "complete", "cancel-read", "" })
            using (var cancel = new CancellationTokenSource()) {
                var schema = WorkerSchema(); var calls = new List<string>();
                var source = new WorkerSource(schema,calls) { Fault = fault, Cancel = cancel, DuringFinish = cancel.Cancel };
                source.Cleanup = new MssqlSnapshotCleanupResult(MssqlSnapshotCleanupState.Unconfirmed,1,Path.Combine(root,"journal.json"),"RestoreUnconfirmed");
                string target = Path.Combine(root,fault+".sql"); File.WriteAllText(target,"original");
                var result = new MssqlSqlExportService(() => source).Export(WorkerRequest(target,schema,MssqlExportMode.DataOnly,null),null,cancel.Token);
                bool canceled = fault == "" || fault == "cancel-read";
                Assert(result.Stage == (canceled ? MssqlExportStage.Canceled : MssqlExportStage.Failed), "Cleanup cancellation must not replace primary source failure: " + fault + " -> " + result.Stage);
                if (!canceled) AssertContains(result.ErrorMessage,fault == "rows" ? "Data (IOException)" : "Schema (InvalidOperationException)","Original category survives cleanup cancellation");
                Assert(result.PublishedPath == null && File.ReadAllText(target) == "original", "Cancellation after successful reading still stops publication");
                Assert(ReferenceEquals(result.SnapshotCleanup,source.Cleanup) && result.ResidualPaths.Contains(source.Cleanup.RecoveryRecordPath),"Independent cleanup outcome retained");
                Assert(calls.Count(x=>x=="restore")==1 && calls.Count(x=>x=="source.dispose")==1,"Both release boundaries run once");
            }
        } finally { Directory.Delete(root,true); }
    }
    private static void SnapshotWorker_ReadAndDisposeFailures()
    {
        string root = Path.Combine(Path.GetTempPath(), "snapshot-reader-primary-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try {
            foreach (string fault in new[] { "read", "dispose", "read-dispose-cancel", "cancel-read" })
            using (var cancel = new CancellationTokenSource()) {
                var schema=WorkerSchema(); var calls=new List<string>();
                var source = new WorkerSource(schema,calls) { FailReaderRead = fault != "dispose", FailReaderDispose = true, Cancel = cancel,
                    CancelOnReaderDispose = fault == "read-dispose-cancel", CancelOnReaderRead = fault == "cancel-read" };
                string target=Path.Combine(root,fault+".sql"); File.WriteAllText(target,"original");
                var result = new MssqlSqlExportService(() => source).Export(WorkerRequest(target,schema,MssqlExportMode.DataOnly,null),null,cancel.Token);
                Assert(result.Stage == (fault == "cancel-read" ? MssqlExportStage.Canceled : MssqlExportStage.Failed), "Reader cleanup preserves failure-time cancellation status: " + fault);
                if(fault != "cancel-read") AssertContains(result.ErrorMessage, fault == "dispose" ? "Data (InvalidOperationException)" : "Data (IOException)", "Read failure takes precedence, disposal-only failure still propagates: " + fault);
                Assert(source.LastReader.IsDisposed && !source.LastReader.DisposeSucceeded,"Failed command disposal remains uncertain for source FinishRead");
                Assert(result.PublishedPath==null && File.ReadAllText(target)=="original" && !calls.Contains("source.complete"),"Neither failed read nor failed disposal can complete source or publish");
                Assert(calls.Count(x=>x=="command.dispose")==1 && calls.Count(x=>x=="restore")==1 && calls.Count(x=>x=="source.dispose")==1,"Reader and source cleanup run once");
            }
        } finally { Directory.Delete(root,true); }
    }
    private static void SnapshotWorker_RecoveryNotices()
    {
        string root = Path.Combine(Path.GetTempPath(), "snapshot-notices-" + Guid.NewGuid().ToString("N"));
        try {
            var store = new MssqlSnapshotRecoveryStore(root);
            var current = new MssqlSnapshotIdentity("server", "fixture", 2, new DateTime(2026, 2, 1));
            var historical = new MssqlSnapshotIdentity("server", "fixture", 1, new DateTime(2026, 1, 1));
            var other = new MssqlSnapshotIdentity("server", "other", 3, new DateTime(2026, 1, 1));
            foreach (var id in new[] { current, historical, other }) store.Create(new MssqlSnapshotRecoveryRecord(Guid.NewGuid(),id,0,MssqlSnapshotRecoveryPhase.Unconfirmed,1,DateTime.UtcNow,MssqlSnapshotRecoveryCode.RestoreUnconfirmed));
            File.WriteAllText(Path.Combine(root,Guid.NewGuid().ToString("N")+".json"),"{");
            var service = new MssqlSqlExportService(() => { throw new Exception("No DB allowed for notices"); },new MssqlArtifactFiles(),null,store);
            var candidates = service.LoadRecoveryNotices(current);
            Assert(candidates.Count == 3 && candidates.Any(n => n.Record != null && n.Record.Identity.DatabaseId == 1) && candidates.Any(n => n.ReadError != MssqlSnapshotRecoveryReadError.None), "Display includes historical same-name and unreadable records");
            Assert(service.LoadRecoveryNotices(null).Count == 4 && service.LoadRecoveryNotices(new MssqlSnapshotIdentity(null,null,0,default(DateTime))).Count == 4, "Unknown identity does not hide pending display candidates");
            Assert(!MssqlSnapshotLifecycle.SameIdentity(current,historical) && Directory.GetFiles(root).Length == 4, "Display never relaxes repair authorization or removes history");
        } finally { if (Directory.Exists(root)) Directory.Delete(root,true); }
    }
    private static void SnapshotWorker_ModeAndCleanupOrder()
    {
        string root = Path.Combine(Path.GetTempPath(), "snapshot-worker-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try {
            foreach (var mode in new[] { MssqlExportMode.SchemaOnly, MssqlExportMode.DataOnly, MssqlExportMode.SchemaAndData })
            foreach (bool snapshot in new[] { false, true }) {
                if (snapshot && mode == MssqlExportMode.SchemaOnly) continue;
                var schema = WorkerSchema(); var events = new List<string>(); var source = new WorkerSource(schema, events);
                string target = Path.Combine(root, mode + "-" + snapshot + ".sql");
                var request = new MssqlExportRequest(Guid.NewGuid(), "fixture", schema.Tables.Select(t => t.Key).ToArray(), mode, false, target, MssqlExportArtifact.CaptureDestination(target, CancellationToken.None), null, snapshot);
                Assert(MssqlExportSource.ReadIsolation(request) == (mode == MssqlExportMode.SchemaOnly ? (IsolationLevel?)null : snapshot ? IsolationLevel.Snapshot : IsolationLevel.ReadCommitted), "Explicit source transaction isolation");
                var service = new MssqlSqlExportService(() => source, new MssqlArtifactFiles(), stage => { if (stage == MssqlExportStage.Verifying) events.Add("local-verify"); }, new SnapshotFakeStore(events));
                var result = service.Export(request, null, CancellationToken.None);
                Assert(result.Stage == MssqlExportStage.Completed && result.UseSnapshot == snapshot, "Mode follows published artifact: " + mode + " / " + snapshot + " / " + result.ErrorMessage);
                Assert(events.IndexOf("restore") < events.IndexOf("local-verify") && events.IndexOf("restore") < events.IndexOf("source.dispose"), "Explicit restoration precedes disposal and local verify");
                Assert(events.Count(x => x == "restore") == 1 && result.SnapshotCleanup != null, "One cleanup result per job");
                string sql = File.ReadAllText(target);
                AssertContains(sql, "-- Source read mode: " + (mode == MssqlExportMode.SchemaOnly ? "Schema only" : snapshot ? "SNAPSHOT" : "READ COMMITTED"), "Header covers DataOnly before early return");
                bool ordinary = !snapshot && mode != MssqlExportMode.SchemaOnly;
                Assert(result.Warnings.Contains(MssqlSqlExportService.OrdinaryReadWarning) == ordinary && sql.Contains(MssqlSqlExportService.OrdinaryReadWarning) == ordinary, "Ordinary consistency/restore warning in result and SQL only for ordinary data");
            }
        } finally { Directory.Delete(root, true); }
    }
    private static void SnapshotWorker_PrimaryFailureAndRecovery()
    {
        string root = Path.Combine(Path.GetTempPath(), "snapshot-outcomes-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try {
            foreach (string fault in new[] { "", "rows", "cancel-read", "complete", "verify", "publish", "mirror", "observer", "finish", "rows-finish" })
            using (var cancellation = new CancellationTokenSource()) {
                var schema = WorkerSchema(); var events = new List<string>(); var source = new WorkerSource(schema, events) { Fault = fault == "rows-finish" ? "rows" : fault, Cancel = cancellation, ThrowFinish = fault.EndsWith("finish") };
                source.Cleanup = new MssqlSnapshotCleanupResult(MssqlSnapshotCleanupState.Unconfirmed, 1, Path.Combine(root,"journal.json"), "RestoreUnconfirmed");
                string target = Path.Combine(root, fault + ".sql");
                var mirror = fault == "mirror" ? new MssqlMirrorOptions(Path.Combine(root,"mirror"),2) : null;
                var request = WorkerRequest(target, schema, MssqlExportMode.DataOnly, mirror);
                var service = new MssqlSqlExportService(() => source, new WorkerFiles(fault,cancellation), stage => {
                    if (fault == "verify" && stage == MssqlExportStage.Verifying || fault == "publish" && stage == MssqlExportStage.Publishing) throw new IOException("private failure");
                }, new SnapshotFakeStore(events));
                var result = service.Export(request, new WorkerProgress(p => { if (fault == "observer") throw new IOException("observer private"); }), cancellation.Token);
                Assert(events.Count(x => x == "restore") == 1 && events.Count(x => x == "source.dispose") == 1, "Cleanup exactly once after " + fault);
                Assert(result.SnapshotCleanup.State == MssqlSnapshotCleanupState.Unconfirmed && result.Warnings.Any(), "Recovery outcome survives " + fault);
                if (!source.ThrowFinish) Assert(result.ResidualPaths.Contains(source.Cleanup.RecoveryRecordPath), "Residual journal in result");
                if (fault == "" || fault == "observer" || fault == "finish") Assert(result.Stage == MssqlExportStage.Completed && result.PublishedPath == target, "Complete data publishes despite unconfirmed restoration");
                else if (fault == "mirror") Assert(result.Stage == MssqlExportStage.LocalCompletedMirrorIncomplete && result.PublishedPath == target, "Mirror truth kept");
                else {
                    Assert(result.Stage == (fault == "cancel-read" ? MssqlExportStage.Canceled : MssqlExportStage.Failed) && result.PublishedPath == null, "Primary outcome preserved " + fault);
                    if (fault == "rows-finish") AssertContains(result.ErrorMessage, "Data (IOException)", "Cleanup failure does not replace primary read failure");
                }
                Assert(!string.Join(" ", result.Warnings).Contains("private"), "No raw errors leaked");
            }
        } finally { Directory.Delete(root, true); }
    }
    private static void SnapshotWorker_SourceCleanupOwnership()
    {
        foreach (string fault in new[] { "", "reader", "release", "off", "dispose" })
        using (var connection = new System.Data.SqlClient.SqlConnection()) {
            var events = new List<string>(); var owner = new MssqlSessionOwner(); int evictions = 0;
            var store = new SnapshotFakeStore(events); var backend = new SnapshotFakeBackend(events) { FailRelease = fault == "release", ThrowOff = fault == "off" };
            var wrapped = new MssqlExportSource.SourceSnapshotBackend(backend);
            var lifecycle = new MssqlSnapshotLifecycle(wrapped, store, Guid.NewGuid()); lifecycle.Prepare(null,CancellationToken.None);
            var source = new MssqlExportSource(new MssqlOwnedSession(owner,connection), store, evictPool: c => { evictions++; events.Add("evict"); });
            Action<string, object> set = (name, value) => typeof(MssqlExportSource).GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(source, value);
            set("request", SnapshotRequest(MssqlExportMode.DataOnly,true)); set("lifecycle", lifecycle); set("snapshotBackend", wrapped);
            var reader = new DataTable().CreateDataReader();
            var rows = new MssqlRowReader(reader, new WorkerDispose(() => { Assert(reader.IsClosed, "Reader closes first"); events.Add("reader-close"); if (fault == "reader") throw new IOException(); }));
            set("activeReader", rows);
            if (fault == "reader") { try { rows.Dispose(); } catch (IOException) { } }
            if (fault == "dispose") { source.Dispose(); Assert(backend.AlterCalls.Count == 1 && evictions == 1 && owner.DisposeCount == 1, "Dispose releases resources without hidden ALTER"); continue; }
            var cleanup = source.FinishRead(stage => { Assert(reader.IsClosed, "Reader before restore callback"); if (fault != "reader") Assert(owner.DisposeCount == 0, "Live owner retained for restoration"); });
            source.Dispose(); Assert(ReferenceEquals(cleanup,source.FinishRead(null)), "FinishRead idempotent");
            Assert(owner.DisposeCount == 1 && evictions == (fault == "reader" || fault == "release" ? 1 : 0), "Eviction only for uncertain session resource closure");
            Assert(backend.SeenDeadlines.All(d => d != null), "Source adapter forwards lifecycle deadlines");
            if (fault == "reader") Assert(backend.AlterCalls.Count == 1 && cleanup.State == MssqlSnapshotCleanupState.Unconfirmed && store.Exists, "Unknown reader closure releases owner and forbids ALTER");
            if (fault == "release") Assert(cleanup.State == MssqlSnapshotCleanupState.RestoredOff && cleanup.WarningCode == "LockReleaseFailed" && store.Exists, "Release failure preserves known OFF and journal warning");
            if (fault == "off") Assert(cleanup.State == MssqlSnapshotCleanupState.Unconfirmed && store.Exists, "OFF uncertainty survives source finish");
            if (fault == "") Assert(cleanup.State == MssqlSnapshotCleanupState.RestoredOff && events.IndexOf("reader-close") < events.IndexOf("OFF"), "Normal source ordering restores after reader");
        }
    }
    private static void SnapshotWorker_StartValidation()
    {
        var reader = new MssqlExportMetadataReader(); var basis = WorkerSchema();
        var identity = new MssqlSnapshotIdentity("server","fixture",1,new DateTime(2026,1,1));
        foreach (int state in new[] { 0,1,2,3,-1 }) foreach (bool? alter in new bool?[] { true,false,null }) foreach (bool snapshot in new[] { false,true }) {
            var schema = new MssqlExportSchema(basis.Tables,null,null,state,null,null,new MssqlSnapshotCapability(identity,state,alter,true));
            var request = new MssqlExportRequest(Guid.NewGuid(),"fixture",schema.Tables.Select(t=>t.Key).ToArray(),MssqlExportMode.DataOnly,false,Path.Combine(Path.GetTempPath(),"start.sql"),null,null,snapshot);
            Assert(!reader.Validate(schema,request).Any(x=>x.Code=="SnapshotUnavailable"),"Shape validation independent of settings capability");
            Assert(reader.ValidateStart(schema,request).Any(x=>x.Code=="SnapshotUnavailable") == (snapshot && (alter != true || state != 0 && state != 1)), "Only selected snapshot requires stable capability");
            var output = new StringWriter(); new MssqlSchemaScriptWriter().WriteBeforeData(schema,request,output,CancellationToken.None);
            Assert(output.ToString().Contains("Source read mode"),"Writing DDL does not recheck ALTER");
        }
    }
    private static void SnapshotWorker_ProgressDoesNotCompleteInterruptedTable()
    {
        var schema = WorkerSchema(); var estimator = new MssqlExportProgressEstimator(Guid.NewGuid(), schema, false);
        estimator.Sample(MssqlExportStage.Data, schema.Tables[0].Key, 5, 50, 100, TimeSpan.Zero, true);
        var restored = estimator.Sample(MssqlExportStage.RestoringSnapshot, null, 0, 0, 100, TimeSpan.FromSeconds(1), true);
        Assert(restored.CompletedTables == 0 && restored.Rows == 5 && restored.Eta == null, "Restoring never completes interrupted table or invents ETA");
    }
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
    { return new MssqlExportRequest(Guid.NewGuid(), "fixture", schema.Tables.Select(x => x.Key).ToArray(), mode, false, target, MssqlExportArtifact.CaptureDestination(target, CancellationToken.None), mirror, mode != MssqlExportMode.SchemaOnly); }
    private sealed class WorkerProgress : IProgress<MssqlExportProgress>
    { private readonly Action<MssqlExportProgress> action; internal WorkerProgress(Action<MssqlExportProgress> action) { this.action = action; } public void Report(MssqlExportProgress p) { action(p); } }
    private sealed class WorkerDispose : IDisposable
    { private readonly Action action; internal WorkerDispose(Action action) { this.action = action; } public void Dispose() { action(); } }
    private sealed class WorkerSource : IMssqlExportSource
    {
        private readonly MssqlExportSchema schema; private readonly List<string> calls; private readonly Action disposed; private bool closed;
        internal string Fault; internal CancellationTokenSource Cancel; internal bool ThrowFinish;
        internal Action DuringFinish; internal bool FailReaderRead, FailReaderDispose, CancelOnReaderRead, CancelOnReaderDispose; internal MssqlRowReader LastReader;
        internal WorkerSource(MssqlExportSchema schema, List<string> calls, Action disposed = null) { this.schema = schema; this.calls = calls; this.disposed = disposed; calls.Add("source.open"); }
        public MssqlExportCatalog ReadCatalog(string db, CancellationToken c) { c.ThrowIfCancellationRequested(); return new MssqlExportCatalog(schema.Tables, 1, null); }
        public MssqlExportPreflight Inspect(MssqlExportRequest r, CancellationToken c) { calls.Add("source.inspect"); return new MssqlExportPreflight(schema, Fault == "inspect" ? new[] { new MssqlExportIssue("Denied", null, null, "denied") } : null); }
        public MssqlExportSchema BeginRead(MssqlExportRequest r, Action<MssqlExportStage> report, CancellationToken c) { calls.Add("source.begin"); if (Fault == "begin") throw new IOException("snapshot failed"); return schema; }
        public MssqlRowReader OpenRows(MssqlTableDefinition t, CancellationToken c) {
            calls.Add("source.rows"); if (Fault == "rows") throw new IOException("DB disconnected private connection-string");
            if (Fault == "cancel-read") { Cancel.Cancel(); c.ThrowIfCancellationRequested(); }
            var data = new DataTable(); data.Columns.Add("id", typeof(int)); data.Rows.Add(1); data.Rows.Add(2); data.Rows.Add(3);
            System.Data.Common.DbDataReader reader = FailReaderRead ? (System.Data.Common.DbDataReader)new WorkerReadFailure(() => { if(CancelOnReaderRead) { Cancel.Cancel(); c.ThrowIfCancellationRequested(); } }) : data.CreateDataReader();
            LastReader = new MssqlRowReader(reader, new WorkerDispose(() => { Assert(reader.IsClosed, "Reader closes before command"); calls.Add("command.dispose"); if(CancelOnReaderDispose) Cancel.Cancel(); if(FailReaderDispose) throw new InvalidOperationException("private cleanup"); }));
            return LastReader;
        }
        public void CompleteRead(MssqlExportSchema s, CancellationToken c) { calls.Add("source.complete"); if (Fault == "complete") throw new InvalidOperationException("schema fingerprint changed"); }
        internal MssqlSnapshotCleanupResult Cleanup = new MssqlSnapshotCleanupResult(MssqlSnapshotCleanupState.NotRequired, null, null, null);
        public MssqlSnapshotCleanupResult FinishRead(Action<MssqlExportStage> report) { calls.Add("restore"); DuringFinish?.Invoke(); if (ThrowFinish) throw new IOException("private cleanup"); report?.Invoke(MssqlExportStage.RestoringSnapshot); return Cleanup; }
        public void Dispose() { if (closed) return; closed = true; calls.Add("source.dispose"); disposed?.Invoke(); }
    }
    private sealed class WorkerReadFailure : System.Data.Common.DbDataReader
    {
        private readonly Action beforeRead; private bool closed;
        internal WorkerReadFailure(Action beforeRead) { this.beforeRead = beforeRead; }
        public override bool Read() { beforeRead(); throw new IOException("private provider read"); }
        public override void Close() { closed = true; }
        public override bool IsClosed => closed;
        public override int FieldCount => 1;
        public override bool HasRows => true;
        public override int Depth => 0;
        public override int RecordsAffected => -1;
        public override bool NextResult() => false;
        public override object this[int i] => throw new NotSupportedException();
        public override object this[string name] => throw new NotSupportedException();
        public override string GetName(int i) => throw new NotSupportedException();
        public override int GetOrdinal(string name) => throw new NotSupportedException();
        public override string GetDataTypeName(int i) => throw new NotSupportedException();
        public override Type GetFieldType(int i) => throw new NotSupportedException();
        public override object GetValue(int i) => throw new NotSupportedException();
        public override int GetValues(object[] values) => throw new NotSupportedException();
        public override bool IsDBNull(int i) => throw new NotSupportedException();
        public override bool GetBoolean(int i) => throw new NotSupportedException();
        public override byte GetByte(int i) => throw new NotSupportedException();
        public override long GetBytes(int i,long offset,byte[] buffer,int start,int count) => throw new NotSupportedException();
        public override char GetChar(int i) => throw new NotSupportedException();
        public override long GetChars(int i,long offset,char[] buffer,int start,int count) => throw new NotSupportedException();
        public override Guid GetGuid(int i) => throw new NotSupportedException();
        public override short GetInt16(int i) => throw new NotSupportedException();
        public override int GetInt32(int i) => throw new NotSupportedException();
        public override long GetInt64(int i) => throw new NotSupportedException();
        public override float GetFloat(int i) => throw new NotSupportedException();
        public override double GetDouble(int i) => throw new NotSupportedException();
        public override string GetString(int i) => throw new NotSupportedException();
        public override decimal GetDecimal(int i) => throw new NotSupportedException();
        public override DateTime GetDateTime(int i) => throw new NotSupportedException();
        public override System.Collections.IEnumerator GetEnumerator() => throw new NotSupportedException();
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
