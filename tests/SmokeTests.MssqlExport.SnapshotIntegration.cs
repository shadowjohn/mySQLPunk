using System;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using mySQLPunk.lib.MssqlExport;

public static partial class SmokeTests
{
    private sealed class IntegrationSnapshotBackend : IMssqlSnapshotBackend, IMssqlSnapshotDeadlineBackend
    {
        private readonly MssqlSnapshotBackend inner;
        private readonly IntegrationFixture fixture;
        internal IntegrationSnapshotBackend(SqlConnection connection, IntegrationFixture fixture) { inner=new MssqlSnapshotBackend(connection); this.fixture=fixture; }
        public IMssqlSnapshotDeadline Deadline { set { ((IMssqlSnapshotDeadlineBackend)inner).Deadline=value; } }
        public bool IsUsable { get { return inner.IsUsable; } }
        public MssqlSnapshotCapability ReadCapability(CancellationToken token) { return inner.ReadCapability(token); }
        public bool TryAcquireLock(CancellationToken token) { bool acquired=inner.TryAcquireLock(token); fixture.LockAudit.Add(acquired?"acquired":"busy");return acquired; }
        public void SetEnabled(bool enabled,CancellationToken token) {
            fixture.BeforeSetting?.Invoke(enabled);
            fixture.SettingAudit.Add(enabled?"ON":"OFF");
            inner.SetEnabled(enabled,token);
            fixture.AfterSetting?.Invoke(enabled);
        }
        public void ReleaseLock(CancellationToken token) { inner.ReleaseLock(token); }
    }
    private static int SnapshotState(IntegrationFixture f,string db=null)
    {
        using(var c=f.Open("master")) using(var cmd=new SqlCommand("SELECT CONVERT(int,snapshot_isolation_state) FROM sys.databases WHERE name=@db",c) { CommandTimeout=5 }) {
            cmd.Parameters.AddWithValue("@db",db??f.Source); return (int)cmd.ExecuteScalar();
        }
    }
    private static void SnapshotOwnerState(IntegrationFixture f,bool enabled,string db=null)
    {
        using(var c=f.Open("master")) using(var cmd=new SqlCommand("ALTER DATABASE "+IntegrationFixture.Q(db??f.Source)+" SET ALLOW_SNAPSHOT_ISOLATION "+(enabled?"ON":"OFF"),c) { CommandTimeout=15 }) cmd.ExecuteNonQuery();
        Assert(SnapshotState(f,db)==(enabled?1:0),"Fixture owner state confirmed");
    }
    private static void SnapshotPoll(Func<bool> predicate,int seconds,string message)
    {
        var elapsed=Stopwatch.StartNew(); while(!predicate()) { Assert(elapsed.Elapsed.TotalSeconds<seconds,message); Thread.Sleep(100); }
    }
    private static void SnapshotResetTarget(IntegrationFixture f)
    { using(var c=f.Open(f.Target)) IntegrationFixture.Exec(c,"IF OBJECT_ID('dbo.Child') IS NOT NULL DROP TABLE dbo.Child; IF OBJECT_ID('dbo.Parent') IS NOT NULL DROP TABLE dbo.Parent;"); }
    private static void SnapshotExpectedRows(IntegrationFixture f,int parent)
    {
        using(var c=f.Open(f.Target)) {
            Assert(Convert.ToInt32(IntegrationFixture.Scalar(c,"SELECT COUNT(*) FROM dbo.Parent"))==1,"Target parent row count");
            Assert(Convert.ToInt32(IntegrationFixture.Scalar(c,"SELECT COUNT(*) FROM dbo.Child WHERE id=1 AND parent="+parent))==1,"Target expected child value");
            Assert(Convert.ToInt32(IntegrationFixture.Scalar(c,"SELECT COUNT(*) FROM sys.foreign_keys WHERE name=N'FK_Snapshot_Child_Parent' AND is_disabled=0 AND is_not_trusted=0"))==1,"Target FK enabled and trusted");
        }
    }
    private static void IntegrationOptionalSnapshot(IntegrationFixture f)
    {
        Assert(SnapshotState(f)==0,"Genuine initial OFF"); f.Setup("snapshot.sql");
        using(var c=f.Open(f.Source)) IntegrationFixture.Exec(c,"CREATE USER fixture_reader WITHOUT LOGIN; GRANT CONNECT, SELECT, VIEW DEFINITION TO fixture_reader; GRANT SELECT ON OBJECT::sys.sql_expression_dependencies TO fixture_reader;");
        foreach(bool enabled in new[]{false,true}) {
            if(enabled)SnapshotOwnerState(f,true);
            foreach(bool restricted in new[]{false,true}) {
                SnapshotResetTarget(f); f.RestrictedReader=restricted; int audit=f.SettingAudit.Count;
                var request=f.Request("ordinary-"+enabled+"-"+restricted+".sql",false,useSnapshot:false);
                var result=f.Export(request); f.RestrictedReader=false;
                Assert(result.Stage==MssqlExportStage.Completed,"Ordinary actual export: "+result.ErrorMessage);
                Assert(result.SnapshotCleanup.State==MssqlSnapshotCleanupState.NotRequired && SnapshotState(f)==(enabled?1:0) && f.SettingAudit.Count==audit,"Ordinary unchanged settings and no backend ALTER");
                f.RecordArtifact(request.TargetPath); f.Restore(request.TargetPath); SnapshotExpectedRows(f,1);
                f.Evidence["comparison-"+Path.GetFileName(request.TargetPath)]="Parent(1), Child(1,1); target FK trusted; source unchanged";
                f.Pass("Ordinary restore initial "+enabled+" restricted "+restricted);
            }
        }
        var denied=f.Request("denied.sql",false,useSnapshot:false); using(var c=f.Open(f.Source))IntegrationFixture.Exec(c,"DENY SELECT ON dbo.Child TO fixture_reader;");
        f.RestrictedReader=true; Assert(!f.Service().Preflight(denied,CancellationToken.None).CanStart,"Necessary table SELECT still required"); Assert(f.Export(denied).Stage==MssqlExportStage.Failed,"Denied read never succeeds"); f.RestrictedReader=false;
        foreach(bool snapshot in new[]{false,true}) {
            SnapshotResetTarget(f); using(var c=f.Open(f.Source)) IntegrationFixture.Exec(c,"UPDATE dbo.Child SET parent=1; DELETE dbo.Parent WHERE id=2;");
            f.ParentFirst=true; bool parentOpened=false,changed=false;
            f.BeforeRows=t=> { if(t.Key.Name=="Parent")parentOpened=true; if(t.Key.Name=="Child") { Assert(parentOpened,"Explicit Parent before Child consumption"); using(var c=f.Open(f.Source))IntegrationFixture.Exec(c,"BEGIN TRAN; INSERT dbo.Parent VALUES(2); UPDATE dbo.Child SET parent=2; COMMIT;"); changed=true; } };
            var request=f.Request("mixed-time-"+snapshot+".sql",false,useSnapshot:snapshot); var result=f.Export(request); f.BeforeRows=null; f.ParentFirst=false;
            Assert(changed && result.Stage==MssqlExportStage.Completed,"Controlled legal source write and export complete"); f.RecordArtifact(request.TargetPath); f.Restore(request.TargetPath,snapshot);
            if(snapshot) SnapshotExpectedRows(f,1); else {
                string log=File.ReadAllText(Directory.GetFiles(f.Root,Path.GetFileName(request.TargetPath)+".restore-*.log").Single());
                Assert(log.Contains("FK_Snapshot_Child_Parent") && log.Contains("547"),"Ordinary actual restore specifically fails trusted FK validation");
                using(var c=f.Open(f.Target))Assert(Convert.ToInt32(IntegrationFixture.Scalar(c,"SELECT COUNT(*) FROM dbo.Child c LEFT JOIN dbo.Parent p ON p.id=c.parent WHERE p.id IS NULL"))==1,"Actual restored mixed-time missing parent");
            }
            f.Evidence["comparison-"+Path.GetFileName(request.TargetPath)]=snapshot?"Target Parent1/Child1 vs changed source Parent1,2/Child2; trusted FK":"Target Parent1/Child2; sqlcmd FK547 expected"; f.Pass("Controlled mixed-time counterexample Snapshot="+snapshot);
        }
        foreach(string fault in new[]{"cancel","ddl"}) using(var cancel=new CancellationTokenSource()) {
            string path=Path.Combine(f.Root,"ordinary-"+fault+".sql"); File.WriteAllText(path,"original");var request=f.Request(Path.GetFileName(path),false,useSnapshot:false);
            f.AfterBegin=()=> { if(fault=="cancel")cancel.Cancel(); else using(var c=f.Open(f.Source))IntegrationFixture.Exec(c,"ALTER TABLE dbo.Child ADD drift int NULL;"); };
            var result=f.Export(request,cancel.Token);f.AfterBegin=null;
            Assert(result.Stage==(fault=="cancel"?MssqlExportStage.Canceled:MssqlExportStage.Failed)&&File.ReadAllText(path)=="original","Ordinary cancellation/drift protects old file");f.Pass("Ordinary "+fault+" old-file protection");
        }
    }
    private static void IntegrationSnapshotLifecycle(IntegrationFixture f)
    {
        Assert(SnapshotState(f)==0,"Lifecycle genuine initial OFF"); f.Setup("snapshot.sql");
        SnapshotCooperation(f); SnapshotCrash(f); SnapshotRevoke(f);
        foreach(bool initial in new[]{false,true}) foreach(string outcome in new[]{"complete","cancel","read-failure"}) using(var cancel=new CancellationTokenSource()) {
            SnapshotOwnerState(f,initial); var request=f.Request("lifecycle-"+initial+"-"+outcome+".sql",false);
            f.AfterBegin=()=> { Assert(SnapshotState(f)==1,"Actual ON before reads"); if(outcome=="cancel")cancel.Cancel(); };
            f.BeforeRows=outcome=="read-failure"?(Action<MssqlTableDefinition>)(_=> {throw new IOException("Injected read failure");}):null;
            var result=f.Export(request,cancel.Token);f.AfterBegin=null;f.BeforeRows=null;
            Assert(result.Stage==(outcome=="complete"?MssqlExportStage.Completed:outcome=="cancel"?MssqlExportStage.Canceled:MssqlExportStage.Failed),"Lifecycle primary outcome");
            Assert(SnapshotState(f)==(initial?1:0) && result.SnapshotCleanup.State==(initial?MssqlSnapshotCleanupState.PreservedOn:MssqlSnapshotCleanupState.RestoredOff),"Actual restored/preserved setting");
            if(outcome=="complete") { SnapshotResetTarget(f); f.RecordArtifact(request.TargetPath);f.Restore(request.TargetPath);SnapshotExpectedRows(f,1); }
            f.Pass("Lifecycle "+initial+" "+outcome);
        }
        SnapshotOwnerState(f,false);
        foreach(string action in new[]{"release","cancel","timeout"}) SnapshotBlockedOn(f,action);
        SnapshotBlockedOff(f);
        SnapshotOwnerState(f,false);
        foreach(string fault in new[]{"ack","disconnect","journal"}) {
            var request=f.Request("fault-"+fault+".sql",false); int count=f.SettingAudit.Count;
            if(fault=="ack")f.AfterSetting=enabled=> {if(enabled) {Assert(SnapshotState(f)==1,"Real ON before injected lost acknowledgement");throw new IOException("Injected acknowledgement failure");}};
            if(fault=="disconnect")f.AfterBegin=()=>f.WorkerConnections.Last().Close();
            if(fault=="journal") { f.JournalOverride=Path.Combine(f.Root,"journal-obstruction");File.WriteAllText(f.JournalOverride,"owned obstruction"); }
            var result=f.Export(request);f.AfterSetting=null;f.AfterBegin=null;f.JournalOverride=null;
            Assert(result.Stage==MssqlExportStage.Failed,"Injected failure retained "+fault);
            if(fault=="disconnect") {Assert(result.SnapshotCleanup.State==MssqlSnapshotCleanupState.Unconfirmed && SnapshotState(f)==1 && File.Exists(result.SnapshotCleanup.RecoveryRecordPath),"Own exporter SqlConnection.Close retains unconfirmed ON and journal");SnapshotOwnerState(f,false);}
            else Assert(SnapshotState(f)==0,"Failed acknowledgement/journal final OFF");
            if(fault=="journal")Assert(f.SettingAudit.Count==count,"Journal IO failure sends no ALTER");
            f.Pass("Fault injection "+fault);
        }
        
    }
    private static void SnapshotBlockedOn(IntegrationFixture f,string action)
    {
        SnapshotOwnerState(f,false); var request=f.Request("blocked-on-"+action+".sql",false);
        using(var blocker=f.Open(f.Source))using(var transaction=blocker.BeginTransaction())using(var cancel=new CancellationTokenSource()) {
            using(var cmd=new SqlCommand("UPDATE dbo.Child SET parent=parent",blocker,transaction){CommandTimeout=5})cmd.ExecuteNonQuery();
            var clock=Stopwatch.StartNew(); var work=Task.Run(()=>f.Export(request,cancel.Token));bool released=false;
            try {
                SnapshotPoll(()=>SnapshotState(f)==3,15,"Actual ON transition observed"); f.Evidence["on-transition-"+action]=3;
                if(action=="release") {transaction.Rollback();released=true;} else if(action=="cancel")cancel.Cancel();
                Assert(work.Wait(TimeSpan.FromSeconds(action=="timeout"?105:45)),"Bounded exporter ON wait");var result=work.Result;
                if(action=="release") Assert(result.Stage==MssqlExportStage.Completed&&SnapshotState(f)==0,"Writer release completes ON/read/OFF");
                else Assert(result.Stage==(action=="cancel"?MssqlExportStage.Canceled:MssqlExportStage.Failed),"Wait primary failure/cancel");
                if(action=="timeout")Assert(clock.Elapsed.TotalSeconds>=59 && clock.Elapsed.TotalSeconds<105,"Real sixty-second enable deadline");
                f.Evidence["on-wait-"+action]=new { seconds=clock.Elapsed.TotalSeconds,state=SnapshotState(f),cleanup=result.SnapshotCleanup };f.Save();
            } finally { if(!released)transaction.Rollback();cancel.Cancel();Assert(work.Wait(TimeSpan.FromSeconds(40)),"Wait ends after own blocker release"); }
        }
        SnapshotPoll(()=>SnapshotState(f)!=3 && SnapshotState(f)!=2,15,"Transition settles after owner writer release");
        if(SnapshotState(f)==1)SnapshotOwnerState(f,false);f.Pass("Real blocked ON "+action);
    }
    private static void SnapshotBlockedOff(IntegrationFixture f)
    {
        SnapshotOwnerState(f,false); var request=f.Request("blocked-off.sql",false);SqlConnection external=null;SqlTransaction transaction=null;
        f.AfterBegin=()=> { external=f.Open(f.Source);transaction=external.BeginTransaction(IsolationLevel.Snapshot);using(var cmd=new SqlCommand("SELECT COUNT(*) FROM dbo.Parent",external,transaction){CommandTimeout=5})cmd.ExecuteScalar(); };
        var clock=Stopwatch.StartNew();var work=Task.Run(()=>f.Export(request));
        try {
            SnapshotPoll(()=>SnapshotState(f)==2,20,"Actual OFF transition observed with external snapshot reader");
            Assert(work.Wait(TimeSpan.FromSeconds(45)),"Independent thirty-second cleanup deadline");var result=work.Result;
            Assert(clock.Elapsed.TotalSeconds>=29 && result.Stage==MssqlExportStage.Completed && result.SnapshotCleanup.State==MssqlSnapshotCleanupState.Unconfirmed && File.Exists(result.SnapshotCleanup.RecoveryRecordPath),"Published file plus bounded unconfirmed cleanup and durable notice");
            f.Evidence["off-wait"]=new { seconds=clock.Elapsed.TotalSeconds,state=SnapshotState(f),cleanup=result.SnapshotCleanup }; f.RecordArtifact(request.TargetPath);
            SnapshotResetTarget(f);f.Restore(request.TargetPath);SnapshotExpectedRows(f,1);
        } finally { transaction?.Dispose();external?.Dispose();f.AfterBegin=null;Assert(work.Wait(TimeSpan.FromSeconds(40)),"Own external reader released"); }
        SnapshotPoll(()=>SnapshotState(f)==0 || SnapshotState(f)==1,15,"State settles after external reader release"); f.Evidence["after-external-reader-release"]=SnapshotState(f); f.Save();
        if(SnapshotState(f)==1)SnapshotOwnerState(f,false); f.Evidence["off-wait-owner-teardown"]="Owner readback and explicit OFF after canceled ALTER; not product recovery"; f.Pass("Real thirty-second OFF wait; external reader released only by fixture owner");
    }
    private static void SnapshotCooperation(IntegrationFixture f)
    {
        SnapshotOwnerState(f,false); SnapshotResetTarget(f);using(var c=f.Open(f.Target))IntegrationFixture.Exec(c,"CREATE TABLE dbo.Parent(id int PRIMARY KEY);INSERT dbo.Parent VALUES(1);");
        var request=f.Request("lock-first.sql",false);using(var first=f.NewSource()) {
            first.BeginRead(request,null,CancellationToken.None);
            var busy=f.Export(f.Request("lock-busy.sql",false));Assert(busy.Stage==MssqlExportStage.Failed && f.LockAudit.Last()=="busy" && SnapshotState(f)==1,"Same-database cooperative lock refuses competing exporter");
            f.SourceOverride=f.Target;
            try {
                using(var other=f.NewSource()) {
                    var catalog=other.ReadCatalog(f.Target,CancellationToken.None);string path=Path.Combine(f.Root,"cross-db.sql");
                    var independent=new MssqlExportRequest(Guid.NewGuid(),f.Target,catalog.Tables.Select(t=>t.Key).ToArray(),MssqlExportMode.SchemaAndData,false,path,MssqlExportArtifact.CaptureDestination(path,CancellationToken.None),null,true);
                    f.Evidence["cross-db-initial"]=catalog.SnapshotState;
                    var schema=other.BeginRead(independent,null,CancellationToken.None);
                    Assert(SnapshotState(f)==1 && SnapshotState(f,f.Target)==1,"Both database lifecycles acquired independently while first lock held");
                    other.CompleteRead(schema,CancellationToken.None);
                    // SQL version retention can span databases. End both read transactions before OFF cleanup.
                    Assert(first.FinishRead(null).State==MssqlSnapshotCleanupState.RestoredOff,"First read ends before independent target OFF");
                    var cleanup=other.FinishRead(null);f.Evidence["cross-db-cleanup"]=cleanup;f.Evidence["cross-db-final"]=SnapshotState(f,f.Target);f.Save(); Assert(cleanup.State==MssqlSnapshotCleanupState.RestoredOff,"Already-owned Target independently manages snapshot: "+cleanup.State+"/"+cleanup.WarningCode);
                }
            } finally {f.SourceOverride=null;}
            Assert(first.FinishRead(null).State==MssqlSnapshotCleanupState.RestoredOff,"First cooperation owner restores source");
        }
        f.Pass("Same database busy and already-owned Target cross-database independence");
    }
    private static void SnapshotRevoke(IntegrationFixture f)
    {
        using(var c=f.Open(f.Source))IntegrationFixture.Exec(c,"CREATE USER fixture_reader WITHOUT LOGIN;GRANT CONNECT,SELECT,VIEW DEFINITION,ALTER TO fixture_reader;GRANT SELECT ON OBJECT::sys.sql_expression_dependencies TO fixture_reader;");
        f.RestrictedReader=true;var request=f.Request("revoke.sql",false);
        f.BeforeSetting=enabled=> {if(enabled)using(var c=f.Open(f.Source))IntegrationFixture.Exec(c,"REVOKE ALTER FROM fixture_reader;");};
        try {var result=f.Export(request);Assert(result.Stage==MssqlExportStage.Failed && SnapshotState(f)==0,"Real ALTER revocation after capability capture blocks enable");}
        finally {f.BeforeSetting=null;f.RestrictedReader=false;}
        f.Pass("Real ALTER revoke between capability capture and dispatch");
    }
    // A narrow mode in this same test executable; no fixture creation or teardown in the child.
    private static int SnapshotCrashChild(string[] args)
    {
        try {
            Assert(args.Length==3 && args[0]=="--snapshot-crash-child","Exact child arguments");
            Guid owner,nonce; Assert(Guid.TryParseExact(args[1],"N",out owner)&&Guid.TryParseExact(args[2],"N",out nonce),"Child token and nonce format");
            string server=Environment.GetEnvironmentVariable("MYSQLPUNK_MSSQL_INTEGRATION_SERVER");
            Assert(server==@"(localdb)\mySQLPunkExportTest_20261012","Child authorized instance only");
            var auth=Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Environment.GetEnvironmentVariable("MYSQLPUNK_MSSQL_INTEGRATION_AUTH")));
            Assert((bool)auth["CreatedByTask"] && (string)auth["Instance"]=="mySQLPunkExportTest_20261012","Child authorization manifest");
            string root=Path.GetFullPath(Environment.GetEnvironmentVariable("MYSQLPUNK_MSSQL_INTEGRATION_OUTPUT"));
            string authorized=Path.GetFullPath((string)auth["FixtureRoot"]).TrimEnd('\\')+"\\";
            Assert(root.StartsWith(@"C:\",StringComparison.OrdinalIgnoreCase)&&root.StartsWith(authorized,StringComparison.OrdinalIgnoreCase),"Child root belongs to authorized C fixture");
            for(var path=new DirectoryInfo(root);path!=null;path=path.Parent)Assert((path.Attributes&FileAttributes.ReparsePoint)==0,"Child has no reparse ancestor");
            Assert(File.ReadAllText(Path.Combine(root,"owner.token"))==args[1],"Child owner token exact");
            string db="mysqlpunk_export_"+args[1]+"_src";
            var provider=new mySQLPunk.lib.my_mssql();provider.SetConn(new SqlConnectionStringBuilder {DataSource=server,InitialCatalog=db,IntegratedSecurity=true,Pooling=false,TrustServerCertificate=true,ApplicationName="mySQLPunk-owned-crash-child"}.ConnectionString);provider.Open();
            var store=new MssqlSnapshotRecoveryStore(Path.Combine(root,"journal"));
            using(var source=new MssqlExportSource(new MssqlOwnedSession(provider,provider.MCT),store)) {
                Assert(Convert.ToString(IntegrationFixture.Scalar(provider.MCT,"SELECT value FROM sys.extended_properties WHERE class=0 AND name=N'mysqlpunk_export_owner'"))==args[1],"Child database token exact");
                using(var cmd=new SqlCommand("SELECT physical_name FROM sys.database_files",provider.MCT){CommandTimeout=5})using(var reader=cmd.ExecuteReader())while(reader.Read())Assert(Path.GetDirectoryName(Path.GetFullPath(reader.GetString(0)))==root,"Child database physical files inside owned root");
                var catalog=source.ReadCatalog(db,CancellationToken.None);Assert(catalog.SnapshotState==0,"Crash starts OFF");
                string target=Path.Combine(root,"crash-child.sql");Guid job=Guid.NewGuid();
                var request=new MssqlExportRequest(job,db,catalog.Tables.Select(t=>t.Key).ToArray(),MssqlExportMode.SchemaAndData,false,target,MssqlExportArtifact.CaptureDestination(target,CancellationToken.None),null,true);
                try {
                    source.BeginRead(request,null,CancellationToken.None);
                    Assert(store.ReadPending().Any(n=>n.Record!=null&&n.Record.JobId==job),"Durable journal precedes ready");
                    // Read state through a distinct owned observer because source owns a transaction.
                    using(var observer=new SqlConnection(new SqlConnectionStringBuilder{DataSource=server,InitialCatalog=db,IntegratedSecurity=true,Pooling=false,TrustServerCertificate=true}.ConnectionString)) {observer.Open();Assert(Convert.ToInt32(IntegrationFixture.Scalar(observer,"SELECT snapshot_isolation_state FROM sys.databases WHERE database_id=DB_ID()"))==1,"Actual ON precedes ready");}
                    string ready=Path.Combine(root,"crash-ready-"+args[2]+".json");File.WriteAllText(ready+".tmp",Newtonsoft.Json.JsonConvert.SerializeObject(new {nonce=args[2],job=job.ToString(),pid=Process.GetCurrentProcess().Id}));File.Move(ready+".tmp",ready);
                    Thread.Sleep(TimeSpan.FromSeconds(120));
                } finally {source.FinishRead(null);}
            }
            return 2; // Normal completion is a failed crash test, even though its finally safely restores.
        } catch(Exception ex) {Console.Error.WriteLine("Crash child validation/work failed: "+ex.GetType().Name);return 3;}
    }
    private static void SnapshotCrash(IntegrationFixture f)
    {
        SnapshotOwnerState(f,false);string nonce=Guid.NewGuid().ToString("N"),ready=Path.Combine(f.Root,"crash-ready-"+nonce+".json");
        var start=new ProcessStartInfo(System.Reflection.Assembly.GetExecutingAssembly().Location,"--snapshot-crash-child "+f.Token+" "+nonce) {UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true};
        using(var child=Process.Start(start)) {
            int ownPid=child.Id;var stdout=child.StandardOutput.ReadToEndAsync();var stderr=child.StandardError.ReadToEndAsync();
            try {
                SnapshotPoll(()=>File.Exists(ready)||child.HasExited,30,"Crash child ready bounded");Assert(File.Exists(ready)&&!child.HasExited,"Child reached durable ready");
                var receipt=Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(ready));Assert((string)receipt["nonce"]==nonce&&(int)receipt["pid"]==ownPid,"Ready belongs to this exact process");
                Guid job=Guid.Parse((string)receipt["job"]);Assert(SnapshotState(f)==1,"Parent sees actual ON before terminating own child");
                child.Kill();Assert(child.WaitForExit(10000),"Own child reaped");
                var store=new MssqlSnapshotRecoveryStore(Path.Combine(f.Root,"journal"));Assert(store.ReadPending().Any(n=>n.Record!=null&&n.Record.JobId==job),"Crash journal remains");
                var identity=f.Service().LoadCatalog(f.Source,CancellationToken.None).SnapshotCapability.Identity;
                Assert(f.Service().LoadRecoveryNotices(identity).Any(n=>n.Record!=null&&n.Record.JobId==job)&&SnapshotState(f)==1,"Next read-only notice does not repair ON");
                f.Evidence["crash"]=new { ownPid,job,stateAfterKill=SnapshotState(f),journal="retained",notice="read-only",repair="fixture owner teardown only" };f.Save();
            } finally {if(!child.HasExited){child.Kill();child.WaitForExit(10000);}File.WriteAllText(Path.Combine(f.Root,"crash-child.log"),stdout.Result+stderr.Result);}
        }
        SnapshotOwnerState(f,false);f.Pass("Real own-child termination leaves ON and durable read-only notice; fixture owner restores");
    }
}
