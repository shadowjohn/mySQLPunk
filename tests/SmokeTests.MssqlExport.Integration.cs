using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using mySQLPunk.lib;
using mySQLPunk.lib.MssqlExport;
using Newtonsoft.Json;

public static partial class SmokeTests
{
    // This entry is inert in ordinary smoke runs. No saved connection configuration is read.
    private static void Integration_Run()
    {
        string profile = Environment.GetEnvironmentVariable("MYSQLPUNK_MSSQL_INTEGRATION");
        if (string.IsNullOrEmpty(profile)) { Console.WriteLine("SKIP live integration (explicit runner -Run required)"); return; }
        Assert(new[] { "OptionalSnapshot", "SnapshotLifecycle", "Core", "Large", "Concurrency", "ClientProbe", "FinalFix", "Geometry", "Properties", "References" }.Contains(profile), "Known integration profile");
        using (var fixture = new IntegrationFixture(profile, profile != "OptionalSnapshot" && profile != "SnapshotLifecycle"))
        {
            if (profile == "OptionalSnapshot") IntegrationOptionalSnapshot(fixture);
            else if (profile == "SnapshotLifecycle") IntegrationSnapshotLifecycle(fixture);
            else if (profile == "ClientProbe") IntegrationClientProbe(fixture);
            else if (profile == "FinalFix") IntegrationFinalFix(fixture);
            else if (profile == "Geometry") IntegrationGeometry(fixture);
            else if (profile == "Properties") IntegrationProperties(fixture);
            else if (profile == "References") IntegrationReferences(fixture);
            else if (profile == "Core") IntegrationCore(fixture);
            else if (profile == "Large") IntegrationLarge(fixture);
            else IntegrationConcurrency(fixture);
        }
    }

