using System;
using System.IO;
using System.Text;
using mySQLPunk.lib.MssqlImport;
public static partial class SmokeTests
{
    // Breaks caught: overwriting a prior job log, losing provider details, logging whole SQL, or buffering start/terminal records.
    private static void MssqlImportLogRetainsAllErrors()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mySQLPunk", "ImportTestFixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var utc = new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc);
        var request = new MssqlImportRequest(ImportProgressJob, @"C:\fixture\large.sql", "fixture", true);
        var scan = new MssqlImportScan(4478414151L, 2147483650L, 20, "UTF-8", "abcd", new MssqlImportScriptInfo(MssqlImportScriptKind.Generic, null, null, null, null, false));
        var target = new MssqlImportTarget("fixture-server", "fixture", 7, utc, "15.0", true, false, null);
        string firstPath = null, secondPath = null;
        try {
            using (var log = new MssqlImportLog(ImportProgressJob, dir, utcNow: () => utc)) {
                firstPath = log.Path;
                log.WriteStart(request, scan, target);
                Assert(new FileInfo(firstPath).Length > 0, "Start is flushed before a caller can execute SQL.");
                for (int i = 0; i < 1283; i++) {
                    string message = i == 0 ? "large=" + new string('z', 70000) + ";message-end" : "event=" + i + ";羽山";
                    var details = new[] { new MssqlImportSqlError(50000, 1, 16, 2, message), new MssqlImportSqlError(547, 2, 16, 9, "detail=" + i) };
                    var error = new MssqlImportBatchException(details);
                    details[0] = new MssqlImportSqlError(0, 0, 0, 0, "mutated provider buffer");
                    log.WriteError(new MssqlImportBatch(2147483649L + i, 3000000000L, 3000000010L, 4000000000L, 4000000009L, "SELECT confidential-entire-sql"), error,
                        "Skip;TransactionCount=0;XactState=0;DatabaseId=7");
                    log.Flush();
                }
                IMssqlImportLog contract = log;
                Assert(contract.Tail.TotalErrors == 1283 && contract.Tail.Snapshot().Length <= 32768, "Log factory interface exposes bounded UI history independently of disk history.");
                log.WriteEnd(new MssqlImportResult(ImportProgressJob, MssqlImportStatus.CompletedWithErrors, 4478414151L,
                    successfulBytes: 3000000000L, skippedBytes: 1478414151L, successfulBatches: 2147483649L, failedBatches: 1283,
                    lastAcknowledgedBatch: 2147483650L, logPath: firstPath, cleanup: new MssqlImportCleanup(true, true, true, null),
                    validationSummary: "Batch execution complete; skipped failures prevent full validation.", totalErrors: 1283, elapsed: TimeSpan.FromSeconds(42)));
                string text;
                using (var file = new FileStream(firstPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    Assert(file.ReadByte() == 0x53, "New logs use UTF8 without an inserted BOM.");
                    file.Position = 0;
                    using (var reader = new StreamReader(file, new UTF8Encoding(false, true), false)) text = reader.ReadToEnd();
                }
                Assert(text.Contains(new string('z', 70000) + ";message-end"), "One oversized provider message is complete on disk.");
                for (int i = 0; i < 1283; i++) Assert(text.Contains("detail=" + i + "\r\n"), "Every provider detail reaches disk: " + i);
                Assert(text.Contains("2147483649") && text.Contains("3000000000") && text.Contains("4000000000"), "Batch/byte/line coordinates retain Int64 values.");
                Assert(text.Contains("50000") && text.Contains("547") && text.Contains("4000000001"), "All error codes and translated source lines are logged.");
                Assert(text.Contains(request.SourcePath) && text.Contains(ImportProgressJob.ToString()) && text.Contains(firstPath), "Start identifies source/job/real log path.");
                Assert(!text.Contains("confidential-entire-sql"), "Logging does not add SQL batch text.");
                Assert(text.Contains("event=1282;羽山") && !text.Contains("mutated provider buffer"), "UTF8 messages and immutable current-command detail copies survive logging.");
                Assert(text.Contains("CompletedWithErrors") && text.Contains("TransactionCleanupConfirmed=True"), "Terminal log preserves partial status and confirmed cleanup facts.");
            }
            using (var second = new MssqlImportLog(ImportProgressJob, dir, utcNow: () => utc)) {
                secondPath = second.Path;
                Assert(secondPath != firstPath && File.Exists(firstPath) && File.Exists(secondPath), "Same job/clock still uses distinct CreateNew files.");
            }
        } finally {
            if (firstPath != null && File.Exists(firstPath)) File.Delete(firstPath);
            if (secondPath != null && File.Exists(secondPath)) File.Delete(secondPath);
            Directory.Delete(dir);
        }
    }
    // Breaks caught: using scanned/current-batch bytes, clock time alone, or every callback as a confirmed sample.
    private static readonly Guid ImportProgressJob = new Guid("10000000-0000-0000-0000-000000000001");
    private static MssqlImportProgress ImportProgress(long success, long skipped = 0, long total = 1000,
        MssqlImportStage stage = MssqlImportStage.Executing, MssqlImportPhase phase = MssqlImportPhase.Data,
        long scanned = 0, long batch = 0, long end = 0)
    {
        return new MssqlImportProgress(ImportProgressJob, total, stage, scannedBytes: scanned,
            successfulBytes: success, skippedBytes: skipped, batchNumber: batch, endByte: end, phase: phase);
    }
    private static void MssqlImportProgressEstimatesConfirmedBytes()
    {
        var clock = new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc);
        var estimator = new MssqlImportProgressEstimator();
        estimator.Observe(ImportProgress(100), clock);
        estimator.Observe(ImportProgress(200), clock.AddSeconds(1));
        Assert(!estimator.Estimate(clock.AddSeconds(1)).IsReliable, "Two confirmations do not estimate.");
        estimator.Observe(ImportProgress(300), clock.AddSeconds(2));
        var known = estimator.Estimate(clock.AddSeconds(2));
        Assert(known.IsReliable && Math.Abs(known.Remaining.Value.TotalSeconds - 7) < 0.01,
            "Three confirmations over two seconds measure 100 bytes/sec and seven seconds remain.");
        Assert(known.EstimatedFinish == clock.AddSeconds(9), "Estimated finish is an absolute UTC estimate.");
        estimator.Observe(ImportProgress(300, scanned: 1000, batch: 2147483649, end: 1000), clock.AddSeconds(6));
        Assert(estimator.Estimate(clock.AddSeconds(6)).IsReliable, "Read-ahead/inflight coordinate changes do not invent throughput samples.");
        Assert(!estimator.Estimate(clock.AddSeconds(7)).IsReliable, "Five seconds since confirmed advancement becomes unknown.");
        foreach (MssqlImportPhase phase in new[] { MssqlImportPhase.Indexes, MssqlImportPhase.Constraints }) {
            estimator.Observe(ImportProgress(400, phase: phase), clock.AddSeconds(8));
            Assert(!estimator.Estimate(clock.AddSeconds(8)).IsReliable, "Indexes and constraints have no byte ETA.");
        }
        foreach (MssqlImportStage stage in new[] { MssqlImportStage.Verifying, MssqlImportStage.Stopping, MssqlImportStage.Finished }) {
            estimator.Observe(ImportProgress(1000, stage: stage), clock.AddSeconds(9));
            Assert(!estimator.Estimate(clock.AddSeconds(9)).IsReliable, "Verification/stopping/finished do not present file-byte ETA.");
        }
        var skipped = new MssqlImportProgressEstimator();
        skipped.Observe(ImportProgress(100, 100), clock);
        skipped.Observe(ImportProgress(200, 200), clock.AddSeconds(1));
        skipped.Observe(ImportProgress(300, 300), clock.AddSeconds(2));
        Assert(Math.Abs(skipped.Estimate(clock.AddSeconds(2)).Remaining.Value.TotalSeconds - 2) < 0.01,
            "Processed bytes include failed/skipped coverage, independently of successful bytes.");
        var big = new MssqlImportProgressEstimator();
        big.Observe(ImportProgress(3000000000L, total: 6000000000L), clock);
        big.Observe(ImportProgress(3500000000L, total: 6000000000L), clock.AddSeconds(1));
        big.Observe(ImportProgress(4000000000L, total: 6000000000L), clock.AddSeconds(2));
        Assert(big.Estimate(clock.AddSeconds(2)).Remaining.Value.TotalSeconds == 4, "Int64 byte progress cannot wrap at 2GiB.");
    }
    // Breaks caught: retaining every error, capping only event count, or decrementing the total on eviction.
    private static void MssqlImportLogTailIsBounded()
    {
        foreach (int count in new[] { 1283, 12830 })
        {
            var tail = new MssqlImportLogTail();
            for (int i = 0; i < count; i++) tail.Add("event=" + i + ";" + new string('x', 1000) + "\r\n");
            string snapshot = tail.Snapshot();
            Assert(tail.TotalErrors == count, "Total SQL error events survives UI eviction.");
            Assert(snapshot.Length <= 32768, "UTF16 UI text is at most 64KiB for either workload.");
            Assert(snapshot.Contains("event=" + (count - 1) + ";"), "The latest error is visible.");
            Assert(!snapshot.Contains("event=0;"), "Old error records are evicted.");
        }
        var small = new MssqlImportLogTail();
        for (int i = 0; i < 1283; i++) small.Add("event=" + i + ";\r\n");
        Assert(!small.Snapshot().Contains("event=1182;") && small.Snapshot().Contains("event=1183;"), "Only the newest 100 short records remain.");
        var oversized = new MssqlImportLogTail();
        oversized.Add("HEAD" + new string('x', 100000) + "LAST DETAIL");
        Assert(oversized.Snapshot().Length <= 32768 && oversized.Snapshot().EndsWith("LAST DETAIL"), "One large event is truncated only in UI and retains its latest detail.");
        Assert(oversized.TotalErrors == 1, "One oversized event still counts once.");
    }

    // Breaks caught: swallowed persistence errors or a writer flush failure preventing the owned stream from closing.
    private sealed class ImportFaultStream : MemoryStream
    {
        internal bool FailWrite, FailFlush, FailDispose, Released;
        public override void Write(byte[] buffer, int offset, int count)
        { if (FailWrite) throw new IOException("injected append failure"); base.Write(buffer, offset, count); }
        public override void Flush()
        { if (FailFlush) throw new IOException("injected flush failure"); base.Flush(); }
        protected override void Dispose(bool disposing)
        { Released = true; base.Dispose(disposing); if (FailDispose) throw new IOException("injected disposal failure"); }
    }
    private static void ImportExpectIo(Action action, string reason)
    {
        bool failed = false;
        try { action(); } catch (IOException) { failed = true; }
        Assert(failed, reason);
    }
    private static void MssqlImportLogFaultsPropagate()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mySQLPunk", "ImportTestFixtures", Guid.NewGuid().ToString("N"));
        var utc = new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc);
        var request = new MssqlImportRequest(ImportProgressJob, null, "fixture");
        var scan = new MssqlImportScan(10, 1, 10, "UTF-8", "abcd", null);
        var target = new MssqlImportTarget("fixture", "fixture", 7, utc, "15", true, false, null);
        var result = new MssqlImportResult(ImportProgressJob, MssqlImportStatus.CleanupUncertain, 10,
            cleanup: new MssqlImportCleanup(false, false, false, "SessionReleaseUnconfirmed"));
        try {
            ImportExpectIo(() => new MssqlImportLog(ImportProgressJob, dir, p => { throw new IOException("injected create failure"); }), "Create failure reaches caller.");
            foreach (string fault in new[] { "start-write", "start-flush", "append", "flush", "end", "dispose-flush", "dispose-stream" }) {
                var stream = new ImportFaultStream();
                var log = new MssqlImportLog(ImportProgressJob, dir, p => stream, () => utc);
                try {
                    if (fault == "start-write" || fault == "start-flush") {
                        stream.FailWrite = fault == "start-write"; stream.FailFlush = fault == "start-flush";
                        ImportExpectIo(() => log.WriteStart(request, scan, target), "Start persistence fails before SQL can begin: " + fault);
                    } else {
                        log.WriteStart(request, scan, target);
                        if (fault == "append") {
                            stream.FailWrite = true;
                            ImportExpectIo(() => log.WriteError(new MssqlImportBatch(1, 0, 10, 1, 1, "private SQL"),
                                new MssqlImportBatchException(new[] { new MssqlImportSqlError(547, 1, 16, 1, new string('x', 70000) + "latest-detail") }), "Stop"), "Append failure reaches caller.");
                            Assert(log.Tail.TotalErrors == 1 && log.Tail.Snapshot().Contains("latest-detail"), "Current error remains in bounded UI tail when disk append fails.");
                        } else if (fault == "flush") {
                            stream.FailFlush = true;
                            ImportExpectIo(log.Flush, "Flush failure reaches actual caller; scheduling assertion belongs to Task4 service.");
                        } else if (fault == "end") {
                            stream.FailFlush = true;
                            ImportExpectIo(() => log.WriteEnd(result), "Terminal persistence failure cannot appear successful.");
                        } else {
                            stream.FailFlush = fault == "dispose-flush"; stream.FailDispose = fault == "dispose-stream";
                            ImportExpectIo(log.Dispose, "Dispose failure remains visible: " + fault);
                            Assert(stream.Released, "Raw stream is released even if StreamWriter disposal fails.");
                            log.Dispose();
                        }
                    }
                } finally { stream.FailWrite = stream.FailFlush = stream.FailDispose = false; log.Dispose(); }
                Assert(stream.Released, "Every fault path releases the owned stream.");
            }
        } finally { Directory.Delete(dir); }
    }

    // Breaks caught: unbounded sample history, stale old rates, discarded throttled confirmations, or missing final callback.
    private static void MssqlImportProgressWindowAndThrottle()
    {
        var utc = new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc);
        var warm = new MssqlImportProgressEstimator();
        warm.Observe(ImportProgress(100), utc);
        warm.Observe(ImportProgress(200), utc.AddMilliseconds(200));
        warm.Observe(ImportProgress(300), utc.AddMilliseconds(400));
        Assert(!warm.Estimate(utc.AddMilliseconds(400)).IsReliable, "Three rapid confirmations still require two seconds of observation.");
        var volatileRate = new MssqlImportProgressEstimator();
        volatileRate.Observe(ImportProgress(1, total: 100000), utc);
        volatileRate.Observe(ImportProgress(2, total: 100000), utc.AddSeconds(1));
        volatileRate.Observe(ImportProgress(3, total: 100000), utc.AddSeconds(2));
        volatileRate.Observe(ImportProgress(10003, total: 100000), utc.AddSeconds(3));
        Assert(!volatileRate.Estimate(utc.AddSeconds(3)).IsReliable, "Positive interval-rate coefficient of variation above one suppresses ETA.");
        var window = new MssqlImportProgressEstimator();
        window.Observe(ImportProgress(100, total: 100000), utc);
        window.Observe(ImportProgress(200, total: 100000), utc.AddSeconds(1));
        window.Observe(ImportProgress(300, total: 100000), utc.AddSeconds(2));
        for (int second = 3; second <= 15; second++) window.Observe(ImportProgress(300 + (second - 2) * 1000, total: 100000), utc.AddSeconds(second));
        Assert(Math.Abs(window.Estimate(utc.AddSeconds(15)).Remaining.Value.TotalSeconds - 86.7) < 0.01,
            "Rates older than the approximately ten-second window no longer dominate.");
        var dense = new MssqlImportProgressEstimator();
        for (int ms = 0; ms <= 3000; ms++) dense.Observe(ImportProgress(ms + 1, total: 100000), utc.AddMilliseconds(ms));
        dense.Observe(ImportProgress(3100, total: 100000), utc.AddMilliseconds(3100));
        Assert(dense.Estimate(utc.AddMilliseconds(3100)).IsReliable, "Dense callbacks are coalesced while latest confirmed totals are retained.");
        Assert(dense.Estimate(utc.AddMilliseconds(8000)).IsReliable, "A coalesced last confirmation refreshes the five-second stall boundary.");
        Assert(!dense.Estimate(utc.AddMilliseconds(8100)).IsReliable, "Exactly five seconds since that confirmation becomes unknown.");
        var gate = new MssqlImportProgressEstimator();
        Assert(gate.ShouldReport(ImportProgress(0, stage: MssqlImportStage.Scanning), utc), "First callback is visible.");
        Assert(!gate.ShouldReport(ImportProgress(0, stage: MssqlImportStage.Scanning), utc.AddMilliseconds(199)), "Same-stage callback is throttled below 200ms.");
        Assert(gate.ShouldReport(ImportProgress(0, stage: MssqlImportStage.Scanning), utc.AddMilliseconds(200)), "The 200ms boundary reports.");
        Assert(gate.ShouldReport(ImportProgress(100), utc.AddMilliseconds(201)), "A stage change is visible immediately.");
        Assert(gate.ShouldReport(ImportProgress(1000, stage: MssqlImportStage.Finished), utc.AddMilliseconds(202)), "Final status cannot be lost to throttling.");
        var readAhead = new MssqlImportProgressEstimator();
        for (int second = 0; second < 3; second++) readAhead.Observe(ImportProgress(0, scanned: 1000, batch: second + 1, end: 1000), utc.AddSeconds(second));
        Assert(!readAhead.Estimate(utc.AddSeconds(2)).IsReliable, "Read-ahead and currently executing batches contribute no confirmed work.");
        var capped = new MssqlImportProgress(ImportProgressJob, 1000, MssqlImportStage.Finished,
            successfulBytes: 400, skippedBytes: 600, recentErrorText: new string('x', 70000), logPath: @"C:\actual\import.log", totalErrors: 1283);
        Assert(capped.ProcessedBytes == 1000 && capped.SuccessfulBytes == 400 && capped.RecentErrorText.Length <= 32768 && capped.LogTruncated,
            "Progress snapshots preserve separate successful coverage and bound a caller-supplied UI tail.");
        Assert(capped.LogPath == @"C:\actual\import.log" && capped.TotalErrors == 1283, "Full path and lifetime error count accompany the bounded snapshot.");
    }

}

