using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using mySQLPunk;
using mySQLPunk.lib.MssqlImport;

public static partial class SmokeTests
{
    private static void ImportWizard_GatesAndInvalidation()
    {
        using (var fixture = new ImportWizardFixture())
        using (var form = fixture.CreateForm())
        {
            form.Show(); Application.DoEvents();
            Assert(fixture.SessionCalls == 0, "Showing selected-target setup must not open DB/session.");
            Assert(!IWFind<CheckBox>(form, "importSkipErrors").Checked, "Skip failed GO batches defaults off.");
            Assert(!IWFind<CheckBox>(form, "importConfirmTarget").Checked, "Target confirmation is never assumed.");
            Assert(IWFind<TextBox>(form, "importTarget").ReadOnly, "Existing tree target is display-only.");
            IWClick(form, "importNext");
            IWPump(() => IWFind<Button>(form, "importNext").Enabled, "Preflight should finish and permit review.");
            Assert(fixture.BatchCalls == 0, "Preflight does not execute source SQL.");
            IWClick(form, "importNext");
            Assert(!IWFind<Button>(form, "importNext").Enabled, "Passed preflight alone cannot start writes.");
            IWFind<CheckBox>(form, "importConfirmTarget").Checked = true;
            Assert(IWFind<Button>(form, "importNext").Enabled, "Passed preflight and explicit confirmation permit Start.");
            IWFind<CheckBox>(form, "importSkipErrors").Checked = true;
            Assert(!IWFind<Button>(form, "importNext").Enabled, "Changing policy invalidates old checked request.");
            Assert(!IWFind<CheckBox>(form, "importConfirmTarget").Checked, "Policy change clears confirmation.");
            fixture.AssertSourceUnlocked();
            IWClick(form, "importRecheck");
            IWPump(() => IWFind<Button>(form, "importRecheck").Enabled, "Policy recheck finishes.");
            IWFind<CheckBox>(form, "importConfirmTarget").Checked = true;
            Assert(IWFind<Button>(form, "importNext").Enabled, "Recheck permits new explicit confirmation.");
            IWClick(form, "importBack"); IWClick(form, "importBack");
            IWFind<TextBox>(form, "importSourcePath").Text = fixture.OtherSourcePath;
            Assert(!IWFind<CheckBox>(form, "importConfirmTarget").Checked, "New file invalidates confirmed target/request.");
            fixture.AssertSourceUnlocked();
            form.Close();
        }
    }

    private static void ImportWizard_PreflightFailureAndRelease()
    {
        using (var fixture = new ImportWizardFixture())
        {
            File.WriteAllText(fixture.SourcePath, "SELECT 1;\r\nGO 2\r\n", new UTF8Encoding(false));
            using (var form = fixture.CreateForm())
            {
                form.Show(); IWClick(form, "importNext");
                IWPump(() => IWFind<Button>(form, "importRecheck").Enabled, "Invalid source must settle preflight.");
                Assert(!IWFind<Button>(form, "importNext").Enabled, "Rejected GO repetition blocks continuation.");
                Assert(fixture.BatchCalls == 0, "Rejected source has no writes.");
                string issue = IWFind<Label>(form, "importCheckSummary").Text;
                Assert(issue.Length > 0 && (issue.Contains("GO") || issue.Contains("Unsupported")), "Safe issue category is visible.");
                form.Close();
            }
            fixture.AssertSourceUnlocked();
        }
        using (var fixture = new ImportWizardFixture { Writable = false })
        using (var form = fixture.CreateForm())
        {
            form.Show(); IWClick(form, "importNext");
            IWPump(() => IWFind<Button>(form, "importRecheck").Enabled, "Readonly target preflight settles.");
            Assert(!IWFind<Button>(form, "importNext").Enabled && fixture.BatchCalls == 0, "Readonly target blocks writes.");
            form.Close();
        }
    }

    private static void ImportWizard_CancellationAndClose()
    {
        foreach (bool closeWindow in new[] { false, true })
        using (var fixture = new ImportWizardFixture { BlockExecution = true, HoldCleanup = true })
        using (var form = fixture.CreateForm())
        {
            try
            {
                form.Show(); IWPrepareAndConfirm(form); IWClick(form, "importNext");
                IWPump(() => fixture.ExecutionEntered.IsSet, "First command enters owned fake execution.");
                int sessions = fixture.SessionCalls;
                IWClick(form, "importNext"); IWClick(form, "importRecheck");
                Assert(fixture.SessionCalls == sessions && fixture.BatchCalls == 1, "Busy handlers cannot start a second job.");
                if (closeWindow) form.Close(); else IWClick(form, "importCancel");
                IWPump(() => fixture.CleanupEntered.IsSet, "Cancellation reaches cleanup only after command unwinds.");
                Assert(!form.IsDisposed && form.Visible, "Close and Cancel retain visible form while cleanup is pending.");
                Assert(form.Result == null, "Cancellation request is not a terminal receipt.");
                Assert(!IWFind<Button>(form, "importNext").Enabled, "Stopping cannot start another job.");
                IWRender(form, "native-stopping-light");
                string stopping = IWFind<Label>(form, "importStage").Text;
                Assert(stopping.Contains("停止") || stopping.Contains("Stopping") || stopping.Contains("Cancel"), "Stopping state remains visible.");
                fixture.ReleaseCleanup.Set();
                IWPump(() => form.Result != null || form.IsDisposed, "Worker terminal settles after cleanup gate.");
                Assert(form.Result != null && form.Result.Status == MssqlImportStatus.Cancelled, "Authoritative result is retained.");
                Assert(fixture.BatchCalls == 1, "Cancellation does not schedule next batch.");
                if (!closeWindow) Assert(form.Visible && !form.IsDisposed, "Cancel retains terminal result for review.");
                form.Close();
                IWPump(() => !form.Visible || form.IsDisposed, "Terminal close returns.");
                fixture.AssertSourceUnlocked();
            }
            finally { fixture.ReleaseExecution.Set(); fixture.ReleaseCleanup.Set(); form.Close(); }
        }
    }