    private sealed class IntegrationFixture : IDisposable
    {
        internal readonly string Server, Root, Token, Source, Target, Profile;
        internal readonly List<string> Created = new List<string>();
        internal readonly Dictionary<string, object> Evidence = new Dictionary<string, object>();
        internal readonly List<SqlConnection> WorkerConnections = new List<SqlConnection>();
        internal Action AfterBegin;
        internal Action<MssqlTableDefinition> BeforeRows;
        internal bool RestrictedReader;
        internal bool ParentFirst;
        internal string SourceOverride, JournalOverride;
        internal Action<bool> BeforeSetting, AfterSetting;
        internal readonly List<string> SettingAudit = new List<string>();
        internal readonly List<string> LockAudit = new List<string>();
        private int restoreSequence;
        internal IntegrationFixture(string profile, bool initialSnapshot = true)
        {
            Profile = profile;
            Server = Environment.GetEnvironmentVariable("MYSQLPUNK_MSSQL_INTEGRATION_SERVER");
            Assert(Server == @"(localdb)\mySQLPunkExportTest_20261012", "Explicit authorized fixture server only");
            Root = Path.GetFullPath(Environment.GetEnvironmentVariable("MYSQLPUNK_MSSQL_INTEGRATION_OUTPUT"));
            Assert(Root.StartsWith(@"C:\", StringComparison.OrdinalIgnoreCase) && Directory.Exists(Root) && !Directory.EnumerateFileSystemEntries(Root).Any(), "New empty C: fixture directory");
            Token = Guid.NewGuid().ToString("N"); Source = "mysqlpunk_export_" + Token + "_src"; Target = "mysqlpunk_export_" + Token + "_dst";
            File.WriteAllText(Path.Combine(Root,"owner.token"),Token);
            Evidence["server"] = Server; Evidence["profile"] = profile; Evidence["source"] = Source; Evidence["target"] = Target; Evidence["owner"] = Token;
            Evidence["processBits"] = IntPtr.Size * 8; Evidence["provider"] = typeof(SqlConnection).Assembly.FullName;
            Save();
            try
            {
                using (var c = Open("master"))
                {
                    Evidence["engine"] = Scalar(c,"SELECT CONVERT(nvarchar(128),SERVERPROPERTY('ProductVersion'))");
                    Evidence["tempdbCollation"] = Scalar(c,"SELECT CONVERT(nvarchar(128),DATABASEPROPERTYEX('tempdb','Collation'))");
                    foreach (var db in new[] { Source, Target })
                    {
                        string data = Path.Combine(Root, db + ".mdf"), log = Path.Combine(Root, db + ".ldf");
                        Exec(c,"CREATE DATABASE " + Q(db) + " ON PRIMARY (NAME=N'data',FILENAME=" + Lit(data) + ",SIZE=16MB,MAXSIZE=3072MB,FILEGROWTH=64MB) LOG ON(NAME=N'log',FILENAME=" + Lit(log) + ",SIZE=16MB,MAXSIZE=512MB,FILEGROWTH=64MB) COLLATE Latin1_General_100_BIN2");
                        Created.Add(db);
                        Exec(c,"USE " + Q(db) + "; EXEC sys.sp_addextendedproperty @name=N'mysqlpunk_export_owner',@value=" + Lit(Token) + ";");
                        Exec(c,"USE master; ALTER DATABASE " + Q(db) + " SET RECOVERY SIMPLE;");
                    }
                    if (initialSnapshot) Exec(c,"ALTER DATABASE " + Q(Source) + " SET ALLOW_SNAPSHOT_ISOLATION ON;");
                    Evidence["initialSnapshotRequested"] = initialSnapshot;
                    Evidence["initialSnapshotObserved"] = Scalar(c,"SELECT snapshot_isolation_state FROM sys.databases WHERE name=" + Lit(Source));
                }
                Evidence["sourceCollation"] = "Latin1_General_100_BIN2";
                Save();
            }
            catch { Dispose(); throw; }
        }
        internal static string Q(string s) { return "[" + s.Replace("]", "]]") + "]"; }
        internal static string Lit(string s) { return "N'" + s.Replace("'", "''") + "'"; }
        internal SqlConnection Open(string db)
        {
            Assert(db == "master" || db == Source || db == Target, "Only own databases");
            var c = new SqlConnection(new SqlConnectionStringBuilder { DataSource = Server, InitialCatalog = db, IntegratedSecurity = true, Pooling = false, TrustServerCertificate = true, ApplicationName = "mySQLPunk-owned-integration" }.ConnectionString);
            c.Open(); return c;
        }
        internal static void Exec(SqlConnection c,string sql)
        { using (var cmd = new SqlCommand(sql,c) { CommandTimeout = 0 }) cmd.ExecuteNonQuery(); }
        internal static object Scalar(SqlConnection c,string sql)
        { using (var cmd = new SqlCommand(sql,c) { CommandTimeout = 0 }) return cmd.ExecuteScalar(); }
        internal void Setup(string name, int factor = 1)
        {
            string sql = File.ReadAllText(Path.Combine(Environment.GetEnvironmentVariable("MYSQLPUNK_MSSQL_INTEGRATION_FIXTURES"),name));
            if (name == "large.sql") sql = sql.Replace("@Rows int=1024", "@Rows int=" + (1024 * factor).ToString(CultureInfo.InvariantCulture));
            using (var c = Open(Source))
                foreach (var batch in Regex.Split(sql,@"(?im)^GO\s*\r?$")) if (!string.IsNullOrWhiteSpace(batch)) Exec(c,batch);
        }
        internal IMssqlExportSource NewSource()
        {
            Assert(SourceOverride == null || SourceOverride == Source || SourceOverride == Target, "Source factory uses exact owned database whitelist");
            Assert(JournalOverride == null || Path.GetFullPath(JournalOverride).StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Injected journal remains inside owned fixture root");
            var provider = new my_mssql();
            provider.SetConn(new SqlConnectionStringBuilder { DataSource = Server, InitialCatalog = SourceOverride ?? Source, IntegratedSecurity = true, Pooling = false, TrustServerCertificate = true }.ConnectionString);
            provider.Open(); WorkerConnections.Add(provider.MCT);
            if (RestrictedReader) Exec(provider.MCT,"EXECUTE AS USER = N'fixture_reader';");
            return new IntegrationSource(new MssqlExportSource(new MssqlOwnedSession(provider,provider.MCT), new MssqlSnapshotRecoveryStore(JournalOverride ?? Path.Combine(Root,"journal")), c => new IntegrationSnapshotBackend(c,this)),this);
        }
        internal MssqlSqlExportService Service(MssqlArtifactFiles files = null, Action<MssqlExportStage> phase = null)
        {
            return new MssqlSqlExportService(NewSource,files ?? new MssqlArtifactFiles(),stage => {
                Assert(WorkerConnections.All(c => c.State == ConnectionState.Closed), "Real source connections released before file " + stage);
                Evidence["sourceReleasedBeforeLocalPhases"] = true;
                if (phase != null) phase(stage);
            }, new MssqlSnapshotRecoveryStore(Path.Combine(Root,"journal")));
        }
        internal MssqlExportRequest Request(string filename = "export.sql", bool views = true, MssqlExportMode mode = MssqlExportMode.SchemaAndData, Func<MssqlTableDefinition,bool> select = null, bool mirror = false, bool? useSnapshot = null)
        {
            var tables = Service().LoadCatalog(Source,CancellationToken.None).Tables.Where(t => select == null || select(t)).Select(t => t.Key).ToArray();
            string path = Path.Combine(Root,filename);
            return new MssqlExportRequest(Guid.NewGuid(),Source,tables,mode,views,path,MssqlExportArtifact.CaptureDestination(path,CancellationToken.None),mirror ? new MssqlMirrorOptions(Path.Combine(Root,"mirror"),2) : null, useSnapshot ?? (mode != MssqlExportMode.SchemaOnly));
        }
        internal MssqlExportResult Export(MssqlExportRequest request, CancellationToken cancellation = default(CancellationToken), MssqlArtifactFiles files = null, Action<MssqlExportStage> phase = null, Action<MssqlExportProgress> progress = null)
        {
            using (var memory = new IntegrationMemory())
            {
                var result = Service(files,phase).Export(request,new WorkerProgress(p => { memory.Stage = p.Stage.ToString(); if (progress != null) progress(p); }),cancellation);
                Evidence["export-" + Path.GetFileName(request.TargetPath)] = new { stage=result.Stage.ToString(), result.Elapsed, result.ErrorMessage, result.PublishedPath, result.MirrorPath, request.UseSnapshot, cleanup = result.SnapshotCleanup, snapshotState = SnapshotState(this), journal = Directory.Exists(Path.Combine(Root,"journal")) ? Directory.GetFiles(Path.Combine(Root,"journal")) : new string[0], settingAudit = SettingAudit.ToArray(), lockAudit = LockAudit.ToArray(), rows = result.RowCounts.Sum(x => x.Rows), memory = memory.Snapshot() };
                Save(); return result;
            }
        }
        internal void Restore(string file, bool expectSuccess = true)
        {
            Assert(File.Exists(file),"Restore artifact exists");
            RecordArtifact(file);
            bool go = Environment.GetEnvironmentVariable("MYSQLPUNK_MSSQL_INTEGRATION_CLIENT") == "Go";
            string endpoint = go ? "np:"+Environment.GetEnvironmentVariable("MYSQLPUNK_MSSQL_INTEGRATION_PIPE") : Server;
            string args = "-S \"" + endpoint + "\" -d " + Target + " -E -b -x " + (go ? "" : "-f 65001 ") + "-i \"" + file + "\"";
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("MYSQLPUNK_MSSQL_INTEGRATION_SQLCMD"),args) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true };
            var elapsed = Stopwatch.StartNew();
            using (var p = Process.Start(start))
            {
                var stdout = p.StandardOutput.ReadToEndAsync(); var stderr = p.StandardError.ReadToEndAsync(); if (!p.WaitForExit(Profile == "Large" ? 3600000 : 300000)) { p.Kill(); p.WaitForExit(10000); throw new TimeoutException("Owned sqlcmd restore exceeded profile deadline"); }
                int attempt = ++restoreSequence;
                File.WriteAllText(file + ".restore-" + attempt + ".log",stdout.Result + stderr.Result);
                elapsed.Stop();
                Evidence["restore-" + Path.GetFileName(file) + "-" + attempt] = new { command = "sqlcmd " + args, exitCode = p.ExitCode, elapsed=elapsed.Elapsed };
                Save(); Assert(expectSuccess ? p.ExitCode == 0 : p.ExitCode != 0,"sqlcmd single-session expected exit for " + file + ": " + p.ExitCode);
            }
        }
        internal void RecordArtifact(string file)
        {
            using (var sha = SHA256.Create()) using (var stream = File.OpenRead(file))
                Evidence["artifact-" + Path.GetFileName(file)] = new { bytes=stream.Length, sha256=BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant() };
            Save();
        }
        internal void Pass(string message) { Console.WriteLine("PASS " + message); Evidence[message] = "PASS"; Save(); }
        internal void Save() { File.WriteAllText(Path.Combine(Root,"evidence.json"),JsonConvert.SerializeObject(Evidence,Formatting.Indented)); }
        public void Dispose()
        {
            Assert(File.ReadAllText(Path.Combine(Root,"owner.token")) == Token,"File ownership matches before cleanup");
            foreach (string db in Created.ToArray())
            {
                Assert(db == "mysqlpunk_export_" + Token + "_src" || db == "mysqlpunk_export_" + Token + "_dst","Exact generated DB before DROP");
                using (var c = Open(db))
                {
                    Assert(Convert.ToString(Scalar(c,"SELECT value FROM sys.extended_properties WHERE class=0 AND name=N'mysqlpunk_export_owner'")) == Token,"DB token before DROP");
                    using (var cmd = new SqlCommand("SELECT physical_name FROM sys.database_files",c)) using (var r = cmd.ExecuteReader())
                        while (r.Read()) Assert(Path.GetDirectoryName(Path.GetFullPath(r.GetString(0))) == Root,"Owned database file path before DROP");
                }
                using (var c = Open("master")) Exec(c,"ALTER DATABASE " + Q(db) + " SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE " + Q(db));
                Evidence["cleaned-" + db] = DateTime.UtcNow; Save();
                Created.Remove(db); Console.WriteLine("CLEANED " + db);
            }
            Evidence["cleanup"] = "All exact token-owned databases dropped; artifacts retained"; Save();
        }
    }

