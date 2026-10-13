using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using mySQLPunk;
using mySQLPunk.lib.MssqlExport;

public static partial class SmokeTests
{
    private static MssqlSnapshotIdentity WIdentity() => new MssqlSnapshotIdentity("fixture-server", "fixture_db", 7, new DateTime(2020, 1, 1));
    private static void SnapshotWizard_DefaultDisableAndRadioGroups()
    {
        foreach (var capability in new[] { new MssqlSnapshotCapability(WIdentity(), 0, true, true), new MssqlSnapshotCapability(WIdentity(), 1, false, true), new MssqlSnapshotCapability(WIdentity(), 1, null, true), new MssqlSnapshotCapability(WIdentity(), 3, true, true), null })
        using (var form = WForm(new WizardService { CapabilityOverride = capability }, Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sql"))) {
            form.Show(); WPump(() => WControl<Button>(form, "mssqlStart").Enabled); WClick(form, "mssqlStart");
            var yes = WControl<RadioButton>(form, "mssqlSnapshotYes"); var no = WControl<RadioButton>(form, "mssqlSnapshotNo");
            Assert(no.Checked && !yes.Checked && yes.Enabled == (capability != null && capability.CanManage), "New wizard defaults No and gates capability");
            Assert(yes.Parent.Name == "mssqlSnapshotOptions" && yes.Parent != WControl<RadioButton>(form, "mssqlSchemaAndData").Parent, "Independent radio groups");
            if (yes.Enabled) { yes.Checked = true; Assert(WControl<RadioButton>(form, "mssqlSchemaAndData").Checked, "Snapshot Yes preserves export mode"); }
            WControl<RadioButton>(form, "mssqlSchemaOnly").Checked = true;
            Assert(no.Checked && !yes.Enabled, "SchemaOnly forces No");
            WControl<RadioButton>(form, "mssqlDataOnly").Checked = true; Assert(no.Checked, "Returning to data never restores Yes");
            WClick(form, "mssqlStart"); WPump(() => WControl<Button>(form, "mssqlBack").Enabled);
            Assert(WControl<Button>(form, "mssqlStart").Enabled, "Ordinary mode is not blocked by snapshot state or capability");
            form.Close();
        }
    }
    private static void SnapshotWizard_RecheckAndGeneration()
    {
        var service = new WizardService { CapabilityOverride = new MssqlSnapshotCapability(WIdentity(), 0, true, true) };
        using (var form = WForm(service, Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sql"))) {
            form.Show(); WPump(() => WControl<Button>(form, "mssqlStart").Enabled); WClick(form, "mssqlStart");
            WControl<RadioButton>(form, "mssqlDataOnly").Checked = true;
            WClick(form, "mssqlRetry"); WPump(() => WControl<Button>(form, "mssqlStart").Enabled);
            Assert(WControl<RadioButton>(form, "mssqlDataOnly").Checked && WControl<DataGridView>(form, "mssqlTableList").Rows.Count == 2, "Settings capability reload preserves mode and catalog selection");
            WControl<RadioButton>(form, "mssqlSnapshotYes").Checked = true; WClick(form, "mssqlStart"); WPump(() => WControl<Button>(form, "mssqlStart").Enabled);
            Assert(service.Seen.UseSnapshot, "Immutable request captures Yes");
            service.CapabilityOverride = new MssqlSnapshotCapability(WIdentity(), 1, false, true); WClick(form, "mssqlRetry"); WPump(() => WControl<Button>(form, "mssqlBack").Enabled);
            Assert(WControl<RadioButton>(form, "mssqlSnapshotNo").Checked && !WControl<Button>(form, "mssqlStart").Enabled, "Capability decline clears prepared Yes and requires another review");
            WClick(form, "mssqlRetry"); WPump(() => WControl<Button>(form, "mssqlStart").Enabled); Assert(!service.Seen.UseSnapshot, "Fresh review captures ordinary mode");
            service.BlockPreflight = true; WClick(form, "mssqlRetry"); WPump(() => service.Inspecting);
            WControl<TextBox>(form, "mssqlOutputPath").Text += ".changed"; service.Release.Set(); WPump(() => WControl<Button>(form, "mssqlBack").Enabled);
            Assert(!WControl<Button>(form, "mssqlStart").Enabled, "Stale capability response cannot prepare request"); form.Close();
        }
    }
    private static void SnapshotWizard_RecoveryAndTerminalStatus()
    {
        foreach (string theme in new[] { "light", "dark" }) {
        ThemeManager.SetTheme(theme, false);
        var record = new MssqlSnapshotRecoveryRecord(Guid.NewGuid(), new MssqlSnapshotIdentity("fixture-server", "fixture_db", 8, new DateTime(2021,1,1)), 0, MssqlSnapshotRecoveryPhase.Unconfirmed, 3, DateTime.UtcNow, MssqlSnapshotRecoveryCode.RestoreUnconfirmed);
        var service = new WizardService { CapabilityOverride = new MssqlSnapshotCapability(WIdentity(), 0, true, true), Notices = new[] { new MssqlSnapshotRecoveryNotice(record, "fixture-only.json", MssqlSnapshotRecoveryReadError.None), new MssqlSnapshotRecoveryNotice(null, null, MssqlSnapshotRecoveryReadError.Overflow) }, Block = true, Cleanup = new MssqlSnapshotCleanupResult(MssqlSnapshotCleanupState.Unconfirmed, 3, "fixture-only.json", "RestoreUnconfirmed") };
        using (var form = WForm(service, Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sql"))) {
            form.Show(); WPump(() => WControl<Button>(form, "mssqlStart").Enabled); WClick(form, "mssqlStart");
            WSaveRender(form, "snapshot-full-output-" + theme);
            string notices = WControl<Label>(form, "mssqlRecoveryNotices").Text;
            AssertContains(notices, "IdentityChanged", "Historical same-name database is explicitly distinguished"); AssertContains(notices, "Overflow", "Unreadable/overflow journals are surfaced"); AssertContains(notices, "sys.databases", "Manual read-only inspection guidance");
            WClick(form, "mssqlStart"); WPump(() => WControl<Button>(form, "mssqlStart").Enabled); WClick(form, "mssqlStart"); WPump(() => service.Exporting);
            WClick(form, "mssqlCancel"); WPump(() => service.Canceled);
            service.Progress.Report(new MssqlExportProgress(service.Seen.JobId, MssqlExportStage.RestoringSnapshot, null, 0, 0, 0, 0, 0, null, null, 0, 2, TimeSpan.Zero, TimeSpan.Zero, null, null, null, MssqlExportEstimationState.Measuring, 0, null, "restoring"));
            WPump(() => WControl<Label>(form, "mssqlStage").Text.Contains("SNAPSHOT"));
            service.Release.Set(); WPump(() => form.Result != null); AssertContains(WControl<Label>(form, "mssqlResult").Text, "Unconfirmed", "Result exposes cleanup independently of cancellation"); WSaveRender(form, "snapshot-full-canceled-unconfirmed-" + theme);
            WControl<Panel>(form, "mssqlStep3").AutoScrollPosition = new System.Drawing.Point(0, WControl<Panel>(form, "mssqlStep3").Controls[0].Height); WSaveRender(form, "snapshot-full-cleanup-detail-" + theme);
            var resultLabel = WControl<Label>(form, "mssqlResult"); var resultPage = WControl<Panel>(form, "mssqlStep3");
            Assert(resultPage.ClientRectangle.IntersectsWith(new System.Drawing.Rectangle(resultPage.PointToClient(resultLabel.PointToScreen(System.Drawing.Point.Empty)), resultLabel.Size)), "Cleanup details are reachable by native scrolling"); form.Close();
        }
        }
        ThemeManager.SetTheme("light", false);
        Routing_ResultStatus();
    }
    private static void WSaveRender(Form form, string name)
    {
        string root = Environment.GetEnvironmentVariable("MYSQLPUNK_MSSQL_WIZARD_RENDER_DIR");
        if (string.IsNullOrWhiteSpace(root)) return;
        Directory.CreateDirectory(root); Application.DoEvents();
        using (var bitmap = new System.Drawing.Bitmap(form.Width, form.Height)) {
            form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
            bitmap.Save(Path.Combine(root, name + ".png"));
        }
    }
    private static void Diagnostic_WizardSnapshot()
    {
        foreach (int state in new[] { 0, 1, 2, 3, 9 })
        using (var form = WForm(new WizardService { SnapshotOverride = state }, Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sql"))) {
            form.Show(); WPump(() => WControl<Button>(form, "mssqlStart").Enabled);
            WClick(form, "mssqlStart"); WClick(form, "mssqlStart"); WPump(() => WControl<Button>(form, "mssqlBack").Enabled);
            string text = WControl<Label>(form, "mssqlConfirmation").Text;
            string expected = state == 0 ? "OFF (0)" : state == 1 ? "ON (1)" : state == 2 ? "IN_TRANSITION_TO_OFF (2)" : state == 3 ? "IN_TRANSITION_TO_ON (3)" : "UNKNOWN (9)";
            AssertContains(text, expected, "Confirmation reports exact source state instead of treating transitions as OFF");
            Assert(WControl<Button>(form, "mssqlStart").Enabled, "Ordinary mode permits OFF, transitions and unknown snapshot states");
            form.Close();
        }
    }
    private static void Diagnostic_WizardRetry()
    {
        var service = new WizardService { CatalogFailure = DiagnosticFailure(DiagnosticSql(229), true) };
        using (var form = WForm(service, Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sql"), new[] { new MssqlObjectKey(2, "sales", "orders") })) {
            form.Show(); WPump(() => WControl<Button>(form, "mssqlCancel").Enabled && WControl<Label>(form, "mssqlStatus").Text.Contains("SqlPermissionDenied"));
            Assert(form.Controls.Find("mssqlRetry", true).Length == 1, "Failed catalog has an in-place retry action");
            Assert(!WControl<Button>(form, "mssqlStart").Enabled, "Catalog permission failure blocks Next");
            service.CatalogFailure = null; WClick(form, "mssqlRetry"); WPump(() => WControl<Button>(form, "mssqlStart").Enabled);
            var grid = WControl<DataGridView>(form, "mssqlTableList");
            Assert(!(bool)grid.Rows[0].Cells[0].Value && (bool)grid.Rows[1].Cells[0].Value, "Catalog retry retains narrow initial selection");
            WControl<TextBox>(form, "mssqlTableSearch").Text = "searchable";
            WClick(form, "mssqlStart"); WControl<RadioButton>(form, "mssqlDataOnly").Checked = true;
            string path = WControl<TextBox>(form, "mssqlOutputPath").Text;
            service.PreflightFailure = DiagnosticFailure(DiagnosticSql(4060), false);
            WClick(form, "mssqlStart"); WPump(() => WControl<Button>(form, "mssqlBack").Enabled);
            AssertContains(WControl<Label>(form, "mssqlConfirmation").Text, "DatabaseUnavailable", "Confirmation retains safe prerequisite failure");
            Assert(!WControl<Button>(form, "mssqlStart").Enabled, "Failed preflight blocks Start");
            service.PreflightFailure = null; WClick(form, "mssqlRetry"); WPump(() => WControl<Button>(form, "mssqlStart").Enabled);
            Assert(service.Seen.Tables.Count == 1 && service.Seen.Tables[0].ObjectId == 2 && service.Seen.Mode == MssqlExportMode.DataOnly && !service.Seen.IncludeViews && service.Seen.TargetPath == path, "Recheck preserves selection, mode, views and path");
            service.BlockPreflight = true; service.IgnoreInspectionCancellation = true;
            WClick(form, "mssqlRetry"); WPump(() => service.Inspecting);
            int calls = service.PreflightCalls; WClick(form, "mssqlRetry");
            Assert(service.PreflightCalls == calls && !WControl<Button>(form, "mssqlStart").Enabled, "Running recheck cannot duplicate or leave earlier Start enabled");
            WClick(form, "mssqlCancel"); service.Release.Set(); WPump(() => WControl<Button>(form, "mssqlBack").Enabled);
            Assert(!WControl<Button>(form, "mssqlStart").Enabled, "Canceled provider returning success cannot prepare stale request");
            string canceled = WControl<Label>(form, "mssqlStatus").Text;
            Assert(canceled.Contains("已取消") || canceled.Contains("Canceled"), "Canceled recheck reaches a terminal canceled message even if provider ignores cancellation");
            form.Close();
        }
    }
    private static void Diagnostic_WizardErrors()
    {
        foreach (bool catalog in new[] { true, false })
        foreach (bool known in new[] { true, false }) {
            var failure = known ? (Exception)new MssqlExportPrerequisiteException(MssqlPrerequisite.ViewDefinitionRequired) : new IOException("Password=private-secret;SELECT confidential");
            var service = new WizardService { CatalogFailure = catalog ? failure : null, PreflightFailure = catalog ? null : failure };
            using (var form = WForm(service, Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sql"))) {
                form.Show();
                if (catalog) WPump(() => service.CatalogCalls == 1 && WControl<Panel>(form, "mssqlStep0").Enabled);
                else {
                    WPump(() => WControl<Button>(form, "mssqlStart").Enabled);
                    WClick(form, "mssqlStart"); WControl<RadioButton>(form, "mssqlSchemaOnly").Checked = true;
                    WClick(form, "mssqlStart"); WPump(() => WControl<Button>(form, "mssqlBack").Enabled);
                }
                string message = WControl<Label>(form, catalog ? "mssqlStatus" : "mssqlConfirmation").Text;
                Assert(message.Contains("ViewDefinitionRequired") == known, "Both wizard surfaces distinguish missing definitions from unknown failures");
                Assert(!message.Contains("private-secret") && !message.Contains("confidential"), "Wizard never displays raw exceptions");
                Assert(!WControl<Button>(form, "mssqlStart").Enabled && WControl<Button>(form, "mssqlRetry").Enabled, "Missing definitions blocks even schema-only; failures remain retryable");
                if (catalog) {
                    service.CatalogFailure = null; service.BlockCatalog = true; service.IgnoreInspectionCancellation = true;
                    WClick(form, "mssqlRetry"); WPump(() => service.Inspecting); WClick(form, "mssqlRetry");
                    Assert(service.CatalogCalls == 2, "Catalog reentry does not launch another load");
                    WClick(form, "mssqlCancel"); service.Release.Set(); WPump(() => WControl<Button>(form, "mssqlRetry").Enabled);
                    Assert(!WControl<Button>(form, "mssqlStart").Enabled && WControl<DataGridView>(form, "mssqlTableList").Rows.Count == 0, "Canceled catalog success does not populate stale selection");
                    service.BlockCatalog = false; WClick(form, "mssqlRetry"); WPump(() => WControl<Button>(form, "mssqlStart").Enabled);
                    Assert(WControl<DataGridView>(form, "mssqlTableList").Rows.Count == 2, "Catalog can recover after cancellation");
                }
                form.Close();
            }
        }
    }
    private static void Diagnostic_WizardGeneration()
    {
        var service = new WizardService();
        using (var form = WForm(service, Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sql"))) {
            form.Show(); WPump(() => WControl<Button>(form, "mssqlStart").Enabled); WConfirm(form);
            service.BlockPreflight = true; WClick(form, "mssqlRetry"); WPump(() => service.Inspecting);
            // Simulate invalidation while a slow response is in flight; the old response must not enable Start.
            string nextPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sql");
            WControl<TextBox>(form, "mssqlOutputPath").Text = nextPath;
            service.Release.Set(); WPump(() => WControl<Button>(form, "mssqlBack").Enabled);
            Assert(!WControl<Button>(form, "mssqlStart").Enabled, "Old preflight generation cannot prepare a changed request");
            service.BlockPreflight = false; WClick(form, "mssqlRetry"); WPump(() => WControl<Button>(form, "mssqlStart").Enabled);
            Assert(service.Seen.TargetPath == nextPath, "Fresh retry captures updated path after stale response was ignored");
            form.Close();
        }
    }
    private static void Wizard_RequiresForm()
    {
        Assert(typeof(MssqlObjectKey).Assembly.GetType("mySQLPunk.MssqlExportWizardForm") != null, "Native export wizard must exist");
    }

    private static void WClick(Form form, string name)
    {
        // DoEvents ends its temporary loop and may uninstall the WinForms context.
        // Real Application.Run retains it; restore it before invoking async clicks.
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        WControl<Button>(form, name).PerformClick();
    }

    private static T WControl<T>(Control form, string name)
        where T : Control
    {
        return (T)form.Controls.Find(name, true).Single();
    }

    private static void WPump(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition() && clock.ElapsedMilliseconds < 6000)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }

        Assert(condition(), "Wizard UI condition timed out");
    }

    private sealed class WizardService : MssqlSqlExportService
    {
        internal readonly ManualResetEventSlim Release = new ManualResetEventSlim(false);
        internal MssqlExportRequest Seen;
        internal IProgress<MssqlExportProgress> Progress;
        internal bool Exporting, Canceled, Block, Deny, Preview, BlockCatalog, BlockPreflight, Inspecting;
        internal MssqlExportIssue[] IssueOverride;
        internal MssqlTableDefinition[] CatalogOverride;
        internal int UiThread, CatalogThread, PreflightThread, CatalogCalls, PreflightCalls;
        internal Exception CatalogFailure, PreflightFailure;
        internal int? SnapshotOverride;
        internal MssqlSnapshotCapability CapabilityOverride;
        internal MssqlSnapshotCleanupResult Cleanup;
        internal IReadOnlyList<MssqlSnapshotRecoveryNotice> Notices = new MssqlSnapshotRecoveryNotice[0];
        internal override IReadOnlyList<MssqlSnapshotRecoveryNotice> LoadRecoveryNotices(MssqlSnapshotIdentity identity) => Notices;
        internal bool IgnoreInspectionCancellation;
        internal WizardService() : base(() =>
        {
            throw new Exception("Fixture must not open DB");
        })
        {
            UiThread = Thread.CurrentThread.ManagedThreadId;
        }

        internal static MssqlTableDefinition Table(int id, string schema, string comment)
        {
            return new MssqlTableDefinition(new MssqlObjectKey(id, schema, "orders"), new Dictionary<string, object> { { "comment", comment }, { "estimated_rows", 100L }, { "estimated_bytes", 10000L } }, null, null, null);
        }

        internal override MssqlExportCatalog LoadCatalog(string db, CancellationToken token)
        {
            CatalogThread = Thread.CurrentThread.ManagedThreadId;
            Interlocked.Increment(ref CatalogCalls);
            if (CatalogFailure != null) throw CatalogFailure;
            if (BlockCatalog)
                WaitInspection(token);
            return new MssqlExportCatalog(CatalogOverride ?? new[] { Table(1, "dbo", "訂單主檔"), Table(2, "sales", "long comment searchable 測試搜尋") }, CapabilityOverride?.State ?? 1, null, CapabilityOverride);
        }

        internal override MssqlExportPreflight Preflight(MssqlExportRequest request, CancellationToken token)
        {
            PreflightThread = Thread.CurrentThread.ManagedThreadId;
            Interlocked.Increment(ref PreflightCalls);
            if (PreflightFailure != null) throw PreflightFailure;
            if (BlockPreflight)
                WaitInspection(token);
            Seen = request;
            if (SnapshotOverride.HasValue) return new MssqlExportPreflight(new MssqlExportSchema(request.Tables.Select(k => Table(k.ObjectId, k.Schema, "")), null, null, SnapshotOverride.Value, null), null);
            return new MssqlExportPreflight(new MssqlExportSchema(request.Tables.Select(k => Table(k.ObjectId, k.Schema, "")), null, null, CapabilityOverride?.State ?? (Deny ? 0 : 1), null, null, CapabilityOverride), IssueOverride ?? (Deny ? new[] { new MssqlExportIssue("snapshot", null, null, "Required metadata unavailable") } : null));
        }

        internal override MssqlExportResult Export(MssqlExportRequest request, IProgress<MssqlExportProgress> progress, CancellationToken token)
        {
            Seen = request;
            Progress = progress;
            Exporting = true;
            if (Preview)
            {
                Frame(request.JobId, 12345678);
                while (!token.IsCancellationRequested)
                    Thread.Sleep(100);
                Thread.Sleep(600);
            }

            if (Block)
            {
                while (!token.IsCancellationRequested)
                    Thread.Sleep(10);
                Canceled = true;
                Release.Wait();
            }

            return new MssqlExportResult(request.JobId, token.IsCancellationRequested ? MssqlExportStage.Canceled : MssqlExportStage.Completed, null, null, null, TimeSpan.FromSeconds(2), null, null, null, request.UseSnapshot, Cleanup);
        }

        private void WaitInspection(CancellationToken token)
        {
            Inspecting = true;
            while (!token.IsCancellationRequested && !Release.IsSet)
                Thread.Sleep(10);
            Canceled = token.IsCancellationRequested;
            Release.Wait();
            if (!IgnoreInspectionCancellation) token.ThrowIfCancellationRequested();
        }

        internal void Frame(Guid id, long bytes, bool stale = false)
        {
            Progress.Report(new MssqlExportProgress(id, MssqlExportStage.Data, Seen.Tables[0], 50, 5000, 50, 5000, bytes, 100, 10000, 0, Seen.Tables.Count, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(stale ? -10 : 2), 2500, TimeSpan.FromSeconds(3), 43, MssqlExportEstimationState.Measuring, 0, null, "fixture"));
        }
    }

    private static MssqlExportWizardForm WForm(WizardService service, string path, MssqlObjectKey[] initial = null)
    {
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        var form = new MssqlExportWizardForm(new Dictionary<string, object> { { "pdo", new object () }, { "isConnect", true } }, "fixture", new MssqlExportWizardOptions(MssqlExportMode.SchemaAndData, initial, true, path, null), service);
        WControl<PunkyReportLoadingControl>(form, "mssqlPunky").Muted = !service.Preview;
        return form;
    }

    private static void WConfirm(Form form)
    {
        WClick(form, "mssqlStart");
        WClick(form, "mssqlStart");
        WPump(() => WControl<Button>(form, "mssqlStart").Enabled);
    }

    private static void Wizard_Selection()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wizard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var service = new WizardService();
            using (var form = WForm(service, Path.Combine(dir, "a.sql")))
            {
                form.Show();
                WPump(() => WControl<DataGridView>(form, "mssqlTableList").Rows.Count == 2);
                var grid = WControl<DataGridView>(form, "mssqlTableList");
                grid.Rows[0].Cells[0].Value = false;
                WControl<TextBox>(form, "mssqlTableSearch").Text = "searchable";
                Assert(grid.Rows.Count == 1 && (bool)grid.Rows[0].Cells[0].Value, "Comment search keeps checked identity");
                WControl<TextBox>(form, "mssqlTableSearch").Text = "";
                WConfirm(form);
                Assert(service.Seen.Tables.Select(k => k.ObjectId).SequenceEqual(new[] { 2 }), "Search keeps object identity");
                Assert(service.CatalogThread != service.UiThread && service.PreflightThread != service.UiThread, "DB work is background");
                Assert(WControl<Button>(form, "mssqlStart").Enabled, "Successful preflight permits start: " + WControl<Label>(form, "mssqlStatus").Text + " / " + WControl<Label>(form, "mssqlConfirmation").Text);
                form.Close();
            }

            using (var form = WForm(new WizardService(), Path.Combine(dir, "b.sql"), new[] { new MssqlObjectKey(42, "sales", "orders") }))
            {
                form.Show();
                WPump(() => WControl<DataGridView>(form, "mssqlTableList").Rows.Count == 2);
                var grid = WControl<DataGridView>(form, "mssqlTableList");
                Assert(!(bool)grid.Rows[0].Cells[0].Value && (bool)grid.Rows[1].Cells[0].Value, "Initial schema/name selects exact table");
                WClick(form, "mssqlSelectNone");
                Assert(!WControl<Button>(form, "mssqlStart").Enabled, "Zero selection blocks next");
                form.Close();
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static void Wizard_Preflight()
    {
        var service = new WizardService
        {
            Deny = true
        };
        using (var form = WForm(service, ""))
        {
            form.Show();
            WPump(() => WControl<DataGridView>(form, "mssqlTableList").Rows.Count == 2);
            WClick(form, "mssqlStart");
            Assert(!WControl<Button>(form, "mssqlStart").Enabled, "Empty path blocks confirmation");
            WControl<TextBox>(form, "mssqlOutputPath").Text = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sql");
            WControl<RadioButton>(form, "mssqlDataOnly").Checked = true;
            Assert(!WControl<CheckBox>(form, "mssqlIncludeViews").Checked && !WControl<CheckBox>(form, "mssqlIncludeViews").Enabled, "Data-only excludes views");
            WControl<RadioButton>(form, "mssqlSchemaOnly").Checked = true;
            Assert(WControl<CheckBox>(form, "mssqlIncludeViews").Enabled, "Schema mode restores views availability");
            WClick(form, "mssqlStart");
            WPump(() => WControl<Button>(form, "mssqlBack").Enabled && WControl<Label>(form, "mssqlConfirmation").Text.Contains("Required metadata unavailable"));
            Assert(!WControl<Button>(form, "mssqlStart").Enabled, "Failed preflight blocks start");
            form.Close();
        }
    }

    private static void Wizard_IssueScopeAndDuration()
    {
        var service = new WizardService { IssueOverride = new[] {
            new MssqlExportIssue("UnsupportedSpatial", new MssqlObjectKey(2, "sales", "orders"), "shape", "Native spatial values require a future codec."),
            new MssqlExportIssue("SnapshotUnavailable", null, null, "SNAPSHOT must be ON.")
        }};
        using (var form = WForm(service, Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sql")))
        {
            form.Show();
            WPump(() => WControl<DataGridView>(form, "mssqlTableList").Rows.Count == 2);
            WClick(form, "mssqlStart");
            WClick(form, "mssqlStart");
            WPump(() => WControl<Label>(form, "mssqlConfirmation").Text.Contains("UnsupportedSpatial"));
            string confirmation = WControl<Label>(form, "mssqlConfirmation").Text;
            AssertContains(confirmation, "sales.orders", "Issue identifies same-name table by schema");
            AssertContains(confirmation, "shape", "Issue identifies exact column");
            AssertContains(confirmation, "UnsupportedSpatial", "Issue displays safe code");
            Assert(confirmation.Contains("Database") || confirmation.Contains("資料庫"), "Database-level issue fallback");
            Assert(confirmation.Contains("Views") || confirmation.Contains("檢視表"), "Scope includes views");
            AssertContains(confirmation, "rowversion", "Scope explains regenerated values");
            form.Close();
        }
        AssertEquals("25:02:03", MssqlExportWizardForm.FormatDuration(TimeSpan.FromHours(25) + TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(3)), "Duration keeps total hours");
    }

    private static void Wizard_Lifecycle()
    {
        var service = new WizardService
        {
            Block = true
        };
        using (var form = WForm(service, Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sql")))
        {
            form.Show();
            WPump(() => WControl<DataGridView>(form, "mssqlTableList").Rows.Count == 2);
            WConfirm(form);
            WClick(form, "mssqlStart");
            WPump(() => service.Exporting);
            Assert(form.Controls.Find("mssqlToggleSound", true).Length == 1, "Mute only; no play or pause");
            service.Frame(service.Seen.JobId, 1234);
            WPump(() => WControl<ProgressBar>(form, "mssqlOverallProgress").Value == 43);
            Assert(WControl<Label>(form, "mssqlStatus").Text == WControl<Label>(form, "mssqlStage").Text, "Running footer follows actual export stage");
            Assert(WControl<ProgressBar>(form, "mssqlTableProgress").Value == 50, "True separate table progress");
            Assert(WControl<Label>(form, "mssqlPercent").Text.Contains("估計") || WControl<Label>(form, "mssqlPercent").Text.Contains("Estimated"), "Numeric percent is explicitly estimated");
            Assert(WControl<Label>(form, "mssqlWritten").Text.Contains("50") && WControl<Label>(form, "mssqlWritten").Text.Contains(" / 2"), "Rows and completed/total table counters visible");
            service.Frame(service.Seen.JobId, 1234, true);
            WPump(() => WControl<Label>(form, "mssqlSpeed").Text.EndsWith("—"));
            Assert(WControl<ProgressBar>(form, "mssqlOverallProgress").Value == 43, "Stalled time does not fabricate progress");
            service.Frame(Guid.NewGuid(), 9999);
            Application.DoEvents();
            Assert(!WControl<Label>(form, "mssqlWritten").Text.Contains("9999"), "Stale job ignored");
            form.Close();
            WPump(() => service.Canceled);
            Assert(!form.IsDisposed, "Close waits for worker cleanup");
            service.Release.Set();
            WPump(() => form.IsDisposed);
        }
    }

    private static void Wizard_RawSelectionAndReset()
    {
        foreach (var seed in new[]
        {
            "orders",
            "sales.orders",
            "missing"
        }

        )
        {
            var service = new WizardService();
            if (seed == "sales.orders")
                service.CatalogOverride = new[]
                {
                    new MssqlTableDefinition(new MssqlObjectKey(3, "dbo", "sales.orders"), null, null, null, null),
                    WizardService.Table(2, "sales", "")
                };
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            using (var form = new MssqlExportWizardForm(new Dictionary<string, object>(), "fixture", new MssqlExportWizardOptions(MssqlExportMode.SchemaAndData, null, true, Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sql"), null, seed), service))
            {
                form.Show();
                WPump(() => WControl<DataGridView>(form, "mssqlTableList").Rows.Count == 2);
                var grid = WControl<DataGridView>(form, "mssqlTableList");
                Assert(grid.Rows.Cast<DataGridViewRow>().Count(r => (bool)r.Cells[0].Value) == (seed == "orders" ? 1 : 0), "Raw provider seed must resolve uniquely; never fall back to all");
                if (seed == "orders")
                {
                    WConfirm(form);
                    WClick(form, "mssqlStart");
                    WPump(() => form.Result != null);
                    WPump(() => WControl<Button>(form, "mssqlStart").Enabled);
                    string result = WControl<Label>(form, "mssqlResult").Text;
                    Assert(result.Contains("rowversion") && (result.Contains("Views") || result.Contains("檢視表")), "Result repeats included/excluded view scope and regenerated values");
                    Assert(result.Contains(" / 1"), "Result includes completed/total table count");
                    WClick(form, "mssqlStart");
                    Assert(form.Result == null && WControl<Label>(form, "mssqlResult").Text == "", "Reset clears previous result");
                }

                form.Close();
            }
        }
    }

    private static void Wizard_InspectionClose()
    {
        foreach (bool duringCatalog in new[]
        {
            true,
            false
        }

        )
        {
            var service = new WizardService
            {
                BlockCatalog = duringCatalog,
                BlockPreflight = !duringCatalog
            };
            using (var form = WForm(service, Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sql")))
            {
                form.Show();
                if (!duringCatalog)
                {
                    WPump(() => WControl<Button>(form, "mssqlStart").Enabled);
                    WClick(form, "mssqlStart");
                    WClick(form, "mssqlStart");
                }

                WPump(() => service.Inspecting);
                form.Close();
                WPump(() => service.Canceled);
                Assert(!form.IsDisposed, "Inspection close must wait for owned worker cleanup");
                service.Release.Set();
                WPump(() => form.IsDisposed);
            }
        }
    }

    private static void Wizard_Layout()
    {
        foreach(string theme in new[]{"light", "dark"})
        foreach(float factor in new[]{1f,1.5f,2f})
        {
            ThemeManager.SetTheme(theme, false);
            using(var form=WForm(new WizardService { CapabilityOverride = new MssqlSnapshotCapability(WIdentity(), 0, true, true) },Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".sql")))
            {
                form.Show(); WPump(()=>WControl<Button>(form,"mssqlStart").Enabled);
                form.Scale(new System.Drawing.SizeF(factor,factor));
                form.MinimumSize=System.Drawing.Size.Empty;
                form.ClientSize=new System.Drawing.Size(640,480);
                form.PerformLayout(); Application.DoEvents();
                foreach(var name in new[]{"mssqlStart","mssqlCancel"})
                {
                    var button=WControl<Button>(form,name);
                    var bounds=new System.Drawing.Rectangle(form.PointToClient(button.PointToScreen(System.Drawing.Point.Empty)),button.Size);
                    Assert(form.ClientRectangle.Contains(bounds),"Fixed footer remains reachable under layout scaling: " + factor + " / " + name + " / " + bounds + " / " + form.ClientRectangle);
                }
                WClick(form, "mssqlStart");
                var page = WControl<Panel>(form, "mssqlStep1");
                Assert(page.ClientSize.Height >= 100, "Scaled compact layout retains scrollable content area: " + factor);
                foreach (var name in new[] { "mssqlSnapshotYes", "mssqlSnapshotNo", "mssqlSnapshotReason", "mssqlSnapshotWarning", "mssqlRecoveryNotices" }) {
                    var control = WControl<Control>(form, name); page.ScrollControlIntoView(control); Application.DoEvents();
                    Assert(control.Visible && control.Height > 0, "Snapshot option/readout remains reachable: " + name);
                    Assert(control.Width <= page.ClientSize.Width, "Snapshot readout wraps within page: " + name);
                    var bounds = new System.Drawing.Rectangle(page.PointToClient(control.PointToScreen(System.Drawing.Point.Empty)), control.Size);
                    Assert(page.ClientRectangle.IntersectsWith(bounds), "Scrolled readout intersects content viewport: " + name);
                    string render = Environment.GetEnvironmentVariable("MYSQLPUNK_MSSQL_WIZARD_RENDER_DIR");
                    if (!string.IsNullOrWhiteSpace(render) && (name == "mssqlSnapshotNo" || name == "mssqlSnapshotWarning")) {
                        Directory.CreateDirectory(render);
                        using (var bitmap = new System.Drawing.Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0,0,form.Width,form.Height)); bitmap.Save(Path.Combine(render, name + "-" + theme + "-" + factor.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".png")); }
                    }
                }
                string renders = Environment.GetEnvironmentVariable("MYSQLPUNK_MSSQL_WIZARD_RENDER_DIR");
                if (!string.IsNullOrWhiteSpace(renders)) {
                    Directory.CreateDirectory(renders); page.AutoScrollPosition = System.Drawing.Point.Empty; Application.DoEvents();
                    using (var bitmap = new System.Drawing.Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, form.Width, form.Height)); bitmap.Save(Path.Combine(renders, "snapshot-output-" + theme + "-" + factor.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".png")); }
                    WClick(form, "mssqlStart"); WPump(() => WControl<Button>(form, "mssqlBack").Enabled);
                    using (var bitmap = new System.Drawing.Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, form.Width, form.Height)); bitmap.Save(Path.Combine(renders, "snapshot-confirm-" + theme + "-" + factor.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".png")); }
                }
                form.Close();
            }
        }
        ThemeManager.SetTheme("light", false);
    }
    private static void Wizard_CancelTerminal()
    {
        var service=new WizardService { Block=true };
        using(var form=WForm(service,Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".sql")))
        {
            form.Show(); WPump(()=>WControl<Button>(form,"mssqlStart").Enabled); WConfirm(form);
            Assert(WControl<Button>(form,"mssqlStart").AccessibleName==WControl<Button>(form,"mssqlStart").Text,"Dynamic Start accessible name follows visible action");
            WClick(form,"mssqlStart"); WPump(()=>service.Exporting); service.Frame(service.Seen.JobId,1234);
            WPump(()=>WControl<ProgressBar>(form,"mssqlOverallProgress").Value==43);
            WClick(form,"mssqlToggleSound");
            Assert(WControl<Button>(form,"mssqlToggleSound").AccessibleName==WControl<Button>(form,"mssqlToggleSound").Text,"Mute accessible name follows action");
            WClick(form,"mssqlCancel"); WPump(()=>service.Canceled); Assert(!form.IsDisposed,"Cancel displays cleanup before result");
            service.Release.Set(); WPump(()=>form.Result!=null && WControl<Button>(form,"mssqlCancel").Enabled);
            Assert(WControl<Button>(form,"mssqlCancel").AccessibleName==WControl<Button>(form,"mssqlCancel").Text,"Terminal Close accessible name follows action");
            var queue=WControl<DataGridView>(form,"mssqlTableQueue");
            Assert(queue.Rows.Cast<DataGridViewRow>().All(r=>!new[]{"匯出中","Exporting"}.Contains((string)r.Cells[2].Value)),"Terminal queue must stop showing Exporting");
            WClick(form,"mssqlCancel"); Assert(form.IsDisposed,"Terminal close remains enabled");
        }
    }
    private static void Wizard_ModalCancellation()
    {
        foreach (bool titlebarClose in new[] { false, true })
        {
            var service = new WizardService { Block = true };
            using (var form = WForm(service, Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sql")))
            using (var driver = new System.Windows.Forms.Timer { Interval = 20 })
            {
                int phase = 0;
                bool cleanupObserved = false, terminalObserved = false;
                Exception failure = null;
                var deadline = Stopwatch.StartNew();
                driver.Tick += (s, e) =>
                {
                    try
                    {
                        Assert(deadline.ElapsedMilliseconds < 6000, "Modal wizard fixture timed out");
                        if (phase == 0 && WControl<Button>(form, "mssqlStart").Enabled)
                        {
                            WClick(form, "mssqlStart");
                            WClick(form, "mssqlStart");
                            phase = 1;
                        }
                        else if (phase == 1 && WControl<Button>(form, "mssqlStart").Enabled)
                        {
                            WClick(form, "mssqlStart");
                            phase = 2;
                        }
                        else if (phase == 2 && service.Exporting)
                        {
                            if (titlebarClose) form.Close();
                            else WClick(form, "mssqlCancel");
                            phase = 3;
                        }
                        else if (phase == 3 && service.Canceled)
                        {
                            cleanupObserved = form.Visible && !form.IsDisposed;
                            service.Release.Set();
                            phase = 4;
                        }
                        else if (phase == 4 && form.Result != null)
                        {
                            terminalObserved = form.Visible && form.Modal;
                            driver.Stop();
                            form.Close();
                        }
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                        driver.Stop();
                        service.Release.Set();
                        form.Close();
                    }
                };
                driver.Start();
                form.ShowDialog();
                driver.Stop();
                service.Release.Set();
                if (failure != null) throw failure;
                Assert(cleanupObserved && form.Result != null, "Modal close waits for worker cleanup and authoritative result");
                Assert(titlebarClose || terminalObserved, "Modal Cancel must retain terminal result until explicit Close");
            }
        }
    }

    private static int WizardPreview()
    {
        Application.EnableVisualStyles();
        if (Environment.GetEnvironmentVariable("MYSQLPUNK_MSSQL_WIZARD_THEME") == "dark")
            ThemeManager.SetTheme("dark", false);
        var service = new WizardService
        {
            Preview = true, CapabilityOverride = new MssqlSnapshotCapability(WIdentity(), 0, true, true)
        };
        using (var form = WForm(service, Path.Combine(Path.GetTempPath(), "fixture-preview-" + Guid.NewGuid().ToString("N") + ".sql")))
        {
            form.Text = "FIXTURE ONLY · " + form.Text;
            Application.Run(form);
        }

        return 0;
    }
}
