using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using mySQLPunk;
using mySQLPunk.lib;
using mySQLPunk.lib.MySqlExport;

public static partial class SmokeTests
{
    private static void MySqlExport_OptionsAndModels_Defaults()
    {
        var options = new MySqlExportWizardOptions(MySqlExportMode.SchemaAndData, @"C:\dump.sql");
        Assert(options.Mode == MySqlExportMode.SchemaAndData, "Default mode should be SchemaAndData");
        Assert(options.SingleTransaction, "SingleTransaction should be true by default");
        Assert(options.DisableForeignKeyChecks, "DisableForeignKeyChecks should be true by default");
        Assert(options.DisableUniqueChecks, "DisableUniqueChecks should be true by default");
        Assert(options.SetNoAutoValueOnZero, "SetNoAutoValueOnZero should be true by default");
        Assert(options.IncludeDropStatements, "IncludeDropStatements should be true by default");
        Assert(options.RemoveDefiner, "RemoveDefiner should be true by default");
        Assert(options.IncludeViews, "IncludeViews should be true by default");
        Assert(options.IncludeRoutines, "IncludeRoutines should be true by default");
        Assert(options.IncludeTriggers, "IncludeTriggers should be true by default");
        Assert(options.InsertBatchSize == 1000, "Default batch size should be 1000");

        var item = new MySqlTableExportItem("users", 1500, true);
        Assert(item.Name == "users", "TableName match");
        Assert(item.EstimatedRows == 1500, "EstimatedRows match");
        Assert(item.IsSelected, "IsSelected match");
    }

    private static void MySqlExport_ServiceAndManifest_AtomicWrite()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "mysqlpunk_export_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string targetFile = Path.Combine(tempDir, "export_test.sql");