    private sealed class IntegrationSource : IMssqlExportSource
    {
        private readonly IMssqlExportSource inner; private readonly IntegrationFixture fixture; private MssqlExportSchema originalReadSchema;
        internal IntegrationSource(IMssqlExportSource inner, IntegrationFixture fixture) { this.inner=inner; this.fixture=fixture; }
        public MssqlExportCatalog ReadCatalog(string d,CancellationToken c) { return inner.ReadCatalog(d,c); }
        public MssqlExportPreflight Inspect(MssqlExportRequest r,CancellationToken c) { return inner.Inspect(r,c); }
        public MssqlExportSchema BeginRead(MssqlExportRequest r,Action<MssqlExportStage> report,CancellationToken c) { var schema=inner.BeginRead(r,report,c); originalReadSchema=schema; if(fixture.AfterBegin!=null)fixture.AfterBegin(); if(fixture.ParentFirst) schema = new MssqlExportSchema(schema.Tables.OrderBy(t=>t.Key.Name=="Parent"?0:1),schema.Views,schema.Dependencies,schema.SnapshotState,schema.Issues,schema.ExtendedProperties,schema.SnapshotCapability); return schema; }
        public MssqlRowReader OpenRows(MssqlTableDefinition t,CancellationToken c) { if(fixture.BeforeRows!=null)fixture.BeforeRows(t); return inner.OpenRows(t,c); }
        public MssqlSnapshotCleanupResult FinishRead(Action<MssqlExportStage> report) { return inner.FinishRead(report); }
        public void CompleteRead(MssqlExportSchema s,CancellationToken c) { inner.CompleteRead(originalReadSchema ?? s,c); }
        public void Dispose() { inner.Dispose(); }
    }

    private sealed class IntegrationMemory : IDisposable
    {
        internal volatile string Stage="Preflight";
        private readonly Dictionary<string,long[]> peaks = new Dictionary<string,long[]>();
        private readonly Timer timer;
        internal IntegrationMemory() { timer=new Timer(_=>Sample(),null,0,25); }
        private void Sample() { using(var p=Process.GetCurrentProcess()) { p.Refresh(); lock(peaks) { long[] pair; if(!peaks.TryGetValue(Stage,out pair))peaks[Stage]=pair=new long[3]; pair[0]=Math.Max(pair[0],p.WorkingSet64);pair[1]=Math.Max(pair[1],p.PrivateMemorySize64);pair[2]++; } } }
        internal object Snapshot() { Sample(); lock(peaks)return peaks.ToDictionary(x=>x.Key,x=>new {workingSet=x.Value[0],privateBytes=x.Value[1],samples=x.Value[2]}); }
        public void Dispose() { using(var done=new ManualResetEvent(false)){timer.Dispose(done);done.WaitOne();} }
    }

    private static void IntegrationFinalFix(IntegrationFixture f)
    {
        const string table = "dbo.IndexedComputed";
        const string ddl = "CREATE TABLE dbo.IndexedComputed(id int NOT NULL, value int NOT NULL, doubled AS (value * 2) PERSISTED); CREATE UNIQUE INDEX IX_IndexedComputed_doubled ON dbo.IndexedComputed(doubled);";
        using (var c = f.Open(f.Source))
            IntegrationFixture.Exec(c, ddl + " INSERT dbo.IndexedComputed(id,value) VALUES(1,7),(2,19);");
        using (var c = f.Open(f.Source))
            IntegrationFixture.Exec(c, "CREATE TABLE dbo.WideEstimate(id int NOT NULL, pad char(4000) NOT NULL); INSERT dbo.WideEstimate SELECT TOP (40) ROW_NUMBER() OVER (ORDER BY object_id), REPLICATE('X',4000) FROM sys.all_objects;");
        using (var c = f.Open(f.Target)) IntegrationFixture.Exec(c, ddl);
        var catalog = f.Service().LoadCatalog(f.Source, CancellationToken.None);
        var selected = catalog.Tables.Single(t => t.Key.Schema == "dbo" && t.Key.Name == "IndexedComputed");
        var wide = catalog.Tables.Single(t => t.Key.Schema == "dbo" && t.Key.Name == "WideEstimate");
        Assert(selected.EstimatedRows.HasValue && selected.EstimatedBytes.HasValue && selected.EstimatedBytes.Value > 0 && wide.EstimatedBytes > selected.EstimatedBytes, "Real native catalog differentiates table storage");
        var estimateSchema = new MssqlExportSchema(new[] { selected, wide }, null, null, 1, null);
        var progress = new MssqlExportProgressEstimator(Guid.NewGuid(), estimateSchema, false).Sample(MssqlExportStage.Data, selected.Key, 1, 100, 1000, TimeSpan.FromSeconds(3), true);
        Assert(progress.EstimatedBytes == selected.EstimatedBytes + wide.EstimatedBytes && progress.Percent.HasValue, "Actual metadata flows into weighted estimator");
        f.Evidence["actualStorageEstimate"] = new { selected.EstimatedRows, selected.EstimatedBytes, wideBytes = wide.EstimatedBytes, progress.Percent };
        var request = f.Request("data-only-indexed-computed.sql", false, MssqlExportMode.DataOnly, t => t.Key.Name == "IndexedComputed");
        var result = f.Export(request);
        Assert(result.Stage == MssqlExportStage.Completed, "DataOnly service artifact: " + result.ErrorMessage);
        string script = File.ReadAllText(result.PublishedPath);
        AssertContains(script, "SET QUOTED_IDENTIFIER ON;", "DataOnly artifact carries session preamble");
        AssertNotContains(script, "CREATE TABLE", "DataOnly artifact has no object creation");
        AssertNotContains(script, "ALTER TABLE", "DataOnly artifact has no object alteration");
        Assert(Environment.GetEnvironmentVariable("MYSQLPUNK_MSSQL_INTEGRATION_CLIENT") == "Classic", "Exact classic sqlcmd required");
        f.RecordArtifact(result.PublishedPath);
        f.Restore(result.PublishedPath);
        using (var c = f.Open(f.Target))
        {
            Assert(Convert.ToInt32(IntegrationFixture.Scalar(c, "SELECT COUNT(*) FROM " + table + " WHERE doubled=value*2")) == 2, "Indexed computed target contains both restored rows");
            Assert(Convert.ToInt32(IntegrationFixture.Scalar(c, "SELECT COUNT(*) FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.IndexedComputed') AND name=N'IX_IndexedComputed_doubled'")) == 1, "Compatible computed-column index remains");
        }
        f.Pass("Real DataOnly service artifact restored through documented classic one-session sqlcmd -E -b -x -f 65001 -i");
    }