// These tests drive the actual importer service with real bounded input/log and no database.
public static partial class SmokeTests
{
    private sealed class ImportWorkerSession : IMssqlImportSession
    {
        internal int Executes, States, Targets, Lobs, Counts, Cleanups, Disposes, Effects;
        internal Action<MssqlImportBatch, System.Threading.CancellationToken> Execute;
        internal Func<MssqlImportSessionState> State;
        internal Func<MssqlImportTarget> Target;
        internal Func<Guid, MssqlImportLobState> Lob;
        internal Func<MssqlImportCleanup> Finish;
        internal Action<System.Threading.CancellationToken> Counting;
        internal MssqlImportCounts CountValue = new MssqlImportCounts(1, 0);
        public MssqlImportTarget ReadTarget(System.Threading.CancellationToken token)
        { token.ThrowIfCancellationRequested(); Targets++; return Target == null ? WorkerTarget() : Target(); }
        public MssqlImportSessionState ReadState(System.Threading.CancellationToken token)
        { token.ThrowIfCancellationRequested(); States++; return State == null ? WorkerState() : State(); }
        public MssqlImportLobState ReadLobState(Guid job, System.Threading.CancellationToken token)
        { token.ThrowIfCancellationRequested(); Lobs++; return Lob == null ? new MssqlImportLobState(true, 1, 1, 1, true, false) : Lob(job); }
        public void ExecuteBatch(MssqlImportBatch batch, System.Threading.CancellationToken token)
        { token.ThrowIfCancellationRequested(); Executes++; if (Execute != null) Execute(batch, token); }
        public MssqlImportCounts CountTargetRows(Action<long, long> progress, System.Threading.CancellationToken token)
        { Counts++; if (Counting != null) Counting(token); token.ThrowIfCancellationRequested(); progress(CountValue.Tables, CountValue.Rows); return CountValue; }
        public void CancelActiveCommand() { }
        public MssqlImportCleanup Cleanup()
        { Cleanups++; return Finish == null ? new MssqlImportCleanup(true, true, true, null) : Finish(); }
        public void Dispose() { Disposes++; }
    }
    private sealed class ImportWorkerFixture : IDisposable
    {
        internal readonly string DirectoryPath, Source;
        internal readonly ImportWorkerSession Session = new ImportWorkerSession();
        internal readonly System.Collections.Generic.List<MssqlImportProgress> Progress = new System.Collections.Generic.List<MssqlImportProgress>();
        internal MssqlImportService Service;
        internal MssqlImportRequest Request;
        internal int Opens;
        internal Func<Guid, IMssqlImportLog> LogFactory;
        internal ImportWorkerFixture(string sql = "SELECT 1;\r\nGO\r\nSELECT 2;\r\nGO\r\n", bool skip = false)
        {
            DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mySQLPunk", "ImportTestFixtures", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath); Source = Path.Combine(DirectoryPath, "worker.sql");
            File.WriteAllText(Source, sql, new UTF8Encoding(false, true));
            Request = new MssqlImportRequest(Guid.NewGuid(), Source, "fixture", skip);
            Service = new MssqlImportService(() => { Opens++; return Opens == 1 ? new ImportWorkerSession() : Session; },
                job => LogFactory == null ? new MssqlImportLog(job, DirectoryPath) : LogFactory(job));
        }
        internal MssqlImportResult Run(System.Threading.CancellationToken token = default(System.Threading.CancellationToken))
        { using (var preflight = Service.Preflight(Request, Progress.Add, token)) { Assert(preflight.Passed, "Valid fixture preflight passes."); return Service.Run(Request, preflight, Progress.Add, token); } }
        public void Dispose()
        { foreach (string file in Directory.GetFiles(DirectoryPath)) File.Delete(file); Directory.Delete(DirectoryPath); }
    }
    private static MssqlImportTarget WorkerTarget(int id = 7, bool writable = true, bool visible = true, long? objects = 0, bool? create = true, DateTime? date = null)
    { return new MssqlImportTarget("fixture-server", "fixture", id, date ?? new DateTime(2026, 10, 13), "15.0", writable, visible, objects, create); }
    private static MssqlImportSessionState WorkerState(int transaction = 0, int xact = 0, int id = 7, bool open = true)
    { return new MssqlImportSessionState(open, id, transaction, xact); }
    private static MssqlImportBatchException WorkerSqlError(int number = 547, int severity = 16, bool uncertain = false)
    { return new MssqlImportBatchException(new[] { new MssqlImportSqlError(number, 1, severity, 1, "完整 detail 一"), new MssqlImportSqlError(50000, 2, 16, 2, "detail two") }, uncertain); }
    private static readonly Guid WorkerNativeJob = new Guid("10000000-0000-0000-0000-000000000009");
    private static string WorkerNative(string body, bool schema = false, long tables = 0, long rows = 0)
    { return "-- Source read mode: READ COMMITTED\r\n" + (schema ? "-- Restore into an empty, compatible target selected by the caller; existing objects fail.\r\n" : "") + body + "-- mysqlpunk-export-complete job=" + WorkerNativeJob.ToString("N") + " tables=" + tables + " rows=" + rows + "\r\n"; }
    private static string WorkerLobInit()
    { return "CREATE TABLE #mp_" + WorkerNativeJob.ToString("N") + "_s ([Sequence] bigint);\r\nGO\r\n"; }

