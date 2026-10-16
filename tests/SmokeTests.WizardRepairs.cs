using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Reflection;
using mySQLPunk.lib;

public static partial class SmokeTests
{
    private static void WizardRepairs_MySqlTableRouting()
    {
        foreach (string handler in new[] { "DumpSelectedTableSql", "DumpCurrentSelectionSqlWithDialog" })
        foreach (bool dataOnly in new[] { true, false })
        using (var fixture = new RoutingFixture("mysql", "Tables", "sales.orders"))
        {
            mySQLPunk.lib.MySqlExport.MySqlExportWizardOptions captured = null;
            typeof(mySQLPunk.Form1).GetField("MySqlExportWizardRunner", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(fixture.Form,
                new Func<IDatabase, string, mySQLPunk.lib.MySqlExport.MySqlExportWizardOptions, mySQLPunk.lib.MySqlExport.MySqlExportResult>((db, name, options) => { captured = options; return null; }));
            fixture.Invoke(handler, dataOnly);
            Assert(captured != null, "Actual table route must open MySQL wizard");
            Assert(captured.Mode == (dataOnly ? mySQLPunk.lib.MySqlExport.MySqlExportMode.DataOnly : mySQLPunk.lib.MySqlExport.MySqlExportMode.SchemaAndData), "Actual route must retain data-only mode");
            Assert(captured.SelectedTables.Count == 1 && captured.SelectedTables[0] == "sales.orders", "Actual route must retain raw selected table identity");
            Assert(!captured.IncludeViews && !captured.IncludeRoutines && !captured.IncludeTriggers, "A table route must not implicitly include unrelated database objects");
        }
    }

    private static void WizardRepairs_AtomicPublication()
    {
        Type type = typeof(IDatabase).Assembly.GetType("mySQLPunk.lib.SqlExportPublication", true);
        MethodInfo publish = type.GetMethod("Publish", BindingFlags.Static | BindingFlags.NonPublic);
        string directory = Path.Combine(Path.GetTempPath(), "mysqlpunk-publish-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string target = Path.Combine(directory, "backup.sql");
            string candidate = Path.Combine(directory, "candidate.tmp");
            File.WriteAllText(target, "original"); File.WriteAllText(candidate, "new backup");
            using (var locked = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bool rejected = false;
                try { publish.Invoke(null, new object[] { candidate, target }); }
                catch (TargetInvocationException ex) { rejected = ex.InnerException is IOException; }
                Assert(rejected, "A locked destination must fail publication");
                AssertEquals("original", File.ReadAllText(target), "Publication failure must preserve original bytes");
                AssertEquals("new backup", File.ReadAllText(candidate), "Publication failure must retain candidate");
            }
            publish.Invoke(null, new object[] { candidate, target });
            AssertEquals("new backup", File.ReadAllText(target), "Replacement must publish candidate");
            Assert(!File.Exists(candidate), "Successful replacement consumes candidate");
            string fresh = Path.Combine(directory, "fresh.sql");
            File.WriteAllText(candidate, "fresh"); publish.Invoke(null, new object[] { candidate, fresh });
            AssertEquals("fresh", File.ReadAllText(fresh), "First publication must work");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void WizardRepairs_NativeTableScope()
    {
        foreach (string provider in new[] { "sqlite", "oracle" })
        foreach (string handler in new[] { "DumpSelectedTableSql", "DumpCurrentSelectionSqlWithDialog" })
        using (var fixture = new RoutingFixture(provider, "Tables", "sales.orders"))
        {
            bool called = false;
            if (provider == "sqlite")
                typeof(mySQLPunk.Form1).GetField("SqliteExportWizardRunner", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(fixture.Form,
                    new Func<IDatabase, string, mySQLPunk.lib.SqliteExport.SqliteExportWizardOptions, mySQLPunk.lib.SqliteExport.SqliteExportResult>((db, name, options) =>
                    {
                        called = true;
                        Assert(options.SelectedTables.Count == 1 && options.SelectedTables[0] == "sales.orders", "SQLite raw seed retained");
                        Assert(!options.IncludeViews && !options.IncludeTriggers, "SQLite table route must not include unrelated objects");
                        return null;
                    }));
            else
                typeof(mySQLPunk.Form1).GetField("OracleExportWizardRunner", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(fixture.Form,
                    new Func<IDatabase, string, mySQLPunk.lib.OracleExport.OracleExportWizardOptions, mySQLPunk.lib.OracleExport.OracleExportResult>((db, name, options) =>
                    {
                        called = true;
                        Assert(options.SelectedTables.Count == 1 && options.SelectedTables[0] == "sales.orders", "Oracle raw seed retained");
                        Assert(!options.IncludeViews, "Oracle table route must not include unrelated views");
                        return null;
                    }));
            fixture.Invoke(handler, false);
            Assert(called, "Actual provider table route must open its wizard");
        }
    }

    private static void WizardRepairs_OwnedSession()
    {
        Type type = typeof(IDatabase).Assembly.GetType("mySQLPunk.lib.WizardOwnedSession", true);
        MethodInfo copy = type.GetMethod("CopySettings", BindingFlags.Static | BindingFlags.NonPublic);
        var original = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            { "pdo", new object() }, { "runtime", new object() }, { "CONNSTRING", "stale" },
            { "isConnect", "T" }, { "host", "fixture.invalid" }, { "initial_database", "source" }
        };
        var settings = (Dictionary<string, object>)copy.Invoke(null, new object[] { original, "mysql", "destination" });
        Assert(!settings.ContainsKey("pdo") && !settings.ContainsKey("runtime") && !settings.ContainsKey("connString") && !settings.ContainsKey("isConnect"), "No cached session or runtime values may cross boundary");
        AssertEquals("destination", Convert.ToString(settings["initial_database"]), "Explicit target must win");
        AssertEquals("source", Convert.ToString(original["initial_database"]), "Source settings must not change");

        string file = Path.Combine(Path.GetTempPath(), "mysqlpunk-owned-" + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            using (var editor = new my_sqlite())
            {
                editor.SetConn("Data Source=" + file + ";Version=3;Pooling=False"); editor.MCT.Open();
                editor.ExecSQL("CREATE TABLE fixture (id INTEGER);");
                var config = new Dictionary<string, object> { { "path", file }, { "pdo", editor } };
                MethodInfo open = type.GetMethod("Open", BindingFlags.Static | BindingFlags.NonPublic);
                var owned = (IDisposable)open.Invoke(null, new object[] { config, "sqlite", "main" });
                var job = (IDatabase)type.GetProperty("Database", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(owned);
                Assert(!object.ReferenceEquals(job, editor), "Wizard must have an independent provider");
                job.ExecSQL("BEGIN; INSERT INTO fixture VALUES (42);");
                Assert(editor.CountRows("main", "fixture") == 0, "Uncommitted job writes must not affect editor session");
                owned.Dispose();
                Assert(editor.State == ConnectionState.Open, "Disposing job must leave editor open");
                Assert(editor.CountRows("main", "fixture") == 0, "Closing job must rollback its transaction");
            }
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }
}