    private static void IntegrationGeometry(IntegrationFixture f)
    {
        Assert(Environment.GetEnvironmentVariable("MYSQLPUNK_MSSQL_INTEGRATION_CLIENT") == "Classic", "Geometry requires documented classic sqlcmd");
        f.Setup("geometry.sql");
        // Fixture creation is independent from exporter allocation; no large string/byte array.
        using(var setupMemory=new IntegrationMemory()) {
            setupMemory.Stage="FixtureGenerationAndUpload";
            IntegrationGeometryLine(f,"large-geometry.wkb","dbo.GeometryLarge",1100001);
            IntegrationGeometryLine(f,"guard-geometry.wkb","dbo.GeometryGuard",20001);
            f.Evidence["geometryFixtureMemorySeparateFromExporter"]=setupMemory.Snapshot();f.Save();
        }
        using(var c=f.Open(f.Source)) {
            Assert(Convert.ToInt32(IntegrationFixture.Scalar(c,"SELECT CASE WHEN geometry::Deserialize(NULL) IS NULL THEN 1 ELSE 0 END"))==1,"Real Deserialize(NULL) returns SQL NULL");
            long length=Convert.ToInt64(IntegrationFixture.Scalar(c,"SELECT DATALENGTH(g.Serialize()) FROM dbo.GeometryLarge"));
            Assert(length>16L*1024*1024,"Actual serialized geometry exceeds 16MiB");f.Evidence["largeGeometrySerializedBytes"]=length;
            Assert(Convert.ToInt32(IntegrationFixture.Scalar(c,"SELECT g.STIsValid() FROM dbo.GeometryCases WHERE id=8"))==0,"Constructible invalid polygon stays invalid");
            Assert(Convert.ToInt32(IntegrationFixture.Scalar(c,"SELECT COUNT(*) FROM dbo.GeometryCases WHERE id=3 AND g.STSrid=3826 AND g.Z=3 AND g.M=4"))==1,"Fixture has independent point SRID/Z/M");
        }
        var req=f.Request("geometry.sql",false);
        Assert(f.Service().Preflight(req,CancellationToken.None).CanStart,"Native geometry actual preflight");
        var result=f.Export(req);Assert(result.Stage==MssqlExportStage.Completed,"Geometry service artifact: "+result.ErrorMessage);
        f.RecordArtifact(result.PublishedPath);f.Restore(result.PublishedPath);IntegrationCompare(f,req);
        using(var a=f.Open(f.Source))using(var b=f.Open(f.Target)) {
            string observations="SELECT id,g.STSrid AS srid,g.STGeometryType() AS kind,g.STIsEmpty() AS empty,g.STIsValid() AS valid,g.STNumPoints() AS points,g.STPointN(1).Z AS z,g.STPointN(1).M AS m,h.STSrid AS secondSrid,h.STGeometryType() AS secondKind,h.STIsEmpty() AS secondEmpty FROM dbo.GeometryCases WHERE id<>8";
            string invalid="SELECT id,g.STSrid AS srid,g.STIsValid() AS valid FROM dbo.GeometryCases WHERE id=8";
            string left=IntegrationCatalog(a,observations)+"\n"+IntegrationCatalog(a,invalid),right=IntegrationCatalog(b,observations)+"\n"+IntegrationCatalog(b,invalid);
            Assert(left==right,"Independent SRID/Z/M/type/empty/validity/point observations preserved");
            File.WriteAllText(Path.Combine(f.Root,"geometry-observations-source.txt"),left);File.WriteAllText(Path.Combine(f.Root,"geometry-observations-target.txt"),right);
            Assert(Convert.ToInt32(IntegrationFixture.Scalar(b,"SELECT COUNT(*) FROM dbo.GeometryCases WHERE id=1 AND g IS NULL AND h.STIsEmpty()=1"))==1,"SQL NULL distinct from valid empty geometry");
            Assert(Convert.ToInt32(IntegrationFixture.Scalar(b,"SELECT g.STIsValid() FROM dbo.GeometryCases WHERE id=8"))==0,"Invalid geometry bytes never repaired");
        }
        f.Pass("Geometry real service classic sqlcmd restore preserves Serialize hashes native catalog SRID Z M empty invalid and curves");
        // All supported modes use the same native type/projection contract.
        var schemaReq=f.Request("geometry-schema-only.sql",false,MssqlExportMode.SchemaOnly,t=>t.Key.Name=="GeometryCases");
        result=f.Export(schemaReq);Assert(result.Stage==MssqlExportStage.Completed,"Geometry SchemaOnly service");
        using(var c=f.Open(f.Target))IntegrationFixture.Exec(c,"DROP TABLE dbo.GeometryCases;");
        f.RecordArtifact(result.PublishedPath);f.Restore(result.PublishedPath);
        using(var c=f.Open(f.Target))Assert(Convert.ToInt32(IntegrationFixture.Scalar(c,"SELECT COUNT(*) FROM dbo.GeometryCases"))==0,"SchemaOnly creates empty native geometry table");
        var dataReq=f.Request("geometry-data-only.sql",false,MssqlExportMode.DataOnly,t=>t.Key.Name=="GeometryCases");
        result=f.Export(dataReq);Assert(result.Stage==MssqlExportStage.Completed,"Geometry DataOnly service");f.RecordArtifact(result.PublishedPath);f.Restore(result.PublishedPath);
        IntegrationCompare(f,req);
        IntegrationGeometryGuards(f);
        using(var c=f.Open(f.Source))IntegrationFixture.Exec(c,"CREATE TABLE dbo.GeometryRejectedGeography(g geography); CREATE TABLE dbo.GeometryRejectedIndex(id int NOT NULL PRIMARY KEY,g geometry); CREATE SPATIAL INDEX IX_GeometryRejected ON dbo.GeometryRejectedIndex(g) USING GEOMETRY_GRID WITH (BOUNDING_BOX=(0,0,100,100));");
        foreach(var name in new[]{"GeometryRejectedGeography","GeometryRejectedIndex"})
        foreach(var mode in new[]{MssqlExportMode.SchemaAndData,MssqlExportMode.DataOnly,MssqlExportMode.SchemaOnly}) {
            var rejected=f.Request("reject-"+name+"-"+mode+".sql",false,mode,t=>t.Key.Name==name);
            var pre=f.Service().Preflight(rejected,CancellationToken.None);string code=name=="GeometryRejectedGeography"?"UnsupportedSpatial":"UnsupportedIndex";
            Assert(!pre.CanStart && pre.Issues.Any(x=>x.Code==code),"Actual unsupported preflight "+name+" "+mode);
            var failed=f.Export(rejected);Assert(failed.Stage==MssqlExportStage.Failed && !File.Exists(rejected.TargetPath),"Actual unsupported geometry selection publishes nothing");
        }
        f.Pass("Geometry all modes and retained geography spatial-index preflight rejection");
    }
    private static void IntegrationGeometryLine(IntegrationFixture f,string filename,string table,int points)
    {
        string file=Path.Combine(f.Root,filename);
        using(var stream=new FileStream(file,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536))using(var writer=new BinaryWriter(stream)) {
            writer.Write((byte)1);writer.Write((uint)2);writer.Write((uint)points);
            for(int i=0;i<points;i++){writer.Write((double)i);writer.Write((double)(i%2));}
        }
        f.RecordArtifact(file);
        using(var c=f.Open(f.Source))using(var stream=File.OpenRead(file))
        using(var command=new SqlCommand("INSERT "+table+"(id,g) VALUES(1,geometry::STGeomFromWKB(@wkb,4326));",c){CommandTimeout=0}) {
            command.Parameters.Add("@wkb",SqlDbType.VarBinary,-1).Value=stream;command.ExecuteNonQuery();
        }
    }
    private static void IntegrationGeometryGuards(IntegrationFixture f)
    {
        using(var c=f.Open(f.Target))IntegrationFixture.Exec(c,"TRUNCATE TABLE dbo.GeometryGuard;");
        var req=f.Request("geometry-guards.sql",false,MssqlExportMode.DataOnly,t=>t.Key.Name=="GeometryGuard");
        var result=f.Export(req);Assert(result.Stage==MssqlExportStage.Completed,"Geometry guarded service artifact");f.RecordArtifact(req.TargetPath);
        var batches=Regex.Split(File.ReadAllText(req.TargetPath),@"(?m)^GO\r?$").Where(s=>!string.IsNullOrWhiteSpace(s)).ToArray();
        var chunks=Enumerable.Range(0,batches.Length).Where(i=>batches[i].Contains(".WRITE(")).ToArray();
        int final=Array.FindLastIndex(batches,s=>s.Contains("INSERT INTO [dbo].[GeometryGuard]"));
        Assert(chunks.Length>2 && final>=0,"Actual geometry uses chunked stage");
        foreach(var fault in new[]{"missing","duplicate","outoforder","malformed","replay"}) {
            using(var c=f.Open(f.Target))IntegrationFixture.Exec(c,"TRUNCATE TABLE dbo.GeometryGuard;");
            var altered=new List<string>();
            for(int i=0;i<batches.Length;i++) {
                if(fault=="missing" && i==chunks[0])continue;
                string sql=batches[i];
                if(fault=="outoforder" && i==chunks[0])sql=batches[chunks[1]];
                if(fault=="outoforder" && i==chunks[1])sql=batches[chunks[0]];
                if(fault=="malformed" && i==chunks[0]) {
                    // Corrupt serialized version byte without changing chunk/length accounting.
                    int at=sql.IndexOf(".WRITE(0x",StringComparison.Ordinal)+9+8;
                    Assert(at>17 && Uri.IsHexDigit(sql[at]),"Serialized geometry header mutation located");
                    sql=sql.Substring(0,at)+"FF"+sql.Substring(at+2);
                }
                altered.Add(sql);if(fault=="duplicate" && i==chunks[0])altered.Add(sql);
            }
            altered.Add(batches[final]);
            // A second attempted row cannot revive Failed; replay cannot insert twice either.
            altered.AddRange(batches.Where(s=>!s.Contains("CREATE TABLE #mp_") || !s.Contains("LOB job already exists")));
            string copy=Path.Combine(f.Root,"geometry-guard-"+fault+".sql");File.WriteAllText(copy,string.Join("\r\nGO\r\n",altered)+"\r\nGO\r\n",new UTF8Encoding(false));
            f.Restore(copy,false);
            using(var c=f.Open(f.Target)) {
                Assert(Convert.ToInt32(IntegrationFixture.Scalar(c,"SELECT COUNT(*) FROM dbo.GeometryGuard"))==(fault=="replay"?1:0),"Fail-fast geometry failed row absent "+fault);
                IntegrationFixture.Exec(c,"TRUNCATE TABLE dbo.GeometryGuard;");int errors=0;
                foreach(string batch in altered)try{IntegrationFixture.Exec(c,batch);}catch(SqlException){errors++;}
                Assert(errors>0 && Convert.ToInt32(IntegrationFixture.Scalar(c,"SELECT COUNT(*) FROM dbo.GeometryGuard"))==(fault=="replay"?1:0),"Continuing importer cannot revive geometry protocol "+fault);
            }
            f.Pass("Geometry "+fault+" fail-fast and continuing importer guards");
        }
    }

