using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using mySQLPunk.lib;
using mySQLPunk.lib.SqliteExport;
using mySQLPunk.lib.SqliteImport;

public static partial class SmokeTests
{
    private static void SqliteExport_OptionsAndModels_Defaults()
    {
        var options = new SqliteExportWizardOptions(SqliteExportMode.SchemaAndData, @"C:\dump.sql");
        Assert(options.Mode == SqliteExportMode.SchemaAndData, "Default mode should be SchemaAndData");
        Assert(options.SingleTransaction, "SingleTransaction should be true by default");
        Assert(options.PragmaForeignKeysOff, "PragmaForeignKeysOff should be true by default");
        Assert(options.PragmaSynchronousOff, "PragmaSynchronousOff should be true by default");
        Assert(options.IncludeDropStatements, "IncludeDropStatements should be true by default");
        Assert(options.IncludeViews, "IncludeViews should be true by default");
        Assert(options.IncludeTriggers, "IncludeTriggers should be true by default");
        Assert(options.IncludeIndexes, "IncludeIndexes should be true by default");
        Assert(options.IncludeComments, "IncludeComments should be true by default");
        Assert(!options.ExcludeSpatialMetadata, "ExcludeSpatialMetadata should be false by default");
        Assert(options.InsertBatchSize == 1000, "Default batch size should be 1000");

        var item = new SqliteTableExportItem("spatial_roads", 1500, true, true, false, false, "[GIS] LineString (SRID 3826)");
        Assert(item.Name == "spatial_roads", "TableName match");
        Assert(item.EstimatedRows == 1500, "EstimatedRows match");
        Assert(item.IsSelected, "IsSelected match");
        Assert(item.IsSpatial, "IsSpatial match");
        Assert(!item.IsSpatialMetadata, "IsSpatialMetadata match");
        AssertContains(item.SpatialInfo, "GIS", "SpatialInfo match");
    }

