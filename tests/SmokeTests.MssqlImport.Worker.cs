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
