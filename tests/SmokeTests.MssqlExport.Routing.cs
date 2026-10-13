using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Windows.Forms;
using mySQLPunk;
using mySQLPunk.lib.MssqlExport;

public static partial class SmokeTests
{
    // Removing the MSSQL branches from any real handler makes these cases fail.
    private sealed class RoutingFixture : IDisposable
    {
        internal readonly object Form = FormatterServices.GetUninitializedObject(typeof(Form1));
        internal readonly TreeView Tree = new TreeView();
        internal readonly Dictionary<string, object> Connection;
        internal readonly List<MssqlExportWizardOptions> Options = new List<MssqlExportWizardOptions>();
        internal readonly List<Dictionary<string, object>> Settings = new List<Dictionary<string, object>>();
        internal readonly List<string> Status = new List<string>();
        internal readonly List<string> LegacyFiles = new List<string>();
        internal MssqlExportResult Result;
        internal int MirrorReads;
        internal MssqlMirrorOptions Mirror;
        internal Action Captured;

        internal RoutingFixture(string provider, string group = null, string rawName = "sales.orders")
        {
            Connection = new Dictionary<string, object>
            {
                { "conn_name", "fixture" }, { "provider", provider }, { "isConnect", "T" },
                { "pdo", new FakeDumpDatabase { Provider = provider, ThrowOnGetTables = true } },
                { "CONNSTRING", "must not cross boundary" }, { "host", "fixture.invalid" },
                { "ssh_enabled", "T" }, { "ssh_host", "ssh.fixture.invalid" },
                { "ssh_port", 2222 }, { "runtime", new object() }
            };
            var mainField = typeof(Form1).GetField("myN", BindingFlags.Instance | BindingFlags.NonPublic);
            var main = FormatterServices.GetUninitializedObject(mainField.FieldType);
            mainField.FieldType.GetField("connections").SetValue(main, new List<Dictionary<string, object>> { Connection });
            mainField.SetValue(Form, main);
            SetField("db_tree", Tree);
            var root = Tree.Nodes.Add("fixture"); root.Tag = 0;
            var database = root.Nodes.Add("fixture_db");
            TreeNode selected = database;
            if (group != null)
            {
                var folder = database.Nodes.Add(group); folder.Name = group;
                selected = folder.Nodes.Add(rawName + " (decorated comment must not be identity)");
                var tagType = typeof(Form1).GetNestedType("TreeObjectNodeTag", BindingFlags.NonPublic);
                selected.Tag = Activator.CreateInstance(tagType, new object[] { group, rawName, "" });
            }
            Tree.SelectedNode = selected;
            SetField("MssqlExportWizardRunner", new Func<Dictionary<string, object>, string, MssqlExportWizardOptions, MssqlExportResult>((settings, db, options) =>
            {
                AssertEquals("fixture_db", db, "Handler passes selected database");
                Options.Add(options); Settings.Add(settings); Captured?.Invoke(); return Result;
            }));
            SetField("SqlExportSaveDialogRunner", new Func<SaveFileDialog, DialogResult>(dialog => { LegacyFiles.Add(dialog.FileName); return DialogResult.Cancel; }));
            SetField("MssqlExportMirrorCapture", new Func<MssqlMirrorOptions>(() => { MirrorReads++; return Mirror; }));
            SetField("MssqlExportStatusSink", new Action<string>(text => Status.Add(text)));
        }

        private void SetField(string name, object value)
        {
            var field = typeof(Form1).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert(field != null, "MSSQL routing invocation boundary must exist: " + name);
            field.SetValue(Form, value);
        }

        internal void Invoke(string handler, params object[] args)
        {
            typeof(Form1).GetMethod(handler, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(Form, args);
        }
        public void Dispose() { Tree.Dispose(); } // Never construct/dispose an uninitialized main form.
    }