    private static void IntegrationCore(IntegrationFixture f)
    {
        f.Setup("core.sql"); f.Setup("core-lob.sql");
        var request=f.Request(mirror:true);
        Assert(f.Service().Preflight(request,CancellationToken.None).CanStart,"Core preflight passes");
        var result=f.Export(request); Assert(result.Stage==MssqlExportStage.Completed,"Core service: "+result.ErrorMessage);
        f.RecordArtifact(result.PublishedPath); f.Restore(result.PublishedPath);
        IntegrationCompare(f,request);
        using(var c=f.Open(f.Target))
        {
            Assert(Convert.ToInt64(IntegrationFixture.Scalar(c,"SELECT DATALENGTH(b) FROM dbo.MultiLob WHERE tag=4"))==67108864,"64MiB binary restore");
            Assert(Convert.ToInt64(IntegrationFixture.Scalar(c,"SELECT DATALENGTH(n) FROM dbo.MultiLob WHERE tag=4"))==16793600,"Unicode >16MiB restore");
            Assert(Convert.ToInt32(IntegrationFixture.Scalar(c,"INSERT dbo.IdentityEmpty(v) VALUES(9); SELECT SCOPE_IDENTITY();"))==11,"Empty identity seed");
            Assert(Convert.ToInt32(IntegrationFixture.Scalar(c,"INSERT dbo.IdentityNegative(v) VALUES(9); SELECT SCOPE_IDENTITY();"))==-9,"Descending identity next");
            Assert(Convert.ToInt32(IntegrationFixture.Scalar(c,"INSERT [一].[同名]]表]([unique]) VALUES(4); SELECT SCOPE_IDENTITY();"))==9,"Ascending identity next");
            Assert(Convert.ToInt32(IntegrationFixture.Scalar(c,"SELECT COUNT(*) FROM [一].[同名]]表] WHERE DATALENGTH(rv)=8 AND calc=[unique]+defaulted"))==4,"Regenerated rowversion and computed values");
        }
        f.Pass("Core full service sqlcmd restore, metadata and streaming raw hashes, identity, computed and rowversion");
        f.Restore(result.PublishedPath,false); // Existing CREATE must fail without replacing destination.
        IntegrationUnsupported(f);
        IntegrationGuards(f);
    }

    private static void IntegrationClientProbe(IntegrationFixture f)
    {
        string probe=Path.Combine(f.Root,"client-probe.sql");
        File.WriteAllText(probe,"CREATE TABLE #probe([中文]]欄] nvarchar(max));\r\nINSERT #probe VALUES(N'中文 $(name) :r literal\r\nGO inside string');\r\nGO\r\nIF NOT EXISTS(SELECT 1 FROM #probe WHERE [中文]]欄]=N'中文 $(name) :r literal\r\nGO inside string') THROW 51000,'UTF8/session/parser mismatch',1;\r\nIF NOT EXISTS(SELECT 1 FROM #probe WHERE UNICODE([中文]]欄])=20013) THROW 51000,'UTF8 codepoint mismatch',1;\r\nIF NOT EXISTS(SELECT 1 FROM tempdb.sys.columns WHERE object_id=OBJECT_ID('tempdb..#probe') AND name=NCHAR(20013)+NCHAR(25991)+N']'+NCHAR(27396)) THROW 51000,'UTF8 identifier mismatch',1;\r\nPRINT 'UTF8_SESSION_VARIABLE_GO_PASS';\r\nDROP TABLE #probe;\r\nGO\r\n",new UTF8Encoding(false));
        f.Restore(probe);
        Assert(File.ReadAllText(probe+".restore-1.log").Contains("UTF8_SESSION_VARIABLE_GO_PASS"),"Native UTF8 literal/identifier, disabled substitution, GO and same-session temp table");
        string failure=Path.Combine(f.Root,"client-failfast.sql");
        File.WriteAllText(failure,"THROW 51000,'EXPECTED_FAILFAST',1;\r\nGO\r\nPRINT 'UNEXPECTED_AFTER_ERROR';\r\nGO\r\n",new UTF8Encoding(false));
        f.Restore(failure,false);
        Assert(!File.ReadAllText(failure+".restore-2.log").Contains("UNEXPECTED_AFTER_ERROR"),"Client -b stops after SQL error");
        f.Pass("Native UTF8 Unicode identifiers/literals, literal dollar-parenthesis, GO parser, one session and fail-fast");
    }