    private static mySQLPunk.lib.SqliteExport.SqliteExportResult SqliteExportFixture(SqliteFixtureDatabase database,string directory, params string[] tables)
    {
        var options=new SqliteExportWizardOptions(SqliteExportMode.SchemaAndData,Path.Combine(directory,Guid.NewGuid().ToString("N")+".sql"))
        {
            SelectedTables=new List<string>(tables), IncludeViews=false, IncludeTriggers=false, IncludeComments=true,
            IncludeDropStatements=true, PragmaForeignKeysOff=false, PragmaSynchronousOff=false
        };
        return new SqliteSqlExportService(database,"main").ExportAsync(options,null,CancellationToken.None).GetAwaiter().GetResult();
    }
    private static void SqliteExport_ServiceAndManifest_StreamingAndHexBlob()
    {
        string directory=Path.Combine(Path.GetTempPath(),"mysqlpunk_sqlite_export_native_"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            using(var source=new SqliteFixtureDatabase()) using(var target=new SqliteFixtureDatabase())
            {
                source.Must("CREATE TABLE roads(id INTEGER PRIMARY KEY,name TEXT,geom BLOB); INSERT INTO roads VALUES(1,'Main Street',X'00010000007C0101000000'); INSERT INTO roads VALUES(2,'Broadway',X''); CREATE VIEW v_roads AS SELECT id,name FROM roads; CREATE TRIGGER tr_roads AFTER INSERT ON roads BEGIN SELECT CASE WHEN new.id>0 THEN 'END;' ELSE 'x' END; END; CREATE INDEX idx_roads_name ON roads(name);");
                var service=new SqliteSqlExportService(source,"main");
                Assert(service.LoadTableCatalog().Count==1,"Catalog has ordinary table only");
                string file=Path.Combine(directory,"roads.sql");
                var options=new SqliteExportWizardOptions(SqliteExportMode.SchemaAndData,file) { SelectedTables=new List<string>{"roads"}, SelectedViews=service.LoadViews(),SelectedTriggers=service.LoadTriggers() };
                var result=service.ExportAsync(options,null,CancellationToken.None).GetAwaiter().GetResult();
                Assert(result.Success,"Actual native SQLite export succeeds: "+result.ErrorMessage);
                Assert(result.TableCount==1 && result.TotalRows==2 && result.ViewCount==1 && result.TriggerCount==1 && result.IndexCount==1,"Native object and row counts match");
                string content=File.ReadAllText(file);
                AssertContains(content,"X'00010000007C0101000000'","Binary geometry bytes use hex without conversion");
                AssertContains(content,"-- Manifest-Payload-SHA256:","Payload checksum scope is explicit");
                Assert(result.FileBytes==new FileInfo(file).Length,"FileBytes describe delivered bytes including footer");
                using(var sha=System.Security.Cryptography.SHA256.Create())
                    AssertEquals(BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(file))).Replace("-","").ToLowerInvariant(),result.Sha256Hash,"Result hash matches external full-file checksum");
                Assert(SqliteImportPreflightScanner.Scan(file,CancellationToken.None).Passed,"Own native dump checksum validates before writes");
                var restored=SqliteRunFixture(target,directory,content);
                Assert(restored.Status==SqliteImportStatus.Completed && restored.ManifestVerified,"Native export imports and row manifest verifies: "+string.Join(";",restored.Errors));
                AssertEquals("00010000007C0101000000",Convert.ToString(target.Scalar("SELECT hex(geom) FROM roads WHERE id=1;")),"Blob bytes restored");
                Assert(target.Exists("trigger","tr_roads") && target.Exists("view","v_roads") && target.Exists("index","idx_roads_name"),"Native DDL restored");
            }
        }
        finally { Directory.Delete(directory,true); }
    }
    private static void SqliteExport_NativeRoundtrip_GeneratedRowidSequenceTextComments()
    {
        string directory=Path.Combine(Path.GetTempPath(),"mysqlpunk_sqlite_roundtrip_"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            using(var source=new SqliteFixtureDatabase()) using(var target=new SqliteFixtureDatabase())
            {
                source.Must("CREATE TABLE g(a INT,b INT GENERATED ALWAYS AS(a*2),c TEXT,d INT GENERATED ALWAYS AS(a+3) STORED); INSERT INTO g(a,c) VALUES(5,'c'); CREATE TABLE t(x TEXT,y BLOB); INSERT INTO t(rowid,x,y) VALUES(-7,'a'||char(10)||'b'||char(13)||'c'||char(13)||char(10)||'中'||char(0)||char(39),X'00FF01'); INSERT INTO t(rowid,x,y) VALUES(9223372036854775806,'large rowid',X''); CREATE TABLE ai(id INTEGER PRIMARY KEY AUTOINCREMENT,x); INSERT INTO ai VALUES(1,'one'); INSERT INTO ai VALUES(100,'removed'); DELETE FROM ai WHERE id=100; CREATE TABLE empty_ai(id INTEGER PRIMARY KEY AUTOINCREMENT); INSERT INTO empty_ai VALUES(200); DELETE FROM empty_ai; CREATE TABLE wr(k TEXT PRIMARY KEY,v) WITHOUT ROWID /* trailing clause comment */; INSERT INTO wr VALUES('k','v'); CREATE TABLE \"space | name\"(x); INSERT INTO \"space | name\" VALUES(1); CREATE TABLE other(x); CREATE TRIGGER other_tr AFTER INSERT ON other BEGIN SELECT 1; END; CREATE TABLE __mysqlpunk_column_comments(table_name TEXT NOT NULL,column_name TEXT NOT NULL,comment TEXT NOT NULL,PRIMARY KEY(table_name,column_name)); INSERT INTO __mysqlpunk_column_comments VALUES('t','x','line'||char(0)||'note'); INSERT INTO __mysqlpunk_column_comments VALUES('other','x','excluded');");
                var service=new SqliteSqlExportService(source,"main");
                Assert(!service.LoadTableCatalog().Exists(i=>i.Name=="__mysqlpunk_column_comments"),"Sidecar excluded from ordinary table selection");
                string file=Path.Combine(directory,"roundtrip.sql");
                var options=new SqliteExportWizardOptions(SqliteExportMode.SchemaAndData,file) { SelectedTables=new List<string>{"g","t","ai","empty_ai","wr","space | name","__mysqlpunk_column_comments"}, IncludeViews=false,SelectedTriggers=service.LoadTriggers(),PragmaForeignKeysOff=false,PragmaSynchronousOff=false };
                var exported=service.ExportAsync(options,null,CancellationToken.None).GetAwaiter().GetResult();
                Assert(exported.Success,"Generated/rowid/sequence/text/comments export succeeds: "+exported.ErrorMessage);
                string content=File.ReadAllText(file);
                AssertNotContains(content,"CREATE TRIGGER other_tr","Unselected table trigger is excluded");
                Assert(content.IndexOf('\0')<0,"Text NUL uses lossless SQL representation");
                var restored=SqliteRunFixture(target,directory,content);
                Assert(restored.Status==SqliteImportStatus.Completed && restored.ManifestVerified,"Generated/rowid/sequence/comments dump imports: "+string.Join(";",restored.Errors));
                Assert(Convert.ToInt64(target.Scalar("SELECT b+d FROM g;"))==18,"Virtual/stored generated columns recompute");
                AssertEquals(Convert.ToString(source.Scalar("SELECT hex(x)||':'||hex(y) FROM t WHERE rowid=-7;")),Convert.ToString(target.Scalar("SELECT hex(x)||':'||hex(y) FROM t WHERE rowid=-7;")),"Mixed newline/Unicode/quote/NUL/text and BLOB bytes exact");
                Assert(Convert.ToInt64(target.Scalar("SELECT max(rowid) FROM t;"))==9223372036854775806L,"Sparse negative/large implicit rowids survive");
                target.Must("INSERT INTO ai(x) VALUES('next'); INSERT INTO empty_ai DEFAULT VALUES;");
                Assert(Convert.ToInt64(target.Scalar("SELECT max(id) FROM ai;"))==101 && Convert.ToInt64(target.Scalar("SELECT id FROM empty_ai;"))==201,"Deleted and empty AUTOINCREMENT history survives");
                AssertEquals("v",Convert.ToString(target.Scalar("SELECT v FROM wr WHERE k='k';")),"WITHOUT ROWID keys survive");
                Assert(Convert.ToInt64(target.Scalar("SELECT count(*) FROM __mysqlpunk_column_comments;"))==1,"Selected comments exported exactly once");
                AssertEquals(Convert.ToString(source.Scalar("SELECT hex(comment) FROM __mysqlpunk_column_comments WHERE table_name='t';")),Convert.ToString(target.Scalar("SELECT hex(comment) FROM __mysqlpunk_column_comments WHERE table_name='t';")),"Comment NUL bytes survive");
            }
        }
        finally { Directory.Delete(directory,true); }
    }
    private static void SqliteExport_Failures_PreserveOutputAndRejectTamper()
    {
        string directory=Path.Combine(Path.GetTempPath(),"mysqlpunk_sqlite_export_fail_"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            using(var source=new SqliteFixtureDatabase())
            {
                source.Must("CREATE TABLE t(x); INSERT INTO t VALUES('old');");
                foreach(string table in new[]{"missing","t"})
                {
                    string file=Path.Combine(directory,"old.sql"); File.WriteAllText(file,"previous good dump");
                    var options=new SqliteExportWizardOptions(SqliteExportMode.SchemaAndData,file) { SelectedTables=new List<string>{table},IncludeViews=false,IncludeTriggers=false };
                    source.FailQueryContains=table=="t" ? "PRAGMA table_xinfo" : null;
                    var result=new SqliteSqlExportService(source,"main").ExportAsync(options,null,CancellationToken.None).GetAwaiter().GetResult();
                    Assert(result.Stage==SqliteExportStage.Failed,"Missing object and QueryError cannot report Completed");
                    AssertEquals("previous good dump",File.ReadAllText(file),"Failed source preserves previous dump");
                    Assert(source.MCT.AutoCommit,"Failed export releases source snapshot");
                }
                source.FailQueryContains=null;
                var good=SqliteExportFixture(source,directory,"t"); Assert(good.Success,"Checksum fixture export succeeds");
                byte[] data=File.ReadAllBytes(good.TargetPath); data[5]^=1; File.WriteAllBytes(good.TargetPath,data);
                Assert(!SqliteImportPreflightScanner.Scan(good.TargetPath,CancellationToken.None).Passed,"One-byte alteration is detected before writes");
                using(var target=new SqliteFixtureDatabase())
                {
                    var result=SqliteRunFixture(target,directory,File.ReadAllText(good.TargetPath));
                    Assert(result.Status==SqliteImportStatus.Failed && !target.TableExists("main","t"),"Tampered dump cannot mutate target");
                }
                source.Must("CREATE TABLE oversized(x BLOB); INSERT INTO oversized VALUES(zeroblob(8388609));");
                var large=SqliteExportFixture(source,directory,"oversized");
                Assert(large.Stage==SqliteExportStage.Failed && !File.Exists(large.TargetPath),"Oversize native row explicitly rejected before publication");
                source.Must("CREATE TABLE shadow(rowid,_rowid_,oid); INSERT INTO shadow VALUES(1,2,3);");
                var shadow=SqliteExportFixture(source,directory,"shadow");
                Assert(shadow.Stage==SqliteExportStage.Failed,"Inaccessible implicit rowid explicitly rejected");
                source.Must("CREATE VIRTUAL TABLE virtual_fixture USING rtree(id,minx,maxx,miny,maxy);");
                var virtualTable=SqliteExportFixture(source,directory,"virtual_fixture");
                Assert(virtualTable.Stage==SqliteExportStage.Failed,"Untested virtual module backup explicitly rejected");
            }
        }
        finally { Directory.Delete(directory,true); }
    }

    private static void SqliteExport_ReviewRegression_RepeatedCloseAndInitialOptions()
    {
        string directory=Path.Combine(Path.GetTempPath(),"mysqlpunk_sqlite_review_options_"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            using(var source=new SqliteFixtureDatabase())
            {
                source.Must("CREATE TABLE t(x); INSERT INTO t VALUES(1); CREATE VIEW v_t AS SELECT x FROM t; CREATE TRIGGER t_external AFTER INSERT ON t BEGIN INSERT INTO missing_dependency VALUES(new.x); END;");
                var initial=new SqliteExportWizardOptions(SqliteExportMode.SchemaAndData,Path.Combine(directory,"one.sql")) { SelectedTables=new List<string>{"t"},IncludeViews=false,IncludeTriggers=false };
                using(var form=SqliteCreateWizard("SqliteExportWizardForm",source,"main",initial))
                {
                    Assert(!((System.Windows.Forms.CheckBox)SqliteWizardField(form,"includeViews").GetValue(form)).Checked && !((System.Windows.Forms.CheckBox)SqliteWizardField(form,"includeTriggers").GetValue(form)).Checked,"Single-table flags seed the actual wizard controls");
                    var load=(Task)form.GetType().GetMethod("LoadCatalogAsync",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(form,null); SqliteWaitWizardTask(load);
                    var options=(SqliteExportWizardOptions)form.GetType().GetMethod("BuildExportOptions",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(form,null);
                    Assert(!options.IncludeViews && !options.IncludeTriggers && options.SelectedTables.Count==1 && options.SelectedTables[0]=="t","Options rebuilt from the actual controls retain the single-table scope");
                    var result=new SqliteSqlExportService(source,"main").ExportAsync(options,null,CancellationToken.None).GetAwaiter().GetResult();
                    Assert(result.Success && result.ViewCount==0 && result.TriggerCount==0,"Actual wizard options omit views and dependency-bearing triggers even in a one-table database");
                    string output=File.ReadAllText(result.TargetPath); AssertNotContains(output,"CREATE VIEW","No implicit view DDL"); AssertNotContains(output,"CREATE TRIGGER","No implicit trigger DDL");
                    SqliteAssertRepeatedCloseBlocked(form,source);
                }
                using(var form=SqliteCreateWizard("SqliteExportWizardForm",source,"main",initial))
                {
                    SqliteWizardField(form,"closing").SetValue(form,true);
                    var task=(Task)form.GetType().GetMethod("LoadCatalogAsync",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(form,null); SqliteWaitWizardTask(task);
                    Assert(form.IsDisposed,"Pending close is honored after export catalog worker cleanup");
                }
            }
        }
        finally { Directory.Delete(directory,true); }
    }

    private sealed class MockSqliteExportDatabase : IDatabase
    {
        public ConnectionState State => ConnectionState.Open;
        public string ProviderName => "sqlite";

        public void SetConn(string connectionString) { }
        public void Open() { }
        public void Close() { }
        public void Dispose() { }

        public DataTable SelectSQL(string sql, Dictionary<string, object> parameters = null)
        {
            var dt = new DataTable();
            if (sql.Contains("FROM sqlite_master WHERE type='table' AND sql LIKE 'CREATE VIRTUAL%'"))
            {
                dt.Columns.Add("name");
                dt.Rows.Add("idx_roads_geom");
                return dt;
            }
            if (sql.Contains("FROM geometry_columns"))
            {
                dt.Columns.Add("f_table_name");
                dt.Columns.Add("f_geometry_column");
                dt.Columns.Add("type");
                dt.Columns.Add("srid");
                dt.Rows.Add("spatial_roads", "geom", "POINT", "3826");
                return dt;
            }
            if (sql.Contains("FROM gpkg_geometry_columns"))
            {
                return dt;
            }
            if (sql.Contains("SELECT name, sql FROM sqlite_master WHERE type='table'"))
            {
                dt.Columns.Add("name");
                dt.Columns.Add("sql");
                dt.Rows.Add("spatial_roads", "CREATE TABLE \"spatial_roads\" (\"id\" INTEGER PRIMARY KEY, \"name\" TEXT, \"geom\" BLOB);");
                // 影子表 (R-Tree node)
                dt.Rows.Add("idx_roads_geom_node", "CREATE TABLE \"idx_roads_geom_node\" (nodeno INTEGER PRIMARY KEY, data BLOB);");
                return dt;
            }
            if (sql.Contains("WHERE type='table' AND name='spatial_roads'"))
            {
                dt.Columns.Add("sql");
                dt.Rows.Add("CREATE TABLE \"spatial_roads\" (\"id\" INTEGER PRIMARY KEY, \"name\" TEXT, \"geom\" BLOB);");
                return dt;
            }
            if (sql.Contains("SELECT name FROM sqlite_master WHERE type='view'"))
            {
                dt.Columns.Add("name");
                dt.Rows.Add("v_roads");
                return dt;
            }
            if (sql.Contains("WHERE type='view' AND name='v_roads'"))
            {
                dt.Columns.Add("sql");
                dt.Rows.Add("CREATE VIEW \"v_roads\" AS SELECT \"id\", \"name\" FROM \"spatial_roads\";");
                return dt;
            }
            if (sql.Contains("SELECT name FROM sqlite_master WHERE type='trigger'"))
            {
                dt.Columns.Add("name");
                dt.Rows.Add("trg_roads_audit");
                return dt;
            }
            if (sql.Contains("WHERE type='trigger' AND name='trg_roads_audit'"))
            {
                dt.Columns.Add("sql");
                dt.Rows.Add("CREATE TRIGGER \"trg_roads_audit\" AFTER INSERT ON \"spatial_roads\" BEGIN SELECT 1; END;");
                return dt;
            }
            if (sql.Contains("WHERE type='index'"))
            {
                dt.Columns.Add("sql");
                dt.Columns.Add("tbl_name");
                dt.Rows.Add("CREATE INDEX \"idx_roads_name\" ON \"spatial_roads\" (\"name\");", "spatial_roads");
                return dt;
            }
            return dt;
        }

        public Dictionary<string, string> ExecSQL(string sql, Dictionary<string, object> parameters = null)
        {
            return new Dictionary<string, string> { { "status", "OK" } };
        }

        public Task<DataTable> SelectSQLAsync(string sql, Dictionary<string, object> parameters = null) => Task.FromResult(SelectSQL(sql, parameters));
        public Task<Dictionary<string, string>> ExecSQLAsync(string sql, Dictionary<string, object> parameters = null) => Task.FromResult(ExecSQL(sql, parameters));

        public List<string> GetDatabases() => new List<string> { "main" };
        public List<string> GetTables(string databaseName) => new List<string> { "spatial_roads", "idx_roads_geom_node" };
        public List<string> GetViews(string databaseName) => new List<string> { "v_roads" };

        public DataTable GetColumns(string databaseName, string tableName)
        {
            var dt = new DataTable();
            dt.Columns.Add("COLUMN_NAME");
            dt.Columns.Add("DATA_TYPE");
            dt.Rows.Add("id", "INTEGER");
            dt.Rows.Add("name", "TEXT");
            dt.Rows.Add("geom", "BLOB");
            return dt;
        }

        public DataTable GetIndexes(string databaseName, string tableName) => new DataTable();
        public DataTable GetTableStatus(string databaseName) => new DataTable();
        public Dictionary<string, string> GetDatabaseInfo(string databaseName) => new Dictionary<string, string>();
        public string GetTableCreateStatement(string databaseName, string tableName) => "CREATE TABLE \"spatial_roads\" (\"id\" INTEGER PRIMARY KEY, \"name\" TEXT, \"geom\" BLOB);";
        public bool TableExists(string databaseName, string tableName) => true;
        public bool ViewExists(string databaseName, string viewName) => true;
        public void RenameTable(string databaseName, string oldTableName, string newTableName) { }
        public void RenameView(string databaseName, string oldViewName, string newViewName) { }
        public long CountRows(string databaseName, string tableName) => 2;
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
            dt.Columns.Add("geom", typeof(byte[]));
            if (offset == 0)
            {
                byte[] sampleBlob = new byte[] { 0x00, 0x01, 0x00, 0x00, 0x00, 0x7C, 0x01, 0x01, 0x00, 0x00, 0x00 };
                dt.Rows.Add(1, "Main Street", sampleBlob);
                dt.Rows.Add(2, "Broadway", sampleBlob);
            }
            return dt;
        }

        public void InsertTableBatch(string databaseName, string tableName, DataTable rows) { }
        public string GetViewCreateStatement(string databaseName, string viewName) => "CREATE VIEW \"v_roads\" AS SELECT \"id\", \"name\" FROM \"spatial_roads\";";
        public void CreateViewFromStatement(string databaseName, string viewName, string sourceViewSql) { }
    }
}