        try
        {
            var mockDb = new MockMySqlExportDatabase();
            var service = new MySqlSqlExportService(mockDb, "test_db");

            var options = new MySqlExportWizardOptions(MySqlExportMode.SchemaAndData, targetFile)
            {
                SingleTransaction = false
            };
            options.SelectedTables.Add("users");
            options.SelectedViews.Add("active_users");
            options.SelectedRoutines.Add("sp_calc");
            options.SelectedTriggers.Add("trg_users_audit");

            MySqlExportProgress lastProgress = null;
            var progress = new Progress<MySqlExportProgress>(p => lastProgress = p);

            var task = service.ExportAsync(options, progress, CancellationToken.None);
            task.Wait();
            var result = task.Result;

            Assert(result.Success, "Export should succeed");
            Assert(File.Exists(targetFile), "Target file must exist after atomic move");
            Assert(result.TableCount == 1, "Exported table count should be 1");
            Assert(result.ViewCount == 1, "Exported view count should be 1");
            Assert(result.RoutineCount == 1, "Exported routine count should be 1");
            Assert(result.TriggerCount == 1, "Exported trigger count should be 1");
            Assert(result.TotalRows == 2, "Exported row count should be 2");

            string content = File.ReadAllText(targetFile);
            AssertContains(content, "-- mySQLPunk MySQL Logical Dump", "Header comment present");
            AssertContains(content, "SET FOREIGN_KEY_CHECKS = 0;", "Disable FK checks statement present");
            AssertContains(content, "SET UNIQUE_CHECKS = 0;", "Disable unique checks statement present");
            AssertContains(content, "SET SQL_MODE = 'NO_AUTO_VALUE_ON_ZERO';", "Set NO_AUTO_VALUE_ON_ZERO statement present");
            AssertContains(content, "SET UNIQUE_CHECKS = 1;", "Restore unique checks statement present");
            AssertContains(content, "USE `test_db`;", "USE database statement present");
            AssertContains(content, "CREATE TABLE `users`", "Table DDL present");
            AssertContains(content, "INSERT INTO `users`", "Table data INSERT present");
            AssertContains(content, "-- Manifest-Table: users | Rows: 2", "Native manifest table entry present");
            AssertContains(content, "-- Tables: 1", "Manifest total tables count present");
            AssertContains(content, "-- TotalRows: 2", "Manifest total rows count present");
            AssertContains(content, "-- Manifest-SHA256:", "Manifest SHA-256 marker present");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    private static void MySqlExport_Regression_GeneratedBoundsAndFailures()
    {
        string directory = Path.Combine(Path.GetTempPath(), "mysqlpunk_export_repair_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "dump.sql");
            var options = new MySqlExportWizardOptions(MySqlExportMode.SchemaAndData, path) { SelectedTables = new List<string> { "users" } };
            var db = new MockMySqlExportDatabase { Generated = true };
            var result = new MySqlSqlExportService(db, "test_db").ExportAsync(options, null, CancellationToken.None).Result;
            Assert(result.Success, "Generated column export succeeds by omitting writable generated value: " + result.ErrorMessage);
            string dump = File.ReadAllText(path);
            AssertContains(dump, "GENERATED ALWAYS", "Generated DDL retained");
            AssertContains(dump, "INSERT INTO `users` (`id`, `name`)", "Generated column absent from explicit INSERT column list");
            Assert(!dump.Contains("`id`, `name`, `computed`"), "Computed values are not inserted");
            Assert(db.ExecutedSql.Contains("ROLLBACK;") && !db.ExecutedSql.Contains("COMMIT;"), "Read snapshot ends with validated rollback");
            Assert(result.FileBytes == new FileInfo(path).Length, "Reported final bytes include checksum footer");
            Assert(mySQLPunk.lib.MySqlImport.MySqlImportPreflightScanner.Scan(path, CancellationToken.None).Passed, "Native generated dump passes its own parser/digest preflight");
            File.AppendAllText(path, "-- tampered");
            Assert(!mySQLPunk.lib.MySqlImport.MySqlImportPreflightScanner.Scan(path, CancellationToken.None).Passed, "Tampered native dump rejected");

            foreach (string failure in new[] { "snapshot", "rollback", "engine", "large" })
            {
                File.WriteAllText(path, "existing complete backup");
                db = new MockMySqlExportDatabase { Failure = failure, MaxRowBytes = failure == "large" ? 1048577 : 32 };
                result = new MySqlSqlExportService(db, "test_db").ExportAsync(options, null, CancellationToken.None).Result;
                Assert(!result.Success && result.Stage == MySqlExportStage.Failed, failure + " fails explicitly");
                AssertEquals("existing complete backup", File.ReadAllText(path), failure + " preserves existing backup");
                if (failure == "large") Assert(db.PageReads == 0, "Oversized row rejected before materializing source page");
            }
            db = new MockMySqlExportDatabase();
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                result = new MySqlSqlExportService(db, "test_db").ExportAsync(options, null, CancellationToken.None).Result;
                Assert(result.Stage == MySqlExportStage.Failed, "Replacement lock failure is reported");
            }
            AssertEquals("existing complete backup", File.ReadAllText(path), "Replacement failure preserves previous complete backup");
            Assert(Directory.GetFiles(directory, "dump.sql.staging.*.tmp").Length == 1, "Completed staging survives publication failure");
            foreach (string retained in Directory.GetFiles(directory, "dump.sql.staging.*.tmp"))
                Assert(mySQLPunk.lib.MySqlImport.MySqlImportPreflightScanner.Scan(retained, CancellationToken.None).Passed, "Retained staging is a complete verified dump");
            db = new MockMySqlExportDatabase { Failure = "page-race" };
            result = new MySqlSqlExportService(db, "test_db").ExportAsync(options, null, CancellationToken.None).Result;
            Assert(!result.Success, "Concurrent enlargement marker fails explicitly");
            AssertEquals("existing complete backup", File.ReadAllText(path), "Concurrent enlargement leaves prior backup intact");
            using (var cancel = new CancellationTokenSource())
            {
                db = new MockMySqlExportDatabase { OnPageRead = () => cancel.Cancel() };
                result = new MySqlSqlExportService(db, "test_db").ExportAsync(options, null, cancel.Token).Result;
                Assert(result.Stage == MySqlExportStage.Cancelled, "Cancellation at last source read cannot publish");
                AssertEquals("existing complete backup", File.ReadAllText(path), "Cancelled export preserves prior backup");
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void MySqlExport_SnapshotCatalogMetadata()
    {
        var engineProperty = typeof(MySqlTableExportItem).GetProperty("Engine");
        Assert(engineProperty != null, "Catalog must expose verified engine metadata before export");
        foreach (string engine in new[] { "InnoDB", "MyISAM", "MEMORY", null })
        {
            var db = new MockMySqlExportDatabase { Engine = engine };
            var items = new MySqlSqlExportService(db, "test_db").LoadTableCatalog();
            AssertEquals(engine ?? "", Convert.ToString(engineProperty.GetValue(items[0])), "Catalog retains engine or unknown");
            Assert(db.StatusReads == 1, "Catalog loads status once rather than per table");
        }
        foreach (string failure in new[] { "throw", "null", "columns", "missing" })
        {
            var items = new MySqlSqlExportService(new MockMySqlExportDatabase { StatusFailure = failure }, "test_db").LoadTableCatalog();
            Assert(items.Count == 1 && string.IsNullOrEmpty(Convert.ToString(engineProperty.GetValue(items[0]))), "Unavailable engine stays unknown; ordinary catalog remains usable: " + failure);
        }
    }

    private static void MySqlExport_OrdinaryModeAndSnapshotGuard()
    {
        string directory = Path.Combine(Path.GetTempPath(), "mysqlpunk-snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "dump.sql");
            var options = new MySqlExportWizardOptions(MySqlExportMode.SchemaAndData, path) { SingleTransaction = false, SelectedTables = new List<string> { "users" } };
            foreach (string engine in new[] { "MyISAM", "MEMORY", null })
            {
                var db = new MockMySqlExportDatabase { Engine = engine };
                var result = new MySqlSqlExportService(db, "test_db").ExportAsync(options, null, CancellationToken.None).Result;
                Assert(result.Success && result.TotalRows == 2, "Ordinary export works without an InnoDB guarantee: " + result.ErrorMessage);
                AssertContains(File.ReadAllText(path), "-- SingleTransaction: False", "Dump must not claim snapshot consistency");
                Assert(!db.ExecutedSql.Exists(sql => sql.Contains("START TRANSACTION") || sql.Contains("LOCK TABLES")), "Ordinary export must not acquire snapshot/table locks");
            }
            options.SingleTransaction = true;
            var drift = new MockMySqlExportDatabase();
            new MySqlSqlExportService(drift, "test_db").LoadTableCatalog();
            drift.Engine = "MyISAM";
            File.WriteAllText(path, "previous backup");
            var failed = new MySqlSqlExportService(drift, "test_db").ExportAsync(options, null, CancellationToken.None).Result;
            Assert(!failed.Success && drift.PageReads == 0, "Fresh runtime guard rejects engine changes before reading pages");
            AssertContains(failed.ErrorMessage, "MyISAM", "Failure identifies actual engine");
            AssertEquals("previous backup", File.ReadAllText(path), "Guard preserves previous backup");
            Assert(Directory.GetFiles(directory, "*.tmp").Length == 0, "Guard leaves no incomplete artifact");
            foreach (var unavailable in new[] { new MockMySqlExportDatabase { StatusFailure = "throw" }, new MockMySqlExportDatabase { StatusName = "Users" } })
            {
                failed = new MySqlSqlExportService(unavailable, "test_db").ExportAsync(options, null, CancellationToken.None).Result;
                Assert(!failed.Success && unavailable.PageReads == 0, "Missing metadata or case-mismatched identity must not borrow another table's engine");
                Assert(failed.ErrorMessage.IndexOf("snapshot", StringComparison.OrdinalIgnoreCase) >= 0 || failed.ErrorMessage.Contains("一致性快照"), "Guard explains snapshot requirement in the selected language");
                Assert(failed.ErrorMessage.IndexOf("settings", StringComparison.OrdinalIgnoreCase) >= 0 || failed.ErrorMessage.Contains("輸出設定"), "Guard provides actionable settings guidance");
                AssertEquals("previous backup", File.ReadAllText(path), "Metadata uncertainty preserves old output");
            }
            var caseItems = new MySqlSqlExportService(new MockMySqlExportDatabase { StatusName = "Users" }, "test_db").LoadTableCatalog();
            Assert(!caseItems[0].SupportsConsistentSnapshot, "Catalog must not assume case-insensitive table identity");
            options.Mode = MySqlExportMode.SchemaOnly;
            Assert(new MySqlSqlExportService(drift, "test_db").ExportAsync(options, null, CancellationToken.None).Result.Success, "Schema-only export requires no transactional engine");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void MySqlExport_SnapshotWizardRecovery()
    {
        string previousLanguage = Localization.CurrentLanguage;
        Localization.SetLanguage(Localization.English, false);
        var wizardType = typeof(IDatabase).Assembly.GetType("mySQLPunk.MySqlExportWizardForm", true);
        string directory = Path.Combine(Path.GetTempPath(), "mysqlpunk-snapshot-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var db = new MockMySqlExportDatabase { OtherEngine = "MyISAM" };
            var options = new MySqlExportWizardOptions(MySqlExportMode.SchemaAndData, Path.Combine(directory, "dump.sql"));
            using (var form = (System.Windows.Forms.Form)Activator.CreateInstance(wizardType, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { db, "test_db", options }, null))
            {
                MySqlSnapshotPump((Task)wizardType.GetMethod("LoadCatalogAsync", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null));
                var snapshot = GetPrivateField<System.Windows.Forms.CheckBox>(form, "singleTransaction");
                var items = GetPrivateField<List<MySqlTableExportItem>>(form, "catalog");
                Action refresh = () => wizardType.GetMethod("UpdateSelectionSummary", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null);
                Assert(!snapshot.Enabled && !snapshot.Checked, "Mixed engines must offer ordinary export before execution");
                AssertContains(GetPrivateField<System.Windows.Forms.Label>(form, "snapshotHint").Text, "legacy (MyISAM)", "Hint identifies incompatible selection");
                GetPrivateField<System.Windows.Forms.TextBox>(form, "search").Text = "users";
                Assert(!snapshot.Enabled, "Search-hidden selected table still controls consistency");
                items[1].IsSelected = false; refresh();
                Assert(snapshot.Enabled && snapshot.Checked, "All selected InnoDB tables restore preferred snapshot");
                snapshot.Checked = false;
                items[1].IsSelected = true; refresh(); items[1].IsSelected = false; refresh();
                Assert(snapshot.Enabled && !snapshot.Checked, "Explicit snapshot-off survives selection changes");
                var schema = GetPrivateField<System.Windows.Forms.RadioButton>(form, "schemaOnly");
                snapshot.Checked = true; schema.Checked = true;
                Assert(!snapshot.Enabled && !snapshot.Checked, "Schema-only must not claim a snapshot");
                wizardType.GetMethod("UpdateConfirmSummary", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null);
                AssertContains(GetPrivateField<System.Windows.Forms.Label>(form, "confirmSummary").Text, "snapshot", "English schema-only summary remains explicit about consistency");
                GetPrivateField<System.Windows.Forms.RadioButton>(form, "schemaData").Checked = true;
                Assert(snapshot.Enabled && snapshot.Checked, "Returning to data mode restores preferred snapshot");
                db.Engine = "MyISAM";
                wizardType.GetField("step", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, 3);
                MySqlSnapshotPump((Task)wizardType.GetMethod("StartExportAsync", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null));
                Assert(GetPrivateField<System.Windows.Forms.Button>(form, "backButton").Enabled, "Failure permits settings recovery after cleanup");
                typeof(System.Windows.Forms.Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(GetPrivateField<System.Windows.Forms.Button>(form, "backButton"), new object[] { EventArgs.Empty });
                Assert((int)wizardType.GetField("step", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form) == 1, "Failure returns to output settings");
                snapshot.Checked = false;
                wizardType.GetMethod("UpdateConfirmSummary", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null);
                AssertContains(GetPrivateField<System.Windows.Forms.Label>(form, "confirmSummary").Text, "cross-table", "Ordinary confirmation warns about cross-table inconsistency");
                MySqlSnapshotPump((Task)wizardType.GetMethod("OnNextAsync", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null));
                Assert(!File.Exists(options.TargetPath), "Returning to settings requires confirmation before retry");
                MySqlSnapshotPump((Task)wizardType.GetMethod("OnNextAsync", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null));
                var result = (mySQLPunk.lib.MySqlExport.MySqlExportResult)wizardType.GetProperty("Result", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
                Assert(result.Success && File.Exists(options.TargetPath), "User can confirm ordinary mode and retry successfully");
                Assert(!GetPrivateField<System.Windows.Forms.Button>(form, "backButton").Enabled, "Success only offers closing/output actions");
                db.OnPageRead = () => GetPrivateField<CancellationTokenSource>(form, "cancellation").Cancel();
                MySqlSnapshotPump((Task)wizardType.GetMethod("StartExportAsync", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null));
                result = (mySQLPunk.lib.MySqlExport.MySqlExportResult)wizardType.GetProperty("Result", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
                Assert(result.Stage == MySqlExportStage.Cancelled && GetPrivateField<System.Windows.Forms.Button>(form, "backButton").Enabled, "Cancellation also permits settings recovery");
                Assert(!GetPrivateField<System.Windows.Forms.Button>(form, "openFileButton").Visible, "Retry/cancel must remove previous success affordances");
                Assert(GetPrivateField<System.Windows.Forms.ProgressBar>(form, "overallBar").Value < 100, "Cancelled retry cannot retain completed progress");
                Assert(mySQLPunk.lib.MySqlImport.MySqlImportPreflightScanner.Scan(options.TargetPath, CancellationToken.None).Passed, "Cancelled retry preserves the prior verified backup");
            }
            foreach (string failure in new[] { "throw", "missing", "columns" })
            using (var form = (System.Windows.Forms.Form)Activator.CreateInstance(wizardType, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { new MockMySqlExportDatabase { StatusFailure = failure }, "test_db", options }, null))
            {
                MySqlSnapshotPump((Task)wizardType.GetMethod("LoadCatalogAsync", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null));
                var snapshot = GetPrivateField<System.Windows.Forms.CheckBox>(form, "singleTransaction");
                Assert(!snapshot.Enabled && !snapshot.Checked, "Unknown engines permit only ordinary export: " + failure);
            }
        }
        finally { Directory.Delete(directory, true); Localization.SetLanguage(previousLanguage, false); }
    }

    private static void MySqlExport_EarlyCompatibilityAndTerminalUi()
    {
        string previousLanguage = Localization.CurrentLanguage;
        Localization.SetLanguage(Localization.English, false);
        string directory = Path.Combine(Path.GetTempPath(), "mysqlpunk-preflight-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Type wizard = typeof(IDatabase).Assembly.GetType("mySQLPunk.MySqlExportWizardForm", true);
            var db = new MockMySqlExportDatabase { MetadataType = "vector" };
            string path = Path.Combine(directory, "dump.sql");
            var options = new MySqlExportWizardOptions(MySqlExportMode.SchemaAndData, path) { SingleTransaction = false };
            using (var form = (System.Windows.Forms.Form)Activator.CreateInstance(wizard, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { db, "test_db", options }, null))
            {
                MySqlSnapshotPump((Task)wizard.GetMethod("LoadCatalogAsync", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null));
                wizard.GetField("step", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, 1);
                wizard.GetMethod("ShowPage", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null);
                MySqlSnapshotPump((Task)wizard.GetMethod("OnNextAsync", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null));
                Assert((int)wizard.GetField("step", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form) == 1, "Unsupported data type must block advance to export confirmation");
                AssertContains(GetPrivateField<System.Windows.Forms.Label>(form, "statusLabel").Text, "vector", "Preflight identifies unsupported type before starting");
                Assert(db.PageReads == 0 && !File.Exists(path), "Early compatibility checks must not read data pages or write the target");
                db.MetadataType = "varchar";
                MySqlSnapshotPump((Task)wizard.GetMethod("OnNextAsync", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null));
                Assert((int)wizard.GetField("step", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form) == 2, "Corrected selection/type permits a fresh preflight and confirmation");
                db.MetadataType = "vector";
                wizard.GetField("step", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, 1);
                GetPrivateField<System.Windows.Forms.RadioButton>(form, "schemaOnly").Checked = true;
                MySqlSnapshotPump((Task)wizard.GetMethod("OnNextAsync", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null));
                Assert((int)wizard.GetField("step", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form) == 2, "Schema-only bypasses data-value type restriction");
                GetPrivateField<System.Windows.Forms.RadioButton>(form, "schemaData").Checked = true;
                var progress = new MySqlExportProgress(MySqlExportStage.WritingRoutines, "procedure", 1, 1, 2, 2, 128, 10, TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(4), "Writing routine");
                wizard.GetMethod("RenderProgress", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, new object[] { progress });
                Assert(GetPrivateField<System.Windows.Forms.ProgressBar>(form, "overallBar").Value == 99, "All data tables done cannot imply publication success");
                db.MetadataType = "varchar";
                db.RowCount = 4; // Two rows are read; the next empty page forces a late count failure.
                db.OnPageRead = () => Thread.Sleep(150); // Give actual progress a measurable speed/ETA.
                wizard.GetField("step", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, 3);
                MySqlSnapshotPump((Task)wizard.GetMethod("StartExportAsync", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null));
                var failed = (mySQLPunk.lib.MySqlExport.MySqlExportResult)wizard.GetProperty("Result", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
                Assert(!failed.Success && failed.TotalRows == 2 && db.PageReads >= 2, "Terminal assertions exercise failure after real page progress, not an empty initial screen");
                AssertEquals("", GetPrivateField<System.Windows.Forms.Label>(form, "etaLabel").Text, "Failed export must remove stale ETA");
                AssertEquals("", GetPrivateField<System.Windows.Forms.Label>(form, "speedLabel").Text, "Failed export must remove stale speed");
                AssertContains(GetPrivateField<System.Windows.Forms.Label>(form, "writtenLabel").Text, "unpublished", "Failure makes unpublished state explicit");
                Assert(GetPrivateField<System.Windows.Forms.Button>(form, "backButton").Enabled, "Early runtime failure still permits returning to settings");
            }
        }
        finally { Directory.Delete(directory, true); Localization.SetLanguage(previousLanguage, false); }
    }

    private static void MySqlExport_CompatibilityCancellationLifetime()
    {
        Type wizard = typeof(IDatabase).Assembly.GetType("mySQLPunk.MySqlExportWizardForm", true);
        foreach (bool closeWhileChecking in new[] { false, true })
        using (var entered = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        {
            var db = new MockMySqlExportDatabase { OnMetadataRead = () => { entered.Set(); Assert(release.Wait(10000), "Synthetic metadata gate must be released"); } };
            var options = new MySqlExportWizardOptions(MySqlExportMode.SchemaAndData, Path.Combine(Path.GetTempPath(), "unused-preflight-" + Guid.NewGuid().ToString("N") + ".sql"));
            using (var form = (System.Windows.Forms.Form)Activator.CreateInstance(wizard, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { db, "test_db", options }, null))
            {
                MySqlSnapshotPump((Task)wizard.GetMethod("LoadCatalogAsync", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null));
                IntPtr handle = form.Handle;
                wizard.GetField("step", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, 1);
                Task check = (Task)wizard.GetMethod("OnNextAsync", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null);
                try
                {
                    Assert(entered.Wait(5000), "Metadata work must execute asynchronously");
                    Assert(!GetPrivateField<System.Windows.Forms.Button>(form, "nextButton").Enabled && !GetPrivateField<System.Windows.Forms.Button>(form, "backButton").Enabled, "Precheck blocks navigation while the worker owns the session");
                    if (closeWhileChecking)
                    {
                        form.Close(); form.Close();
                        Assert(!form.IsDisposed, "Repeated close must wait for metadata worker cleanup");
                    }
                    else wizard.GetMethod("RequestCancel", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null);
                }
                finally { release.Set(); }
                MySqlSnapshotPump(check);
                Assert(!(bool)wizard.GetField("busy", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form), "Precheck must release busy state");
                Assert(GetPrivateField<CancellationTokenSource>(form, "cancellation") == null, "Precheck owns and releases exactly one CTS");
                Assert((int)wizard.GetField("step", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form) == 1, "Cancelled/closing precheck cannot advance to confirmation");
                if (closeWhileChecking) Assert(form.IsDisposed, "Requested close completes after worker cleanup");
                else
                {
                    db.OnMetadataRead = null;
                    MySqlSnapshotPump((Task)wizard.GetMethod("OnNextAsync", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null));
                    Assert((int)wizard.GetField("step", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form) == 2, "Cancelled check can be retried with a fresh CTS");
                }
            }
        }
    }

    private static void MySqlSnapshotPump(Task task)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!task.IsCompleted && watch.ElapsedMilliseconds < 15000)
        {
            System.Windows.Forms.Application.DoEvents();
            Thread.Sleep(1);
        }
        Assert(task.IsCompleted, "Synthetic wizard task must complete within 15 seconds");
        task.GetAwaiter().GetResult();
        System.Windows.Forms.Application.DoEvents();
    }

    private sealed class MockMySqlExportDatabase : IDatabase
    {
        public ConnectionState State => ConnectionState.Open;
        public string ProviderName => "MySQL";
        public bool Generated;
        public string Failure;
        public string Engine = "InnoDB", OtherEngine, StatusFailure;
        public string StatusName = "users";
        public string MetadataType = "varchar";
        public Action OnMetadataRead;
        public int StatusReads;
        public long MaxRowBytes = 32;
        public long RowCount = 2;
        public int PageReads;
        public Action OnPageRead;
        public readonly List<string> ExecutedSql = new List<string>();

        public void SetConn(string connectionString) { }
        public void Open() { }
        public void Close() { }
        public void Dispose() { }

        public DataTable SelectSQL(string sql, Dictionary<string, object> parameters = null)
        {
            var dt = new DataTable();
            if (sql.Contains("AS MaxRowBytes")) { dt.Columns.Add("MaxRowBytes", typeof(long)); dt.Rows.Add(MaxRowBytes); return dt; }
            if (sql.Contains("AS __mysqlpunk_row_bytes"))
            {
                bool first = sql.Contains("LIMIT 0,");
                dt = SelectTablePage("test_db", "users", first ? 0 : 16, 16);
                dt.Columns.Add("__mysqlpunk_row_bytes", typeof(long));
                foreach (DataRow row in dt.Rows) row["__mysqlpunk_row_bytes"] = Failure == "page-race" ? 1048577L : MaxRowBytes;
                PageReads++;
                OnPageRead?.Invoke();
                return dt;
            }
            if (sql.Contains("ROUTINES"))
            {
                dt.Columns.Add("ROUTINE_NAME");
                dt.Columns.Add("ROUTINE_TYPE");
                dt.Rows.Add("sp_calc", "PROCEDURE");
                return dt;
            }
            if (sql.Contains("TRIGGERS") && sql.Contains("information_schema"))
            {
                dt.Columns.Add("TRIGGER_NAME");
                dt.Rows.Add("trg_users_audit");
                return dt;
            }
            if (sql.Contains("SHOW CREATE VIEW"))
            {
                dt.Columns.Add("View");
                dt.Columns.Add("Create View");
                dt.Rows.Add("active_users", "CREATE VIEW `active_users` AS SELECT * FROM `users` WHERE active = 1");
                return dt;
            }
            if (sql.Contains("SHOW CREATE PROCEDURE"))
            {
                dt.Columns.Add("Procedure");
                dt.Columns.Add("Create Procedure");
                dt.Rows.Add("sp_calc", "CREATE PROCEDURE `sp_calc`() BEGIN SELECT 1; END");
                return dt;
            }
            if (sql.Contains("SHOW CREATE TRIGGER"))
            {
                dt.Columns.Add("Trigger");
                dt.Columns.Add("SQL Original Statement");
                dt.Rows.Add("trg_users_audit", "CREATE TRIGGER `trg_users_audit` AFTER INSERT ON `users` FOR EACH ROW BEGIN END");
                return dt;
            }
            return dt;
        }

        public Dictionary<string, string> ExecSQL(string sql, Dictionary<string, object> parameters = null)
        {
            ExecutedSql.Add(sql);
            bool fail = Failure == "snapshot" && sql.StartsWith("START TRANSACTION") || Failure == "rollback" && sql.StartsWith("ROLLBACK");
            return fail ? new Dictionary<string, string> { { "status", "NO" }, { "reason", "injected " + Failure } } : new Dictionary<string, string> { { "status", "OK" } };
        }

        public Task<DataTable> SelectSQLAsync(string sql, Dictionary<string, object> parameters = null)
        {
            return Task.FromResult(SelectSQL(sql, parameters));
        }

        public Task<Dictionary<string, string>> ExecSQLAsync(string sql, Dictionary<string, object> parameters = null)
        {
            return Task.FromResult(ExecSQL(sql, parameters));
        }

        public List<string> GetDatabases() => new List<string> { "test_db" };
        public List<string> GetTables(string databaseName) => OtherEngine == null ? new List<string> { "users" } : new List<string> { "users", "legacy" };
        public List<string> GetViews(string databaseName) => new List<string> { "active_users" };

        public DataTable GetColumns(string databaseName, string tableName)
        {
            OnMetadataRead?.Invoke();
            var dt = new DataTable();
            dt.Columns.Add("COLUMN_NAME");
            dt.Columns.Add("DATA_TYPE");
            dt.Columns.Add("Extra");
            dt.Rows.Add("id", "int", "");
            dt.Rows.Add("name", MetadataType, "");
            if (Generated) dt.Rows.Add("computed", "int", "VIRTUAL GENERATED");
            return dt;
        }

        public DataTable GetIndexes(string databaseName, string tableName) => new DataTable();
        public DataTable GetTableStatus(string databaseName)
        {
            StatusReads++;
            if (StatusFailure == "throw") throw new InvalidOperationException("metadata unavailable");
            if (StatusFailure == "null") return null;
            var dt = new DataTable();
            if (StatusFailure == "columns") return dt;
            dt.Columns.Add("Name"); dt.Columns.Add("Engine");
            if (StatusFailure != "missing") dt.Rows.Add(StatusName, Failure == "engine" ? "MyISAM" : Engine);
            if (OtherEngine != null) dt.Rows.Add("legacy", OtherEngine);
            return dt;
        }
        public Dictionary<string, string> GetDatabaseInfo(string databaseName) => new Dictionary<string, string>();

        public string GetTableCreateStatement(string databaseName, string tableName)
        {
            if (Generated) return "CREATE TABLE `users` (`id` int, `name` varchar(50), `computed` int GENERATED ALWAYS AS (id+1) VIRTUAL) ENGINE=InnoDB;";
            return "CREATE TABLE `users` (`id` int NOT NULL, `name` varchar(50) DEFAULT NULL, PRIMARY KEY (`id`)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;";
        }

        public bool TableExists(string databaseName, string tableName) => true;
        public bool ViewExists(string databaseName, string viewName) => true;
        public void RenameTable(string databaseName, string oldTableName, string newTableName) { }
        public void RenameView(string databaseName, string oldViewName, string newViewName) { }
        public long CountRows(string databaseName, string tableName) => RowCount;
        public DataTable GetCopyColumns(string databaseName, string tableName) => new DataTable();
        public DataTable GetCopyIndexes(string databaseName, string tableName) => new DataTable();
        public void CreateTableForCopy(string databaseName, string tableName, DataTable sourceColumns, string sourceProvider) { }
        public void DropTableForCopy(string databaseName, string tableName) { }
        public void CreateIndexesForCopy(string databaseName, string tableName, DataTable sourceIndexes, string sourceProvider) { }

        public DataTable SelectTablePage(string databaseName, string tableName, long offset, int limit)
        {
            var dt = new DataTable();
            dt.Columns.Add("id", typeof(int));
            dt.Columns.Add("name", typeof(string));
            if (Generated) dt.Columns.Add("computed", typeof(int));
            if (offset == 0)
            {
                dt.Rows.Add(1, "Alice");
                dt.Rows.Add(2, "Bob");
            }
            if (Generated) foreach (DataRow row in dt.Rows) row["computed"] = Convert.ToInt32(row["id"]) + 1;
            return dt;
        }

        public void InsertTableBatch(string databaseName, string tableName, DataTable rows) { }
        public string GetViewCreateStatement(string databaseName, string viewName) => "CREATE VIEW `active_users` AS SELECT * FROM `users` WHERE active = 1;";
        public void CreateViewFromStatement(string databaseName, string viewName, string sourceViewSql) { }
    }
}