    private static void IntegrationLarge(IntegrationFixture f)
    {
        int factor=int.Parse(Environment.GetEnvironmentVariable("MYSQLPUNK_MSSQL_INTEGRATION_FACTOR"),CultureInfo.InvariantCulture);
        Assert(factor==1 || factor==10,"Known large factor");
        string prior=Environment.GetEnvironmentVariable("MYSQLPUNK_MSSQL_INTEGRATION_RESTORE");
        f.Setup("large.sql",factor);
        if(!string.IsNullOrEmpty(prior))
        {
            Assert(factor==10 && new FileInfo(prior).Length>2147483648L,"Reuse actual >2GiB service artifact");
            string artifact=Path.Combine(f.Root,"export.sql"); File.Copy(prior,artifact,false);
            string tail;
            using(var stream=File.OpenRead(artifact))
            { stream.Seek(Math.Max(0,stream.Length-16384),SeekOrigin.Begin); using(var reader=new StreamReader(stream,Encoding.UTF8))tail=reader.ReadToEnd(); }
            var pk=Regex.Match(tail,@"ADD CONSTRAINT \[(?<name>PK__[^\]]+)\] PRIMARY KEY");
            Assert(pk.Success,"Original generated Large PK name");
            using(var c=f.Open(f.Source))
            {
                string old=Convert.ToString(IntegrationFixture.Scalar(c,"SELECT name FROM sys.key_constraints WHERE parent_object_id=OBJECT_ID('dbo.LargeBinary')"));
                IntegrationFixture.Exec(c,"EXEC sys.sp_rename "+IntegrationFixture.Lit("dbo."+old)+","+IntegrationFixture.Lit(pk.Groups["name"].Value)+",N'OBJECT';");
                f.Evidence["sourcePayloadBytes"]=IntegrationFixture.Scalar(c,"SELECT SUM(CONVERT(bigint,DATALENGTH(payload))) FROM dbo.LargeBinary");
            }
            var reusedRequest=f.Request("unused.sql",views:false);
            f.Evidence["reusedServiceArtifact"]=prior; f.RecordArtifact(artifact); f.Restore(artifact); IntegrationCompare(f,reusedRequest);
            f.Pass("Reused actual >2GiB final-service artifact single-session restore and streaming hashes");
            return;
        }
        var request=f.Request(views:false,mirror:true);
        using(var c=f.Open(f.Source)) f.Evidence["sourcePayloadBytes"]=IntegrationFixture.Scalar(c,"SELECT SUM(CONVERT(bigint,DATALENGTH(payload))) FROM dbo.LargeBinary");
        var result=f.Export(request); Assert(result.Stage==MssqlExportStage.Completed,"Large service: "+result.ErrorMessage);
        long length=new FileInfo(result.PublishedPath).Length;
        if(factor==10)Assert(length>2147483648L,"Actual artifact exceeds 2GiB");
        f.RecordArtifact(result.PublishedPath); f.Restore(result.PublishedPath); IntegrationCompare(f,request);
        Assert(result.RowCounts.Sum(x=>x.Rows)==1024L*factor,"Actual large row count");
        f.Pass("Large factor="+factor+" actual bytes="+length+" full service restore and hashes");
    }

    private static void IntegrationCompare(IntegrationFixture f,MssqlExportRequest request)
    {
        using(var a=f.Open(f.Source))using(var b=f.Open(f.Target))
        {
            foreach(var table in request.Tables)
            {
                string qualified=IntegrationFixture.Q(table.Schema)+"."+IntegrationFixture.Q(table.Name);
                var expressions=new List<string>();
                using(var cmd=new SqlCommand("SELECT c.name,ty.name FROM sys.columns c JOIN sys.types ty ON c.user_type_id=ty.user_type_id WHERE c.object_id=OBJECT_ID("+IntegrationFixture.Lit(qualified)+") ORDER BY column_id",a))using(var r=cmd.ExecuteReader())
                    while(r.Read())
                    {
                        string name=IntegrationFixture.Q(r.GetString(0)), type=r.GetString(1);
                        if(type=="timestamp")continue;
                        if(type=="geometry")name+=".Serialize()";
                        if(type=="text")name="CONVERT(varchar(max),"+name+")";
                        if(type=="ntext"||type=="xml")name="CONVERT(nvarchar(max),"+name+")";
                        expressions.Add("CONVERT(varbinary(max),"+name+")");
                    }
                string sql="SELECT "+string.Join(",",expressions)+" FROM "+qualified;
                var ha=IntegrationRowHashes(a,sql);var hb=IntegrationRowHashes(b,sql);
                Assert(ha.SequenceEqual(hb),"Streaming SHA256 rows including NULL/length: "+qualified);
                using(var sha=SHA256.Create())f.Evidence["values-"+qualified]=new {rows=ha.Count,sha256=BitConverter.ToString(sha.ComputeHash(Encoding.ASCII.GetBytes(string.Join("",ha)))).Replace("-","")};
            }
            foreach(var item in IntegrationMetadataQueries())
            {
                string left=IntegrationCatalog(a,item.Value),right=IntegrationCatalog(b,item.Value);
                File.WriteAllText(Path.Combine(f.Root,"catalog-"+item.Key+"-source.txt"),left);
                File.WriteAllText(Path.Combine(f.Root,"catalog-"+item.Key+"-target.txt"),right);
                Assert(left==right,"Catalog equality "+item.Key);
            }
        }
        f.Save();
    }
    private static List<string> IntegrationRowHashes(SqlConnection c,string sql)
    {
        var result=new List<string>(); var buffer=new byte[65536];
        using(var cmd=new SqlCommand(sql,c){CommandTimeout=0})using(var r=cmd.ExecuteReader(CommandBehavior.SequentialAccess))
            while(r.Read())using(var hash=SHA256.Create())
            {
                for(int i=0;i<r.FieldCount;i++)
                {
                    bool nil=r.IsDBNull(i);long len=nil?-1:r.GetBytes(i,0,null,0,0);var marker=BitConverter.GetBytes(len);hash.TransformBlock(marker,0,marker.Length,null,0);
                    if(nil)continue;long at=0;int count;while((count=(int)r.GetBytes(i,at,buffer,0,buffer.Length))>0){hash.TransformBlock(buffer,0,count,null,0);at+=count;}
                    Assert(at==len,"Streaming verifier DATALENGTH");
                }
                hash.TransformFinalBlock(new byte[0],0,0);result.Add(BitConverter.ToString(hash.Hash));
            }
        result.Sort(StringComparer.Ordinal);return result;
    }
    private static string IntegrationCatalog(SqlConnection c,string sql)
    {
        var rows=new List<string>();using(var cmd=new SqlCommand(sql,c))using(var r=cmd.ExecuteReader())while(r.Read())
        {var fields=new List<string>();for(int i=0;i<r.FieldCount;i++)fields.Add(r.IsDBNull(i)?"<NULL>":(r.GetName(i)=="definition" ? Convert.ToString(r.GetValue(i),CultureInfo.InvariantCulture).Trim() : Convert.ToString(r.GetValue(i),CultureInfo.InvariantCulture)));rows.Add(JsonConvert.SerializeObject(fields));}
        rows.Sort(StringComparer.Ordinal);return string.Join("\n",rows);
    }
    private static Dictionary<string,string> IntegrationMetadataQueries()
    {
        return new Dictionary<string,string> {
            {"columns", "SELECT s.name,t.name,c.column_id,c.name,ty.name,c.max_length,c.precision,c.scale,c.collation_name,c.is_nullable,c.is_identity,CONVERT(nvarchar(60),ic.seed_value),CONVERT(nvarchar(60),ic.increment_value),cc.definition,cc.is_persisted,dc.name,dc.definition FROM sys.tables t JOIN sys.schemas s ON t.schema_id=s.schema_id JOIN sys.columns c ON t.object_id=c.object_id JOIN sys.types ty ON c.user_type_id=ty.user_type_id LEFT JOIN sys.identity_columns ic ON c.object_id=ic.object_id AND c.column_id=ic.column_id LEFT JOIN sys.computed_columns cc ON c.object_id=cc.object_id AND c.column_id=cc.column_id LEFT JOIN sys.default_constraints dc ON c.default_object_id=dc.object_id WHERE t.is_ms_shipped=0"},
            {"indexes", "SELECT s.name,t.name,i.name,i.type,i.is_unique,i.is_primary_key,i.is_unique_constraint,i.is_disabled,i.filter_definition,c.name,ic.key_ordinal,ic.is_descending_key,ic.is_included_column FROM sys.tables t JOIN sys.schemas s ON t.schema_id=s.schema_id JOIN sys.indexes i ON t.object_id=i.object_id JOIN sys.index_columns ic ON i.object_id=ic.object_id AND i.index_id=ic.index_id JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id WHERE t.is_ms_shipped=0"},
            {"checks", "SELECT OBJECT_SCHEMA_NAME(parent_object_id),OBJECT_NAME(parent_object_id),name,definition,is_disabled,is_not_trusted,is_not_for_replication FROM sys.check_constraints"},
            {"foreignKeys", "SELECT OBJECT_SCHEMA_NAME(f.parent_object_id),OBJECT_NAME(f.parent_object_id),f.name,OBJECT_SCHEMA_NAME(f.referenced_object_id),OBJECT_NAME(f.referenced_object_id),COL_NAME(fc.parent_object_id,fc.parent_column_id),COL_NAME(fc.referenced_object_id,fc.referenced_column_id),fc.constraint_column_id,f.is_disabled,f.is_not_trusted,f.delete_referential_action,f.update_referential_action FROM sys.foreign_keys f JOIN sys.foreign_key_columns fc ON f.object_id=fc.constraint_object_id"},
            {"views", "SELECT s.name,v.name,m.definition,m.uses_ansi_nulls,m.uses_quoted_identifier FROM sys.views v JOIN sys.schemas s ON v.schema_id=s.schema_id JOIN sys.sql_modules m ON v.object_id=m.object_id"},
            {"comments", "SELECT OBJECT_SCHEMA_NAME(major_id),OBJECT_NAME(major_id),minor_id,name,CONVERT(nvarchar(max),value) FROM sys.extended_properties WHERE class=1 AND name=N'MS_Description'"}
        };
    }