    private static void Routing_FourHandlers()
    {
        foreach (string provider in new[] { "mssql", "SQLSERVER" })
        foreach (string handler in new[] { "DumpSelectedTableSql", "DumpCurrentSelectionSqlWithDialog", "DumpSelectedDatabaseSqlWithDialog", "BackupSelectedDatabaseWithDialog" })
        {
            bool table = handler == "DumpSelectedTableSql" || handler == "DumpCurrentSelectionSqlWithDialog";
            using (var fixture = new RoutingFixture(provider, table ? "Tables" : null))
            {
                fixture.Mirror = new MssqlMirrorOptions(Path.Combine(Path.GetTempPath(), "fixture-mirror"), 3);
                fixture.Captured = () => fixture.Connection["host"] = "changed.invalid";
                fixture.Invoke(handler, table ? new object[] { true } : new object[0]);
                Assert(fixture.Options.Count == 1 && fixture.LegacyFiles.Count == 0, handler + " opens only wizard for wrapper-like provider");
                var options = fixture.Options.Single();
                Assert(options.Mode == (table ? MssqlExportMode.DataOnly : MssqlExportMode.SchemaAndData), "Actual handler retains data-only/table mode");
                AssertEquals(table ? "sales.orders" : null, options.InitialObjectName, "Original tagged seed survives decorated text");
                Assert(options.InitialTables == null && options.IncludeViews == !table, "Table seed selects one; database default includes views");
                bool backup = handler == "BackupSelectedDatabaseWithDialog";
                Assert(fixture.MirrorReads == (backup ? 1 : 0) && (options.Mirror != null) == backup, "Only backup captures mirror once");
                AssertContains(options.DefaultFileName, backup ? "_backup_" : (table ? "_data" : "_dump_"), "Preserve entry filename intent");
                var captured = fixture.Settings.Single();
                Assert(!ReferenceEquals(captured, fixture.Connection), "Connection handoff is detached");
                Assert(!captured.Keys.Any(k => new[] { "pdo", "isConnect", "connString", "runtime" }.Contains(k, StringComparer.OrdinalIgnoreCase)), "No live/runtime settings cross boundary");
                fixture.Connection["ssh_host"] = "changed.invalid";
                fixture.Mirror = null;
                AssertEquals("ssh.fixture.invalid", Convert.ToString(captured["ssh_host"]), "SSH facts are retained and copied");
                AssertEquals("fixture.invalid", Convert.ToString(captured["host"]), "Settings mutation during modal handoff cannot affect captured endpoint");
                if (backup) Assert(options.Mirror.RetainCount == 3, "Captured mirror policy survives later settings changes");
                Assert(fixture.Status.Count == 0, "Setup cancellation produces no success status");
            }
        }
        foreach (string handler in new[] { "DumpSelectedTableSql", "DumpCurrentSelectionSqlWithDialog" })
        using (var fixture = new RoutingFixture("mssql", "Tables", "dbo.name.with.[brackets]"))
        {
            fixture.Invoke(handler, false);
            Assert(fixture.Options.Single().Mode == MssqlExportMode.SchemaAndData, "Table structure and data mode retained");
            AssertEquals("dbo.name.with.[brackets]", fixture.Options.Single().InitialObjectName, "Do not split dots or brackets");
        }
        using (var fixture = new RoutingFixture("mssql"))
        {
            fixture.Invoke("DumpCurrentSelectionSqlWithDialog", true);
            Assert(fixture.Options.Single().Mode == MssqlExportMode.SchemaAndData, "Database scope ignores table-only data flag");
        }
    }

    private static void Routing_LegacyBranches()
    {
        foreach (string provider in new[] { "mysql", "sqlite", "postgresql" })
        foreach (string handler in new[] { "DumpSelectedTableSql", "DumpCurrentSelectionSqlWithDialog", "DumpSelectedDatabaseSqlWithDialog", "BackupSelectedDatabaseWithDialog" })
        using (var fixture = new RoutingFixture(provider, "Tables"))
        {
            fixture.Invoke(handler, handler == "DumpSelectedTableSql" || handler == "DumpCurrentSelectionSqlWithDialog" ? new object[] { false } : new object[0]);
            Assert(fixture.Options.Count == 0 && fixture.LegacyFiles.Count == 1, "Other providers retain legacy dialog: " + provider + "/" + handler);
        }
        foreach (string handler in new[] { "DumpCurrentSelectionSqlWithDialog", "DumpSelectedViewSql" })
        using (var fixture = new RoutingFixture("mssql", "Views", "sales.report"))
        {
            fixture.Invoke(handler, handler == "DumpCurrentSelectionSqlWithDialog" ? new object[] { false } : new object[0]);
            Assert(fixture.Options.Count == 0 && fixture.LegacyFiles.Count == 1, "View-only selection stays on single-view branch");
            AssertContains(fixture.LegacyFiles.Single(), "sales.report_view.sql", "Legacy view filename retained");
        }
    }

