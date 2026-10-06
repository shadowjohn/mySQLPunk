using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using mySQLPunk;
using mySQLPunk.lib;
using Newtonsoft.Json.Linq;

public static partial class ReportExportTests
{
    static string repository, artifacts;
    static int assertions;
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            repository = args[0]; artifacts = args.Length > 1 ? args[1] : null;
            if (!string.IsNullOrEmpty(artifacts))
            {
                artifacts = Path.GetFullPath(artifacts);
                if (Directory.Exists(artifacts) && Directory.EnumerateFileSystemEntries(artifacts).Any())
                    throw new InvalidOperationException("Artifact output must be a new or empty dedicated directory. Existing reports will never be overwritten.");
                Directory.CreateDirectory(artifacts);
            }
            Localization.SetLanguage(Localization.TraditionalChinese, false);
            TestOpenXml(); Console.WriteLine("PASS Office roundtrip and cancellation");
            TestPdf(); Console.WriteLine("PASS PDF pagination, embedding and cancellation");
            TestExportService(); Console.WriteLine("PASS schema collection and atomic publication");
            TestNativeWizard(); Console.WriteLine("PASS native wizard and SQLite integration");
            if (!string.IsNullOrEmpty(artifacts))
                foreach (ReportExportFormat format in Enum.GetValues(typeof(ReportExportFormat)))
                    foreach (var template in ReportTemplateCatalog.ForFormat(format))
                        SchemaReportExportService.WriteDocument(SampleReport(), template, Path.Combine(artifacts, template.SampleStem + template.Extension), false, CancellationToken.None, null);
            Console.WriteLine("Report export assertions passed: " + assertions);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    public static void Assert(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    public static SchemaReportDocument SampleReport()
    {
        var schema = JObject.Parse(File.ReadAllText(Path.Combine(repository, "docs/design/report-export/samples/demo-schema.json")));
        var report = new SchemaReportDocument { DatabaseName = "demo_shop", EngineName = "MySQL", Title = "demo_shop 資料庫結構報告", Author = "mySQLPunk 示範樣版", GeneratedAt = new DateTime(2026, 10, 6, 12, 0, 0) };
        foreach (var item in schema["tables"])
        {
            var table = new SchemaReportTable { Name = (string)item["name"], Comment = (string)item["comment"] };
            foreach (var field in item["fields"])
                table.Columns.Add(new SchemaReportColumn { Name = (string)field[0], Comment = (string)field[1], Type = (string)field[2], Key = (string)field[3] == "PK" ? "PRI" : (string)field[3] == "UK" ? "UNI" : (string)field[3] == "FK" ? "MUL" : "", Nullable = (string)field[4], DefaultValue = (string)field[5], Extra = (string)field[6] });
            report.Tables.Add(table);
        }
        return report;
    }
    sealed class InlineProgress : IProgress<ReportExportProgress>
    {
        readonly Action<ReportExportProgress> action;
        public InlineProgress(Action<ReportExportProgress> action) { this.action = action; }
        public void Report(ReportExportProgress progress) { action(progress); }
    }
    static string ReadPart(string path, string part)
    {
        using (var zip = ZipFile.OpenRead(path)) using (var reader = new StreamReader(zip.GetEntry(part).Open())) return reader.ReadToEnd();
    }
    static void TestExportService()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mysqlpunk-report-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            var db = new ReportDatabase();
            var names = SchemaReportExportService.ListTables(() => db, "demo", CancellationToken.None);
            Assert(names.Contains("Case") && names.Contains("case") && db.Disposed, "Exact case names and owned list connection");
            db = new ReportDatabase();
            var details = SchemaReportExportService.ListTableDetails(() => db, "demo", CancellationToken.None);
            Assert(db.Disposed && db.CommentQueries == 1, "Picker owns connection and reads comments once");
            Assert(details.Select(table => table.Name).SequenceEqual(new[] { "Case", "case" }), "Picker preserves case-sensitive table identity and order");
            Assert(details[0].DisplayName == "Case" && details[1].DisplayName == "case - 表格說明", "Picker appends only available comments");
            var multiline = new SchemaReportTableInfo { Name = " exact name ", Comment = "  說明\r\n 第二行\t內容  " };
            Assert(multiline.DisplayName == " exact name  - 說明 第二行 內容" && multiline.Name == " exact name ", "Picker flattens comments without rewriting table identifiers");

            var request = new ReportExportRequest { DatabaseName = "demo", EngineName = "mysql", Tables = new List<string> { "case" }, TemplateId = "docx.classic-dictionary", OutputPath = Path.Combine(dir, "result.docx") };
            db = new ReportDatabase(); SchemaReportExportService.Run(() => db, request, CancellationToken.None, null);
            Assert(db.ColumnsRead.SequenceEqual(new[] { "case" }) && db.Disposed, "Only selected schema, connection disposed");
            string document = ReadPart(request.OutputPath, "word/document.xml");
            Assert(document.Contains("保留中文註解") && document.Contains("varchar(40)"), "Metadata comments/types roundtrip");
            Assert(document.Contains(">UNI<"), "Unique index supersedes an earlier ordinary index");
            Assert(!document.Contains("TOP_SECRET_ROW"), "No data rows in report");
            byte[] original = File.ReadAllBytes(request.OutputPath); request.Overwrite = true;
            using (var cts = new CancellationTokenSource())
            {
                db = new ReportDatabase { OnColumns = () => cts.Cancel() };
                bool cancelled = false;
                try { SchemaReportExportService.Run(() => db, request, cts.Token, null); } catch (OperationCanceledException) { cancelled = true; }
                Assert(cancelled && db.Disposed && original.SequenceEqual(File.ReadAllBytes(request.OutputPath)), "Metadata cancellation preserves destination/disposes");
            }
            foreach (string stage in new[] { "render", "validate" })
                using (var cts = new CancellationTokenSource())
                {
                    bool cancelled = false;
                    try { SchemaReportExportService.Run(() => new ReportDatabase(), request, cts.Token, new InlineProgress(p => { if (p.Stage == stage) cts.Cancel(); })); } catch (OperationCanceledException) { cancelled = true; }
                    Assert(cancelled && original.SequenceEqual(File.ReadAllBytes(request.OutputPath)), stage + " cancellation preserves original");
                    Assert(!Directory.GetFiles(dir, ".mysqlpunk-report-*").Any(), "No abandoned partial file");
                }
            db = new ReportDatabase { FailColumns = true };
            bool failed = false;
            try { SchemaReportExportService.Run(() => db, request, CancellationToken.None, null); } catch (InvalidOperationException) { failed = true; }
            Assert(failed && db.Disposed && original.SequenceEqual(File.ReadAllBytes(request.OutputPath)), "Tagged metadata error cannot publish partial success");
            request.Overwrite = false; failed = false;
            try { SchemaReportExportService.Run(() => new ReportDatabase(), request, CancellationToken.None, null); } catch (IOException) { failed = true; }
            Assert(failed, "Overwrite requires consent");
            request.Tables.Clear(); failed = false;
            try { SchemaReportExportService.Run(() => new ReportDatabase(), request, CancellationToken.None, null); } catch (ArgumentException) { failed = true; }
            Assert(failed, "Empty table selection rejected");
        }
        finally { Assert(Path.GetFullPath(dir).StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase), "Scoped temporary cleanup"); Directory.Delete(dir, true); }
    }
    static T Find<T>(Control root, string name) where T : Control { return (T)root.Controls.Find(name, true).Single(); }
    static void PumpUntilIdle(Form form)
    {
        var busy = form.GetType().GetField("busy", BindingFlags.Instance | BindingFlags.NonPublic);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        do { Application.DoEvents(); Thread.Sleep(10); } while ((bool)busy.GetValue(form) && DateTime.UtcNow < deadline);
        Application.DoEvents(); Assert(!(bool)busy.GetValue(form), "Native background operation completed within timeout");
    }
    static void Capture(Form form, string name)
    {
        if (string.IsNullOrEmpty(artifacts)) return;
        using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.Combine(artifacts, name + ".png")); }
    }
    static void TestNativeWizard()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mysqlpunk-wizard-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "source.db");
        using (var db = new my_sqlite())
        {
            db.SetConn("Data Source=" + path); db.Open();
            db.ExecSQL("CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT NOT NULL DEFAULT '測試');");
            db.ExecSQL("CREATE TABLE orders (id INTEGER, customer_id INTEGER);");
            db.ExecSQL("CREATE INDEX a_lookup ON orders(customer_id);");
            db.ExecSQL("CREATE UNIQUE INDEX z_unique ON orders(customer_id DESC);");
            db.ExecSQL("INSERT INTO customers (name) VALUES ('TOP_SECRET_ROW');");
        }
        string previousTheme = ThemeManager.CurrentTheme;
        try
        {
            foreach (string theme in new[] { ThemeManager.Light, ThemeManager.Dark })
            {
                ThemeManager.SetTheme(theme, false);
                using (var form = new ReportExportWizardForm(new Dictionary<string, object> { { "db_kind", "sqlite" }, { "path", path } }, "main"))
                {
                    form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-2500, -2500); form.Show(); PumpUntilIdle(form);
                    var list = Find<CheckedListBox>(form, "reportTableList");
                    Assert(list.Items.Count == 2 && list.CheckedItems.Count == 2, "Native table load defaults all checked");
                    Find<Button>(form, "reportSelectNone").PerformClick(); Assert(!Find<Button>(form, "reportNext").Enabled, "Zero selection blocks navigation");
                    Find<Button>(form, "reportSelectAll").PerformClick(); Find<TextBox>(form, "reportTableSearch").Text = "orders";
                    Assert(Find<Label>(form, "reportSelectionSummary").Text.Contains("2 / 2"), "Search preserves hidden selection");
                    Find<TextBox>(form, "reportTableSearch").Clear(); Capture(form, "wizard-tables-" + theme);
                    Find<Button>(form, "reportNext").PerformClick(); Capture(form, "wizard-templates-" + theme);
                    var format = Find<ComboBox>(form, "reportFormat");
                    for (int i = 0; i < 3; i++) { format.SelectedIndex = i; Assert(Find<FlowLayoutPanel>(form, "reportTemplateCards").Controls.Count == (i == 0 ? 3 : 2), "Three DOCX template cards; two per other format"); }
                    format.SelectedIndex = 0; Find<Button>(form, "reportNext").PerformClick();
                    Find<TextBox>(form, "reportOutputPath").Text = Path.Combine(dir, theme + ".docx"); Capture(form, "wizard-confirm-" + theme);
                    Find<Button>(form, "reportNext").PerformClick(); PumpUntilIdle(form);
                    Assert(File.Exists(Path.Combine(dir, theme + ".docx")) && Find<ProgressBar>(form, "reportProgress").Value == 100, "Native real SQLite export completed");
                    Assert(!ReadPart(Path.Combine(dir, theme + ".docx"), "word/document.xml").Contains("TOP_SECRET_ROW"), "SQLite data row excluded");
                    Assert(ReadPart(Path.Combine(dir, theme + ".docx"), "word/document.xml").Contains(">UNI<"), "SQLite descending unique index preserved");
                    Capture(form, "wizard-complete-" + theme); form.Close();
                }
            }
        }
        finally
        {
            ThemeManager.SetTheme(previousTheme, false);
            System.Data.SQLite.SQLiteConnection.ClearAllPools();
            Assert(Path.GetFullPath(dir).StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase), "Scoped SQLite cleanup"); Directory.Delete(dir, true);
        }
    }
    sealed class ReportDatabase : IDatabase
    {
        public int CommentQueries; public bool Disposed, FailColumns; public Action OnColumns; public List<string> ColumnsRead = new List<string>();
        public string ProviderName => "mysql"; public ConnectionState State => ConnectionState.Open;
        public void SetConn(string s) { } public void Open() { } public void Close() { } public void Dispose() { Disposed = true; }
        public List<string> GetTables(string d) => new List<string> { "Case", "case" };
        public DataTable GetColumns(string d, string t)
        {
            ColumnsRead.Add(t); var r = new DataTable(); foreach (string n in new[] { "Field", "Type", "Null", "Key", "Default", "Extra", "Comment" }) r.Columns.Add(n);
            r.Rows.Add("name", "varchar(40)", "YES", "", DBNull.Value, "", "保留中文註解"); if (FailColumns) r.ExtendedProperties["QueryError"] = "test metadata failure"; OnColumns?.Invoke(); return r;
        }
        public DataTable GetIndexes(string d, string t) { var r = new DataTable(); r.Columns.Add("Key_name"); r.Columns.Add("Column_name"); r.Columns.Add("Non_unique"); r.Rows.Add("a_lookup", "name", "1"); r.Rows.Add("z_unique", "name", "0"); return r; }
        public DataTable GetTableStatus(string d) => throw new Exception("Schema report must not require row statistics privileges");
        public List<string> GetDatabases() => throw new NotSupportedException(); public List<string> GetViews(string d) => throw new NotSupportedException();
        public DataTable SelectSQL(string s, Dictionary<string, object> p = null)
        {
            CommentQueries++;
            Assert(s.StartsWith("SELECT TABLE_NAME AS Name, TABLE_COMMENT AS Comment FROM information_schema.TABLES", StringComparison.Ordinal), "Only comment metadata queried");
            var r = new DataTable(); r.Columns.Add("Name"); r.Columns.Add("Comment"); r.Rows.Add("case", "表格說明"); return r;
        }
        public Dictionary<string, string> ExecSQL(string s, Dictionary<string, object> p = null) => throw new Exception("Unexpected write");
        public Task<DataTable> SelectSQLAsync(string s, Dictionary<string, object> p = null) => throw new NotSupportedException();
        public Task<Dictionary<string, string>> ExecSQLAsync(string s, Dictionary<string, object> p = null) => throw new NotSupportedException();
        public Dictionary<string, string> GetDatabaseInfo(string d) => throw new NotSupportedException(); public string GetTableCreateStatement(string d, string t) => throw new Exception("Unused DDL query");
        public bool TableExists(string d, string t) => throw new NotSupportedException(); public bool ViewExists(string d, string t) => throw new NotSupportedException();
        public void RenameTable(string d, string a, string b) => throw new NotSupportedException(); public void RenameView(string d, string a, string b) => throw new NotSupportedException();
        public long CountRows(string d, string t) => throw new Exception("Data rows must not be counted");
        public DataTable GetCopyColumns(string d, string t) => throw new NotSupportedException(); public DataTable GetCopyIndexes(string d, string t) => throw new NotSupportedException();
        public void CreateTableForCopy(string d, string t, DataTable c, string p) => throw new NotSupportedException(); public void DropTableForCopy(string d, string t) => throw new NotSupportedException();
        public void CreateIndexesForCopy(string d, string t, DataTable c, string p) => throw new NotSupportedException(); public DataTable SelectTablePage(string d, string t, long o, int l) => throw new Exception("Data rows must not be read");
        public void InsertTableBatch(string d, string t, DataTable r) => throw new NotSupportedException(); public string GetViewCreateStatement(string d, string t) => throw new NotSupportedException(); public void CreateViewFromStatement(string d, string t, string s) => throw new NotSupportedException();
    }
}