    private static void IntegrationUnsupported(IntegrationFixture f)
    {
        f.Setup("unsupported.sql");
        foreach(string table in new[]{"RejectSpatial","RejectVariant","RejectAliasTable","RejectTypedXml","RejectSequenceDependency"})
        {
            var req=f.Request("reject-"+table+".sql",false,select:t=>t.Key.Name==table);
            var pre=f.Service().Preflight(req,CancellationToken.None);
            string expected=table=="RejectSpatial"?"UnsupportedSpatial":table=="RejectVariant"?"UnsupportedType":table=="RejectAliasTable"?"UnsupportedUdt":table=="RejectTypedXml"?"UnsupportedTypedXml":"MissingDependency";
            Assert(!pre.CanStart && pre.Issues.Any(x=>x.Code==expected),"Reject "+table+" with "+expected);var res=f.Export(req);Assert(res.Stage==MssqlExportStage.Failed&&!File.Exists(req.TargetPath),"Rejected selection creates no artifact");
            f.Evidence["preflight-"+table]=pre.Issues.Select(x=>x.Code).ToArray();
        }
        var encrypted=f.Request("encrypted.sql",true,select:t=>t.Key.Name=="IdentityEmpty");
        var encryptedCheck=f.Service().Preflight(encrypted,CancellationToken.None);
        Assert(!encryptedCheck.CanStart && encryptedCheck.Issues.Any(x=>x.Code=="UnreadableDefinition" && x.ObjectKey!=null && x.ObjectKey.Name=="RejectEncrypted"),"Encrypted definition preflight refusal");
        f.Evidence["encryptedDefinitionIssues"]=encryptedCheck.Issues.Select(x=>x.Code+":"+x.ObjectKey?.Name).ToArray();
        using(var c=f.Open("master"))IntegrationFixture.Exec(c,"ALTER DATABASE "+IntegrationFixture.Q(f.Source)+" SET ALLOW_SNAPSHOT_ISOLATION OFF;");
        var off=f.Request("snapshot-off.sql",false,select:t=>t.Key.Name=="IdentityEmpty");
        Assert(f.Service().Preflight(off,CancellationToken.None).CanStart,"Snapshot OFF with confirmed ALTER capability may start");
        using(var c=f.Open("master"))IntegrationFixture.Exec(c,"ALTER DATABASE "+IntegrationFixture.Q(f.Source)+" SET ALLOW_SNAPSHOT_ISOLATION ON;");
        f.Pass("Live unsupported spatial variant alias typed XML sequence encrypted definition and manageable SNAPSHOT OFF preflight");
        using(var c=f.Open(f.Source))IntegrationFixture.Exec(c,"CREATE USER fixture_reader WITHOUT LOGIN; GRANT SELECT TO fixture_reader; DENY VIEW DEFINITION TO fixture_reader;");
        var hidden=f.Request("hidden.sql",false,select:t=>t.Key.Schema=="一");
        f.RestrictedReader=true;
        try { AssertThrows<InvalidOperationException>(()=>f.Service().Preflight(hidden,CancellationToken.None),"Denied definitions rejected"); Assert(f.Export(hidden).Stage==MssqlExportStage.Failed && !File.Exists(hidden.TargetPath),"Denied definition creates no artifact"); }
        finally { f.RestrictedReader=false; }
        f.Pass("Live SELECT reader denied VIEW DEFINITION blocked");
    }

    private static void IntegrationGuards(IntegrationFixture f)
    {
        using(var c=f.Open(f.Source))IntegrationFixture.Exec(c,"CREATE TABLE dbo.GuardRow(id int,b varbinary(max),x xml); INSERT dbo.GuardRow VALUES(0,0x,CONVERT(xml,N'<ok/>')),(1,CONVERT(varbinary(max),REPLICATE(CONVERT(varchar(max),'x'),300000)),CONVERT(xml,N'<r/>')); ");
        using(var c=f.Open(f.Target))IntegrationFixture.Exec(c,"CREATE TABLE dbo.GuardRow(id int,b varbinary(max),x xml);");
        var req=f.Request("guards.sql",false,MssqlExportMode.DataOnly,t=>t.Key.Name=="GuardRow");
        var result=f.Export(req);Assert(result.Stage==MssqlExportStage.Completed,"Guard service artifact");
        var batches=Regex.Split(File.ReadAllText(req.TargetPath),@"(?m)^GO\r?$").Where(s=>!string.IsNullOrWhiteSpace(s)).ToArray();
        var chunks=Enumerable.Range(0,batches.Length).Where(i=>batches[i].Contains(".WRITE(")).ToArray();
        int final=Array.FindLastIndex(batches,s=>s.Contains("INSERT INTO [dbo].[GuardRow]"));
        Assert(chunks.Length>=2&&final>=0,"Guard artifact contains staged chunks and final insert");
        foreach(var fault in new[]{"missing","duplicate","outoforder","conversion","replay"})
        {
            using(var c=f.Open(f.Target))IntegrationFixture.Exec(c,"TRUNCATE TABLE dbo.GuardRow;");
            var altered=new List<string>();
            for(int i=0;i<batches.Length;i++)
            {
                if(fault=="missing"&&i==chunks[0])continue;
                string sql=batches[i];
                if(fault=="outoforder"&&i==chunks[0])sql=batches[chunks[1]];
                if(fault=="outoforder"&&i==chunks[1])sql=batches[chunks[0]];
                if(fault=="conversion"&&i==final){string replacement=sql.Replace("CONVERT(xml,[c2],1)","CONVERT(xml,N'<broken',1)");Assert(replacement!=sql,"Conversion fault actually applied");sql=replacement;}
                altered.Add(sql);if(fault=="duplicate"&&i==chunks[0])altered.Add(sql);
            }
            altered.Add(batches[final]);
            string copy=Path.Combine(f.Root,"guard-"+fault+".sql");File.WriteAllText(copy,string.Join("\r\nGO\r\n",altered)+"\r\nGO\r\n",new UTF8Encoding(false));
            f.Restore(copy,false);
            using(var c=f.Open(f.Target))
            {
                Assert(Convert.ToInt32(IntegrationFixture.Scalar(c,"SELECT COUNT(*) FROM dbo.GuardRow"))==(fault=="replay"?2:1),"Earlier row remains; failed row absent or terminal replay inserts once "+fault);
                IntegrationFixture.Exec(c,"TRUNCATE TABLE dbo.GuardRow;");int errors=0;
                // Deliberately adversarial client continues after every failed batch, same session.
                foreach(string batch in altered)try{IntegrationFixture.Exec(c,batch);}catch(SqlException){errors++;}
                Assert(errors>0&&Convert.ToInt32(IntegrationFixture.Scalar(c,"SELECT COUNT(*) FROM dbo.GuardRow"))==(fault=="replay"?2:1),"Continue-after-error no NULL substitute/duplicate "+fault);
            }
            f.Pass("Artifact mutation "+fault+" fail-fast sqlcmd and adversarial continuing client; earlier successful row retained");
        }
    }