    private static void ImportWizard_ThemesAndCompactLayout()
    {
        string priorTheme = ThemeManager.CurrentTheme;
        try
        {
            foreach (string theme in new[] { "light", "dark" })
            foreach (float scale in new[] { 1f, 1.5f, 2f })
            using (var fixture = new ImportWizardFixture())
            using (var form = fixture.CreateForm())
            {
                ThemeManager.SetTheme(theme, false); ThemeManager.ApplyTo(form);
                form.Show(); form.Scale(new SizeF(scale, scale)); form.MinimumSize = Size.Empty;
                form.ClientSize = new Size(640, 480); form.PerformLayout(); Application.DoEvents();
                IWAssertReachableFooter(form, "importNext"); IWAssertReachableFooter(form, "importCancel");
                IWPrepareAndConfirm(form);
                var page = IWFind<Panel>(form, "importStep2");
                Assert(page.AutoScroll && page.ClientSize.Height >= 80, "Compact confirmation retains scrollable readable content.");
                foreach (string name in new[] { "importConfirmTarget", "importSkipErrors", "importConfirmSummary" })
                {
                    Control control = IWFind<Control>(form, name); page.ScrollControlIntoView(control); Application.DoEvents();
                    Rectangle bounds = new Rectangle(page.PointToClient(control.PointToScreen(Point.Empty)), control.Size);
                    Assert(page.ClientRectangle.IntersectsWith(bounds), "Confirmation content reachable: " + name);
                    Assert(control.Width <= page.ClientSize.Width, "Confirmation text wraps without horizontal overflow: " + name);
                }
                IWAssertReachableFooter(form, "importCancel");
                IWRender(form, "native-confirm-" + theme + "-" + scale.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Assert(IWFind<TextBox>(form, "importErrorText").ReadOnly, "Log stays readonly in both themes.");
                Assert(form.Controls.Find("importPlay", true).Length == 0 && form.Controls.Find("importPause", true).Length == 0,
                    "Punky exposes mute only, no play/pause controls.");
                form.Close();
            }
        }
        finally { ThemeManager.SetTheme(priorTheme, false); }
    }

    private static void ImportWizard_DurableLogAndHonestResult()
    {
        using (var fixture = new ImportWizardFixture { FailFirstBatch = true })
        using (var form = fixture.CreateForm())
        {
            form.Show(); IWPrepareAndConfirm(form); IWClick(form, "importNext");
            IWPump(() => form.Result != null, "Default SQL failure returns a terminal result.");
            Assert(form.Result.Status == MssqlImportStatus.Failed && fixture.BatchCalls == 1, "Default failure never executes batch2.");
            Assert(!string.IsNullOrWhiteSpace(form.Result.LogPath) && File.Exists(form.Result.LogPath), "Result points to actual durable log.");
            string log = File.ReadAllText(form.Result.LogPath, new UTF8Encoding(false, true));
            Assert(log.Contains("fixture constraint detail") && log.Contains("547") && log.Contains("fixture second detail") && log.Contains("2627"), "Every supplied provider detail reaches actual file.");
            var tail = IWFind<TextBox>(form, "importErrorText");
            Assert(tail.ReadOnly && tail.Text.Length <= 32768 && tail.Text.Contains("fixture constraint detail"), "UI contains bounded readable error preview.");
            string path = IWFind<TextBox>(form, "importLogPath").Text;
            Assert(path == form.Result.LogPath, "Visible path is the actual job log, not prototype text.");
            string result = IWFind<Label>(form, "importResult").Text;
            Assert(result.Contains("部分") || result.Contains("partial"), "Failed batch may have partial effects; result says so.");
            Assert(!result.Contains("完整還原通過"), "Failure never claims complete restoration.");
            IWRender(form, "native-error-log-" + ThemeManager.CurrentTheme);
            form.Close();
        }
        using (var fixture = new ImportWizardFixture())
        using (var form = fixture.CreateForm())
        {
            form.Show(); IWPrepareAndConfirm(form); IWClick(form, "importNext");
            IWPump(() => form.Result != null, "Generic complete run returns.");
            Assert(form.Result.Status == MssqlImportStatus.Completed && fixture.BatchCalls == 2, "Generic batches execute in sequence.");
            string result = IWFind<Label>(form, "importResult").Text;
            Assert(result.Contains("未核對資料內容") || result.Contains("data contents not verified"), "UI shows actual generic validation boundary in user wording.");
            Assert(!result.Contains("完整還原通過"), "Generic execution is not a native count/content restore proof.");
            form.Close();
        }
    }

    private static void ImportWizard_HeartbeatExpiresEstimate()
    {
        using (var fixture = new ImportWizardFixture { BlockExecution = true, BlockBatch = 5, DelayMilliseconds = 700 })
        using (var form = fixture.CreateForm())
        {
            // Four confirmations over2s warm ETA, then fifth command sends no callbacks.
            File.WriteAllText(fixture.SourcePath,
                "SELECT 1;\r\nGO\r\nSELECT 2;\r\nGO\r\nSELECT 3;\r\nGO\r\nSELECT 4;\r\nGO\r\nSELECT 5;\r\nGO\r\nSELECT 6;\r\n",
                new UTF8Encoding(false));
            try
            {
                form.Show(); IWPrepareAndConfirm(form); IWClick(form, "importNext");
                IWPump(() => fixture.BatchCalls == 5, "Fifth command blocks after acknowledged advancing bytes.");
                var eta = IWFind<Label>(form, "importEta");
                IWPump(() => !string.IsNullOrWhiteSpace(eta.Text) && !IWEtaUnknown(eta.Text), "Steady confirmed advancing samples first display useful ETA.");
                string elapsed = IWFind<Label>(form, "importElapsed").Text;
                IWPump(() => IWFind<Label>(form, "importElapsed").Text != elapsed, "Elapsed remains live without worker callback.");
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var snapshot = (MssqlImportProgress)typeof(MssqlImportWizardForm).GetField("latest", flags).GetValue(form);
                int version = (int)typeof(MssqlImportWizardForm).GetField("generation", flags).GetValue(form);
                var publish = typeof(MssqlImportWizardForm).GetMethod("Publish", flags);
                using (var phaseOnly = new System.Windows.Forms.Timer { Interval = 100 })
                {
                    phaseOnly.Tick += (s,e) => publish.Invoke(form, new object[] {
                        new MssqlImportProgress(snapshot.JobId, snapshot.FileBytes, MssqlImportStage.Executing,
                            successfulBytes: snapshot.SuccessfulBytes, skippedBytes: snapshot.SkippedBytes, phase: MssqlImportPhase.Schema,
                            remaining: snapshot.Remaining, estimatedFinish: snapshot.EstimatedFinish), version, snapshot.JobId });
                    phaseOnly.Start();
                    IWPump(() => IWEtaUnknown(eta.Text), "Phase-only callbacks cannot refresh five-second confirmed-byte ETA freshness.");
                    phaseOnly.Stop();
                }
                Assert(form.Result == null && fixture.BatchCalls == 5, "Unknown ETA does not terminate or acknowledge held command.");
                IWClick(form, "importCancel");
                IWPump(() => form.Result != null, "Heartbeat fixture still cancels via authoritative worker cleanup.");
                Assert(IWEtaUnknown(eta.Text), "Stopping/terminal state cannot restore old running ETA.");
            }
            finally { fixture.ReleaseExecution.Set(); fixture.ReleaseCleanup.Set(); form.Close(); }
        }
    }
    private static bool IWEtaUnknown(string text)
    {
        // Assert the user-visible unknown state, independent of the estimator implementation.
        return text.Contains("計算") || text.Contains("未知") || text.Contains("目前指令")
            || text.IndexOf("unknown", StringComparison.OrdinalIgnoreCase) >= 0
            || text.IndexOf("estimating", StringComparison.OrdinalIgnoreCase) >= 0
            || text.IndexOf("command", StringComparison.OrdinalIgnoreCase) >= 0;
    }
    private static void IWPrepareAndConfirm(Form form)
    {
        IWClick(form, "importNext");
        IWPump(() => IWFind<Button>(form, "importNext").Enabled, "Preflight passes.");
        IWClick(form, "importNext");
        Assert(!IWFind<Button>(form, "importNext").Enabled, "Explicit confirmation required.");
        IWFind<CheckBox>(form, "importConfirmTarget").Checked = true;
    }
    private static void IWClick(Form form, string name)
    {
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        IWFind<Button>(form, name).PerformClick();
    }
    private static T IWFind<T>(Control form, string name) where T : Control
    {
        return (T)form.Controls.Find(name, true).Single();
    }
    private static void IWPump(Func<bool> condition, string message)
    {
        var clock = Stopwatch.StartNew();
        while (!condition() && clock.ElapsedMilliseconds < 6000) { Application.DoEvents(); Thread.Sleep(10); }
        Assert(condition(), message + " (bounded UI wait)");
    }
    private static void IWAssertReachableFooter(Form form, string name)
    {
        Control button = IWFind<Control>(form, name);
        Rectangle bounds = new Rectangle(form.PointToClient(button.PointToScreen(Point.Empty)), button.Size);
        Assert(form.ClientRectangle.Contains(bounds), "Fixed footer remains reachable: " + name);
    }
    private static void IWRender(Form form, string name)
    {
        // Dedicated IMPORT variable, independent of existing export fixture knobs.
        string root = Environment.GetEnvironmentVariable("MYSQLPUNK_MSSQL_IMPORT_WIZARD_RENDER_DIR");
        if (string.IsNullOrWhiteSpace(root)) return;
        Directory.CreateDirectory(root); form.PerformLayout(); Application.DoEvents();
        using (var bitmap = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            bitmap.Save(Path.Combine(root, name + ".png"));
        }
    }

    private sealed class ImportWizardFixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "mySQLPunk", "ImportTestFixtures", "wizard-" + Guid.NewGuid().ToString("N"));
        internal string SourcePath { get { return Path.Combine(Root, "fixture.sql"); } }
        internal string OtherSourcePath { get { return Path.Combine(Root, "other.sql"); } }
        internal readonly ManualResetEventSlim ExecutionEntered = new ManualResetEventSlim();
        internal readonly ManualResetEventSlim ReleaseExecution = new ManualResetEventSlim();
        internal readonly ManualResetEventSlim CleanupEntered = new ManualResetEventSlim();
        internal readonly ManualResetEventSlim ReleaseCleanup = new ManualResetEventSlim();
        internal bool Writable = true, BlockExecution, HoldCleanup, FailFirstBatch, BlockTarget, UncertainCleanup, BlockCancelCallback, FailLogEnd;
        internal readonly ManualResetEventSlim CancelCallbackEntered = new ManualResetEventSlim(), ReleaseCancelCallback = new ManualResetEventSlim();
        internal readonly ManualResetEventSlim TargetEntered = new ManualResetEventSlim(), ReleaseTarget = new ManualResetEventSlim();
        internal int FactoryThread, LargeSqlErrorMessageCodeUnits;
        internal int SessionCalls, BatchCalls, OwnerReleases, BlockBatch = 1, DelayMilliseconds;
        internal readonly List<string> GeneratedLogs = new List<string>();
        internal ImportWizardFixture()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(SourcePath, "SELECT 1;\r\nGO\r\nSELECT 2;\r\n", new UTF8Encoding(false));
            File.WriteAllText(OtherSourcePath, "SELECT 3;\r\n", new UTF8Encoding(false));
        }
        internal MssqlImportService CreateService()
        {
            return new MssqlImportService(() => {
                FactoryThread = Thread.CurrentThread.ManagedThreadId; Interlocked.Increment(ref SessionCalls); return new ImportWizardOwnedSession(this);
            }, job => {
                var log = new MssqlImportLog(job, Root);
                lock (GeneratedLogs) GeneratedLogs.Add(log.Path);
                return FailLogEnd ? (IMssqlImportLog)new ImportWizardEndFaultLog(log) : log;
            });
        }
        internal MssqlImportWizardForm CreateForm()
        {
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            var form = new MssqlImportWizardForm(new MssqlImportRequest(Guid.NewGuid(), SourcePath, "fixture_db"), CreateService);
            IWFind<PunkyReportLoadingControl>(form, "importPunky").Muted = true;
            return form;
        }
        internal void AssertSourceUnlocked()
        {
            using (var file = new FileStream(SourcePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        }
        private bool OwnedFilesUnlocked()
        {
            try
            {
                using (var file = new FileStream(SourcePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                lock (GeneratedLogs) foreach (string log in GeneratedLogs)
                    if (File.Exists(log)) using (var file = new FileStream(log, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                return true;
            }
            catch (IOException) { return false; }
        }
        public void Dispose()
        {
            ReleaseExecution.Set(); ReleaseCleanup.Set(); ReleaseTarget.Set(); ReleaseCancelCallback.Set();
            IWPump(() => OwnerReleases == SessionCalls && OwnedFilesUnlocked(), "Fixture settles owned sessions/files before cleanup.");
            // Exact generated paths only, after outer using form/worker settlement. Never user SQL/Transfer.
            File.Delete(SourcePath); File.Delete(OtherSourcePath);
            lock (GeneratedLogs) foreach (string log in GeneratedLogs) File.Delete(log);
            Directory.Delete(Root, false);
            ExecutionEntered.Dispose(); ReleaseExecution.Dispose(); CleanupEntered.Dispose(); ReleaseCleanup.Dispose(); TargetEntered.Dispose(); ReleaseTarget.Dispose(); CancelCallbackEntered.Dispose(); ReleaseCancelCallback.Dispose();
        }
    }
    private sealed class ImportWizardOwnedSession : IMssqlImportSession
    {
        private readonly ImportWizardFixture fixture;
        private bool executed, released;
        private MssqlImportCleanup receipt;
        internal ImportWizardOwnedSession(ImportWizardFixture fixture) { this.fixture = fixture; }
        public MssqlImportTarget ReadTarget(CancellationToken token)
        {
            if (fixture.BlockTarget) { fixture.TargetEntered.Set(); if (!fixture.ReleaseTarget.Wait(6000)) throw new InvalidOperationException("FixtureTargetDeadline"); }
            token.ThrowIfCancellationRequested();
            return new MssqlImportTarget("fixture-server", "fixture_db", 7, new DateTime(2020, 1, 1), "15.0.2000.5",
                fixture.Writable, true, 0, true, true);
        }
        public MssqlImportSessionState ReadState(CancellationToken token)
        { token.ThrowIfCancellationRequested(); return new MssqlImportSessionState(!released, 7, 0, 0); }
        public MssqlImportLobState ReadLobState(Guid job, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return new MssqlImportLobState(false, 0, 0, 0, false, false); }
        public void ExecuteBatch(MssqlImportBatch batch, CancellationToken token)
        {
            executed = true; Interlocked.Increment(ref fixture.BatchCalls);
            using (token.Register(() => {
                if (fixture.BlockCancelCallback) { fixture.CancelCallbackEntered.Set(); fixture.ReleaseCancelCallback.Wait(6000); }
            }))
            {
                fixture.ExecutionEntered.Set();
                if (fixture.DelayMilliseconds > 0 && batch.Number < fixture.BlockBatch && token.WaitHandle.WaitOne(fixture.DelayMilliseconds)) token.ThrowIfCancellationRequested();
                if (fixture.BlockExecution && batch.Number == fixture.BlockBatch && !fixture.ReleaseExecution.Wait(12000, token)) throw new InvalidOperationException("FixtureExecutionDeadline");
                token.ThrowIfCancellationRequested();
            }
            if (fixture.LargeSqlErrorMessageCodeUnits > 0)
                throw new MssqlImportBatchException(new[] {
                    new MssqlImportSqlError(547, 0, 16, 1,
                        "batch-tail-" + batch.Number + ":" + new string('x', fixture.LargeSqlErrorMessageCodeUnits))
                });
            if (fixture.FailFirstBatch && batch.Number == 1)
                throw new MssqlImportBatchException(new[] { new MssqlImportSqlError(547, 0, 16, 1, "fixture constraint detail"), new MssqlImportSqlError(2627, 1, 16, 2, "fixture second detail") });
        }
        public MssqlImportCounts CountTargetRows(Action<long,long> progress, CancellationToken token)
        { token.ThrowIfCancellationRequested(); progress?.Invoke(0,0); return new MssqlImportCounts(0,0); }
        public void CancelActiveCommand() { /* Real cancellation is the supplied job token; fake owns no SqlCommand. */ }
        public MssqlImportCleanup Cleanup()
        {
            if (receipt != null) return receipt;
            if (executed)
            {
                fixture.CleanupEntered.Set();
                if (fixture.HoldCleanup && !fixture.ReleaseCleanup.Wait(6000)) throw new InvalidOperationException("FixtureCleanupDeadline");
            }
            Release(); return receipt = new MssqlImportCleanup(true, true, !fixture.UncertainCleanup, fixture.UncertainCleanup ? "CleanupUnconfirmed" : null);
        }
        private void Release() { if (!released) { released = true; Interlocked.Increment(ref fixture.OwnerReleases); } }
        public void Dispose() { Release(); }
    }
    private static void ImportWizard_Routing()
    {
        foreach (string provider in new[] { "mssql", "SQLSERVER", "oracle" })
        using (var fixture = new RoutingFixture(provider, "Tables"))
        {
            int opened = 0, legacy = 0;
            Dictionary<string, object> captured = null;
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            typeof(Form1).GetField("MssqlImportWizardRunner", flags).SetValue(fixture.Form,
                new Func<Dictionary<string, object>, MssqlImportRequest, MssqlImportResult>((settings, req) => {
                    opened++; captured = settings;
                    Assert(req.DatabaseName == "fixture_db" && req.SourcePath == "" && !req.SkipSqlErrors, "Route has explicit DB and no presumed file/skip policy.");
                    fixture.Connection["host"] = "changed.invalid"; return null;
                }));
            typeof(Form1).GetField("SqlImportOpenDialogRunner", flags).SetValue(fixture.Form,
                new Func<OpenFileDialog, DialogResult>(dialog => { legacy++; return DialogResult.Cancel; }));
            typeof(Form1).GetField("MssqlImportStatusSink", flags).SetValue(fixture.Form, new Action<string>(text => fixture.Status.Add(text)));
            fixture.Invoke("ImportSqlWithDialog");
            bool native = provider != "oracle";
            Assert(opened == (native ? 1 : 0) && legacy == (native ? 0 : 1), "Actual file handler routes only MSSQL before legacy file selection.");
            if (native)
            {
                Assert(captured["db_kind"].ToString() == "mssql" && captured["initial_database"].ToString() == "fixture_db", "Import-only adapter receives canonical provider and catalog.");
                Assert(!captured.Keys.Any(k => new[] { "pdo", "isConnect", "connString", "runtime" }.Contains(k, StringComparer.OrdinalIgnoreCase)), "No shared provider/runtime state crosses wizard boundary.");
                Assert(captured["host"].ToString() == "fixture.invalid" && captured["ssh_host"].ToString() == "ssh.fixture.invalid", "Private endpoint snapshot cannot change with tree settings.");
                Assert(fixture.Status.Count == 0, "Closing setup cannot report success.");
                var handler = typeof(Form1).GetMethod("ImportSqlWithDialog", flags);
                var entryDelegates = IWTypes(typeof(Form1)).SelectMany(t => t.GetMethods(flags)).Where(m => m.Name.Contains("b__") && m.GetParameters().Length == 2 && IWCalls(m, handler)).ToArray();
                Assert(entryDelegates.Length == 5, "Five existing toolbar/table/database/connection click delegates still call the common file handler.");
                foreach (var entry in entryDelegates) {
                    object owner = IWClosureOwner(entry.DeclaringType, fixture.Form);
                    entry.Invoke(owner, new object[] { null, EventArgs.Empty });
                }
                Assert(opened == 6 && legacy == 0, "All five compiled click actions route MSSQL without constructing Form1 or loading profiles.");
                using (var menu = new ContextMenuStrip())
                {
                    fixture.Invoke("AddTableGroupMenuItems", menu, fixture.Tree.SelectedNode.Parent);
                    foreach (string label in new[] { Localization.T("Tool.ImportWizard"), Localization.T("Tool.ExecuteSqlFile") })
                        menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == label).PerformClick();
                    Assert(opened == 8 && legacy == 0, "Both table-group file entry actions use actual MSSQL route.");
                }
                foreach (var outcome in new[] { MssqlImportStatus.CompletedWithErrors, MssqlImportStatus.Cancelled, MssqlImportStatus.CleanupUncertain, MssqlImportStatus.Failed })
                {
                    typeof(Form1).GetField("MssqlImportWizardRunner", flags).SetValue(fixture.Form,
                        new Func<Dictionary<string, object>, MssqlImportRequest, MssqlImportResult>((settings, req) => new MssqlImportResult(req.JobId, outcome, 100, logPath: "fixture.log")));
                    fixture.Invoke("ImportSqlWithDialog");
                    Assert(fixture.Status.Last().Contains("fixture.log"), "Terminal main status retains log path for " + outcome);
                    Assert(!fixture.Status.Last().Contains("匯入執行完成"), "Partial/unknown/failure statuses are not successful execution.");
                }
            }
        }
    }
    private static void ImportWizard_SnapshotsAndActions()
    {
        foreach (string theme in new[] { "light", "dark" })
        using (var fixture = new ImportWizardFixture { BlockExecution = true })
        using (var form = fixture.CreateForm())
        {
            ThemeManager.SetTheme(theme, false); ThemeManager.ApplyTo(form); form.Text = "FIXTURE ONLY · " + form.Text;
            var opened = new List<string>(); string copied = null;
            form.PathOpener = path => opened.Add(path); form.ClipboardWriter = path => copied = path;
            try
            {
                form.Show(); IWPrepareAndConfirm(form); IWClick(form, "importNext");
                IWPump(() => fixture.ExecutionEntered.IsSet, "Snapshot fixture owns blocked execution.");
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                int version = (int)typeof(MssqlImportWizardForm).GetField("generation", flags).GetValue(form);
                Guid job = (Guid)typeof(MssqlImportWizardForm).GetField("activeJob", flags).GetValue(form);
                var publish = typeof(MssqlImportWizardForm).GetMethod("Publish", flags);
                string path = fixture.GeneratedLogs.Single();
                var tail = new MssqlImportLogTail();
                for (int n = 0; n < 1283; n++) tail.Add("fixture error " + n + " · SQL 547 約束衝突\r\n");
                var progress = new MssqlImportProgress(job, 7000000000, MssqlImportStage.Executing, successfulBytes: 3000000000,
                    skippedBytes: 1000000000, successfulBatches: 2100, failedBatches: 1283, batchNumber: 4294967297,
                    startLine: 5000000000, endLine: 5000000001, phase: MssqlImportPhase.Data,
                    recentErrorText: tail.Snapshot(), totalErrors: tail.TotalErrors, logTruncated: tail.Truncated, logPath: path);
                System.Threading.Tasks.Task.Run(() => {
                    for (int n = 0; n < 10000; n++) publish.Invoke(form, new object[] { progress, version, job });
                    publish.Invoke(form, new object[] { new MssqlImportProgress(Guid.NewGuid(), 9, MssqlImportStage.Executing), version, job });
                    publish.Invoke(form, new object[] { new MssqlImportProgress(job, 9, MssqlImportStage.Executing), version - 1, job });
                }).GetAwaiter().GetResult();
                IWPump(() => IWFind<Label>(form, "importCoverage").Text.Contains("4,000,000,000"), "Newest valid Int64 snapshot reaches timer-rendered UI.");
                Assert(IWFind<Label>(form, "importPosition").Text.Contains("4,294,967,297"), "Current batch never truncates to Int32.");
                Assert(IWFind<TextBox>(form, "importErrorText").Text.Contains("1282") && !IWFind<TextBox>(form, "importErrorText").Text.Contains("fixture error 0 "), "UI tail keeps newest events.");
                Assert(IWFind<Label>(form, "importErrorCount").Text.Contains("1,283") && IWFind<Label>(form, "importErrorCount").Text.Contains("100"), "Tail cutoff and all-error total stay explicit.");
                IWRender(form, "native-progress-" + theme);
                var page = IWFind<Panel>(form, "importStep3"); page.ScrollControlIntoView(IWFind<Button>(form, "importCopyPath")); Application.DoEvents();
                IWRender(form, "native-error-log-" + theme);
                foreach (string action in new[] { "importOpenLog", "importOpenFolder", "importCopyPath" }) IWClick(form, action);
                Assert(opened.SequenceEqual(new[] { path, Path.GetDirectoryName(path) }) && copied == path, "Native actions use only actual existing file/folder/path.");
                form.PathOpener = value => { throw new IOException("private exception payload"); };
                IWClick(form, "importOpenLog"); Assert(!IWFind<Label>(form, "importStatus").Text.Contains("private"), "Action errors do not expose raw exception text.");
                var huge = new MssqlImportProgress(job, 7000000000, MssqlImportStage.Executing, recentErrorText: new string('x', 100000) + "newest detail", logPath: path);
                publish.Invoke(form, new object[] { huge, version, job });
                IWPump(() => IWFind<TextBox>(form, "importErrorText").Text.EndsWith("newest detail"), "Oversized snapshot keeps newest suffix.");
                Assert(IWFind<TextBox>(form, "importErrorText").Text.Length <= 32768, "Native error textbox is bounded to64KiB UTF16.");
                IWClick(form, "importCancel"); IWPump(() => form.Result != null, "Snapshot fixture cancellation finishes.");
                string terminal = IWFind<Label>(form, "importResult").Text;
                publish.Invoke(form, new object[] { progress, version, job }); Application.DoEvents();
                Assert(IWFind<Label>(form, "importResult").Text == terminal, "Late progress cannot overwrite authoritative terminal UI.");
                form.Close();
            }
            finally { fixture.ReleaseExecution.Set(); fixture.ReleaseCleanup.Set(); form.Close(); }
        }
        ThemeManager.SetTheme("light", false);
    }
    private static void ImportWizard_PreflightLateAndDispose()
    {
        foreach (bool forceDispose in new[] { false, true })
        using (var fixture = new ImportWizardFixture { BlockTarget = true })
        using (var form = fixture.CreateForm())
        {
            try
            {
                form.Show(); IWClick(form, "importNext"); IWPump(() => fixture.TargetEntered.IsSet, "Preflight metadata blocks in background.");
                Assert(fixture.FactoryThread != Thread.CurrentThread.ManagedThreadId, "Service factory and IO run away from UI thread.");
                if (forceDispose) form.Dispose(); else IWFind<TextBox>(form, "importSourcePath").Text = fixture.OtherSourcePath;
                fixture.ReleaseTarget.Set();
                IWPump(() => fixture.OwnerReleases == fixture.SessionCalls, "Old preflight releases owned metadata session.");
                if (!forceDispose)
                {
                    IWPump(() => IWFind<Button>(form, "importRecheck").Enabled, "Stale preflight settles without preparing current request.");
                    Assert(!IWFind<Button>(form, "importNext").Enabled && fixture.BatchCalls == 0, "Late obsolete preflight cannot enable Start.");
                    fixture.AssertSourceUnlocked(); form.Close();
                }
                else Assert(form.IsDisposed, "Disposed UI survives late background continuation.");
            }
            finally { fixture.ReleaseTarget.Set(); form.Close(); }
        }
    }
    private static void ImportWizard_ModalAndOutcomes()
    {
        foreach (bool closeWindow in new[] { false, true })
        using (var fixture = new ImportWizardFixture { BlockExecution = true, HoldCleanup = true })
        using (var form = fixture.CreateForm())
        using (var driver = new System.Windows.Forms.Timer { Interval = 20 })
        {
            int phase = 0; Exception failure = null; bool waitingVisible = false; var timeout = Stopwatch.StartNew();
            driver.Tick += (s, e) => {
                try
                {
                    Assert(timeout.ElapsedMilliseconds < 6000, "Modal UI scenario is bounded.");
                    if (phase == 0) { phase = 1; IWClick(form, "importNext"); }
                    else if (phase == 1 && IWFind<Button>(form, "importNext").Enabled) { phase = 2; IWClick(form, "importNext"); IWFind<CheckBox>(form, "importConfirmTarget").Checked = true; IWClick(form, "importNext"); }
                    else if (phase == 2 && fixture.ExecutionEntered.IsSet) { phase = 3; if (closeWindow) form.Close(); else IWClick(form, "importCancel"); }
                    else if (phase == 3 && fixture.CleanupEntered.IsSet) { waitingVisible = form.Visible && form.Modal && !form.IsDisposed && form.Result == null; phase = 4; fixture.ReleaseCleanup.Set(); }
                    else if (phase == 4 && form.Result != null) { driver.Stop(); form.Close(); }
                }
                catch (Exception error) { failure = error; driver.Stop(); fixture.ReleaseExecution.Set(); fixture.ReleaseCleanup.Set(); form.Close(); }
            };
            driver.Start(); form.ShowDialog(); driver.Stop(); fixture.ReleaseExecution.Set(); fixture.ReleaseCleanup.Set();
            if (failure != null) throw failure;
            Assert(waitingVisible && form.Result?.Status == MssqlImportStatus.Cancelled, "Modal Cancel/close waits cleanup and preserves terminal receipt.");
        }
        foreach (bool uncertain in new[] { false, true })
        using (var fixture = new ImportWizardFixture { FailFirstBatch = !uncertain, UncertainCleanup = uncertain })
        using (var form = fixture.CreateForm())
        {
            form.Show(); IWFind<CheckBox>(form, "importSkipErrors").Checked = true;
            IWPrepareAndConfirm(form); IWClick(form, "importNext"); IWPump(() => form.Result != null, "Partial/uncertain terminal scenario finishes.");
            Assert(form.Result.Status == (uncertain ? MssqlImportStatus.CleanupUncertain : MssqlImportStatus.CompletedWithErrors), "Partial/cleanup status stays authoritative.");
            Assert(!IWFind<Label>(form, "importResult").Text.Contains("完整還原通過"), "Neither outcome claims complete restore.");
            form.Close();
        }
    }
    private static int ImportWizardPreview()
    {
        Application.EnableVisualStyles(); ThemeManager.SetTheme(Environment.GetEnvironmentVariable("MYSQLPUNK_MSSQL_IMPORT_WIZARD_THEME") == "dark" ? "dark" : "light", false);
        using (var fixture = new ImportWizardFixture { BlockExecution = true })
        using (var form = fixture.CreateForm())
        {
            form.Text = "FIXTURE ONLY · 無 DB 連線 · " + form.Text;
            IWFind<PunkyReportLoadingControl>(form, "importPunky").Muted = false;
            Application.Run(form);
        }
        return 0;
    }
    private static void ImportWizard_CancelDoesNotBlockUi()
    {
        using (var fixture = new ImportWizardFixture { BlockExecution = true, BlockCancelCallback = true })
        using (var form = fixture.CreateForm())
        {
            try
            {
                form.Show(); IWPrepareAndConfirm(form); IWClick(form, "importNext");
                IWPump(() => fixture.ExecutionEntered.IsSet, "Provider cancellation fixture starts.");
                var watch = Stopwatch.StartNew(); IWClick(form, "importCancel");
                Assert(watch.ElapsedMilliseconds < 1000, "A synchronous provider cancellation callback must not block the UI click.");
                IWPump(() => fixture.CancelCallbackEntered.IsSet, "Owned cancellation reaches delayed provider callback.");
                Assert(form.Result == null && IWFind<Label>(form, "importStage").Text.Contains("停止"), "UI remains stopping until actual command/callback unwind.");
                string elapsed = IWFind<Label>(form, "importElapsed").Text;
                IWPump(() => IWFind<Label>(form, "importElapsed").Text != elapsed, "Timer remains responsive while provider Cancel callback waits.");
                fixture.ReleaseCancelCallback.Set(); IWPump(() => form.Result != null, "Cancellation settles after callback is released.");
                form.Close();
            }
            finally { fixture.ReleaseCancelCallback.Set(); fixture.ReleaseExecution.Set(); form.Close(); }
        }
    }
    private static bool IWCalls(System.Reflection.MethodInfo candidate, System.Reflection.MethodInfo target)
    {
        var body = candidate.GetMethodBody(); if (body == null) return false;
        byte[] il = body.GetILAsByteArray();
        // These compiler-generated void click lambdas load this, call the handler and return.
        if (il.Length > 24) return false;
        for (int n = 0; n + 4 < il.Length; n++)
            if ((il[n] == 0x28 || il[n] == 0x6f) && BitConverter.ToInt32(il, n + 1) == target.MetadataToken) return true;
        return false;
    }
    private static System.Collections.Generic.IEnumerable<Type> IWTypes(Type root)
    {
        yield return root;
        foreach (var nested in root.GetNestedTypes(System.Reflection.BindingFlags.NonPublic))
            foreach (var type in IWTypes(nested)) yield return type;
    }
    private static object IWClosureOwner(Type type, object form)
    {
        if (type == typeof(Form1)) return form;
        object closure = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(type);
        foreach (var field in type.GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            if (field.FieldType == typeof(Form1) || field.FieldType.Name.Contains("DisplayClass"))
                field.SetValue(closure, IWClosureOwner(field.FieldType, form));
        return closure;
    }
    private static void ImportWizard_CloseCompletionRaceAndLogFailure()
    {
        for (int n = 0; n < 6; n++)
        using (var fixture = new ImportWizardFixture { BlockExecution = true })
        using (var form = fixture.CreateForm())
        {
            form.Show(); IWPrepareAndConfirm(form); IWClick(form, "importNext");
            IWPump(() => fixture.ExecutionEntered.IsSet, "Close/completion race starts a real worker.");
            fixture.ReleaseExecution.Set(); form.Close();
            IWPump(() => !form.Visible || form.IsDisposed, "Close settles after success or cancellation cleanup.");
            Assert(form.Result != null && (form.Result.Status == MssqlImportStatus.Completed || form.Result.Status == MssqlImportStatus.Cancelled),
                "Completion/cancel race retains an authoritative terminal receipt.");
            Assert(fixture.OwnerReleases == fixture.SessionCalls, "Close releases only both owned metadata/execution sessions.");
            fixture.AssertSourceUnlocked();
        }
        using (var fixture = new ImportWizardFixture { FailLogEnd = true })
        using (var form = fixture.CreateForm())
        {
            form.Show(); IWPrepareAndConfirm(form); IWClick(form, "importNext"); IWPump(() => form.Result != null, "Log final-write fault reaches terminal UI.");
            Assert(form.Result.Status == MssqlImportStatus.Failed && form.Result.ValidationSummary.Contains("LogIncomplete"), "Log fault cannot preserve successful status.");
            string result = IWFind<Label>(form, "importResult").Text;
            Assert(result.Contains("未確認存妥") || result.Contains("persistence unconfirmed"), "Result says full log was not confirmed saved.");
            string path = form.Result.LogPath; Assert(File.Exists(path), "Partial actual log still exists.");
            int calls = 0; form.PathOpener = value => calls++; form.ClipboardWriter = value => calls++;
            File.Delete(path);
            foreach (string button in new[] { "importOpenLog", "importOpenFolder", "importCopyPath" }) IWClick(form, button);
            Assert(calls == 0, "Removed/nonexistent log cannot launch a path action or copy an invented log.");
            form.Close();
        }
    }
    private sealed class ImportWizardEndFaultLog : IMssqlImportLog
    {
        private readonly IMssqlImportLog inner;
        internal ImportWizardEndFaultLog(IMssqlImportLog inner) { this.inner = inner; }
        public string Path { get { return inner.Path; } }
        public MssqlImportLogTail Tail { get { return inner.Tail; } }
        public void WriteStart(MssqlImportRequest request, MssqlImportScan scan, MssqlImportTarget target) { inner.WriteStart(request, scan, target); }
        public void WriteError(MssqlImportBatch batch, MssqlImportBatchException error, string action) { inner.WriteError(batch, error, action); }
        public void Flush() { inner.Flush(); }
        public void WriteEnd(MssqlImportResult result) { throw new IOException("fixture final-log write fault"); }
        public void Dispose() { inner.Dispose(); }
    }
    private static void ImportWizard_TerminalTailTruncation()
    {
        using (var fixture = new ImportWizardFixture { LargeSqlErrorMessageCodeUnits = 20000 })
        using (var form = fixture.CreateForm())
        {
            form.Show();
            IWFind<CheckBox>(form, "importSkipErrors").Checked = true;
            IWPrepareAndConfirm(form);
            IWClick(form, "importNext");
            IWPump(() => form.Result != null && IWFind<Button>(form, "importCancel").Enabled,
                "Two large ordinary errors finish and release the worker.");
            Assert(form.Result.Status == MssqlImportStatus.CompletedWithErrors && form.Result.TotalErrors == 2,
                "Exactly two failed GO batches produce an amber terminal result.");
            string preview = IWFind<TextBox>(form, "importErrorText").Text;
            Console.WriteLine("Terminal log tail: totalErrors=" + form.Result.TotalErrors + "; retainedUTF16=" + preview.Length);
            Assert(preview.Length > 0 && preview.Length < 32768,
                "Whole-event eviction leaves less than the character bound.");
            Assert(preview.Contains("batch-tail-2:") && !preview.Contains("batch-tail-1:"),
                "Native preview retains only the newest event after byte-bound eviction.");
            string caption = IWFind<Label>(form, "importErrorCount").Text;
            Assert(caption.Contains("已截段") || caption.Contains("Truncated"),
                "Terminal native caption preserves authoritative truncation with fewer than100events.");
            string disk = File.ReadAllText(form.Result.LogPath, new UTF8Encoding(false, true));
            Assert(disk.Contains("batch-tail-1:") && disk.Contains("batch-tail-2:"),
                "Both original events remain in the actual full log.");

            // A clean execution in this same window must not inherit the previous cutoff.
            fixture.LargeSqlErrorMessageCodeUnits = 0;
            IWClick(form, "importNext");
            IWPump(() => IWFind<Button>(form, "importNext").Enabled, "Fresh clean preflight passes.");
            IWClick(form, "importNext");
            IWFind<CheckBox>(form, "importConfirmTarget").Checked = true;
            IWClick(form, "importNext");
            IWPump(() => form.Result != null && IWFind<Button>(form, "importCancel").Enabled,
                "Fresh clean job finishes in the same window.");
            Assert(form.Result.Status == MssqlImportStatus.Completed && form.Result.TotalErrors == 0,
                "Second job has no error events.");
            caption = IWFind<Label>(form, "importErrorCount").Text;
            Assert(!caption.Contains("已截段") && !caption.Contains("Truncated"),
                "New-job final caption does not inherit prior truncation.");
            form.Close();
        }
    }
}