    private static void Routing_SeedResolution()
    {
        var catalog = new[]
        {
            WizardService.Table(1, "dbo", ""), WizardService.Table(2, "sales", ""),
            new MssqlTableDefinition(new MssqlObjectKey(3, "dbo", "sales.orders"), null, null, null, null),
            new MssqlTableDefinition(new MssqlObjectKey(4, "odd.schema]", "table.name]"), null, null, null, null)
        };
        foreach (var test in new[]
        {
            new { Seed = "orders", ExpectedId = 1, Catalog = catalog },
            new { Seed = "sales.orders", ExpectedId = 2, Catalog = catalog.Take(2).ToArray() },
            new { Seed = "odd.schema].table.name]", ExpectedId = 4, Catalog = catalog },
            new { Seed = "sales.orders", ExpectedId = 0, Catalog = catalog },
            new { Seed = "missing", ExpectedId = 0, Catalog = catalog }
        })
        using (var fixture = new RoutingFixture("mssql", "Tables", test.Seed))
        {
            fixture.Invoke("DumpSelectedTableSql", false);
            var service = new WizardService { CatalogOverride = test.Catalog };
            using (var wizard = new MssqlExportWizardForm(fixture.Settings.Single(), "fixture_db", fixture.Options.Single(), service))
            {
                wizard.Show();
                var grid = WControl<DataGridView>(wizard, "mssqlTableList");
                WPump(() => grid.Rows.Count == test.Catalog.Length);
                var selected = grid.Rows.Cast<DataGridViewRow>().Where(row => (bool)row.Cells[0].Value).ToList();
                Assert(selected.Count == (test.ExpectedId == 0 ? 0 : 1), "Captured raw seed resolves uniquely or selects none");
                if (test.ExpectedId != 0)
                {
                    var key = test.Catalog.Single(t => t.Key.ObjectId == test.ExpectedId).Key;
                    AssertEquals(key.Schema, Convert.ToString(selected.Single().Cells[1].Value), "Native schema identity is preserved");
                    AssertEquals(key.Name, Convert.ToString(selected.Single().Cells[2].Value), "Native table identity is preserved");
                }
                else Assert(!string.IsNullOrWhiteSpace(WControl<Label>(wizard, "mssqlStatus").Text), "Unresolved seed explains empty selection");
                wizard.Close();
            }
        }
    }

    private static void Routing_ResultStatus()
    {
        string previousLanguage = Localization.CurrentLanguage;
        try
        {
            Localization.SetLanguage(Localization.English, false);
            foreach (var stage in new[] { MssqlExportStage.Completed, MssqlExportStage.Canceled, MssqlExportStage.Failed, MssqlExportStage.LocalCompletedMirrorIncomplete })
            using (var fixture = new RoutingFixture("mssql"))
            {
                fixture.Result = new MssqlExportResult(Guid.NewGuid(), stage, stage == MssqlExportStage.Completed || stage == MssqlExportStage.LocalCompletedMirrorIncomplete ? "local.sql" : null, null, null, TimeSpan.Zero, null, null, "fixture reason");
                fixture.Invoke("BackupSelectedDatabaseWithDialog");
                Assert(fixture.Status.Count == 1, "Terminal wizard result updates main status once");
                AssertContains(fixture.Status.Single(), stage == MssqlExportStage.Completed ? "completed" : stage == MssqlExportStage.Canceled ? "canceled" : stage == MssqlExportStage.Failed ? "failed" : "mirror incomplete", "Status follows authoritative terminal stage");
                if (stage == MssqlExportStage.LocalCompletedMirrorIncomplete) AssertContains(fixture.Status.Single(), "local.sql", "Incomplete mirror retains published local path");
            }
        }
        finally { Localization.SetLanguage(previousLanguage, false); }
    }
}