    private static void IntegrationConcurrency(IntegrationFixture f)
    {
        using(var c=f.Open(f.Source))IntegrationFixture.Exec(c,"CREATE TABLE dbo.Parent(id int PRIMARY KEY,v int,payload varbinary(max)); CREATE TABLE dbo.Child(id int PRIMARY KEY,parent int REFERENCES dbo.Parent(id),v int); INSERT dbo.Parent VALUES(1,1,CONVERT(varbinary(max),REPLICATE(CONVERT(varchar(max),'x'),1048576))); INSERT dbo.Child VALUES(1,1,1);");
        bool updated=false;
        f.BeforeRows=t=>{
            if(t.Key.Name!="Child")return;
            using(var c=f.Open(f.Source))IntegrationFixture.Exec(c,"BEGIN TRAN; UPDATE dbo.Parent SET v=2; UPDATE dbo.Child SET v=2; COMMIT;");
            updated=true;
        };
        var req=f.Request(views:false); var result=f.Export(req);
        Assert(updated&&result.Stage==MssqlExportStage.Completed,"Live snapshot export completes");f.BeforeRows=null;
        f.RecordArtifact(req.TargetPath);f.Restore(req.TargetPath);
        using(var c=f.Open(f.Target))Assert(Convert.ToInt32(IntegrationFixture.Scalar(c,"SELECT COUNT(*) FROM dbo.Parent p JOIN dbo.Child c ON c.parent=p.id WHERE p.v=1 AND c.v=1"))==1,"Parent/child both from pre-update snapshot");
        using(var c=f.Open(f.Source))Assert(Convert.ToInt32(IntegrationFixture.Scalar(c,"SELECT COUNT(*) FROM dbo.Parent p JOIN dbo.Child c ON c.parent=p.id WHERE p.v=2 AND c.v=2"))==1,"Second connection committed both changes");
        f.Pass("Concurrent second connection parent-child transaction restores one consistent snapshot");

        foreach(string fault in new[]{"ddl","disconnect","cancel-read","write","verify","mirror","cancel-verify-read","cancel-mirror-read","overwrite-race"})
        using(var cancel=new CancellationTokenSource())
        {
            string filename=fault+"_backup_export.sql",path=Path.Combine(f.Root,filename);File.WriteAllText(path,"original");
            bool mirrored=fault.Contains("mirror");req=f.Request(filename,false,mirror:mirrored);
            f.AfterBegin=fault=="ddl"?(Action)(()=>{using(var c=f.Open(f.Source))IntegrationFixture.Exec(c,"ALTER TABLE dbo.Child ADD extra int NULL;");}):null;
            f.BeforeRows=fault=="disconnect"?(Action<MssqlTableDefinition>)(_=>f.WorkerConnections.Last().Close()):null;
            bool triggered=false;
            result=f.Export(req,cancel.Token,new WorkerFiles(fault,cancel),stage=>{
                if(fault=="verify"&&stage==MssqlExportStage.Verifying)throw new IOException("controlled verify fault");
                if(fault=="overwrite-race"&&stage==MssqlExportStage.Publishing){File.WriteAllText(path,"racing replacement");triggered=true;}
            },p=>{
                if(fault=="cancel-read"&&p.Stage==MssqlExportStage.Data&&p.SourceBytes>0 || fault=="cancel-verify-read"&&p.Stage==MssqlExportStage.Verifying&&p.PhaseProcessedBytes>0 || fault=="cancel-mirror-read"&&p.Stage==MssqlExportStage.Mirroring&&p.PhaseProcessedBytes>0)
                {triggered=true;cancel.Cancel();}
            });
            f.AfterBegin=null;f.BeforeRows=null;
            if(mirrored)Assert(result.Stage==MssqlExportStage.LocalCompletedMirrorIncomplete&&result.PublishedPath==path&&File.ReadAllText(path)!="original","Mirror failure retains confirmed local success "+fault);
            else
            {
                Assert(result.Stage==(fault.StartsWith("cancel")?MssqlExportStage.Canceled:MssqlExportStage.Failed)&&result.PublishedPath==null,"Expected failure/cancel outcome "+fault+": "+result.Stage);
                Assert(File.ReadAllText(path)==(fault=="overwrite-race"?"racing replacement":"original"),"Prior/racing target preserved "+fault);
            }
            if(fault.StartsWith("cancel")||fault=="overwrite-race")Assert(triggered,"Fault actually reached "+fault);
            Assert(f.WorkerConnections.All(c=>c.State==ConnectionState.Closed),"Live source disposed "+fault);
            Assert(!Directory.GetFiles(f.Root,"*.partial",SearchOption.AllDirectories).Any(),"Owned partials cleaned "+fault);
            f.Pass("Live service "+fault+" outcome, old-file preservation and source cleanup");
        }

        // Block metadata with an uncommitted schema lock, then cancel the actual native command.
        req=f.Request("blocked.sql",false);File.WriteAllText(req.TargetPath,"original");req=f.Request("blocked.sql",false);
        using(var blocker=f.Open(f.Source))using(var transaction=blocker.BeginTransaction())using(var cmd=new SqlCommand("ALTER TABLE dbo.Parent ADD blocked int NULL",blocker,transaction))using(var cancel=new CancellationTokenSource())
        {
            cmd.ExecuteNonQuery();cancel.CancelAfter(600);var clock=Stopwatch.StartNew();result=f.Export(req,cancel.Token);clock.Stop();
            Assert(result.Stage==MssqlExportStage.Canceled&&clock.Elapsed.TotalSeconds<15,"Blocked actual command cancels promptly");
            Assert(File.ReadAllText(req.TargetPath)=="original","Blocked cancel preserves target");
            Assert(f.WorkerConnections.All(c=>c.State==ConnectionState.Closed),"Blocked command source released");
            transaction.Rollback();f.Evidence["blockedCancelMilliseconds"]=clock.ElapsedMilliseconds;
        }
        f.Pass("Live schema-lock command wait cancellation and rollback cleanup");
    }
}