    private static void MssqlImportWorkerStopAndSkip()
    {
        using (var f = new ImportWorkerFixture()) {
            f.Session.Execute = (b, t) => { f.Session.Effects++; throw WorkerSqlError(); };
            var r = f.Run();
            Assert(r.Status == MssqlImportStatus.Failed && f.Session.Executes == 1 && f.Opens == 2, "DefaultStopsAtFirstError: no next batch, reconnect or replay.");
            Assert(r.SuccessfulBatches == 0 && r.FailedBatches == 1 && r.ProcessedBytes == 0 && f.Session.Effects == 1, "PartialBatchEffectsAreNotReportedAsRows or successful coverage.");
            string log = File.ReadAllText(r.LogPath);
            Assert(log.Contains("完整 detail 一") && log.Contains("detail two"), "All provider SQL details survive default stop.");
        }
        using (var f = new ImportWorkerFixture(skip: true)) {
            f.Session.Execute = (b, t) => { if (b.Number == 1) throw WorkerSqlError(); };
            var r = f.Run();
            Assert(r.Status == MssqlImportStatus.CompletedWithErrors && f.Session.Executes == 2 && r.SuccessfulBatches == 1 && r.FailedBatches == 1,
                "SkipContinuesOnlySafeBatch executes actual next SQL.");
            Assert(r.SkippedBytes > 0 && r.ProcessedBytes == new FileInfo(f.Source).Length && r.LastAcknowledgedBatch == 2 && f.Session.Counts == 0, "Skipped EOF coverage and summary remain distinct.");
        }
        foreach (int code in new[] { -2, 0, 11, 20, 64, 233, 10053, 10054, 10060 }) using (var f = new ImportWorkerFixture(skip: true)) {
            f.Session.Execute = (b, t) => { throw WorkerSqlError(code); };
            Assert(f.Run().Status == MssqlImportStatus.Failed && f.Session.Executes == 1, "Never skip transport/timeout/attention code " + code);
        }
        foreach (int severity in new[] { 10, 17, 20 }) using (var f = new ImportWorkerFixture(skip: true)) {
            f.Session.Execute = (b, t) => { throw WorkerSqlError(severity: severity); };
            Assert(f.Run().Status == MssqlImportStatus.Failed && f.Session.Executes == 1, "Every detail must be ordinary severity.");
        }
        using (var f = new ImportWorkerFixture(skip: true)) {
            f.Session.Execute = (b, t) => { throw WorkerSqlError(uncertain: true); };
            var r = f.Run(); Assert(r.UncertainBatch == 1 && f.Session.Executes == 1, "Unknown outcome is separate and never replayed.");
        }
        foreach (var unsafeState in new[] { WorkerState(1), WorkerState(0, -1), WorkerState(id: 8), WorkerState(open: false) }) using (var f = new ImportWorkerFixture(skip: true)) {
            f.Session.State = () => f.Session.Executes == 0 ? WorkerState() : unsafeState;
            f.Session.Execute = (b, t) => { throw WorkerSqlError(); };
            Assert(f.Run().Status == MssqlImportStatus.Failed && f.Session.Executes == 1, "Unsafe post-state blocks skip.");
        }
    }
    private sealed class WorkerFlushStream : MemoryStream
    {
        internal bool Fail, CancelOnFlush;
        internal System.Threading.CancellationTokenSource Cancellation;
        public override void Flush()
        { if (CancelOnFlush) { CancelOnFlush = false; Cancellation.Cancel(); } if (Fail) throw new IOException("fixture flush fault"); base.Flush(); }
    }
    private static void MssqlImportWorkerFlushAndLogFaults()
    {
        using (var f = new ImportWorkerFixture(skip: true)) {
            var stream = new WorkerFlushStream();
            f.LogFactory = job => new MssqlImportLog(job, f.DirectoryPath, p => stream);
            f.Session.Execute = (b, t) => { stream.Fail = true; throw WorkerSqlError(); };
            var r = f.Run();
            Assert(f.Session.Executes == 1 && r.Status == MssqlImportStatus.Failed && r.SkippedBytes == 0 && r.ValidationSummary.Contains("LogIncomplete"), "FlushFailsBeforeSkip actual log stream prevents NextBatchCalls.");
        }
        using (var f = new ImportWorkerFixture()) {
            var stream = new WorkerFlushStream(); f.LogFactory = job => new MssqlImportLog(job, f.DirectoryPath, p => stream);
            f.Session.Counting = t => { stream.Fail = true; };
            f.Session.Execute = (b, t) => { if (b.Number == 2) stream.Fail = true; };
            var r = f.Run(); Assert(r.Status == MssqlImportStatus.Failed && r.ValidationSummary.Contains("LogIncomplete"), "Terminal logging failure cannot claim successful saved log.");
        }
    }
    private static void MssqlImportWorkerPreflightOwnership()
    {
        using (var f = new ImportWorkerFixture("SELECT '\u0000';")) {
            using (var p = f.Service.Preflight(f.Request, null, default(System.Threading.CancellationToken))) {
                Assert(!p.Passed && f.Opens == 0 && p.Issues[0].Code == "EmbeddedNul" && p.Issues[0].Line > 0, "Parser preflight failure precedes any session and exposes safe coordinates.");
            }
        }
        using (var f = new ImportWorkerFixture()) {
            using (var p = f.Service.Preflight(f.Request, null, default(System.Threading.CancellationToken))) {
                Assert(p.Passed && File.Exists(p.LogPath), "Preflight retains real established log.");
                var wrong = new MssqlImportRequest(f.Request.JobId, f.Source, "fixture", true);
                Assert(f.Service.Run(wrong, p, null, default(System.Threading.CancellationToken)).Status == MssqlImportStatus.Failed && f.Session.Executes == 0, "Changed skip policy invalidates preflight.");
                var r = f.Service.Run(f.Request, p, null, default(System.Threading.CancellationToken));
                Assert(r.Status == MssqlImportStatus.Completed, "Matching request claims once.");
                int calls = f.Session.Executes;
                Assert(f.Service.Run(f.Request, p, null, default(System.Threading.CancellationToken)).Status == MssqlImportStatus.Failed && f.Session.Executes == calls && f.Opens == 2, "ReusedPreflightIsRejected before new session.");
            }
        }
        using (var f = new ImportWorkerFixture()) {
            f.Session.Target = () => WorkerTarget(date: new DateTime(2026, 10, 14));
            Assert(f.Run().Status == MssqlImportStatus.Failed && f.Session.Executes == 0, "Recreated target fails identity before first write.");
        }
        using (var f = new ImportWorkerFixture()) {
            var p = f.Service.Preflight(f.Request, null, default(System.Threading.CancellationToken)); p.Dispose(); p.Dispose();
            Assert(f.Service.Run(f.Request, p, null, default(System.Threading.CancellationToken)).Status == MssqlImportStatus.Failed && f.Opens == 1, "Disposed preflight cannot execute.");
        }
        using (var f = new ImportWorkerFixture()) {
            var missing = new MssqlImportRequest(Guid.NewGuid(), f.Source, " ");
            using (var p = f.Service.Preflight(missing, null, default(System.Threading.CancellationToken))) Assert(!p.Passed && f.Opens == 0, "Missing target rejects pre-IO/session.");
        }
    }
    private static void MssqlImportWorkerEofAndNative()
    {
        using (var f = new ImportWorkerFixture(skip: true)) {
            f.Session.Execute = (b, t) => { if (b.Number == 1) throw WorkerSqlError(); };
            f.Session.State = () => f.Session.States >= 5 ? WorkerState(1, 1) : WorkerState();
            Assert(f.Run().Status == MssqlImportStatus.Failed && f.Session.Executes == 2, "SkippedRunStillChecksEofState.");
        }
        using (var f = new ImportWorkerFixture()) {
            f.Session.State = () => WorkerState(f.Session.Executes == 1 ? 1 : 0, f.Session.Executes == 1 ? 1 : 0);
            Assert(f.Run().Status == MssqlImportStatus.Completed, "Successful generic transaction may span GO then commit.");
        }
        string lobSql = WorkerNative(WorkerLobInit() + "SELECT 2;\r\nGO\r\n");
        foreach (var state in new[] { new MssqlImportLobState(true, 1, 2, 1, false, false), new MssqlImportLobState(false, 0, 0, 0, false, false), new MssqlImportLobState(true, 2, 1, 1, true, false), new MssqlImportLobState(true, 1, 1, 1, true, true) }) using (var f = new ImportWorkerFixture(lobSql)) {
            f.Session.Lob = job => { Assert(job == WorkerNativeJob, "LOB query receives validated exact job."); return state; };
            Assert(f.Run().Status == MssqlImportStatus.Failed && f.Session.Counts == 0, "NativeData missing/duplicate/failed/pending EOF cannot complete.");
        }
        using (var f = new ImportWorkerFixture(lobSql, true)) {
            f.Session.Execute = (b, t) => { throw WorkerSqlError(); };
            Assert(f.Run().Status == MssqlImportStatus.Failed && f.Session.Executes == 1, "Native init error fatal even transaction0.");
        }
        using (var f = new ImportWorkerFixture(WorkerNative(WorkerLobInit() + "UPDATE #mp_" + WorkerNativeJob.ToString("N") + "_s SET [Sequence]=1;\r\nGO\r\nSELECT 1;\r\nGO\r\n"), true)) {
            f.Session.Execute = (b, t) => { if (b.Number == 2) throw WorkerSqlError(); };
            Assert(f.Run().Status == MssqlImportStatus.Failed && f.Session.Executes == 2, "Native assembly/commit error cannot skip.");
        }
        string nativeSchema = WorkerNative("CREATE TABLE dbo.t(i int);\r\nGO\r\n", true, 1, 0);
        using (var f = new ImportWorkerFixture(nativeSchema)) {
            var r = f.Run(); Assert(r.Status == MssqlImportStatus.Completed && f.Session.Counts == 1 && r.ProcessedBytes == new FileInfo(f.Source).Length, "NativeSchema summary count and trailing footer coverage.");
        }
        using (var f = new ImportWorkerFixture(nativeSchema)) {
            f.Session.CountValue = new MssqlImportCounts(1, 9);
            Assert(f.Run().Status == MssqlImportStatus.Failed, "Footer row mismatch blocks success.");
        }
        foreach (var target in new[] { WorkerTarget(visible: false, objects: null), WorkerTarget(objects: 1), WorkerTarget(create: false) }) using (var f = new ImportWorkerFixture(nativeSchema)) {
            f.Session.Target = () => target;
            Assert(f.Run().Status == MssqlImportStatus.Failed && f.Session.Executes == 0, "Execution target native capabilities rechecked.");
        }
    }
    private static void MssqlImportWorkerCancellationAndCleanup()
    {
        using (var f = new ImportWorkerFixture(skip: true)) using (var stop = new System.Threading.CancellationTokenSource()) {
            f.Session.Execute = (b, t) => { stop.Cancel(); throw WorkerSqlError(uncertain: true); };
            var r = f.Run(stop.Token);
            Assert(r.Status == MssqlImportStatus.Cancelled && f.Session.Executes == 1 && r.UncertainBatch == 1 && r.FailedBatches == 1, "Cancellation retains SQL error details and blocks skip.");
            Assert(File.ReadAllText(r.LogPath).Contains("detail two") && f.Progress.Exists(p => p.Stage == MssqlImportStage.Stopping), "Stopping and SQL details survive cancellation.");
        }
        using (var f = new ImportWorkerFixture()) using (var stop = new System.Threading.CancellationTokenSource()) {
            f.Session.Execute = (b, t) => { stop.Cancel(); t.ThrowIfCancellationRequested(); };
            f.Session.Finish = () => new MssqlImportCleanup(false, true, false, "CommandStillActive");
            Assert(f.Run(stop.Token).Status == MssqlImportStatus.CleanupUncertain && f.Session.Executes == 1, "Unacknowledged command cannot be called cancelled.");
        }
        using (var f = new ImportWorkerFixture(skip: true)) using (var stop = new System.Threading.CancellationTokenSource()) {
            var stream = new WorkerFlushStream { Cancellation = stop };
            f.LogFactory = job => new MssqlImportLog(job, f.DirectoryPath, p => stream);
            f.Session.Execute = (b, t) => { stream.CancelOnFlush = true; throw WorkerSqlError(); };
            Assert(f.Run(stop.Token).Status == MssqlImportStatus.Cancelled && f.Session.Executes == 1, "Cancel during error flush cannot schedule next.");
        }
        using (var f = new ImportWorkerFixture()) {
            f.Session.Execute = (b, t) => { throw WorkerSqlError(); };
            f.Session.Finish = () => { throw new IOException("cleanup fault"); };
            var r = f.Run(); Assert(r.Status == MssqlImportStatus.Failed && r.ValidationSummary.Contains("SqlBatchFailed") && r.ValidationSummary.Contains("Cleanup"), "Original failed result survives cleanup exception.");
        }
        using (var f = new ImportWorkerFixture(WorkerNative("CREATE TABLE dbo.t(i int);\r\nGO\r\n", true, 1))) using (var stop = new System.Threading.CancellationTokenSource()) {
            f.Session.Counting = t => { stop.Cancel(); t.ThrowIfCancellationRequested(); };
            Assert(f.Run(stop.Token).Status == MssqlImportStatus.Cancelled && f.Session.Counts == 1, "Verification COUNT_BIG cancellation is acknowledged after cleanup.");
        }
    }
    private static void MssqlImportWorkerPhaseAndBoundaries()
    {
        string sql = WorkerNative("CREATE TABLE dbo.t(i int);\r\nGO\r\nCREATE NONCLUSTERED INDEX IX ON dbo.t(i);\r\nGO\r\nALTER TABLE dbo.t WITH CHECK ADD CONSTRAINT ck CHECK(i>0);\r\nGO\r\n", true, 1);
        using (var f = new ImportWorkerFixture(sql)) {
            f.Session.Execute = (b, t) => {
                var p = f.Progress[f.Progress.Count - 1];
                if (b.Number == 2) Assert(p.Phase == MssqlImportPhase.Indexes && !p.Remaining.HasValue, "Index phase immediately disables ETA before command.");
                if (b.Number == 3) Assert(p.Phase == MssqlImportPhase.Constraints && !p.Remaining.HasValue, "Constraint phase immediately disables ETA before command.");
                Assert(p.SuccessfulBatches == b.Number - 1, "Read-ahead is not acknowledged progress.");
            };
            Assert(f.Run().Status == MssqlImportStatus.Completed, "Bounded phase lexer preserves SQL execution.");
        }
        using (var f = new ImportWorkerFixture("SELECT '#mp_10000000000000000000000000000009_s CREATE INDEX FOREIGN KEY';\r\nGO\r\n")) {
            using (var p = f.Service.Preflight(f.Request, null, default(System.Threading.CancellationToken))) {
                Assert(p.Scan.Script.Kind == MssqlImportScriptKind.Generic, "String decoys are generic.");
                Assert(f.Service.Run(f.Request, p, value => { throw new InvalidOperationException("observer"); }, default(System.Threading.CancellationToken)).Status == MssqlImportStatus.Completed, "Observer failure cannot alter execution truth.");
            }
        }
    }
}
public static partial class SmokeTests
{
    private static void MssqlImportWorkerAdditionalBoundaries()
    {
        using (var f = new ImportWorkerFixture()) {
            f.Session.Execute = (b, t) => { throw new IOException("unknown transport"); };
            var r = f.Run(); Assert(r.Status == MssqlImportStatus.Failed && r.UncertainBatch == 1 && r.ProcessedBytes == 0, "Non-SQL execution failure is uncertain and unacknowledged.");
        }
        using (var f = new ImportWorkerFixture()) {
            f.Session.State = () => { if (f.Session.Executes != 0) throw WorkerSqlError(uncertain: true); return WorkerState(); };
            var r = f.Run(); Assert(r.Status == MssqlImportStatus.Failed && File.ReadAllText(r.LogPath).Contains("detail two"), "Probe SQL detail also survives worker failure.");
        }
        using (var f = new ImportWorkerFixture()) using (var stop = new System.Threading.CancellationTokenSource()) {
            f.Session.State = () => { if (f.Session.Executes == 1) { stop.Cancel(); stop.Token.ThrowIfCancellationRequested(); } return WorkerState(); };
            var r = f.Run(stop.Token); Assert(r.Status == MssqlImportStatus.Cancelled && f.Session.Executes == 1 && r.SuccessfulBatches == 0, "Cancellation during post-state prevents acknowledgement and next SQL.");
        }
        using (var f = new ImportWorkerFixture()) using (var stop = new System.Threading.CancellationTokenSource()) {
            using (var p = f.Service.Preflight(f.Request, null, stop.Token)) {
                var r = f.Service.Run(f.Request, p, value => { if (value.Stage == MssqlImportStage.Finished) stop.Cancel(); f.Progress.Add(value); }, stop.Token);
                Assert(r.Status == MssqlImportStatus.Completed && f.Progress[f.Progress.Count - 1].Stage == MssqlImportStage.Finished, "Late cancellation cannot publish stopping after terminal callback.");
            }
        }
        using (var f = new ImportWorkerFixture()) {
            f.LogFactory = job => { throw new IOException("log create fault"); };
            using (var p = f.Service.Preflight(f.Request, null, default(System.Threading.CancellationToken))) Assert(!p.Passed && f.Session.Executes == 0 && f.Opens == 1, "Log creation failure blocks execution factory.");
        }
        using (var f = new ImportWorkerFixture()) {
            using (var p = f.Service.Preflight(f.Request, null, default(System.Threading.CancellationToken))) {
                var original = p.Scan;
                // Same real retained input; alter only expected digest through internal immutable construction.
                var input = MssqlImportInput.Open(f.Source, "fixture");
                var log = new MssqlImportLog(f.Request.JobId, f.DirectoryPath); log.WriteStart(f.Request, original, p.Target);
                var badScan = new MssqlImportScan(original.FileBytes, original.BatchCount, original.MaxBatchCodeUnits, original.EncodingName, "mismatched", original.Script);
                using (var changed = new MssqlImportPreflight(f.Service, f.Request, Path.GetFullPath(f.Source), input, badScan, p.Target, log, new MssqlImportIssue[0])) {
                    var r = f.Service.Run(f.Request, changed, null, default(System.Threading.CancellationToken));
                    Assert(r.Status == MssqlImportStatus.Failed && r.ValidationSummary.Contains("DigestMismatch"), "Every EOF compares actual execution digest to retained scan.");
                }
            }
        }
        using (var f = new ImportWorkerFixture()) {
            using (var p = f.Service.Preflight(f.Request, null, default(System.Threading.CancellationToken))) {
                var entered = new System.Threading.ManualResetEventSlim(); var release = new System.Threading.ManualResetEventSlim();
                f.Session.Execute = (b, t) => { if (b.Number == 1) { entered.Set(); Assert(release.Wait(3000), "Fixture release arrives."); } };
                var task = System.Threading.Tasks.Task.Run(() => f.Service.Run(f.Request, p, null, default(System.Threading.CancellationToken)));
                try {
                    Assert(entered.Wait(3000), "Running worker owns first command."); p.Dispose();
                    Assert(f.Service.Run(f.Request, p, null, default(System.Threading.CancellationToken)).Status == MssqlImportStatus.Failed && f.Opens == 2, "Concurrent/stale preflight cannot acquire or dispose running worker.");
                } finally { release.Set(); }
                Assert(task.GetAwaiter().GetResult().Status == MssqlImportStatus.Completed && f.Session.Executes == 2, "Stale dispose leaves retained execution input/log alive.");
                entered.Dispose(); release.Dispose();
            }
        }
        using (var f = new ImportWorkerFixture()) using (var stop = new System.Threading.CancellationTokenSource()) {
            var entered = new System.Threading.ManualResetEventSlim(); var release = new System.Threading.ManualResetEventSlim(); var stopping = new System.Threading.ManualResetEventSlim();
            f.Session.Execute = (b, t) => { entered.Set(); Assert(release.Wait(3000), "Blocked provider fixture releases."); t.ThrowIfCancellationRequested(); };
            using (var p = f.Service.Preflight(f.Request, null, stop.Token)) {
                var task = System.Threading.Tasks.Task.Run(() => f.Service.Run(f.Request, p, value => { if (value.Stage == MssqlImportStage.Stopping) stopping.Set(); }, stop.Token));
                try { Assert(entered.Wait(3000), "Provider fixture enters."); stop.Cancel(); Assert(stopping.Wait(1000) && !task.IsCompleted && f.Session.Cleanups == 0, "Stopping precedes command unwind and no premature terminal/cleanup."); }
                finally { release.Set(); }
                Assert(task.GetAwaiter().GetResult().Status == MssqlImportStatus.Cancelled && f.Session.Executes == 1 && f.Session.Cleanups == 1, "Actual command unwind precedes truthful cleanup/cancelled result.");
            }
            entered.Dispose(); release.Dispose(); stopping.Dispose();
        }
        using (var f = new ImportWorkerFixture()) using (var stop = new System.Threading.CancellationTokenSource()) {
            f.Session.State = () => { if (f.Session.Executes == 1) { stop.Cancel(); throw WorkerSqlError(uncertain: true); } return WorkerState(); };
            var r = f.Run(stop.Token); Assert(r.Status == MssqlImportStatus.Cancelled && File.ReadAllText(r.LogPath).Contains("detail two"), "Cancellation with SQL probe details must log then cancel.");
        }
        using (var f = new ImportWorkerFixture(skip: true)) {
            f.Session.Execute = (b, t) => { throw WorkerSqlError(); };
            f.Session.State = () => { if (f.Session.Executes != 0) throw new MssqlImportBatchException(new[] { new MssqlImportSqlError(50000, 1, 16, 1, "distinct safety probe detail") }); return WorkerState(); };
            var r = f.Run(); string log = File.ReadAllText(r.LogPath);
            Assert(r.Status == MssqlImportStatus.Failed && f.Session.Executes == 1 && log.Contains("detail two") && log.Contains("RelativeLine=1 SourceLine=unknown Message=distinct safety probe detail") && log.Contains("Origin=Probe;DuringBatch=1"), "Safety probe failure cannot discard either SQL error collection.");
        }
        using (var f = new ImportWorkerFixture(skip: true)) {
            f.Session.State = () => f.Session.Executes == 0 ? WorkerState(1, 1) : WorkerState();
            f.Session.Execute = (b, t) => { throw WorkerSqlError(); };
            Assert(f.Run().Status == MssqlImportStatus.Failed && f.Session.Executes == 1, "Pre-batch open transaction prohibits skipping even after rollback0.");
        }
        foreach (int failAt in new[] { 1, 2, 3 }) using (var f = new ImportWorkerFixture(WorkerNative(WorkerLobInit() + "SELECT 2;\r\nGO\r\n"), true)) {
            f.Session.Lob = job => f.Session.Lobs >= failAt ? new MssqlImportLobState(true, 1, 2, 1, false, false) : new MssqlImportLobState(true, 1, 1, 1, true, false);
            f.Session.Execute = (b, t) => { if (b.Number == 2 && failAt < 3) throw WorkerSqlError(); };
            Assert(f.Run().Status == MssqlImportStatus.Failed, "Ordinary error/EOF cannot bypass initialized LOB terminal gate.");
        }
    }
}