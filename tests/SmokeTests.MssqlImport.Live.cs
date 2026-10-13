using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Collections;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Newtonsoft.Json;
using mySQLPunk.lib;
using mySQLPunk.lib.MssqlExport;
using System.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using mySQLPunk.lib.MssqlImport;

public static partial class SmokeTests
{
    private const string ImportFixtureServer = @"(localdb)\mySQLPunkExportTest_20261012";
    private const string ImportFixtureRoot = @"C:\Users\johnho\AppData\Local\Temp\mysqlpunk-export-20261012-7cadb673332543c3b17c61924490a702";
    private static void MssqlImportIntegrationGuards()
    {
        var manifest = new JObject { ["Instance"]="mySQLPunkExportTest_20261012", ["EngineVersion"]="17.0", ["CreatedByTask"]=true,
            ["UserAuthorization"]="2026-10-12 dedicated LocalDB creation and startup approved", ["FixtureRoot"]=ImportFixtureRoot };
        string output=Path.Combine(ImportFixtureRoot,"guard-only");
        ImportValidateEnvelope(manifest,ImportFixtureServer,output,20L*1024*1024*1024,path => new ImportGuardPath());
        ImportCleanupGuardCases();
        foreach(string fault in new[]{"server","instance","approval","root","sibling","root-itself","relative","non-c","nonempty","reparse","space"}) {
            var modified=(JObject)manifest.DeepClone();string server=ImportFixtureServer,path=output;long free=20L*1024*1024*1024;
            Func<string,ImportGuardPath> probe=x=>new ImportGuardPath();
            switch(fault) {
                case "server":server="localhost";break;case "instance":modified["Instance"]="MSSQLLocalDB";break;
                case "approval":modified["UserAuthorization"]="";break;case "root":modified["FixtureRoot"]=@"C:\other";break;
                case "sibling":path=ImportFixtureRoot+"-sibling\\x";break;case "root-itself":path=ImportFixtureRoot;break;
                case "relative":path="C:relative";break;case "non-c":path=@"D:\fixture";break;case "space":free--;break;
                case "nonempty":probe=x=>new ImportGuardPath {Exists=true,IsDirectory=true,HasEntries=true};break;
                case "reparse":probe=x=>new ImportGuardPath {Exists=true,IsDirectory=true,Reparse=true};break;
            }
            AssertThrows<InvalidOperationException>(()=>ImportValidateEnvelope(modified,server,path,free,probe),"Guard rejects before any SQL/file mutation: "+fault);
        }
    }
    private sealed class ImportGuardPath { internal bool Exists,IsDirectory,HasEntries,Reparse; }
    private static string ImportValidateEnvelope(JObject manifest,string server,string output,long free,Func<string,ImportGuardPath> probe)
    {
        if(manifest==null || server!=ImportFixtureServer || (string)manifest["Instance"]!="mySQLPunkExportTest_20261012" ||
            manifest["CreatedByTask"]==null || manifest["CreatedByTask"].Type!=JTokenType.Boolean || !(bool)manifest["CreatedByTask"] ||
            (string)manifest["EngineVersion"]!="17.0" || (string)manifest["UserAuthorization"]!="2026-10-12 dedicated LocalDB creation and startup approved")
            throw new InvalidOperationException("FixtureAuthorizationMismatch");
        string declared=(string)manifest["FixtureRoot"];
        if(!ImportAbsoluteC(declared) || !string.Equals(Path.GetFullPath(declared).TrimEnd('\\'),ImportFixtureRoot,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("FixtureRootMismatch");
        if(!ImportAbsoluteC(output)) throw new InvalidOperationException("FixtureOutputMustBeAbsoluteC");
        output=Path.GetFullPath(output).TrimEnd('\\');
        if(!output.StartsWith(ImportFixtureRoot+"\\",StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("FixtureOutputNotDescendant");
        var leaf=probe(output);
        if(leaf.Exists && (!leaf.IsDirectory || leaf.HasEntries)) throw new InvalidOperationException("FixtureOutputNotEmpty");
        ImportNoReparse(output,probe);
        if(free<20L*1024*1024*1024) throw new InvalidOperationException("FixtureInsufficientSpace");
        return output;
    }
    private static bool ImportAbsoluteC(string path)
    { return !string.IsNullOrEmpty(path) && path.Length>=3 && (path[0]=='C'||path[0]=='c') && path[1]==':' && (path[2]=='\\'||path[2]=='/'); }
    private static void ImportNoReparse(string path,Func<string,ImportGuardPath> probe)
    {
        for(string ancestor=path;ancestor!=null;) {
            var value=probe(ancestor);
            if(value.Exists&&value.Reparse) throw new InvalidOperationException("FixtureReparsePath");
            var parent=Directory.GetParent(ancestor);ancestor=parent==null?null:parent.FullName;
        }
    }
    private static ImportGuardPath ImportReadGuardPath(string path)
    {
        bool dir=Directory.Exists(path),file=File.Exists(path);
        if(!dir&&!file)return new ImportGuardPath();
        return new ImportGuardPath {Exists=true,IsDirectory=dir,HasEntries=dir&&Directory.EnumerateFileSystemEntries(path).Any(),Reparse=(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0};
    }
    private static void ImportAssertCleanup(string database,string[] created,string[] generated,string root,string memoryOwner,string fileOwner,string dbOwner,string[] files,Func<string,ImportGuardPath> probe)
    {
        if(!generated.Contains(database)||!created.Contains(database)||string.IsNullOrEmpty(memoryOwner)||fileOwner!=memoryOwner||dbOwner!=memoryOwner)
            throw new InvalidOperationException("FixtureCleanupOwnershipMismatch");
        var expected=new[]{Path.Combine(root,database+".mdf"),Path.Combine(root,database+".ldf")};
        if(files==null||files.Length!=2||files.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=2) throw new InvalidOperationException("FixtureCleanupFileSetMismatch");
        foreach(string file in files) {
            if(!ImportAbsoluteC(file))throw new InvalidOperationException("FixtureCleanupFileNotC");
            string full=Path.GetFullPath(file);
            if(!full.StartsWith(root+"\\",StringComparison.OrdinalIgnoreCase)||!expected.Contains(full,StringComparer.OrdinalIgnoreCase))throw new InvalidOperationException("FixtureCleanupFileNotOwned");
            ImportNoReparse(full,probe);
        }
    }
    private static void ImportCleanupGuardCases()
    {
        string root=Path.Combine(ImportFixtureRoot,"pure-cleanup");
        string name="mysqlpunk_import_"+Guid.NewGuid().ToString("N")+"_src";string[] generated={name,name.Substring(0,name.Length-4)+"_dst"};
        string[] files={Path.Combine(root,name+".mdf"),Path.Combine(root,name+".ldf")};Func<string,ImportGuardPath> probe=x=>new ImportGuardPath();
        ImportAssertCleanup(name,new[]{name},generated,root,"invented-owner","invented-owner","invented-owner",files,probe);
        foreach(string fault in new[]{"db","unrecorded","file-token","db-token","missing-file","sibling-file","reparse"}) {
            string db=name,fileToken="invented-owner",dbToken="invented-owner";string[] created={name},actual=files;var observed=probe;
            switch(fault){case "db":db="Transfer";break;case "unrecorded":created=new string[0];break;case "file-token":fileToken="other";break;case "db-token":dbToken=null;break;
                case "missing-file":actual=new[]{files[0]};break;case "sibling-file":actual=new[]{files[0],root+"-other\\x.ldf"};break;case "reparse":observed=x=>new ImportGuardPath{Exists=true,Reparse=true};break;}
            AssertThrows<InvalidOperationException>(()=>ImportAssertCleanup(db,created,generated,root,"invented-owner",fileToken,dbToken,actual,observed),"Cleanup guard rejects: "+fault);
        }
    }
    // Every instance is test-owned, a single target phase and one-shot checkpoint.
    // No saved configuration, global provider hook, command text substitution or replay.
    private sealed class ImportLiveCheckpoint : IDisposable
    {
        internal readonly string TargetPhase;
        internal readonly ManualResetEventSlim Issued = new ManualResetEventSlim();
        internal readonly ManualResetEventSlim ReaderReady = new ManualResetEventSlim();
        internal readonly ManualResetEventSlim DisposeEntered = new ManualResetEventSlim();
        internal readonly ManualResetEventSlim DisposeRelease = new ManualResetEventSlim(true);
        internal Action OnRead, OnNext;
        internal int FiniteTimeoutSeconds, CancelCalls, ReadCalls, NextCalls, ValueGetterCalls; internal int ReadCheckpointAt = 1;
        internal bool HoldReaderDispose;
        private int readCheckpoint, nextCheckpoint;
        internal ImportLiveCheckpoint(string phase) { TargetPhase = phase; }
        internal bool Matches(string sql) { return Phase(sql) == TargetPhase; }
        internal static string Phase(string sql)
        {
            if (sql == "DECLARE @databaseId int=DB_ID(); SELECT @databaseId,@@TRANCOUNT,XACT_STATE();") return "StateProbe";
            if (sql.StartsWith("IF OBJECT_ID(N'tempdb..#mp_", StringComparison.Ordinal)) return "LobProbe";
            if (sql.StartsWith("SELECT COUNT_BIG(*) FROM [", StringComparison.Ordinal)) return "Count";
            if (sql.Contains("/*IMPORT_INTEGRATION_DRAIN*/")) return "Drain";
            if (sql.Contains("/*IMPORT_INTEGRATION_TIMEOUT*/")) return "Timeout";
            return "Other";
        }
        internal void BeforeRead()
        {
            int read = Interlocked.Increment(ref ReadCalls);
            if (read >= ReadCheckpointAt && Interlocked.Exchange(ref readCheckpoint, 1) == 0 && OnRead != null) OnRead();
        }
        internal void BeforeNext()
        {
            Interlocked.Increment(ref NextCalls);
            if (Interlocked.Exchange(ref nextCheckpoint, 1) == 0 && OnNext != null) OnNext();
        }
        public void Dispose() { Issued.Dispose(); ReaderReady.Dispose(); DisposeEntered.Dispose(); DisposeRelease.Dispose(); }
    }

    private sealed class ImportLiveCommand : DbCommand
    {
        private readonly SqlCommand inner;
        private readonly ImportLiveCheckpoint point;
        internal ImportLiveCommand(SqlCommand command, ImportLiveCheckpoint checkpoint) { inner = command; point = checkpoint; }
        public override string CommandText { get { return inner.CommandText; } set { inner.CommandText = value; } }
        public override int CommandTimeout { get { return inner.CommandTimeout; } set { inner.CommandTimeout = value; } }
        public override CommandType CommandType { get { return inner.CommandType; } set { inner.CommandType = value; } }
        public override UpdateRowSource UpdatedRowSource { get { return inner.UpdatedRowSource; } set { inner.UpdatedRowSource = value; } }
        public override bool DesignTimeVisible { get { return inner.DesignTimeVisible; } set { inner.DesignTimeVisible = value; } }
        protected override DbConnection DbConnection { get { return inner.Connection; } set { inner.Connection = (SqlConnection)value; } }
        protected override DbTransaction DbTransaction { get { return inner.Transaction; } set { inner.Transaction = (SqlTransaction)value; } }
        protected override DbParameterCollection DbParameterCollection { get { return inner.Parameters; } }
        protected override DbParameter CreateDbParameter() { return inner.CreateParameter(); }
        public override void Cancel() { if (point != null && point.Matches(CommandText)) Interlocked.Increment(ref point.CancelCalls); inner.Cancel(); }
        public override void Prepare() { inner.Prepare(); }
        public override int ExecuteNonQuery() { throw new InvalidOperationException("Importer must use its actual streaming reader."); }
        public override object ExecuteScalar() { throw new InvalidOperationException("Importer probes must use owned reader acknowledgement."); }
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) { throw new InvalidOperationException("Expected async adapter startup."); }
        protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellation)
        {
            bool selected = point != null && point.Matches(CommandText);
            // Only the timeout fixture's test-local actual SqlCommand receives finite timeout.
            // Production adapter's timeout selection remains untouched.
            if (selected && point.TargetPhase == "Timeout" && point.FiniteTimeoutSeconds > 0) inner.CommandTimeout = point.FiniteTimeoutSeconds;
            Task<SqlDataReader> pending = inner.ExecuteReaderAsync(behavior, cancellation);
            if (selected) point.Issued.Set(); // Actual provider method invoked, not a synthetic command.
            SqlDataReader actual = await pending.ConfigureAwait(false);
            if (!selected) return actual;
            point.ReaderReady.Set();
            return new ImportLiveReader(actual, point);
        }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    private sealed class ImportLiveReader : DbDataReader
    {
        private readonly DbDataReader inner; private readonly ImportLiveCheckpoint point;
        internal ImportLiveReader(DbDataReader reader, ImportLiveCheckpoint checkpoint) { inner = reader; point = checkpoint; }
        public override async Task<bool> ReadAsync(CancellationToken cancellation) { point.BeforeRead(); return await inner.ReadAsync(cancellation).ConfigureAwait(false); }
        public override async Task<bool> NextResultAsync(CancellationToken cancellation) { point.BeforeNext(); return await inner.NextResultAsync(cancellation).ConfigureAwait(false); }
        public override bool Read() { return inner.Read(); }
        public override bool NextResult() { return inner.NextResult(); }
        public override int Depth { get { return inner.Depth; } }
        public override int FieldCount { get { return inner.FieldCount; } }
        public override bool HasRows { get { return inner.HasRows; } }
        public override bool IsClosed { get { return inner.IsClosed; } }
        public override int RecordsAffected { get { return inner.RecordsAffected; } }
        public override object this[int ordinal] { get { return inner[ordinal]; } }
        public override object this[string name] { get { return inner[name]; } }
        public override bool GetBoolean(int ordinal) { return inner.GetBoolean(ordinal); }
        public override byte GetByte(int ordinal) { return inner.GetByte(ordinal); }
        public override long GetBytes(int ordinal, long offset, byte[] buffer, int bufferOffset, int length) { Interlocked.Increment(ref point.ValueGetterCalls); return inner.GetBytes(ordinal, offset, buffer, bufferOffset, length); }
        public override char GetChar(int ordinal) { return inner.GetChar(ordinal); }
        public override long GetChars(int ordinal, long offset, char[] buffer, int bufferOffset, int length) { Interlocked.Increment(ref point.ValueGetterCalls); return inner.GetChars(ordinal, offset, buffer, bufferOffset, length); }
        public override string GetDataTypeName(int ordinal) { return inner.GetDataTypeName(ordinal); }
        public override DateTime GetDateTime(int ordinal) { return inner.GetDateTime(ordinal); }
        public override decimal GetDecimal(int ordinal) { return inner.GetDecimal(ordinal); }
        public override double GetDouble(int ordinal) { return inner.GetDouble(ordinal); }
        public override Type GetFieldType(int ordinal) { return inner.GetFieldType(ordinal); }
        public override float GetFloat(int ordinal) { return inner.GetFloat(ordinal); }
        public override Guid GetGuid(int ordinal) { return inner.GetGuid(ordinal); }
        public override short GetInt16(int ordinal) { return inner.GetInt16(ordinal); }
        public override int GetInt32(int ordinal) { return inner.GetInt32(ordinal); }
        public override long GetInt64(int ordinal) { return inner.GetInt64(ordinal); }
        public override string GetName(int ordinal) { return inner.GetName(ordinal); }
        public override int GetOrdinal(string name) { return inner.GetOrdinal(name); }
        public override string GetString(int ordinal) { return inner.GetString(ordinal); }
        public override object GetValue(int ordinal) { Interlocked.Increment(ref point.ValueGetterCalls); return inner.GetValue(ordinal); }
        public override int GetValues(object[] values) { Interlocked.Increment(ref point.ValueGetterCalls); return inner.GetValues(values); }
        public override bool IsDBNull(int ordinal) { return inner.IsDBNull(ordinal); }
        public override DataTable GetSchemaTable() { return inner.GetSchemaTable(); }
        public override IEnumerator GetEnumerator() { return ((IEnumerable)inner).GetEnumerator(); }
        public override void Close() { inner.Close(); }
        protected override void Dispose(bool disposing)
        {
            if (disposing) {
                try {
                    if (point.HoldReaderDispose) {
                        point.DisposeEntered.Set();
                        if (!point.DisposeRelease.Wait(10000)) throw new TimeoutException("Test-owned reader Dispose checkpoint deadline");
                    }
                } finally { inner.Dispose(); }
            }
            base.Dispose(disposing);
        }
    }

    // Deferred Task 3 minor fix: owner.Released is emitted INSIDE fake Dispose before
    // adapter ReleaseOwner.finally publishes SessionReleased. Reuse at the two offline
    // pending-cleanup assertions only while their provider release gate remains held.
    private static MssqlImportCleanup ImportWaitForPublishedReleaseWhileActive(MssqlImportSqlSession session)
    {
        var wait = Stopwatch.StartNew();
        while (true) {
            MssqlImportCleanup receipt = session.Cleanup();
            Assert(!receipt.CommandEnded && !receipt.TransactionCleanupConfirmed,
                "Provider still held: cleanup cannot fabricate command/rollback completion while release publication catches up.");
            if (receipt.SessionReleased) return receipt;
            if (wait.ElapsedMilliseconds >= 2000) throw new Exception("Adapter did not publish owner-release receipt before test deadline.");
            Thread.Sleep(5);
        }
    }

    // Live checkpoint usage (the normal importer retains its production constructor):
    // var owned = ConnectionOpenService.OpenMssqlImportSession(explicitFixtureSettings);
    // var connection = owned.Connection; // Capture before transferring this exact lifetime.
    // var adapter = new MssqlImportSqlSession(owned,
    //     () => new ImportLiveCommand(connection.CreateCommand(), point),
    //     () => connection.State == ConnectionState.Open);
    // Service factory returns this actual adapter; normal runs use production constructor.
    // All test checkpoint subscriptions/events/gates disposed in finally AFTER worker unwind.
    // Count: independent fixture-owned transaction holds table X lock; wait Issued then
    // cancel; release lock in finally. Actual SQL started and actual CancelCalls>0 required.
    // State/Lob: OnRead cancellation after ReaderReady proves actual probe startup and
    // adapter callback path, not prolonged server execution; label that limited boundary.
    // Drain: cancel after the configured selected read count,
    // next-result callback, and held Dispose; independent DB sentinel validates no next GO.
    // Timeout: unchanged WAITFOR fixture SQL+finite SqlCommand timeout; require error -2.
    // Transport: exact transferred owner.Dispose while command active; require uncertain
    // failure and released-session evidence, never stop instance/KILL unrelated SPID.
    // Closing race: hold actual reader Dispose, cancel/close in parallel, inspect pending
    // cleanup; release gate then require worker unwind before final receipt. No broad sleep.

    private static void ImportDraftRequireFixture(string server, string database, string[] allowedDatabases)
    {
        Assert(server == @"(localdb)\mySQLPunkExportTest_20261012", "Exact authorized LocalDB endpoint only.");
        Assert(allowedDatabases != null && allowedDatabases.Length == 2 && allowedDatabases.Contains(database), "Exact two generated owned DB allowlist.");
        foreach (string name in allowedDatabases) {
            Assert(name.StartsWith("mysqlpunk_import_", StringComparison.Ordinal) && (name.EndsWith("_src", StringComparison.Ordinal) || name.EndsWith("_dst", StringComparison.Ordinal)), "Generated fixture DB name only.");
            Guid id;
            Assert(name.Length == "mysqlpunk_import_".Length + 32 + 4 && Guid.TryParseExact(name.Substring("mysqlpunk_import_".Length, 32), "N", out id) && id != Guid.Empty, "GUID fixture identifier only.");
        }
    }
    private static MssqlOwnedSession ImportDraftOpenOwned(string server, string database, string[] allowedDatabases)
    {
        ImportDraftRequireFixture(server, database, allowedDatabases);
        ImportLiveFixture.ImportAssertRunning();
        var provider = new my_mssql();
        try {
            provider.SetConn(new SqlConnectionStringBuilder {
                DataSource = server, InitialCatalog = database, IntegratedSecurity = true,
                Pooling = false, MultipleActiveResultSets = false, ConnectRetryCount = 0,
                TrustServerCertificate = true, ConnectTimeout = 8
            }.ConnectionString);
            provider.Open();
            return new MssqlOwnedSession(provider, provider.MCT);
        } catch { provider.Dispose(); throw; }
    }
    private static MssqlImportResult ImportDraftRunActual(string server, string target, string[] allowedDatabases,
        string script, string output, bool skip = false, CancellationToken cancellation = default(CancellationToken),
        ImportLiveCheckpoint point = null, Func<Guid, IMssqlImportLog> logFactory = null,
        Action<MssqlImportProgress> progress = null)
    {
        // No fake session, external sqlcmd, Exec loop or synthetic success result.
        Func<IMssqlImportSession> sessions = () => {
            var owned = ImportDraftOpenOwned(server, target, allowedDatabases);
            if (point == null) return new ImportObservedSession(new MssqlImportSqlSession(owned), Path.Combine(output,"state-"+Guid.NewGuid().ToString("N")+".csv"));
            var connection = owned.Connection;
            return new MssqlImportSqlSession(owned, () => new ImportLiveCommand(connection.CreateCommand(), point),
                () => connection.State == ConnectionState.Open);
        };
        var service = new MssqlImportService(sessions, logFactory ?? (job => new MssqlImportLog(job, Path.Combine(output, "logs"))));
        var request = new MssqlImportRequest(Guid.NewGuid(), script, target, skip);
        using (var preflight = service.Preflight(request, progress, cancellation)) {
            Assert(preflight.Passed, "Actual SUT preflight: " + string.Join(",", preflight.Issues.Where(x => !x.IsWarning).Select(x => x.Code)));
            return service.Run(request, preflight, progress, cancellation);
        }
    }
    private static void ImportDraftRunGenericSessionCase(string server, string target, string[] allowed, string output)
    {
        const string text = @"SET DATEFIRST 3;
CREATE TABLE #carry(id int NOT NULL PRIMARY KEY);
INSERT #carry VALUES(7);
CREATE TABLE dbo.ImportSession(id int IDENTITY(10,2) NOT NULL PRIMARY KEY,v int NOT NULL);
GO -- supported delimiter comment
IF @@DATEFIRST<>3 OR (SELECT COUNT(*) FROM #carry)<>1 THROW 51000,'Session continuity',1;
SET IDENTITY_INSERT dbo.ImportSession ON;
GO
INSERT dbo.ImportSession(id,v) VALUES(37,7);
SET IDENTITY_INSERT dbo.ImportSession OFF;
BEGIN TRANSACTION;
INSERT dbo.ImportSession(v) VALUES(11);
GO
IF @@TRANCOUNT<>1 THROW 51000,'Transaction continuity',1;
INSERT dbo.ImportSession(v) VALUES(13);
SELECT id,v FROM dbo.ImportSession ORDER BY id;
SELECT COUNT_BIG(*) AS rows FROM dbo.ImportSession;
COMMIT TRANSACTION;
GO
";
        string path = Path.Combine(output, "generic-session.sql");
        File.WriteAllText(path, text.Replace("\r\n", "\n").Replace("\n", "\r\n"), new UTF8Encoding(false));
        var result = ImportDraftRunActual(server, target, allowed, path, output);
        Assert(result.Status == MssqlImportStatus.Completed && result.FailedBatches == 0 && result.LastAcknowledgedBatch == 4,
            "Actual generic same-session/transaction/identity/result batches complete.");
        Assert(result.Cleanup.CommandEnded && result.Cleanup.SessionReleased && result.Cleanup.TransactionCleanupConfirmed,
            "Actual command, owned connection and transaction cleanup each confirmed.");
        using (var owner = ImportDraftOpenOwned(server, target, allowed)) {
            using (var cmd = new SqlCommand("SELECT COUNT_BIG(*) FROM dbo.ImportSession WHERE (id=37 AND v=7) OR (id=39 AND v=11) OR (id=41 AND v=13)", owner.Connection))
                Assert(Convert.ToInt64(cmd.ExecuteScalar()) == 3, "Independent actual DB content matches identity and committed transaction.");
        }
    }
    private static MssqlExportRequest ImportDraftExportNative(string server, string source, string[] allowed,
        string output, string filename, MssqlExportMode mode, Func<MssqlTableDefinition, bool> select = null, bool views = true)
    {
        var recovery = new MssqlSnapshotRecoveryStore(Path.Combine(output, "export-journal"));
        var exporter = new MssqlSqlExportService(() => new MssqlExportSource(ImportDraftOpenOwned(server, source, allowed), recovery),
            new MssqlArtifactFiles(), null, recovery);
        var tables = exporter.LoadCatalog(source, CancellationToken.None).Tables.Where(t => select == null || select(t)).Select(t => t.Key).ToArray();
        string path = Path.Combine(output, filename);
        var request = new MssqlExportRequest(Guid.NewGuid(), source, tables, mode, views, path,
            MssqlExportArtifact.CaptureDestination(path, CancellationToken.None), null, false);
        Assert(exporter.Preflight(request, CancellationToken.None).CanStart, "Actual exporter fixture preflight.");
        var exported = exporter.Export(request, new WorkerProgress(p => { }), CancellationToken.None);
        Assert(exported.Stage == MssqlExportStage.Completed && File.Exists(exported.PublishedPath), "Actual native exporter published artifact.");
        return request;
    }
    private static void ImportDraftCompareSmallCatalogs(SqlConnection source, SqlConnection target)
    {
        // Existing tiny catalog helper allowed here; never applied to unbounded user DBs.
        foreach (var query in IntegrationMetadataQueries()) {
            if (query.Key == "comments") continue;
            Assert(IntegrationCatalog(source, query.Value) == IntegrationCatalog(target, query.Value), "Actual native catalog equality: " + query.Key);
        }
        const string properties = "SELECT OBJECT_SCHEMA_NAME(major_id),OBJECT_NAME(major_id),minor_id,name,CONVERT(nvarchar(max),value),CONVERT(nvarchar(128),SQL_VARIANT_PROPERTY(value,'BaseType')) FROM sys.extended_properties WHERE class=1";
        Assert(IntegrationCatalog(source, properties) == IntegrationCatalog(target, properties), "All table/column properties match; database ownership marker excluded.");
    }
    private static string ImportDraftCurrentRowDigest(SqlDataReader reader, byte[] buffer)
    {
        using (var hash = SHA256.Create()) {
            for (int i = 0; i < reader.FieldCount; i++) {
                bool nil = reader.IsDBNull(i);
                // Sequential column hashes bind NULL and byte length, not only concatenated bytes.
                long length = nil ? -1 : reader.GetBytes(i, 0, null, 0, 0);
                byte[] marker = BitConverter.GetBytes(length);
                hash.TransformBlock(marker, 0, marker.Length, null, 0);
                if (nil) continue;
                long at = 0; int read;
                while ((read = checked((int)reader.GetBytes(i, at, buffer, 0, buffer.Length))) != 0) {
                    hash.TransformBlock(buffer, 0, read, null, 0); at += read;
                }
                Assert(at == length, "Sequential raw-byte read matches actual length.");
            }
            hash.TransformFinalBlock(new byte[0], 0, 0);
            return BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant();
        }
    }
    private static long ImportDraftCompareOrderedBytes(SqlConnection source, SqlConnection target, string fixedFixtureSql)
    {
        var leftBuffer = new byte[65536]; var rightBuffer = new byte[65536]; long rows = 0;
        using (var a = new SqlCommand(fixedFixtureSql, source) { CommandTimeout = 0 })
        using (var b = new SqlCommand(fixedFixtureSql, target) { CommandTimeout = 0 })
        using (var left = a.ExecuteReader(CommandBehavior.SequentialAccess))
        using (var right = b.ExecuteReader(CommandBehavior.SequentialAccess)) {
            Assert(left.FieldCount == right.FieldCount, "Same projected column count.");
            while (left.Read()) {
                Assert(right.Read(), "Target has corresponding ordered row.");
                Assert(ImportDraftCurrentRowDigest(left, leftBuffer) == ImportDraftCurrentRowDigest(right, rightBuffer), "NULL/length/raw-byte row digest matches.");
                rows++;
            }
            Assert(!right.Read(), "No extra target rows.");
        }
        return rows;
    }

    // Fixed fixture SQL supplies a stable unique ORDER BY; query projects varbinary(max),
    // text/ntext/XML via CONVERT, geometry via Serialize(). Never hash rowversion as copied
    // content; verify regenerated length/metadata separately. Do not reuse List<string>
    // IntegrationRowHashes for millions of rows, and do not GetValue on any LOB.

    private const string ImportDraftLobSetup = @"
CREATE TABLE dbo.ImportLob(id int NOT NULL PRIMARY KEY,b varbinary(max),n nvarchar(max),x xml);
INSERT dbo.ImportLob VALUES(1,NULL,NULL,NULL),(2,0x,N'',CONVERT(xml,N'<empty/>')),
(3,CONVERT(varbinary(max),REPLICATE(CONVERT(varchar(max),'B'),67108864)),
 REPLICATE(CONVERT(nvarchar(max),N'W'),9437182)+NCHAR(0xD83D)+NCHAR(0xDE00),CONVERT(xml,N'<r xml:space=""preserve""> a  b </r>',1));
CREATE TABLE dbo.ImportLobGuard(id int NOT NULL PRIMARY KEY,b varbinary(max),x xml);
INSERT dbo.ImportLobGuard VALUES(1,CONVERT(varbinary(max),REPLICATE(CONVERT(varchar(max),'x'),300000)),CONVERT(xml,N'<ok/>')),
(2,0x42,CONVERT(xml,N'<next/>'));
";
    private const string ImportDraftGenericPartialFailure = @"
CREATE TABLE dbo.ImportFault(id int NOT NULL PRIMARY KEY);
GO
INSERT dbo.ImportFault VALUES(1);
RAISERROR(N'first runtime error',16,1);
RAISERROR(N'second runtime error',16,2);
GO
INSERT dbo.ImportFault VALUES(2);
GO
";
    private const string ImportDraftTimeout = @"
CREATE TABLE dbo.ImportTimeout(id int);
GO
/*IMPORT_INTEGRATION_TIMEOUT*/ WAITFOR DELAY '00:00:08';
INSERT dbo.ImportTimeout VALUES(1);
GO
INSERT dbo.ImportTimeout VALUES(2);
GO
";
    private const string ImportDraftDrainingResults = @"
/*IMPORT_INTEGRATION_DRAIN*/
WITH n AS(SELECT TOP(2500000) ROW_NUMBER() OVER(ORDER BY (SELECT NULL)) AS n FROM sys.all_objects a CROSS JOIN sys.all_objects b)
SELECT n FROM n;
SELECT CONVERT(varbinary(max),REPLICATE(CONVERT(varchar(max),'z'),67108864)) AS big;
SELECT 1 AS final_result;
GO
";
    private const string ImportDraftTransactionFailure = @"
CREATE TABLE dbo.ImportTransaction(id int NOT NULL PRIMARY KEY);
GO
BEGIN TRANSACTION;
INSERT dbo.ImportTransaction VALUES(1);
GO
INSERT dbo.ImportTransaction VALUES(1);
GO
INSERT dbo.ImportTransaction VALUES(2);
COMMIT TRANSACTION;
GO
";

    // Mutate only the first large-row protocol in a TINY guard artifact generated by
    // actual exporter. Retains at most two batch strings. Footer/native GUID unchanged.
    private static void ImportDraftMutateGuardArtifact(string source, string destination, string fault)
    {
        using (var input = MssqlImportInput.Open(source))
        using (var writer = new StreamWriter(new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536), new UTF8Encoding(false), 65536)) {
            var scan=input.Scan(CancellationToken.None,null);
            bool firstChunk = false, changed = false, firstCommit = false;
            MssqlImportBatch held = null;
            foreach (var batch in input.ReadBatches(CancellationToken.None)) {
                string text = batch.Text;
                bool chunk = text.Contains(".WRITE(");
                if (fault == "reordered" && held != null) {
                    Assert(chunk, "Guard fixture first chunks are adjacent export batches.");
                    writer.Write(text); writer.Write("\r\nGO\r\n");
                    writer.Write(held.Text); writer.Write("\r\nGO\r\n");
                    held = null; changed = true; continue;
                }
                if (chunk && !firstChunk) {
                    firstChunk = true;
                    if (fault == "missing") { changed = true; continue; }
                    if (fault == "reordered") { held = batch; continue; }
                    if (fault == "deserialize") {
                        int hex = text.IndexOf(".WRITE(0x", StringComparison.Ordinal) + 9;
                        Assert(hex >= 9 && hex + 10 <= text.Length, "Serialized geometry first chunk found.");
                        // Serialized header: SRID 4 bytes then version byte; alter version,
                        // retain total byte count so error occurs in actual Deserialize.
                        text = text.Substring(0, hex + 8) + "FF" + text.Substring(hex + 10); changed = true;
                    }
                }
                writer.Write(text); writer.Write("\r\nGO\r\n");
                if (chunk && firstChunk && !changed && fault == "duplicate") { writer.Write(text); writer.Write("\r\nGO\r\n"); changed = true; }
                // Artifact selected only ImportLobGuard or GeometryGuard; first commit
                // identified by target INSERT, not arbitrary comments/messages.
                bool commit = text.Contains("INSERT INTO [dbo].[ImportLobGuard]") || text.Contains("INSERT INTO [dbo].[GeometryGuard]");
                if (fault == "replayed" && commit && !firstCommit) { firstCommit = true; writer.Write(text); writer.Write("\r\nGO\r\n"); changed = true; }
            }
            Assert(held == null && changed, "Requested bounded protocol mutation applied once.");
            writer.Write("-- mysqlpunk-export-complete job="+scan.Script.NativeJobId.Value.ToString("N")+" tables="+scan.Script.ExpectedTables+" rows="+scan.Script.ExpectedRows+"\r\n");
        }
    }
    private static void ImportDraftGenerateGeometry(SqlConnection source, string wkbPath, string fixedOwnedTable, int points)
    {
        Assert(fixedOwnedTable == "dbo.GeometryLarge" || fixedOwnedTable == "dbo.GeometryGuard", "Exact fixed fixture table only.");
        using (var stream = new FileStream(wkbPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536))
        using (var writer = new BinaryWriter(stream)) {
            writer.Write((byte)1); writer.Write((uint)2); writer.Write((uint)points);
            for (int i=0; i<points; i++) { writer.Write((double)i); writer.Write((double)(i%2)); }
        }
        using (var stream = File.OpenRead(wkbPath))
        using (var command = new SqlCommand("INSERT " + fixedOwnedTable + "(id,g) VALUES(1,geometry::STGeomFromWKB(@wkb,4326));", source) { CommandTimeout = 0 }) {
            command.Parameters.Add("@wkb", SqlDbType.VarBinary, -1).Value = stream;
            command.ExecuteNonQuery();
        }
    }

    // Lob workflow: apply fixed setup through separate fixture connection;
    // export native full, import actual SUT; independent ordered byte projections:
    // SELECT CONVERT(varbinary(max),id),b,CONVERT(varbinary(max),n),
    // CONVERT(varbinary(max),CONVERT(nvarchar(max),x)) FROM dbo.ImportLob ORDER BY id.
    // Assert DATALENGTH b=67108864 and n=18874368 at id3, NULL/empty distinct.
    // Native DataOnly repeats after fixture-owned table truncate only; no full restore
    // verification claim. Guard target pre-created and row2 is next-row sentinel.
    // Constraint case target CHECK(id<>1) forces final INSERT failure. XML mutation
    // changes raw UTF16 '<ok/>' to '<bad>' same length before final CONVERT(xml).
    // Missing state / duplicate state / pending EOF mutations must preserve native
    // header/footer and inspector role; SQL-corruption preflight failure is separate.

    // Faults workflow: new case-specific tables/targets, run actual SUT skip=false
    // then skip=true; independent SELECT proves partial id1 remains, id2 only on safe
    // skip. Full actual log must contain both RAISERROR details before allowing skip.
    // Transaction error runs both policies: rollback cleanup should leave no committed
    // rows and no next batch, while earlier DDL may remain. Unclosed successful BEGIN
    // reaching EOF is failure; receipt records rollback confirmed only from actual query.
    // Timeout checkpoint wraps actual WAITFOR SqlCommand with 1 sec timeout, requires
    // provider -2 and stopped sentinel. No timeout on ordinary production execution.
    // Log factory fail-create is before write; wrapper of actual log can fail Start,
    // append, Flush or End independently, preserving any already written evidence.

    private static long ImportDraftGenerateMemorySql(string destination, int scale)
    {
        Assert(scale == 1 || scale == 10, "Only planned 1x/10x memory scales.");
        // Identical ~1MiB command batches, 256 vs2560 batches. Small retained comment
        // block and fixed writer buffers; no whole-file bytes/text/batch collections.
        string block = new string('m', 65536);
        using (var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536))
        using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), 65536)) {
            for (int batch = 0; batch < checked(256 * scale); batch++) {
                writer.Write("SELECT CAST(1 AS int); /*");
                for (int part=0; part<16; part++) writer.Write(block);
                writer.Write("*/\r\nGO\r\n");
            }
        }
        return new FileInfo(destination).Length;
    }
    private sealed class ImportDraftMemory : IDisposable
    {
        internal volatile string Stage = "Baseline";
        private readonly object gate = new object();
        private readonly Dictionary<string, ImportDraftMemoryStage> stages = new Dictionary<string, ImportDraftMemoryStage>();
        private readonly Timer timer;
        internal ImportDraftMemory() { timer = new Timer(_ => Sample(), null, 0, 25); }
        private void Sample()
        {
            using (var process = Process.GetCurrentProcess()) {
                process.Refresh(); string stage = Stage;
                lock (gate) {
                    ImportDraftMemoryStage value;
                    if (!stages.TryGetValue(stage, out value)) stages[stage] = value = new ImportDraftMemoryStage {
                        FirstPrivateBytes=process.PrivateMemorySize64, FirstWorkingSet=process.WorkingSet64
                    };
                    value.PeakPrivateBytes=Math.Max(value.PeakPrivateBytes,process.PrivateMemorySize64);
                    value.PeakWorkingSet=Math.Max(value.PeakWorkingSet,process.WorkingSet64);
                    value.LastPrivateBytes=process.PrivateMemorySize64; value.LastWorkingSet=process.WorkingSet64;
                    value.Samples++;
                }
            }
        }
        internal Dictionary<string, ImportDraftMemoryStage> Snapshot()
        {
            Sample(); var result = new Dictionary<string, ImportDraftMemoryStage>();
            lock(gate) foreach(var pair in stages) result[pair.Key] = pair.Value.Copy();
            return result;
        }
        public void Dispose() { using(var done = new ManualResetEvent(false)) { timer.Dispose(done); Assert(done.WaitOne(5000), "Memory sampler disposed within deadline."); } }
    }
    private sealed class ImportDraftMemoryStage
    {
        public long FirstPrivateBytes,FirstWorkingSet,PeakPrivateBytes,PeakWorkingSet,LastPrivateBytes,LastWorkingSet,Samples;
        internal ImportDraftMemoryStage Copy() { return (ImportDraftMemoryStage)MemberwiseClone(); }
    }
    // Memory child contract:
    // - Parent saves/restores MYSQLPUNK_MSSQL_IMPORT_MEMORY_SCALE as well as five required
    //   import flags+fixed smoke filter. Scale accepts only1/10, no arbitrary child command.
    // - Child repeats exact manifest/endpoint/root/ownership guards, requires IntPtr.Size=8.
    // - Generate input before measurement; own same tiny DB setup; warm actual importer
    //   with identical bounded batch shape before baseline. Parent builds once before any
    //   measured child. Never ReportExportTests or parallel builds/profiles.
    // - New sampler after warmup; Stage from actual import progress: Scanning/Executing/
    //   Verifying/Finished, plus Begin measurement. Actual Run consumes same preflight
    //   handle, completes all256/2560 batches. Record script streamed SHA + exact bytes.
    // - Child saves actual result/cleanup/log, baseline and per-stage aggregate receipts.
    //   Parent independently requires both child receipts and6 expected Memory case IDs.
    // - Compare peak-private-minus-warm-baseline deltas and raw peaks; >256MiB difference
    //   requires investigation/fix; ratio is evidence, never sole pass when no work matched.
    // - Include million-row and64MiB returned-LOB drain cases with separate aggregates;
    //   no result field getters or DataTable and no per-row memory/progress history.

    private sealed class ImportDraftLogFailure : IMssqlImportLog
    {
        private readonly MssqlImportLog inner; private readonly ImportDraftFailingStream stream;
        private readonly string fault;
        internal ImportDraftLogFailure(Guid job,string directory,string mode)
        {
            fault=mode;
            if(mode=="create") throw new IOException("Fixture log create failure");
            ImportDraftFailingStream created=null;
            inner=new MssqlImportLog(job,directory,path => created=new ImportDraftFailingStream(new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read)));
            stream=created;
        }
        public string Path {get{return inner.Path;}}
        public MssqlImportLogTail Tail {get{return inner.Tail;}}
        public void WriteStart(MssqlImportRequest request,MssqlImportScan scan,MssqlImportTarget target)
        { if(fault=="start") stream.FailWrite=true; inner.WriteStart(request,scan,target); }
        public void WriteError(MssqlImportBatch batch,MssqlImportBatchException error,string action)
        {
            if(fault=="append") stream.FailWrite=true;
            inner.WriteError(batch,error,action);
            // append fixture uses >=3 actual SQL errors of1800 chars each so actual
            // StreamWriter writes >4096 buffer while WriteError is still running.
        }
        public void Flush() { if(fault=="flush") stream.FailFlush=true; inner.Flush(); }
        public void WriteEnd(MssqlImportResult result) { if(fault=="end") stream.FailWrite=true; inner.WriteEnd(result); }
        public void Dispose() { inner.Dispose(); }
    }
    private sealed class ImportDraftFailingStream : Stream
    {
        private readonly FileStream inner;
        internal bool FailWrite,FailFlush; internal int RejectedWrites,RejectedFlushes;
        internal ImportDraftFailingStream(FileStream file){inner=file;}
        public override bool CanRead{get{return false;}} public override bool CanSeek{get{return inner.CanSeek;}} public override bool CanWrite{get{return true;}}
        public override long Length{get{return inner.Length;}} public override long Position{get{return inner.Position;}set{inner.Position=value;}}
        public override int Read(byte[] buffer,int offset,int count){throw new NotSupportedException();}
        public override long Seek(long offset,SeekOrigin origin){return inner.Seek(offset,origin);}
        public override void SetLength(long value){inner.SetLength(value);}
        public override void Write(byte[] buffer,int offset,int count){if(FailWrite){RejectedWrites++;throw new IOException("Fixture actual log stream write failure");}inner.Write(buffer,offset,count);}
        public override void Flush(){if(FailFlush){RejectedFlushes++;throw new IOException("Fixture actual log stream flush failure");}inner.Flush(true);}
        protected override void Dispose(bool disposing){if(disposing)inner.Dispose();base.Dispose(disposing);}
    }
    // Actual service's factory: job=>new ImportDraftLogFailure(job,ownedRunLogs,mode).
    // Preflight failure for create/start, worker stop for append/flush/end; verify actual
    // DB next-GO sentinel absence and status, not merely seam exception count. Available
    // log bytes/hash and rejected stream operation counts survive into failure receipt.
    // Provider unwind precedes watchdog owner publication; the actual worker must wait
    // only within its bounded diagnostic window and never retry the owned Dispose.
    private static void ImportInverseReleasePublication(bool beyondWindow)
    {
        using(var f=new ImportWorkerFixture("SELECT 'inverse-release';\r\nGO\r\n"))
        using(var issued=new ManualResetEventSlim())using(var providerReturn=new ManualResetEventSlim())
        using(var ownerEntered=new ManualResetEventSlim())using(var ownerReturn=new ManualResetEventSlim())
        using(var cleanupEntered=new ManualResetEventSlim())using(var cancel=new CancellationTokenSource()) {
            var owner=new ImportSessionOwner{Error=DiagnosticSql(2627,50001),BeforeReturn=()=>{ownerEntered.Set();Assert(ownerReturn.Wait(5000),"Inverse owner gate bounded.");}};
            int opens=0;MssqlImportSqlSession execution=null;
            f.Service=new MssqlImportService(()=>{
                bool worker=++opens==2;
                var adapter=new MssqlImportSqlSession(worker?owner:new ImportSessionOwner(),()=>{
                    var command=new ImportSessionCommand(null);
                    command.Start=(behavior,token)=>{
                        if(command.CommandText.StartsWith("SELECT CONVERT(nvarchar(128),SERVERPROPERTY",StringComparison.Ordinal))return Task.FromResult<DbDataReader>(ImportValues(ImportIdentity(1)));
                        if(command.CommandText=="SELECT COUNT_BIG(*) FROM sys.objects WHERE is_ms_shipped=0;")return Task.FromResult<DbDataReader>(ImportValues(0L));
                        if(command.CommandText=="DECLARE @databaseId int=DB_ID(); SELECT @databaseId,@@TRANCOUNT,XACT_STATE();")return Task.FromResult<DbDataReader>(ImportValues(7,0,0));
                        Assert(worker&&command.CommandText.Contains("inverse-release"),"Only unchanged source batch reaches provider gate.");
                        issued.Set();Assert(providerReturn.Wait(5000),"Inverse provider gate bounded.");
                        return Task.FromResult<DbDataReader>(new ImportSessionReader(new[]{0},true));
                    };return command;
                },()=>true,40,beyondWindow?150:2000);
                if(!worker)return (IMssqlImportSession)adapter;
                execution=adapter;return new ImportCleanupEnteredSession(adapter,cleanupEntered);
            },job=>new MssqlImportLog(job,f.DirectoryPath));
            Task<MssqlImportResult> run=null;
            try {
                using(var pre=f.Service.Preflight(f.Request,null,CancellationToken.None)) {
                    Assert(pre.Passed,"Inverse release actual adapter preflight passes.");
                    run=Task.Run(()=>f.Service.Run(f.Request,pre,null,cancel.Token));
                    Assert(issued.Wait(2000),"Inverse worker command entered.");cancel.Cancel();
                    Assert(ownerEntered.Wait(2000),"Watchdog enters owned Dispose before provider unwind.");
                    providerReturn.Set();Assert(cleanupEntered.Wait(2000),"Provider unwinds first and actual worker enters Cleanup.");
                    if(!beyondWindow) {
                        Assert(!run.Wait(75),"Final worker log cannot precede an in-window owner publication.");
                        ownerReturn.Set();Assert(run.Wait(3000),"Worker completes after delivered owner details.");
                    }else Assert(run.Wait(2000),"Pending owner cannot hold final worker beyond bounded cleanup wait.");
                    var result=run.Result;
                    Assert(result.Status==MssqlImportStatus.CleanupUncertain&&result.Cleanup.CommandEnded&&!result.Cleanup.SessionReleased&&!result.Cleanup.TransactionCleanupConfirmed,
                        "Inverse cancellation preserves command acknowledgement and uncertain release/rollback truth.");
                    string log=File.ReadAllText(result.LogPath);
                    if(!beyondWindow) {
                        Assert(result.Cleanup.Errors.Select(x=>x.Number).SequenceEqual(new[]{2627,50001}),"In-window inverse publication retains both typed SqlError details.");
                        foreach(int code in new[]{2627,50001})Assert(log.Split(new[]{"Number="+code+" "},StringSplitOptions.None).Length==2,"Actual worker log records delivered owner error exactly once.");
                        Assert(log.IndexOf("Origin=Cleanup",StringComparison.Ordinal)<log.IndexOf("EndUtc=",StringComparison.Ordinal),"Inverse cleanup diagnostics precede terminal record.");
                    }else {
                        Assert(result.Cleanup.Errors.Count==0&&result.Cleanup.IssueCode.Contains("SessionReleasePending")&&log.Contains("SessionReleasePending"),"Expired release window is explicitly pending, not confirmed or silently complete.");
                        ownerReturn.Set();
                        var completed=typeof(MssqlImportSqlSession).GetField("releaseCompleted",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
                        Assert(completed!=null&&SpinWait.SpinUntil(()=>{var publication=typeof(MssqlImportSqlSession).GetField("gate",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(execution);lock(publication)return (bool)completed.GetValue(execution);},2000),"Late publication is observed boundedly after terminal timeout.");
                        Assert(log==File.ReadAllText(result.LogPath),"Late publication cannot append duplicate or post-terminal worker log entries.");
                    }
                    Assert(object.ReferenceEquals(result.Cleanup,execution.Cleanup())&&owner.Disposes==1,"Final snapshot remains one-shot; late owner completion cannot redispose or relog.");
                    execution.Dispose();Assert(owner.Disposes==1,"Dispose after cached cleanup never retries owner.");
                }
            }finally {
                ownerReturn.Set();providerReturn.Set();
                if(run!=null)Assert(run.Wait(3000),"Inverse worker always unwinds before fixture release.");
            }
        }
    }
    private sealed class ImportCleanupEnteredSession : IMssqlImportSession
    {
        private readonly MssqlImportSqlSession inner;private readonly ManualResetEventSlim entered;
        internal ImportCleanupEnteredSession(MssqlImportSqlSession inner,ManualResetEventSlim entered){this.inner=inner;this.entered=entered;}
        public MssqlImportTarget ReadTarget(CancellationToken token){return inner.ReadTarget(token);}
        public MssqlImportSessionState ReadState(CancellationToken token){return inner.ReadState(token);}
        public MssqlImportLobState ReadLobState(Guid job,CancellationToken token){return inner.ReadLobState(job,token);}
        public void ExecuteBatch(MssqlImportBatch batch,CancellationToken token){inner.ExecuteBatch(batch,token);}
        public MssqlImportCounts CountTargetRows(Action<long,long> progress,CancellationToken token){return inner.CountTargetRows(progress,token);}
        public void CancelActiveCommand(){inner.CancelActiveCommand();}
        public MssqlImportCleanup Cleanup(){entered.Set();return inner.Cleanup();}
        public void Dispose(){inner.Dispose();}
    }
    private static void MssqlImportCleanupErrorDiagnostics()
    {
        ImportInverseReleasePublication(false);
        ImportInverseReleasePublication(true);
        var cleanupCommand=new ImportSessionCommand(ImportValues(0,0)){StartError=DiagnosticSql(547,50000)};
        using(var session=new MssqlImportSqlSession(new ImportSessionOwner(),()=>cleanupCommand,()=>true)) {
            var receipt=session.Cleanup();
            Assert(receipt.Errors.Count==2 && receipt.Errors[0].Number==547 && receipt.Errors[1].Number==50000,
                "Cleanup must preserve every delivered provider SqlError, rather than only a fixed failure code.");
            Assert(receipt.CommandEnded&&receipt.SessionReleased&&!receipt.TransactionCleanupConfirmed&&receipt.IssueCode=="TransactionCleanupFailed", "Preserving diagnostics cannot fabricate rollback success.");
        }
        var releaseOwner=new ImportSessionOwner{Error=DiagnosticSql(2627,50001)};
        using(var session=new MssqlImportSqlSession(releaseOwner,()=>new ImportSessionCommand(ImportValues(0,0)),()=>true)){
            var receipt=session.Cleanup();
            Assert(receipt.Errors.Select(x=>x.Number).SequenceEqual(new[]{2627,50001}),"Every actual typed SqlError thrown by owned Dispose survives cleanup receipt.");
            Assert(receipt.CommandEnded&&!receipt.SessionReleased&&receipt.TransactionCleanupConfirmed,"Release SQL details cannot rewrite release/rollback truth.");
            Assert(object.ReferenceEquals(receipt,session.Cleanup())&&releaseOwner.Disposes==1,"Cached receipt neither repeats release nor duplicates release details.");
        }
        var mergedOwner=new ImportSessionOwner{Error=DiagnosticSql(2627,50001)};
        using(var session=new MssqlImportSqlSession(mergedOwner,()=>new ImportSessionCommand(ImportValues(0,0)){StartError=DiagnosticSql(547,50000)},()=>true))
        using(var f=new ImportWorkerFixture()){
            f.Session.Finish=()=>session.Cleanup();var result=f.Run();
            Assert(result.Cleanup.Errors.Select(x=>x.Number).SequenceEqual(new[]{547,50000,2627,50001}),"Rollback and current-release SQL details merge once in actual order.");
            string log=File.ReadAllText(result.LogPath);
            Assert(log.Contains("Origin=Cleanup")&&new[]{547,50000,2627,50001}.All(number=>log.Contains("Number="+number+" ")),"Actual worker log preserves merged cleanup and release diagnostics.");
            Assert(result.Status==MssqlImportStatus.CleanupUncertain&&!result.Cleanup.SessionReleased&&!result.Cleanup.TransactionCleanupConfirmed,"Merged error log retains uncertain cleanup truth.");
        }        foreach(bool primaryFailure in new[]{false,true})using(var f=new ImportWorkerFixture()) {
            if(primaryFailure)f.Session.Execute=(b,t)=>{throw WorkerSqlError();};
            var errors=new[]{new MssqlImportSqlError(547,2,16,9,"cleanup detail one"),new MssqlImportSqlError(50000,3,16,10,"cleanup detail two")};
            f.Session.Finish=()=>new MssqlImportCleanup(true,true,false,"TransactionCleanupFailed",errors);
            var result=f.Run();string log=File.ReadAllText(result.LogPath);
            Assert(log.Contains("Origin=Cleanup")&&log.Contains("RelativeLine=9 SourceLine=unknown Message=cleanup detail one")&&log.Contains("cleanup detail two"),
                "Actual worker log streams all cleanup SQL diagnostics with unknown source coordinates.");
            Assert(log.IndexOf("Origin=Cleanup",StringComparison.Ordinal)<log.IndexOf("EndUtc=",StringComparison.Ordinal),"Cleanup diagnostics precede terminal record.");
            Assert(result.Status==(primaryFailure?MssqlImportStatus.Failed:MssqlImportStatus.CleanupUncertain),"Primary failure/uncertain cleanup result remains truthful.");
        }
    }    private static readonly string[] ImportProfiles = { "Core", "Lob", "Geometry", "Faults", "Memory" };
    private sealed class ImportLiveFixture : IDisposable
    {
        internal readonly string Server,Root,Source,Target,AcceptanceProfile;
        internal readonly string[] Databases;
        private readonly string owner = Guid.NewGuid().ToString("N");
        private readonly List<string> created = new List<string>();
        private bool disposed;
        internal string CleanupStatus = "Pending";
        internal readonly Dictionary<string,object> Receipts = new Dictionary<string,object>();
        internal ImportLiveFixture(string acceptanceProfile)
        {
            if(Environment.GetEnvironmentVariable("MYSQLPUNK_MSSQL_IMPORT_INTEGRATION")!="1" || !ImportProfiles.Contains(acceptanceProfile))throw new InvalidOperationException("ExplicitImportRunnerRequired");
            AcceptanceProfile=acceptanceProfile;Server=Environment.GetEnvironmentVariable("MYSQLPUNK_MSSQL_IMPORT_SERVER");
            string authorization=Environment.GetEnvironmentVariable("MYSQLPUNK_MSSQL_IMPORT_AUTH");
            if(string.IsNullOrWhiteSpace(authorization)||!File.Exists(authorization))throw new InvalidOperationException("FixtureAuthorizationMissing");
            JObject manifest=JObject.Parse(File.ReadAllText(authorization));
            string output=Environment.GetEnvironmentVariable("MYSQLPUNK_MSSQL_IMPORT_OUTPUT");
            if(string.IsNullOrWhiteSpace(output))throw new InvalidOperationException("FixtureOutputMissing");
            Root=ImportValidateEnvelope(manifest,Server,Path.Combine(output,acceptanceProfile),new DriveInfo("C").AvailableFreeSpace,ImportReadGuardPath);
            ImportAssertRunning(); // SqlConnection.Open can implicitly start LocalDB; reject BEFORE Open.
            string identity=Guid.NewGuid().ToString("N");Source="mysqlpunk_import_"+identity+"_src";Target="mysqlpunk_import_"+identity+"_dst";Databases=new[]{Source,Target};
            Directory.CreateDirectory(Root);
            using(var token=new FileStream(Path.Combine(Root,"owner.token"),FileMode.CreateNew,FileAccess.Write,FileShare.None))
            using(var writer=new StreamWriter(token,new UTF8Encoding(false))){writer.Write(owner);writer.Flush();token.Flush(true);}
            try {CreateOwned(Source);CreateOwned(Target);}
            catch {try{Dispose();}catch{}throw;}
        }
        internal static void ImportAssertRunning()
        {
            var start=new ProcessStartInfo("sqllocaldb.exe","info mySQLPunkExportTest_20261012") {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            using(var process=Process.Start(start)) {
                string output=process.StandardOutput.ReadToEnd();string error=process.StandardError.ReadToEnd();
                if(!process.WaitForExit(5000)||process.ExitCode!=0||output.IndexOf("LOCALDB#",StringComparison.OrdinalIgnoreCase)<0)
                    throw new InvalidOperationException("AuthorizedLocalDBMustAlreadyBeRunning");
                // No owner/account/raw instance output is printed or retained in evidence.
                GC.KeepAlive(error);
            }
        }
        private void RequireOwnerFile()
        {
            ImportNoReparse(Path.Combine(Root,"owner.token"),ImportReadGuardPath);
            if(File.ReadAllText(Path.Combine(Root,"owner.token"))!=owner)throw new InvalidOperationException("FixtureFileOwnershipMismatch");
        }
        private SqlConnection OpenAdmin(string database)
        {
            if(database!="master"&&!Databases.Contains(database))throw new InvalidOperationException("FixtureDatabaseNotAllowed");
            RequireOwnerFile();ImportAssertRunning();
            var connection=new SqlConnection(new SqlConnectionStringBuilder{DataSource=Server,InitialCatalog=database,IntegratedSecurity=true,Pooling=false,ConnectRetryCount=0,MultipleActiveResultSets=false,TrustServerCertificate=true,ConnectTimeout=8}.ConnectionString);
            try{connection.Open();return connection;}catch{connection.Dispose();throw;}
        }
        internal SqlConnection Open(string database)
        { if(!Databases.Contains(database))throw new InvalidOperationException("FixtureDatabaseNotAllowed");return OpenAdmin(database); }
        private void CreateOwned(string database)
        {
            if(!Databases.Contains(database)||created.Contains(database))throw new InvalidOperationException("FixtureCreateNameNotOwned");
            RequireOwnerFile();
            if(new DriveInfo("C").AvailableFreeSpace<20L*1024*1024*1024)throw new InvalidOperationException("FixtureInsufficientSpace");
            string data=Path.Combine(Root,database+".mdf"),log=Path.Combine(Root,database+".ldf");
            if(File.Exists(data)||File.Exists(log))throw new InvalidOperationException("FixtureCreateFilesAlreadyExist");
            ImportNoReparse(data,ImportReadGuardPath);ImportNoReparse(log,ImportReadGuardPath);
            using(var connection=OpenAdmin("master"))Exec(connection,"CREATE DATABASE "+Q(database)+" ON PRIMARY(NAME=N'data',FILENAME="+L(data)+",SIZE=16MB,MAXSIZE=8192MB,FILEGROWTH=64MB) LOG ON(NAME=N'log',FILENAME="+L(log)+",SIZE=16MB,MAXSIZE=2048MB,FILEGROWTH=64MB) COLLATE Latin1_General_100_BIN2;");
            created.Add(database); // CREATE succeeded: retain exact name even if marking fails.
            using(var connection=OpenAdmin(database))Exec(connection,"EXEC sys.sp_addextendedproperty @name=N'mysqlpunk_import_owner',@value="+L(owner)+";");
        }
        private void RequireOwnedDatabaseFiles(string database)
        {
            // Fresh ownership and exact physical-file evidence before any owned DB mutation.
            RequireOwnerFile();
            if(!Databases.Contains(database)||!created.Contains(database))throw new InvalidOperationException("FixtureMutationNameNotOwned");
            string marker;var files=new List<string>();
            using(var connection=OpenAdmin(database)) {
                marker=Convert.ToString(Scalar(connection,"SELECT value FROM sys.extended_properties WHERE class=0 AND name=N'mysqlpunk_import_owner';"));
                using(var command=new SqlCommand("SELECT physical_name FROM sys.database_files;",connection))using(var reader=command.ExecuteReader())while(reader.Read())files.Add(reader.GetString(0));
            }
            ImportAssertCleanup(database,created.ToArray(),Databases,Root,owner,File.ReadAllText(Path.Combine(Root,"owner.token")),marker,files.ToArray(),ImportReadGuardPath);
        }
        internal void SetTargetReadOnly(bool readOnly)
        {
            RequireOwnedDatabaseFiles(Target);
            // Fixed exact target/options; NO_WAIT fails rather than terminating other sessions.
            using(var connection=OpenAdmin("master"))Exec(connection,"ALTER DATABASE "+Q(Target)+(readOnly?" SET READ_ONLY WITH NO_WAIT;":" SET READ_WRITE WITH NO_WAIT;"));
        }
        internal void RecordTargetProperties(string label)
        {
            using(var connection=OpenAdmin("master"))
            using(var command=new SqlCommand("SELECT is_read_only,is_auto_close_on FROM sys.databases WHERE name="+L(Target)+";",connection))
            using(var reader=command.ExecuteReader()) {
                Assert(reader.Read(),"Exact owned target remains visible for lifecycle observation.");
                File.WriteAllText(Path.Combine(Root,"target-"+label+".json"),JsonConvert.SerializeObject(new{label,database=Target,observationDatabase=connection.Database,isReadOnly=reader.GetBoolean(0),autoClose=reader.GetBoolean(1)},Formatting.Indented));
            }
        }
        private void DropOwned(string database)
        {
            RequireOwnedDatabaseFiles(database);
            // Exact generated DB and physical paths + dual ownership rechecked immediately before mutation.
            using(var connection=OpenAdmin("master"))Exec(connection,"ALTER DATABASE "+Q(database)+" SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE "+Q(database)+";");
            created.Remove(database);
        }
        internal void ResetTarget(){DropOwned(Target);CreateOwned(Target);}
        internal static string Q(string identifier){return "["+identifier.Replace("]","]]")+"]";}
        internal static string L(string value){return "N'"+value.Replace("'","''")+"'";}
        internal static void Exec(SqlConnection connection,string sql){using(var command=new SqlCommand(sql,connection){CommandTimeout=60})command.ExecuteNonQuery();}
        internal static object Scalar(SqlConnection connection,string sql){using(var command=new SqlCommand(sql,connection){CommandTimeout=60})return command.ExecuteScalar();}
        internal string WriteScript(string name,string text){string path=Path.Combine(Root,name);File.WriteAllText(path,text.Replace("\r\n","\n").Replace("\n","\r\n"),new UTF8Encoding(false));return path;}
        internal void Setup(string smallFixture)
        {
            string fixture=Path.Combine(Environment.GetEnvironmentVariable("MYSQLPUNK_MSSQL_IMPORT_FIXTURES"),smallFixture);
            Assert(new FileInfo(fixture).Length<1024*1024,"Only small tracked fixture source may be read wholly.");
            string sql=File.ReadAllText(fixture);using(var connection=Open(Source))foreach(string batch in System.Text.RegularExpressions.Regex.Split(sql,@"(?im)^GO\s*\r?$"))if(!string.IsNullOrWhiteSpace(batch))Exec(connection,batch);
        }
        internal void Case(string caseId,Action assertion)
        {
            var clock=Stopwatch.StartNew();string status="Failed";
            try{assertion();status="Passed";}
            finally {
                string path=Path.Combine(Root,caseId+".json");File.WriteAllText(path,JsonConvert.SerializeObject(new{schemaVersion=1,caseId,status,executed=true,durationMs=clock.ElapsedMilliseconds},Formatting.Indented));
                string hash;using(var sha=SHA256.Create())using(var stream=File.OpenRead(path))hash=BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant();
                Receipts[caseId]=new{caseId,status,executed=true,assertionsPassed=status=="Passed"?1:0,assertionsFailed=status=="Passed"?0:1,receiptSha256=hash};
            }
        }
        public void Dispose()
        {
            if(disposed)return;disposed=true;
            try{foreach(string db in created.ToArray())DropOwned(db);CleanupStatus="Confirmed";}
            finally{File.WriteAllText(Path.Combine(Root,"cleanup.json"),JsonConvert.SerializeObject(new{status=CleanupStatus,remaining=created.ToArray()},Formatting.Indented));}
        }
    }    private static void MssqlImportPublishedReleaseRace()
    {
        using(var startup=new ManualResetEventSlim())using(var providerRelease=new ManualResetEventSlim())
        using(var ownerInDispose=new ManualResetEventSlim())using(var ownerReturn=new ManualResetEventSlim())using(var cancel=new CancellationTokenSource()) {
            var owner=new ImportSessionOwner { BeforeReturn=()=>{ownerInDispose.Set();Assert(ownerReturn.Wait(3000),"Controlled owner-return gate releases.");} };
            var command=new ImportSessionCommand(new ImportSessionReader(new[]{0},true));
            command.Start=(behavior,token)=>{startup.Set();Assert(providerRelease.Wait(3000),"Controlled provider gate releases.");return Task.FromResult<DbDataReader>(command.Reader);};
            using(var session=new MssqlImportSqlSession(owner,()=>command,()=>true,40)) {
                var worker=Task.Run(()=>{try{session.ExecuteBatch(ImportSessionBatch,cancel.Token);}catch(OperationCanceledException){}});
                try {
                    Assert(startup.Wait(1000),"Provider actual seam entered before cancellation.");cancel.Cancel();
                    Assert(ownerInDispose.Wait(2000),"Fake owner signals while still inside Dispose, before adapter publication.");
                    var pending=session.Cleanup();Assert(!pending.CommandEnded&&!pending.SessionReleased&&!pending.TransactionCleanupConfirmed,"Pre-return event is not published adapter release receipt.");
                    ownerReturn.Set();var published=ImportWaitForPublishedReleaseWhileActive(session);
                    Assert(published.SessionReleased&&!published.CommandEnded,"Bounded published-receipt wait removes the fake pre-return event race.");
                }finally{ownerReturn.Set();providerRelease.Set();}
                Assert(worker.Wait(2000),"Provider unwinds after gates release.");
            }
        }
    }    private static void MssqlImportIntegrationLive()
    {
        if(Environment.GetEnvironmentVariable("MYSQLPUNK_MSSQL_IMPORT_INTEGRATION")!="1") {Console.WriteLine("SKIP native import live acceptance (explicit runner required)");return;}
        string acceptanceProfile=Environment.GetEnvironmentVariable("MYSQLPUNK_MSSQL_IMPORT_PROFILE");
        if(!ImportProfiles.Contains(acceptanceProfile))throw new InvalidOperationException("UnknownImportAcceptanceProfile");
        ImportLiveFixture fixture=null;bool complete=false;
        try {
            fixture=new ImportLiveFixture(acceptanceProfile);
            switch(acceptanceProfile) {
                case "Core":ImportLiveCore(fixture);break;
                case "Lob":ImportLiveLob(fixture);break;
                case "Geometry":ImportLiveGeometry(fixture);break;
                case "Faults":ImportLiveFaults(fixture);break;
                case "Memory":
                    string scale=Environment.GetEnvironmentVariable("MYSQLPUNK_MSSQL_IMPORT_MEMORY_SCALE");
                    if(scale=="1"||scale=="10")ImportLiveMemoryChild(fixture,int.Parse(scale));else ImportLiveMemory(fixture);
                    break;
                default:throw new NotImplementedException("Actual profile not yet wired: "+acceptanceProfile);
            }
            complete=true;
        } finally {
            if(fixture!=null) {
                try{fixture.Dispose();}
                finally {
                    string exe=typeof(SmokeTests).Assembly.Location;string hash;using(var sha=SHA256.Create())using(var file=File.OpenRead(exe))hash=BitConverter.ToString(sha.ComputeHash(file)).Replace("-","").ToLowerInvariant();
                    File.WriteAllText(Path.Combine(fixture.Root,"profile-receipt.json"),JsonConvert.SerializeObject(new{schemaVersion=1,profile=acceptanceProfile,
                        runId=Environment.GetEnvironmentVariable("MYSQLPUNK_MSSQL_IMPORT_RUN_ID"),complete,processBits=IntPtr.Size*8,executableSha256=hash,
                        cases=fixture.Receipts.Values.ToArray(),cleanupStatus=fixture.CleanupStatus},Formatting.Indented));
                }
            }
        }
    }
    private static void ImportLiveCore(ImportLiveFixture f)
    {
        using(var c=f.Open(f.Target)){
            var observations=new Dictionary<string,int[]>();
            foreach(var query in new[]{new[]{"alone","SELECT @@TRANCOUNT,XACT_STATE();"},new[]{"db-id-first","SELECT DB_ID(),@@TRANCOUNT,XACT_STATE();"},new[]{"db-id-captured","DECLARE @db int=DB_ID(); SELECT @db,@@TRANCOUNT,XACT_STATE();"},new[]{"xact-captured","DECLARE @x int=XACT_STATE(); SELECT DB_ID(),@@TRANCOUNT,@x;"},new[]{"options","SELECT @@OPTIONS;"}})
                using(var cmd=new SqlCommand(query[1],c))using(var reader=cmd.ExecuteReader()){Assert(reader.Read(),"State diagnostic row.");var values=new int[reader.FieldCount];for(int i=0;i<values.Length;i++)values[i]=Convert.ToInt32(reader.GetValue(i));observations[query[0]]=values;}
            File.WriteAllText(Path.Combine(f.Root,"initial-state-observations.json"),JsonConvert.SerializeObject(observations,Formatting.Indented));
        }        f.Setup("core.sql");
        using(var source=f.Open(f.Source))ImportLiveFixture.Exec(source,"CREATE TABLE dbo.Untrusted(id int NOT NULL,parent int NULL); ALTER TABLE dbo.Untrusted WITH NOCHECK ADD CONSTRAINT FK_Untrusted FOREIGN KEY(parent) REFERENCES [一].[同名]]表](id); CREATE TABLE dbo.CompositeParent(a int NOT NULL,b int NOT NULL,PRIMARY KEY(a,b)); INSERT dbo.CompositeParent VALUES(1,2); CREATE TABLE dbo.CompositeChild(a int,b int,CONSTRAINT FK_Composite FOREIGN KEY(a,b) REFERENCES dbo.CompositeParent(a,b) ON DELETE NO ACTION ON UPDATE NO ACTION); INSERT dbo.CompositeChild VALUES(1,2);");
        // Extend only this owned Core source; preserve existing tables/rows/case IDs and compare raw target bytes below.
        using(var source=f.Open(f.Source)){
            ImportLiveFixture.Exec(source,"ALTER TABLE [一].[同名]]表] ADD legacydt datetime NULL,legacysmall smalldatetime NULL;");
            ImportLiveFixture.Exec(source,@"UPDATE [一].[同名]]表] SET legacydt=CASE [unique] WHEN 1 THEN CONVERT(datetime,'1753-01-01T00:00:00.003',126) WHEN 2 THEN CONVERT(datetime,'9999-12-31T23:59:59.997',126) END,
legacysmall=CASE [unique] WHEN 1 THEN CONVERT(smalldatetime,'1900-01-01T00:00:00.000',126) WHEN 2 THEN CONVERT(smalldatetime,'2079-06-06T23:59:00.000',126) END;");
        }
        MssqlExportRequest full=null;
        f.Case("core.native-schema-data",()=>{
            full=ImportDraftExportNative(f.Server,f.Source,f.Databases,f.Root,"native-full.sql",MssqlExportMode.SchemaAndData);
            var result=ImportDraftRunActual(f.Server,f.Target,f.Databases,full.TargetPath,f.Root);
            ImportRecordResult(f,"full",full.TargetPath,result);
            Assert(result.Status==MssqlImportStatus.Completed&&result.ValidationSummary.Contains("NativeSummaryCountsMatched"),"Actual native importer EOF/table/row verification completed.");
            using(var source=f.Open(f.Source))using(var target=f.Open(f.Target))ImportDraftCompareSmallCatalogs(source,target);
            ImportCompareFixtureRows(f,full);
        });
        f.Case("core.catalog-identities-indexes",()=>{using(var c=f.Open(f.Target)){
            Assert(Convert.ToInt32(ImportLiveFixture.Scalar(c,"INSERT dbo.IdentityEmpty(v) VALUES(9);SELECT SCOPE_IDENTITY();"))==11,"Empty identity seed preserved.");
            Assert(Convert.ToInt32(ImportLiveFixture.Scalar(c,"INSERT dbo.IdentityNegative(v) VALUES(9);SELECT SCOPE_IDENTITY();"))==-9,"Descending identity next preserved.");
            Assert(Convert.ToInt32(ImportLiveFixture.Scalar(c,"SELECT COUNT(*) FROM sys.indexes WHERE name=N'IX_filtered' AND has_filter=1;"))==1,"Filtered included index exists.");
        }});
        f.Case("core.catalog-fk-check-trust",()=>{using(var c=f.Open(f.Target)){
            Assert(Convert.ToInt32(ImportLiveFixture.Scalar(c,"SELECT COUNT(*) FROM sys.foreign_keys WHERE name=N'FK_Untrusted' AND is_not_trusted=1;"))==1,"Untrusted FK stays untrusted.");
            Assert(Convert.ToInt32(ImportLiveFixture.Scalar(c,"SELECT COUNT(*) FROM sys.foreign_keys WHERE name=N'FK_same' AND is_not_trusted=0;"))==1,"Trusted FK stays trusted.");
            Assert(Convert.ToInt32(ImportLiveFixture.Scalar(c,"SELECT COUNT(*) FROM sys.foreign_key_columns WHERE constraint_object_id=OBJECT_ID(N'dbo.FK_Composite');"))==2,"Actual native composite FK preserves both column ordinals.");
        }});
        f.Case("core.catalog-views-properties",()=>{using(var a=f.Open(f.Source))using(var b=f.Open(f.Target)){
            const string sql="SELECT OBJECT_SCHEMA_NAME(major_id),OBJECT_NAME(major_id),minor_id,name,CONVERT(nvarchar(max),value) FROM sys.extended_properties WHERE class=1";
            Assert(IntegrationCatalog(a,sql)==IntegrationCatalog(b,sql),"Table/column properties independently match.");
            Assert(Convert.ToInt32(ImportLiveFixture.Scalar(b,"SELECT COUNT(*) FROM [一].[view]]中文]"))==3,"Native view executes actual restored rows.");
        }});
        f.ResetTarget();
        f.Case("core.native-schema-only",()=>{
            var request=ImportDraftExportNative(f.Server,f.Source,f.Databases,f.Root,"native-schema.sql",MssqlExportMode.SchemaOnly);
            var result=ImportDraftRunActual(f.Server,f.Target,f.Databases,request.TargetPath,f.Root);ImportRecordResult(f,"schema",request.TargetPath,result);
            Assert(result.Status==MssqlImportStatus.Completed,"Actual SchemaOnly native importer completes.");
            using(var target=f.Open(f.Target))Assert(Convert.ToInt64(ImportLiveFixture.Scalar(target,"SELECT COUNT_BIG(*) FROM dbo.MultiLob"))==0,"SchemaOnly creates empty native table.");
        });
        f.Case("core.native-data-only",()=>{
            var request=ImportDraftExportNative(f.Server,f.Source,f.Databases,f.Root,"native-data.sql",MssqlExportMode.DataOnly,views:false);
            var result=ImportDraftRunActual(f.Server,f.Target,f.Databases,request.TargetPath,f.Root);ImportRecordResult(f,"data",request.TargetPath,result);
            Assert(result.Status==MssqlImportStatus.Completed&&!result.ValidationSummary.Contains("NativeSummaryCountsMatched"),"DataOnly completes without pretending full schema/content verification.");
            ImportCompareFixtureRows(f,request);
        });
        f.Case("core.generic-session-temp-set",()=>ImportDraftRunGenericSessionCase(f.Server,f.Target,f.Databases,f.Root));
        f.Case("core.generic-identity-insert",()=>{using(var c=f.Open(f.Target))Assert(Convert.ToInt64(ImportLiveFixture.Scalar(c,"SELECT COUNT_BIG(*) FROM dbo.ImportSession WHERE id IN(37,39,41)"))==3,"Actual generic IDENTITY_INSERT continuity result.");});
        f.Case("core.generic-cross-go-transaction",()=>{using(var c=f.Open(f.Target))Assert(Convert.ToInt64(ImportLiveFixture.Scalar(c,"SELECT SUM(CONVERT(bigint,v)) FROM dbo.ImportSession"))==31,"Actual committed cross-GO transaction visible on independent session.");});
        f.Case("core.generic-multiple-results",()=>{
            string script=f.WriteScript("multi-results.sql","SELECT 1;SELECT 2 WHERE 1=0;SELECT 3;\r\nGO\r\nINSERT dbo.ImportSession(v) VALUES(17);\r\nGO\r\n");
            var result=ImportDraftRunActual(f.Server,f.Target,f.Databases,script,f.Root);ImportRecordResult(f,"multi",script,result);
            Assert(result.Status==MssqlImportStatus.Completed&&result.SuccessfulBatches==2,"All result sets drained before next GO acknowledgement.");
        });
        f.Case("core.parser-failure-no-write",()=>{
            string path=f.WriteScript("parser-invalid.sql","CREATE TABLE dbo.NeverParser(id int);\r\nGO 2\r\n");int opens=0;
            var service=new MssqlImportService(()=>{opens++;return new MssqlImportSqlSession(ImportDraftOpenOwned(f.Server,f.Target,f.Databases));},job=>new MssqlImportLog(job,Path.Combine(f.Root,"logs")));
            using(var pre=service.Preflight(new MssqlImportRequest(Guid.NewGuid(),path,f.Target),null,CancellationToken.None))Assert(!pre.Passed&&opens==0,"Actual parser rejection precedes any metadata/execution connection.");
            using(var c=f.Open(f.Target))Assert(ImportLiveFixture.Scalar(c,"SELECT OBJECT_ID(N'dbo.NeverParser')")==DBNull.Value,"Parser failure did not create target object.");
        });
        f.Case("core.target-identity-recheck",()=>{
            string path=f.WriteScript("identity-recheck.sql","CREATE TABLE dbo.NeverRecreated(id int);\r\nGO\r\n");
            var service=new MssqlImportService(()=>new MssqlImportSqlSession(ImportDraftOpenOwned(f.Server,f.Target,f.Databases)),job=>new MssqlImportLog(job,Path.Combine(f.Root,"logs")));
            var request=new MssqlImportRequest(Guid.NewGuid(),path,f.Target);
            using(var pre=service.Preflight(request,null,CancellationToken.None)){
                Assert(pre.Passed,"Actual identity preflight.");f.ResetTarget();var result=service.Run(request,pre,null,CancellationToken.None);ImportRecordResult(f,"recreated",path,result);
                Assert(result.Status==MssqlImportStatus.Failed&&result.SuccessfulBatches==0,"Recreated owned target rejected before execution.");
            }
        });
    }
    private static void ImportCompareFixtureRows(ImportLiveFixture f,MssqlExportRequest request)
    {
        using(var source=f.Open(f.Source))using(var target=f.Open(f.Target))foreach(var table in request.Tables){
            string qualified=ImportLiveFixture.Q(table.Schema)+"."+ImportLiveFixture.Q(table.Name);var expressions=new List<string>();
            using(var command=new SqlCommand("SELECT c.name,ty.name FROM sys.columns c JOIN sys.types ty ON c.user_type_id=ty.user_type_id WHERE c.object_id=OBJECT_ID("+ImportLiveFixture.L(qualified)+") ORDER BY column_id",source))using(var reader=command.ExecuteReader())while(reader.Read()){
                string name=ImportLiveFixture.Q(reader.GetString(0)),type=reader.GetString(1);if(type=="timestamp")continue;
                if(type=="geometry")name+=".Serialize()";if(type=="text")name="CONVERT(varchar(max),"+name+")";if(type=="ntext"||type=="xml")name="CONVERT(nvarchar(max),"+name+")";expressions.Add("CONVERT(varbinary(max),"+name+")");
            }
            string query="SELECT "+string.Join(",",expressions)+" FROM "+qualified;
            Assert(IntegrationRowHashes(source,query).SequenceEqual(IntegrationRowHashes(target,query)),"Actual fixture raw bytes/NULL/length match "+qualified);
        }
    }
    private static void ImportRecordResult(ImportLiveFixture f,string key,string script,MssqlImportResult result)
    {
        string hash;using(var sha=SHA256.Create())using(var file=File.OpenRead(script))hash=BitConverter.ToString(sha.ComputeHash(file)).Replace("-","").ToLowerInvariant();
        File.WriteAllText(Path.Combine(f.Root,"result-"+key+".json"),JsonConvert.SerializeObject(new{script=Path.GetFileName(script),bytes=new FileInfo(script).Length,sha256=hash,
            result.Status,result.FileBytes,result.ProcessedBytes,result.SuccessfulBatches,result.FailedBatches,result.LastAcknowledgedBatch,result.UncertainBatch,result.LogPath,result.ValidationSummary,result.Elapsed,
            cleanup=result.Cleanup==null?null:new{result.Cleanup.CommandEnded,result.Cleanup.SessionReleased,result.Cleanup.TransactionCleanupConfirmed,result.Cleanup.IssueCode}},Formatting.Indented));
    }    private sealed class ImportObservedSession : IMssqlImportSession
    {
        private readonly IMssqlImportSession inner;private readonly string path;private readonly Action<MssqlImportTarget> targetObserved;private readonly Queue<string> states=new Queue<string>();private long batch;private Guid job;
        internal ImportObservedSession(IMssqlImportSession session,string tracePath,Action<MssqlImportTarget> targetObserved=null){inner=session;path=tracePath;this.targetObserved=targetObserved;}
        public MssqlImportTarget ReadTarget(CancellationToken token){var target=inner.ReadTarget(token);if(targetObserved!=null)targetObserved(target);return target;}
        public MssqlImportSessionState ReadState(CancellationToken token){var state=inner.ReadState(token);states.Enqueue(batch+","+state.DatabaseId+","+state.TransactionCount+","+state.XactState+","+state.IsOpen);while(states.Count>64)states.Dequeue();return state;}
        public MssqlImportLobState ReadLobState(Guid identity,CancellationToken token){var state=inner.ReadLobState(identity,token);File.WriteAllText(path+".lob.json",JsonConvert.SerializeObject(new{batch,state.Exists,state.RowCount,state.Sequence,state.ConsumedThrough,state.Consumed,state.Failed}));return state;}
        public void ExecuteBatch(MssqlImportBatch value,CancellationToken token){batch=value.Number;if(job==Guid.Empty){var match=System.Text.RegularExpressions.Regex.Match(value.Text,@"#mp_([a-fA-F0-9]{32})_s");if(match.Success)job=Guid.ParseExact(match.Groups[1].Value,"N");}inner.ExecuteBatch(value,token);}
        public MssqlImportCounts CountTargetRows(Action<long,long> progress,CancellationToken token){return inner.CountTargetRows(progress,token);}
        public void CancelActiveCommand(){inner.CancelActiveCommand();}
        public MssqlImportCleanup Cleanup(){if(job!=Guid.Empty)try{var current=ReadState(CancellationToken.None);if(current.IsOpen)ReadLobState(job,CancellationToken.None);}catch{}return inner.Cleanup();}
        public void Dispose(){try{inner.Dispose();}finally{File.WriteAllLines(path,new[]{"batch,databaseId,transactionCount,xactState,isOpen"}.Concat(states),new UTF8Encoding(false));}}
    }    private static void ImportLiveLob(ImportLiveFixture f)
    {
        using(var c=f.Open(f.Source))ImportLiveFixture.Exec(c,ImportDraftLobSetup);
        MssqlExportRequest full=null;
        f.Case("lob.binary-64m-roundtrip",()=>{
            full=ImportDraftExportNative(f.Server,f.Source,f.Databases,f.Root,"lob-full.sql",MssqlExportMode.SchemaAndData,views:false);
            var result=ImportDraftRunActual(f.Server,f.Target,f.Databases,full.TargetPath,f.Root);ImportRecordResult(f,"lob-full",full.TargetPath,result);
            Assert(result.Status==MssqlImportStatus.Completed,"Actual 64MiB binary native import completed.");
            using(var source=f.Open(f.Source))using(var target=f.Open(f.Target))Assert(ImportDraftCompareOrderedBytes(source,target,"SELECT CONVERT(varbinary(max),id),b FROM dbo.ImportLob ORDER BY id")==3,"64MiB binary bytes stream equal.");
        });
        f.Case("lob.unicode-18m-roundtrip",()=>{using(var a=f.Open(f.Source))using(var b=f.Open(f.Target)){
            Assert(Convert.ToInt64(ImportLiveFixture.Scalar(b,"SELECT DATALENGTH(n) FROM dbo.ImportLob WHERE id=3"))==18L*1024*1024,"18MiB Unicode length including surrogate pair.");
            Assert(ImportDraftCompareOrderedBytes(a,b,"SELECT CONVERT(varbinary(max),id),CONVERT(varbinary(max),n) FROM dbo.ImportLob ORDER BY id")==3,"18MiB UTF16 raw bytes match.");
        }});
        f.Case("lob.null-empty-xml-roundtrip",()=>{using(var a=f.Open(f.Source))using(var b=f.Open(f.Target)){
            Assert(ImportDraftCompareOrderedBytes(a,b,"SELECT CONVERT(varbinary(max),id),b,CONVERT(varbinary(max),n),CONVERT(varbinary(max),CONVERT(nvarchar(max),x)) FROM dbo.ImportLob ORDER BY id")==3,"NULL/empty/XML raw-byte projections match.");
            Assert(Convert.ToInt64(ImportLiveFixture.Scalar(b,"SELECT COUNT_BIG(*) FROM dbo.ImportLob WHERE (id=1 AND b IS NULL AND n IS NULL AND x IS NULL) OR(id=2 AND DATALENGTH(b)=0 AND DATALENGTH(n)=0)"))==2,"NULL remains distinct from empty.");
        }});
        f.Case("lob.native-data-only",()=>{
            using(var c=f.Open(f.Target)){ImportLiveFixture.Exec(c,"TRUNCATE TABLE dbo.ImportLob;TRUNCATE TABLE dbo.ImportLobGuard;");}
            var request=ImportDraftExportNative(f.Server,f.Source,f.Databases,f.Root,"lob-data.sql",MssqlExportMode.DataOnly,views:false);
            var result=ImportDraftRunActual(f.Server,f.Target,f.Databases,request.TargetPath,f.Root);ImportRecordResult(f,"lob-data",request.TargetPath,result);
            Assert(result.Status==MssqlImportStatus.Completed&&!result.ValidationSummary.Contains("NativeSummaryCountsMatched"),"Actual native DataOnly verifies protocol but does not claim full schema manifest validation.");
            using(var a=f.Open(f.Source))using(var b=f.Open(f.Target))Assert(ImportDraftCompareOrderedBytes(a,b,"SELECT CONVERT(varbinary(max),id),b,CONVERT(varbinary(max),n) FROM dbo.ImportLob ORDER BY id")==3,"DataOnly large byte streams equal.");
        });
        var guard=ImportDraftExportNative(f.Server,f.Source,f.Databases,f.Root,"lob-guard.sql",MssqlExportMode.DataOnly,t=>t.Key.Name=="ImportLobGuard",false);
        foreach(var fault in new[]{"missing","duplicate","reordered","replayed","constraint","xml","missing-state","duplicate-state","pending-eof","sequence"}){
            string id= fault=="missing"?"lob.chunk-missing":fault=="duplicate"?"lob.chunk-duplicate":fault=="reordered"?"lob.chunk-reordered":fault=="replayed"?"lob.final-replayed":fault=="constraint"?"lob.final-constraint-failed0tran":fault=="xml"?"lob.xml-conversion-failed0tran":fault=="missing-state"?"lob.state-missing-after-init":fault=="duplicate-state"?"lob.state-duplicate":fault=="pending-eof"?"lob.pending-eof":"lob.sequence-eof";
            f.Case(id,()=>ImportGuardFault(f,guard.TargetPath,fault,"ImportLobGuard",false));
        }
    }
    private static void ImportGuardFault(ImportLiveFixture f,string source,string fault,string table,bool geometry)
    {
        string altered=Path.Combine(f.Root,table+"-"+fault+".sql");
        if(new[]{"missing","duplicate","reordered","replayed","deserialize"}.Contains(fault))ImportDraftMutateGuardArtifact(source,altered,fault);
        else if(fault=="constraint")File.Copy(source,altered);
        else ImportMutateProtocolState(source,altered,fault,table);
        foreach(bool skip in new[]{false,true}){
            f.ResetTarget();
            using(var target=f.Open(f.Target))ImportLiveFixture.Exec(target,"CREATE TABLE dbo."+table+"(id int NOT NULL PRIMARY KEY,"+(geometry?"g geometry NULL":"b varbinary(max),x xml")+(fault=="constraint"?",CONSTRAINT CK_reject CHECK(id<>1)":"")+");");
            var result=ImportDraftRunActual(f.Server,f.Target,f.Databases,altered,f.Root,skip);ImportRecordResult(f,table+"-"+fault+"-"+skip,altered,result);
            Assert(result.Status==MssqlImportStatus.Failed,"Actual native protocol failure cannot be skipped: "+fault+" skip="+skip);
            using(var target=f.Open(f.Target)){
                long count=Convert.ToInt64(ImportLiveFixture.Scalar(target,"SELECT COUNT_BIG(*) FROM dbo."+table));
                long next=Convert.ToInt64(ImportLiveFixture.Scalar(target,"SELECT COUNT_BIG(*) FROM dbo."+table+" WHERE id=2"));
                Assert(next==0&&count==(fault=="replayed"||fault=="sequence"?1:0),"Failed native row/next-row boundary preserved: "+fault);
            }
        }
    }
    private static void ImportMutateProtocolState(string source,string destination,string fault,string table)
    {
        using(var input=MssqlImportInput.Open(source)){
            var scan=input.Scan(CancellationToken.None,null);string state="#mp_"+scan.Script.NativeJobId.Value.ToString("N")+"_s";bool applied=false,stopBody=false;
            using(var writer=new StreamWriter(new FileStream(destination,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536),new UTF8Encoding(false),65536)){
                foreach(var batch in input.ReadBatches(CancellationToken.None)){
                    string text=batch.Text;
                    if(stopBody)continue;
                    if(fault=="xml"&&text.Contains("3C006F006B002F003E00")){text=text.Replace("3C006F006B002F003E00","3C006200610064003E00");applied=true;}
                    writer.Write(text);writer.Write("\r\nGO\r\n");
                    if(!applied&&text.Contains("LOB job already exists")&&(fault=="missing-state"||fault=="duplicate-state")){
                        writer.Write(fault=="missing-state"?"DROP TABLE "+state+";":"INSERT INTO "+state+" SELECT * FROM "+state+";");writer.Write("\r\nGO\r\n");applied=true;
                    }
                    if(!applied&&fault=="pending-eof"&&text.Contains("[Consumed]=0")&&text.Contains("CREATE TABLE #mp_")){applied=true;stopBody=true;}
                    if(!applied&&fault=="sequence"&&text.Contains("INSERT INTO [dbo].["+table+"]")){
                        writer.Write("UPDATE "+state+" SET [Sequence]=[Sequence]+1;\r\nGO\r\n");applied=true;
                    }
                }
                Assert(applied,"Actual tiny exporter protocol mutation applied: "+fault);
                writer.Write("-- mysqlpunk-export-complete job="+scan.Script.NativeJobId.Value.ToString("N")+" tables="+scan.Script.ExpectedTables+" rows="+scan.Script.ExpectedRows+"\r\n");
            }
        }
    }    private static void ImportLiveGeometry(ImportLiveFixture f)
    {
        f.Setup("geometry.sql");
        using(var c=f.Open(f.Source)){
            ImportDraftGenerateGeometry(c,Path.Combine(f.Root,"geometry-large.wkb"),"dbo.GeometryLarge",1100001);
            ImportDraftGenerateGeometry(c,Path.Combine(f.Root,"geometry-guard.wkb"),"dbo.GeometryGuard",20001);
            ImportLiveFixture.Exec(c,"INSERT dbo.GeometryGuard(id,g) VALUES(2,geometry::Point(3,4,4326));");
        }
        MssqlExportRequest full=null;
        f.Case("geometry.bytes-metadata-roundtrip",()=>{
            full=ImportDraftExportNative(f.Server,f.Source,f.Databases,f.Root,"geometry-full.sql",MssqlExportMode.SchemaAndData,views:false);
            var result=ImportDraftRunActual(f.Server,f.Target,f.Databases,full.TargetPath,f.Root);ImportRecordResult(f,"geometry-full",full.TargetPath,result);
            Assert(result.Status==MssqlImportStatus.Completed,"Actual native Geometry full importer completes.");ImportCompareFixtureRows(f,full);
            using(var source=f.Open(f.Source))using(var target=f.Open(f.Target)){
                ImportDraftCompareSmallCatalogs(source,target);
                const string sql="SELECT id,g.STSrid AS srid,g.STGeometryType() AS kind,g.STIsEmpty() AS empty,g.STIsValid() AS valid,g.STNumPoints() AS points,g.STPointN(1).Z AS z,g.STPointN(1).M AS m,h.STSrid AS secondSrid,h.STGeometryType() AS secondKind,h.STIsEmpty() AS secondEmpty FROM dbo.GeometryCases WHERE id<>8";
                Assert(IntegrationCatalog(source,sql)==IntegrationCatalog(target,sql),"Native SRID/type/Z/M/curves/empty metadata independently equal.");
            }
        });
        f.Case("geometry.large-serialize-roundtrip",()=>{using(var source=f.Open(f.Source))using(var target=f.Open(f.Target)){
            Assert(Convert.ToInt64(ImportLiveFixture.Scalar(target,"SELECT DATALENGTH(g.Serialize()) FROM dbo.GeometryLarge"))>16L*1024*1024,"Actual serialized Geometry exceeds16MiB.");
            Assert(ImportDraftCompareOrderedBytes(source,target,"SELECT CONVERT(varbinary(max),id),g.Serialize() FROM dbo.GeometryLarge ORDER BY id")==1,"Large Geometry raw Serialize stream equal.");
        }});
        f.Case("geometry.null-empty-invalid-curves",()=>{using(var target=f.Open(f.Target)){
            Assert(Convert.ToInt32(ImportLiveFixture.Scalar(target,"SELECT COUNT(*) FROM dbo.GeometryCases WHERE id=1 AND g IS NULL AND h.STIsEmpty()=1"))==1,"NULL and valid empty remain distinct.");
            Assert(Convert.ToInt32(ImportLiveFixture.Scalar(target,"SELECT g.STIsValid() FROM dbo.GeometryCases WHERE id=8"))==0,"Invalid-but-storable geometry never normalized or repaired.");
            Assert(Convert.ToInt32(ImportLiveFixture.Scalar(target,"SELECT COUNT(*) FROM dbo.GeometryCases WHERE id=3 AND g.STSrid=3826 AND g.Z=3 AND g.M=4"))==1,"SRID/Z/M preserved.");
            Assert(Convert.ToString(ImportLiveFixture.Scalar(target,"SELECT g.STGeometryType() FROM dbo.GeometryCases WHERE id=6"))=="CircularString","Curve remains native curve.");
        }});
        f.Case("geometry.native-data-only",()=>{
            using(var target=f.Open(f.Target))ImportLiveFixture.Exec(target,"TRUNCATE TABLE dbo.GeometryCases;TRUNCATE TABLE dbo.GeometryLarge;TRUNCATE TABLE dbo.GeometryGuard;");
            var data=ImportDraftExportNative(f.Server,f.Source,f.Databases,f.Root,"geometry-data.sql",MssqlExportMode.DataOnly,views:false);
            var result=ImportDraftRunActual(f.Server,f.Target,f.Databases,data.TargetPath,f.Root);ImportRecordResult(f,"geometry-data",data.TargetPath,result);
            Assert(result.Status==MssqlImportStatus.Completed&&!result.ValidationSummary.Contains("NativeSummaryCountsMatched"),"Actual Geometry DataOnly protocol verified with limited summary claim.");ImportCompareFixtureRows(f,data);
        });
        var guard=ImportDraftExportNative(f.Server,f.Source,f.Databases,f.Root,"geometry-guard.sql",MssqlExportMode.DataOnly,t=>t.Key.Name=="GeometryGuard",false);
        foreach(string fault in new[]{"missing","duplicate","reordered","replayed","deserialize","constraint","pending-eof"}){
            string id=fault=="missing"?"geometry.chunk-missing":fault=="duplicate"?"geometry.chunk-duplicate":fault=="reordered"?"geometry.chunk-reordered":fault=="replayed"?"geometry.final-replayed":fault=="deserialize"?"geometry.deserialize-failed0tran":fault=="constraint"?"geometry.final-constraint-failed0tran":"geometry.pending-eof";
            f.Case(id,()=>ImportGuardFault(f,guard.TargetPath,fault,"GeometryGuard",true));
        }
    }
    private static void ImportLiveFaults(ImportLiveFixture f)
    {
        foreach (bool skip in new[] { false, true }) {
            bool localSkip = skip;
            f.Case("faults.partial-batch-" + (skip ? "skip" : "stop"), () => {
                f.ResetTarget();
                string path = f.WriteScript("partial-" + localSkip + ".sql", ImportDraftGenericPartialFailure);
                var result = ImportDraftRunActual(f.Server, f.Target, f.Databases, path, f.Root, localSkip);
                ImportRecordResult(f, "partial-" + localSkip, path, result);
                Assert(result.Status == (localSkip ? MssqlImportStatus.CompletedWithErrors : MssqlImportStatus.Failed), "Actual partial SQL error status follows requested stop/skip policy.");
                Assert(result.FailedBatches == 1 && result.LastAcknowledgedBatch == (localSkip ? 3 : 1), "Only acknowledged successful/skipped GO boundaries advance the receipt.");
                Assert(result.SkippedBytes > 0 == localSkip, "Skipped byte accounting follows safe skip decision.");
                using (var c = f.Open(f.Target)) {
                    Assert(Convert.ToInt64(ImportLiveFixture.Scalar(c, "SELECT COUNT_BIG(*) FROM dbo.ImportFault WHERE id=1")) == 1, "Autocommitted partial row survives the failed batch.");
                    Assert(Convert.ToInt64(ImportLiveFixture.Scalar(c, "SELECT COUNT_BIG(*) FROM dbo.ImportFault WHERE id=2")) == (localSkip ? 1 : 0), "Independent connection proves next GO sentinel follows safe stop/skip.");
                }
                string log = File.ReadAllText(result.LogPath);
                Assert(log.Contains("first runtime error") && log.Contains("second runtime error"), "Every real provider error from failed batch reaches the log.");
                Assert(log.IndexOf("ErrorUtc=", StringComparison.Ordinal) < log.IndexOf("EndUtc=", StringComparison.Ordinal), "Error details precede terminal log record.");
                ImportFaultClean(result, true);
            });
        }
        f.Case("faults.sqlerror-all-details", () => {
            f.ResetTarget();
            string path = f.WriteScript("all-errors.sql", "CREATE TABLE dbo.ImportFault(id int PRIMARY KEY);\nGO\nRAISERROR(N'fixture detail alpha',16,17);\nRAISERROR(N'fixture detail beta',16,18);\nRAISERROR(N'fixture detail gamma',16,19);\nGO\nINSERT dbo.ImportFault VALUES(2);\nGO\n");
            var capture = new ImportFaultObservedLog(f);
            var result = ImportDraftRunActual(f.Server, f.Target, f.Databases, path, f.Root, true, logFactory: job => capture.Open(job, f.Root));
            ImportRecordResult(f, "all-errors", path, result);
            Assert(result.Status == MssqlImportStatus.CompletedWithErrors && capture.Details.Count == 3, "Three actual SqlErrors survive provider aggregation.");
            string log = File.ReadAllText(result.LogPath);
            foreach (var detail in capture.Details) {
                Assert(detail.Number == 50000 && detail.Class == 16 && detail.RelativeLine > 0, "Real provider number/class/line retained.");
                Assert(log.Contains("Number=" + detail.Number + " State=" + detail.State + " Class=" + detail.Class + " RelativeLine=" + detail.RelativeLine) && log.Contains(detail.Message), "Full log contains every delivered typed provider detail.");
            }
            Assert(capture.Details.Select(x => x.State).SequenceEqual(new[] { 17, 18, 19 }), "Distinct provider states preserve order.");
            Assert(capture.ErrorDurableBeforeNext, "Error append and durable flush completed while independent next-GO row count was still zero.");
            ImportFaultClean(result, true);
        });
        foreach (bool abort in new[] { false, true }) {
            bool localAbort = abort;
            f.Case(abort ? "faults.xact-abort" : "faults.transaction-error", () => {
                f.ResetTarget();
                string path = f.WriteScript("transaction-" + localAbort + ".sql", "CREATE TABLE dbo.ImportFault(id int PRIMARY KEY);\nGO\nSET XACT_ABORT " + (localAbort ? "ON" : "OFF") + "; BEGIN TRAN; INSERT dbo.ImportFault VALUES(1);\nGO\nBEGIN TRY\nINSERT dbo.ImportFault VALUES(1);\nEND TRY\nBEGIN CATCH\nDECLARE @actual int=XACT_STATE(); RAISERROR(N'actual caught XACT_STATE=%d',16,7,@actual);\nEND CATCH;\nGO\nINSERT dbo.ImportFault VALUES(2);COMMIT;\nGO\n");
                var result = ImportDraftRunActual(f.Server, f.Target, f.Databases, path, f.Root, true);
                ImportRecordResult(f, "transaction-" + localAbort, path, result);
                Assert(result.Status == MssqlImportStatus.Failed && result.LastAcknowledgedBatch == 2 && result.FailedBatches == 1 && result.SkippedBytes == 0, "Explicit transaction failure cannot be safely skipped, even with skip enabled.");
                Assert(File.ReadAllText(result.LogPath).Contains("actual caught XACT_STATE=" + (localAbort ? "-1" : "1")), "Server TRY/CATCH records actual doomed/committable transaction state before batch unwind.");
                using (var c = f.Open(f.Target)) Assert(Convert.ToInt64(ImportLiveFixture.Scalar(c, "SELECT COUNT_BIG(*) FROM dbo.ImportFault")) == 0, "Owned cleanup rollback removes transaction row and prevents later GO.");
                ImportFaultClean(result, true);
            });
        }
        f.Case("faults.unclosed-transaction-eof", () => {
            f.ResetTarget();
            string path = f.WriteScript("transaction-eof.sql", "CREATE TABLE dbo.ImportFault(id int);\nGO\nBEGIN TRAN;INSERT dbo.ImportFault VALUES(1);\nGO\n");
            var result = ImportDraftRunActual(f.Server, f.Target, f.Databases, path, f.Root, true);
            ImportRecordResult(f, "transaction-eof", path, result);
            Assert(result.Status == MssqlImportStatus.Failed && result.ValidationSummary == "EofTransactionOpen", "Actual EOF gate refuses success with a live transaction.");
            using (var c = f.Open(f.Target)) Assert(Convert.ToInt64(ImportLiveFixture.Scalar(c, "SELECT COUNT_BIG(*) FROM dbo.ImportFault")) == 0, "EOF cleanup rolled back uncommitted target row.");
            ImportFaultClean(result, true);
        });
        f.Case("faults.finite-timeout", () => {
            f.ResetTarget();
            string path = f.WriteScript("timeout.sql", ImportDraftTimeout);
            using (var point = new ImportLiveCheckpoint("Timeout") { FiniteTimeoutSeconds = 1 }) {
                var result = ImportDraftRunActual(f.Server, f.Target, f.Databases, path, f.Root, true, point: point);
                ImportRecordResult(f, "timeout", path, result);
                Assert(point.Issued.IsSet && result.Status == MssqlImportStatus.Failed && result.UncertainBatch == 2 && result.SkippedBytes == 0, "Finite actual SqlCommand timeout remains uncertain and cannot be skipped.");
                Assert(File.ReadAllText(result.LogPath).Contains("Number=-2 "), "Actual provider timeout -2 is logged.");
                using (var c = f.Open(f.Target)) Assert(Convert.ToInt64(ImportLiveFixture.Scalar(c, "SELECT COUNT_BIG(*) FROM dbo.ImportTimeout")) == 0, "Timeout stops before source/next GO sentinel inserts.");
                ImportFaultClean(result, true);
            }
        });
        f.Case("faults.owned-transport-loss", () => {
            f.ResetTarget();
            string path = f.WriteScript("owned-transport.sql", ImportDraftTimeout);
            using (var point = new ImportLiveCheckpoint("Timeout")) using (var cancellation = new CancellationTokenSource()) {
                var context = new ImportFaultRunContext(f, point);
                var service = context.Service();
                var request = new MssqlImportRequest(Guid.NewGuid(), path, f.Target, true);
                using (var preflight = service.Preflight(request, null, CancellationToken.None)) {
                    Assert(preflight.Passed, "Transport case passes real preflight.");
                    var worker = Task.Run(() => service.Run(request, preflight, null, cancellation.Token));
                    try { Assert(point.Issued.Wait(8000), "Actual WAITFOR SqlCommand starts before exact owner is closed."); context.WorkerSession.Dispose(); }
                    finally { if (!worker.Wait(15000)) { cancellation.Cancel(); Assert(worker.Wait(15000), "Owned transport worker must unwind after close/cancel."); } }
                    var result = worker.GetAwaiter().GetResult(); ImportRecordResult(f, "transport", path, result);
                    Assert(result.Status == MssqlImportStatus.Failed && result.UncertainBatch == 2 && result.LastAcknowledgedBatch == 1 && result.SkippedBytes == 0, "Actual owned connection loss never becomes safe skip/success.");
                    Assert(result.Cleanup.CommandEnded && result.Cleanup.SessionReleased && !result.Cleanup.TransactionCleanupConfirmed, "Closed session proves release, while rollback acknowledgement remains unavailable.");
                    using (var c = f.Open(f.Target)) Assert(Convert.ToInt64(ImportLiveFixture.Scalar(c, "SELECT COUNT_BIG(*) FROM dbo.ImportTimeout")) == 0, "Independent connection sees no transport or later sentinel row.");
                }
            }
        });
        foreach (string mode in new[] { "create", "start", "append", "flush", "end" }) {
            string localMode = mode;
            f.Case("faults.log-" + (mode == "append" ? "error-append" : mode), () => ImportFaultLogCase(f, localMode));
        }
        f.Case("faults.cancel-result-read", () => {
            f.ResetTarget();
            string path = f.WriteScript("cancel-read.sql", ImportDraftDrainingResults);
            ImportFaultCancel(f, "result-read", path, "Drain", false, 4096);
            f.ResetTarget();
            string lob = f.WriteScript("cancel-returned-lob.sql", "/*IMPORT_INTEGRATION_DRAIN*/ SELECT CONVERT(varbinary(max),REPLICATE(CONVERT(varchar(max),'z'),67108864));SELECT 2;\nGO\nCREATE TABLE dbo.NeverAfterCancel(id int);\nGO\n");
            ImportFaultCancel(f, "returned-lob", lob, "Drain", false, 2);
        });
        f.Case("faults.cancel-next-result", () => {
            f.ResetTarget();
            string path = f.WriteScript("cancel-next.sql", "/*IMPORT_INTEGRATION_DRAIN*/ SELECT 1; SELECT 2; SELECT 3;\nGO\nCREATE TABLE dbo.NeverAfterCancel(id int);\nGO\n");
            ImportFaultCancel(f, "next-result", path, "Drain", true, 1);
        });
        f.Case("faults.cancel-state-probe", () => {
            f.ResetTarget();
            string path = f.WriteScript("cancel-state.sql", "CREATE TABLE dbo.NeverAfterCancel(id int);\nGO\n");
            ImportFaultCancel(f, "state", path, "StateProbe", false, 1);
        });
        using (var source = f.Open(f.Source)) ImportLiveFixture.Exec(source, "CREATE TABLE dbo.ImportFaultCount(id int PRIMARY KEY);INSERT dbo.ImportFaultCount VALUES(1);CREATE TABLE dbo.ImportFaultLob(id int PRIMARY KEY,b varbinary(max));INSERT dbo.ImportFaultLob VALUES(1,CONVERT(varbinary(max),REPLICATE(CONVERT(varchar(max),'x'),300000)));");
        var nativeCount = ImportDraftExportNative(f.Server, f.Source, f.Databases, f.Root, "fault-native-count.sql", MssqlExportMode.SchemaAndData, t => t.Key.Name == "ImportFaultCount", false);
        var nativeLob = ImportDraftExportNative(f.Server, f.Source, f.Databases, f.Root, "fault-native-lob.sql", MssqlExportMode.SchemaAndData, t => t.Key.Name == "ImportFaultLob", false);
        f.Case("faults.cancel-lob-probe", () => { f.ResetTarget(); ImportFaultCancel(f, "lob-probe", nativeLob.TargetPath, "LobProbe", false, 1); });
        f.Case("faults.cancel-count-big", () => {
            f.ResetTarget();
            SqlConnection blocker = null;
            using (var point = new ImportLiveCheckpoint("Count")) using (var cancellation = new CancellationTokenSource()) {
                int blocked = 0;
                var context = new ImportFaultRunContext(f, point);
                var service = context.Service(); var request = new MssqlImportRequest(Guid.NewGuid(), nativeCount.TargetPath, f.Target);
                using (var preflight = service.Preflight(request, null, CancellationToken.None)) {
                    Assert(preflight.Passed, "Count cancellation uses actual full native import preflight.");
                    Exception blockerFailure = null;
                    Action<MssqlImportProgress> progress = p => {
                        context.Observe(p);
                        if (p.Stage == MssqlImportStage.Verifying && Interlocked.Exchange(ref blocked, 1) == 0) {
                            try { blocker = f.Open(f.Target); ImportLiveFixture.Exec(blocker, "BEGIN TRAN;SELECT TOP(1) id FROM dbo.ImportFaultCount WITH(TABLOCKX,HOLDLOCK);"); }
                            catch (Exception error) { blockerFailure = error; }
                        }
                    };
                    var worker = Task.Run(() => service.Run(request, preflight, progress, cancellation.Token));
                    try {
                        Assert(point.Issued.Wait(10000), "Actual COUNT_BIG provider method issued while independent owned TABLOCKX transaction holds target.");
                        Assert(blockerFailure == null && blocker != null && !point.ReaderReady.IsSet, "Count is genuinely blocked at SQL provider rather than a fake reader gate.");
                        cancellation.Cancel(); Assert(worker.Wait(15000), "Production 10s cancellation watchdog bounds blocked COUNT_BIG unwind.");
                    } finally {
                        cancellation.Cancel();
                        if (blocker != null) { try { if (blocker.State == ConnectionState.Open) ImportLiveFixture.Exec(blocker, "IF @@TRANCOUNT>0 ROLLBACK;"); } finally { blocker.Dispose(); } }
                        Assert(worker.Wait(15000), "Count worker ended before fixture cleanup.");
                    }
                    var result = worker.GetAwaiter().GetResult(); ImportRecordResult(f, "count-cancel", nativeCount.TargetPath, result);
                    ImportFaultCancelled(result, context, point, true);
                    Assert(result.UncertainBatch == null, "Verification cancellation cannot invent an executing source batch.");
                }
            }
        });
        f.Case("faults.cancel-close-race", () => {
            f.ResetTarget();
            string path = f.WriteScript("cancel-close.sql", "/*IMPORT_INTEGRATION_DRAIN*/ SELECT 1;SELECT 2;\nGO\nCREATE TABLE dbo.NeverAfterCancel(id int);\nGO\n");
            using (var point = new ImportLiveCheckpoint("Drain") { HoldReaderDispose = true }) using (var cancellation = new CancellationTokenSource()) {
                point.DisposeRelease.Reset(); point.OnRead = () => cancellation.Cancel();
                var context = new ImportFaultRunContext(f, point); var service = context.Service(); var request = new MssqlImportRequest(Guid.NewGuid(), path, f.Target);
                using (var preflight = service.Preflight(request, null, CancellationToken.None)) {
                    Assert(preflight.Passed, "Close race passes actual preflight.");
                    var worker = Task.Run(() => service.Run(request, preflight, context.Observe, cancellation.Token));
                    try {
                        Assert(point.DisposeEntered.Wait(8000), "Actual reader Dispose is held after real provider reader started and cancellation requested.");
                        Assert(context.Stopping != null, "Cancellation publishes Stopping before close.");
                        var pending = context.WorkerSession.Cleanup(); Assert(!pending.CommandEnded && !pending.TransactionCleanupConfirmed, "Held reader cannot claim command completion or rollback.");
                        context.WorkerSession.Dispose();
                        pending = context.WorkerSession.Cleanup(); Assert(!pending.CommandEnded && pending.SessionReleased && !pending.TransactionCleanupConfirmed, "Exact owner close proves release only while actual reader remains held.");
                    } finally { point.DisposeRelease.Set(); cancellation.Cancel(); Assert(worker.Wait(15000), "Reader and worker unwind before final race receipt."); }
                    var result = worker.GetAwaiter().GetResult(); ImportRecordResult(f, "cancel-close", path, result);
                    ImportFaultCancelled(result, context, point, false);
                    Assert(result.Status == MssqlImportStatus.CleanupUncertain && !result.Cleanup.TransactionCleanupConfirmed, "Close race preserves missing rollback acknowledgement in final status.");
                }
            }
        });
        f.Case("faults.cancel-success-race", () => {
            f.ResetTarget();
            string path = f.WriteScript("success-race.sql", "CREATE TABLE dbo.ImportFault(id int);INSERT dbo.ImportFault VALUES(1);\nGO\n");
            using (var cancellation = new CancellationTokenSource()) {
                int finishSeen = 0, stoppingSeen = 0;
                var result = ImportDraftRunActual(f.Server, f.Target, f.Databases, path, f.Root, cancellation: cancellation.Token, progress: p => {
                    if (p.Stage == MssqlImportStage.Stopping) Interlocked.Increment(ref stoppingSeen);
                    if (p.Stage == MssqlImportStage.Finished) { Interlocked.Increment(ref finishSeen); cancellation.Cancel(); }
                });
                ImportRecordResult(f, "success-race", path, result);
                Assert(finishSeen == 1 && stoppingSeen == 0 && cancellation.IsCancellationRequested && result.Status == MssqlImportStatus.Completed, "Cancellation arriving after terminal publication cannot rewrite completed truth or publish later Stopping.");
                ImportFaultClean(result, true);
                using (var c = f.Open(f.Target)) Assert(Convert.ToInt64(ImportLiveFixture.Scalar(c, "SELECT COUNT_BIG(*) FROM dbo.ImportFault")) == 1, "Independent committed success receipt.");
            }
        });
        f.Case("faults.metadata-low-privilege-boundary", () => ImportFaultPermissions(f, nativeCount.TargetPath));
        f.Case("faults.target-unavailable-boundary", () => {
            f.ResetTarget();
            string path = f.WriteScript("unavailable.sql", "CREATE TABLE dbo.NeverAfterCancel(id int);\nGO\n");
            int targetOpens=0;
            Action<string> masterState=f.RecordTargetProperties;
            var service=new MssqlImportService(()=>{
                int ordinal=++targetOpens;var owned=ImportDraftOpenOwned(f.Server,f.Target,f.Databases);
                try {
                if(ordinal<=3)masterState("factory-open-"+ordinal);
                return new ImportObservedSession(new MssqlImportSqlSession(owned),Path.Combine(f.Root,"target-trace-"+ordinal+".csv"),target=>{
                    if(ordinal<=3)File.WriteAllText(Path.Combine(f.Root,"target-sut-"+ordinal+".json"),JsonConvert.SerializeObject(new{ordinal,target.ServerName,target.DatabaseName,target.DatabaseId,target.DatabaseCreateDate,target.Writable},Formatting.Indented));
                });
                }catch{owned.Dispose();throw;}
            },job=>new MssqlImportLog(job,Path.Combine(f.Root,"logs")));
            var request = new MssqlImportRequest(Guid.NewGuid(), path, f.Target);
            try {
                f.SetTargetReadOnly(true);
                using(var denied=service.Preflight(request,null,CancellationToken.None)) {
                    Assert(!denied.Passed&&denied.Target!=null&&!denied.Target.Writable&&denied.Issues.Any(x=>x.Code=="TargetReadOnly"&&!x.IsWarning),"Actual readonly target rejects preflight without source SQL.");
                    using(var c=f.Open(f.Target))Assert(ImportLiveFixture.Scalar(c,"SELECT OBJECT_ID(N'dbo.NeverAfterCancel')")==DBNull.Value,"Readonly preflight makes zero source writes.");
                    File.WriteAllText(Path.Combine(f.Root,"target-readonly-preflight.json"),JsonConvert.SerializeObject(new{passed=denied.Passed,writable=denied.Target.Writable,issueCodes=denied.Issues.Select(x=>x.Code).ToArray(),sourceWrites=0},Formatting.Indented));
                }
            }finally{f.SetTargetReadOnly(false);masterState("restored-"+targetOpens);}
            using(var checkedTarget=service.Preflight(request,null,CancellationToken.None)) {
                Assert(checkedTarget.Passed&&checkedTarget.Target.Writable,"Read/write target passes before readonly transition.");
                try {
                    f.SetTargetReadOnly(true);
                    using(var readonlyProbe=f.Open(f.Target)) {
                        Assert(Convert.ToString(ImportLiveFixture.Scalar(readonlyProbe,"SELECT DATABASEPROPERTYEX(DB_NAME(),'Updateability')"))=="READ_ONLY","Fixture actual updateability transition completed.");
                        masterState("probe-open");
                        masterState("before-run");
                        var rejected=service.Run(request,checkedTarget,null,CancellationToken.None);ImportRecordResult(f,"target-readonly-after-preflight",path,rejected);
                        masterState("after-run");
                        using(var c=f.Open(f.Target)) {
                            bool sourceExists=ImportLiveFixture.Scalar(c,"SELECT OBJECT_ID(N'dbo.NeverAfterCancel')")!=DBNull.Value;
                            bool directCreated=false;var directErrors=new List<int>();
                            try{ImportLiveFixture.Exec(c,"CREATE TABLE dbo.NeverDirectReadonly(id int);");directCreated=true;}
                            catch(SqlException error){foreach(SqlError detail in error.Errors)directErrors.Add(detail.Number);}
                            File.WriteAllText(Path.Combine(f.Root,"target-readonly-effects.json"),JsonConvert.SerializeObject(new{sourceExists,directCreated,directErrors},Formatting.Indented));
                            Assert(!directCreated&&directErrors.Contains(3906),"Fresh independent SqlConnection rejects equivalent CREATE in actual readonly target.");
                        }
                        Assert(rejected.Status==MssqlImportStatus.Failed&&rejected.ValidationSummary=="TargetReadOnly"&&rejected.ProcessedBytes==0&&rejected.SuccessfulBatches==0&&rejected.FailedBatches==0,"Actual run rechecks readonly target before source execution.");
                        ImportFaultClean(rejected,true);
                        using(var c=f.Open(f.Target))Assert(ImportLiveFixture.Scalar(c,"SELECT OBJECT_ID(N'dbo.NeverAfterCancel')")==DBNull.Value,"Readonly execution gate makes zero source writes.");
                    }
                }finally{try{masterState("probe-closed");}finally{f.SetTargetReadOnly(false);masterState("restored-"+targetOpens);}}
            }
            using(var c=f.Open(f.Target))Assert(Convert.ToString(ImportLiveFixture.Scalar(c,"SELECT DATABASEPROPERTYEX(DB_NAME(),'Updateability')"))=="READ_WRITE","Finally restores actual read/write before recreation/cleanup.");
            using (var preflight = service.Preflight(request, null, CancellationToken.None)) {
                Assert(preflight.Passed, "Unavailable case initially passes actual preflight.");
                // Guarded drop/recreate changes the exact owned target lifetime; no user DB/instance is touched.
                f.ResetTarget();
                var result = service.Run(request, preflight, null, CancellationToken.None); ImportRecordResult(f, "target-recreated", path, result);
                Assert(result.Status == MssqlImportStatus.Failed && result.ValidationSummary == "TargetChanged" && result.SuccessfulBatches == 0, "Recreated target is rejected before any source SQL executes.");
                ImportFaultClean(result, true);
                using (var c = f.Open(f.Target)) Assert(ImportLiveFixture.Scalar(c, "SELECT OBJECT_ID(N'dbo.NeverAfterCancel')") == DBNull.Value, "New target has no source write.");
            }
        });
    }

    private static void ImportFaultClean(MssqlImportResult result, bool transactionConfirmed)
    {
        Assert(result.Cleanup != null && result.Cleanup.CommandEnded && result.Cleanup.SessionReleased && result.Cleanup.TransactionCleanupConfirmed == transactionConfirmed, "Separate actual command/session/transaction receipts match asserted boundary.");
    }
    private static void ImportFaultCancelled(MssqlImportResult result, ImportFaultRunContext context, ImportLiveCheckpoint point, bool transactionConfirmed)
    {
        Assert(point.Issued.IsSet && point.CancelCalls > 0, "Cancellation reached the exact actual provider command.");
        Assert(context.Stopping != null && context.Finished != null && context.Finished.Stage == MssqlImportStage.Finished, "Stopping and terminal progress are observed independently of command cancellation.");
        Assert(result.Status == (transactionConfirmed ? MssqlImportStatus.Cancelled : MssqlImportStatus.CleanupUncertain), "Final cancellation status follows actual cleanup acknowledgement.");
        ImportFaultClean(result, transactionConfirmed);
    }
    private static void ImportFaultCancel(ImportLiveFixture f, string key, string path, string phase, bool next, int readAt)
    {
        using (var point = new ImportLiveCheckpoint(phase) { ReadCheckpointAt = readAt }) using (var cancellation = new CancellationTokenSource()) {
            var context = new ImportFaultRunContext(f, point);
            if (next) point.OnNext = () => cancellation.Cancel(); else point.OnRead = () => cancellation.Cancel();
            var service = context.Service(); var request = new MssqlImportRequest(Guid.NewGuid(), path, f.Target);
            using (var preflight = service.Preflight(request, null, CancellationToken.None)) {
                Assert(preflight.Passed, "Cancellation fixture passes real preflight: " + key);
                var elapsed = Stopwatch.StartNew();
                var worker = Task.Run(() => service.Run(request, preflight, context.Observe, cancellation.Token));
                try { Assert(worker.Wait(20000), "Real reader/probe cancellation finishes within provider+production watchdog budget: " + key); }
                finally { cancellation.Cancel(); Assert(worker.Wait(15000), "Cancelled worker is joined before fixture advances: " + key); }
                var result = worker.GetAwaiter().GetResult(); ImportRecordResult(f, "cancel-" + key, path, result);
                ImportFaultCancelled(result, context, point, true);
                Assert(point.ReaderReady.IsSet && (next ? point.NextCalls > 0 : point.ReadCalls >= readAt), "Cancellation occurs at actual selected reader callback.");
                Assert(elapsed.ElapsedMilliseconds < 20000, "Cancellation callback and cleanup remain bounded.");
                using (var c = f.Open(f.Target)) Assert(ImportLiveFixture.Scalar(c, "SELECT OBJECT_ID(N'dbo.NeverAfterCancel')") == DBNull.Value, "Later GO sentinel absent after selected cancellation boundary.");
                File.WriteAllText(Path.Combine(f.Root, "cancel-" + key + "-boundary.json"), JsonConvert.SerializeObject(new { phase, point.CancelCalls, point.ReadCalls, point.NextCalls, elapsedMs = elapsed.ElapsedMilliseconds, result.LastAcknowledgedBatch, result.UncertainBatch, result.Status, context.Stopping, context.Finished }, Formatting.Indented));
            }
        }
    }
    private sealed class ImportFaultRunContext
    {
        private readonly ImportLiveFixture fixture; private readonly ImportLiveCheckpoint point; private int opens;
        internal MssqlImportSqlSession WorkerSession; internal MssqlImportProgress Stopping, Finished;
        internal ImportFaultRunContext(ImportLiveFixture f, ImportLiveCheckpoint checkpoint) { fixture = f; point = checkpoint; }
        internal void Observe(MssqlImportProgress p) { if (p.Stage == MssqlImportStage.Stopping) Stopping = p; if (p.Stage == MssqlImportStage.Finished) Finished = p; }
        internal MssqlImportService Service() { return new MssqlImportService(() => {
            var owned = ImportDraftOpenOwned(fixture.Server, fixture.Target, fixture.Databases); var connection = owned.Connection;
            int ordinal = Interlocked.Increment(ref opens);
            // Preflight is plain actual adapter. The selected checkpoint belongs only to worker commands.
            if (ordinal == 1) return new MssqlImportSqlSession(owned);
            WorkerSession = new MssqlImportSqlSession(owned, () => new ImportLiveCommand(connection.CreateCommand(), point), () => connection.State == ConnectionState.Open);
            return WorkerSession;
        }, job => new MssqlImportLog(job, Path.Combine(fixture.Root, "logs"))); }
    }
    private sealed class ImportFaultObservedLog : IMssqlImportLog
    {
        private MssqlImportLog inner; private readonly ImportLiveFixture fixture; internal readonly List<MssqlImportSqlError> Details = new List<MssqlImportSqlError>();
        internal bool ErrorDurableBeforeNext;
        internal ImportFaultObservedLog(ImportLiveFixture f) { fixture = f; }
        internal IMssqlImportLog Open(Guid job, string root) { inner = new MssqlImportLog(job, System.IO.Path.Combine(root, "logs")); return this; }
        public string Path { get { return inner.Path; } } public MssqlImportLogTail Tail { get { return inner.Tail; } }
        public void WriteStart(MssqlImportRequest r, MssqlImportScan s, MssqlImportTarget t) { inner.WriteStart(r, s, t); }
        public void WriteError(MssqlImportBatch b, MssqlImportBatchException e, string a) { Details.AddRange(e.Errors); inner.WriteError(b, e, a); }
        public void Flush() { inner.Flush(); if (Details.Count > 0) { string log; using(var file=new FileStream(inner.Path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite))using(var reader=new StreamReader(file))log=reader.ReadToEnd(); using (var c = fixture.Open(fixture.Target)) ErrorDurableBeforeNext = Details.All(x => log.Contains(x.Message)) && Convert.ToInt64(ImportLiveFixture.Scalar(c, "SELECT COUNT_BIG(*) FROM dbo.ImportFault WHERE id=2;")) == 0; } }
        public void WriteEnd(MssqlImportResult r) { inner.WriteEnd(r); } public void Dispose() { inner.Dispose(); }
    }
    private static void ImportFaultLogCase(ImportLiveFixture f, string mode)
    {
        f.ResetTarget();
        string errors = "DECLARE @message nvarchar(1800)=REPLICATE(N'x',1750);RAISERROR(@message,16,1);RAISERROR(@message,16,2);RAISERROR(@message,16,3);";
        string path = f.WriteScript("log-" + mode + ".sql", "CREATE TABLE dbo.ImportFault(id int);\nGO\nINSERT dbo.ImportFault VALUES(1);" + errors + "\nGO\nINSERT dbo.ImportFault VALUES(2);\nGO\n");
        int sessions = 0; ImportDraftLogFailure logFault = null;
        Func<Guid, IMssqlImportLog> logs;
        if (mode == "create") {
            string occupied = Path.Combine(f.Root, "log-directory-is-file"); File.WriteAllText(occupied, "owned fixture file", new UTF8Encoding(false));
            logs = job => new MssqlImportLog(job, occupied); // Real Directory.CreateDirectory IO failure.
        } else logs = job => logFault = new ImportDraftLogFailure(job, Path.Combine(f.Root, "logs"), mode);
        var service = new MssqlImportService(() => { sessions++; return new MssqlImportSqlSession(ImportDraftOpenOwned(f.Server, f.Target, f.Databases)); }, logs);
        var request = new MssqlImportRequest(Guid.NewGuid(), path, f.Target, true);
        using (var preflight = service.Preflight(request, null, CancellationToken.None)) {
            if (mode == "create" || mode == "start") {
                Assert(!preflight.Passed && sessions == 1 && preflight.Issues.Any(x => !x.IsWarning), "Actual filesystem header/create failure prevents execution ownership claim.");
                using (var c = f.Open(f.Target)) Assert(ImportLiveFixture.Scalar(c, "SELECT OBJECT_ID(N'dbo.ImportFault')") == DBNull.Value, "Logging startup failure executes no source SQL.");
                // Rejecting Run with this preflight must not open a worker connection.
                var rejected = service.Run(request, preflight, null, CancellationToken.None);
                Assert(rejected.Status == MssqlImportStatus.Failed && rejected.ValidationSummary == "PreflightInvalidOrConsumed", "Failed real preflight cannot be claimed for execution.");
                Assert(sessions == 1, "Failed log preflight did not open a second SQL session.");
            } else {
                Assert(preflight.Passed, "Log failure is armed at worker error/end phase only.");
                var result = service.Run(request, preflight, null, CancellationToken.None); ImportRecordResult(f, "log-" + mode, path, result);
                Assert(result.Status == MssqlImportStatus.Failed && result.ValidationSummary.Contains("LogIncomplete"), "Actual log stream failure is visible in final result.");
                using (var c = f.Open(f.Target)) Assert(Convert.ToInt64(ImportLiveFixture.Scalar(c, "SELECT COUNT_BIG(*) FROM dbo.ImportFault WHERE id=2")) == (mode == "end" ? 1 : 0), "Error write/flush failure stops before next GO; terminal failure reports writes already acknowledged.");
                Assert(result.LastAcknowledgedBatch == (mode == "end" ? 3 : 1), "Logging failure cannot advance an unacknowledged SQL batch.");
                ImportFaultClean(result, true);
            }
        }
    }
    private static void ImportFaultPermissions(ImportLiveFixture f, string nativeScript)
    {
        foreach (string permissionMode in new[] { "no-view-definition", "user-deny", "role-deny", "public-deny" }) {
            f.ResetTarget();
            const string user = "ImportFixtureLimited", role = "ImportFixtureLimitedRole";
            using (var c = f.Open(f.Target)) {
                ImportLiveFixture.Exec(c, "CREATE TABLE dbo.HiddenFixture(id int);INSERT dbo.HiddenFixture VALUES(1);CREATE USER " + ImportLiveFixture.Q(user) + " WITHOUT LOGIN;CREATE ROLE " + ImportLiveFixture.Q(role) + ";ALTER ROLE " + ImportLiveFixture.Q(role) + " ADD MEMBER " + ImportLiveFixture.Q(user) + ";EXEC(N'CREATE SCHEMA import_owned AUTHORIZATION " + ImportLiveFixture.Q(user) + ";');GRANT CREATE TABLE TO " + ImportLiveFixture.Q(user) + ";");
                if (permissionMode != "no-view-definition") {
                    ImportLiveFixture.Exec(c, "GRANT VIEW DEFINITION TO " + ImportLiveFixture.Q(user) + ";GRANT VIEW SECURITY DEFINITION TO " + ImportLiveFixture.Q(user) + ";DENY VIEW DEFINITION ON OBJECT::dbo.HiddenFixture TO " + ImportLiveFixture.Q(permissionMode == "user-deny" ? user : permissionMode == "role-deny" ? role : "public") + ";");
                }
            }
            Func<IMssqlImportSession> sessions = () => {
                var owned = ImportDraftOpenOwned(f.Server, f.Target, f.Databases);
                try { ImportLiveFixture.Exec(owned.Connection, "EXECUTE AS USER=" + ImportLiveFixture.L(user) + ";"); return new MssqlImportSqlSession(new ImportFaultRevertOwner(owned), () => owned.Connection.CreateCommand(), () => owned.Connection.State == ConnectionState.Open); }
                catch { owned.Dispose(); throw; }
            };
            // Direct independent impersonated view proves catalog filtering before adapter decision.
            using (var c = f.Open(f.Target)) try {
                ImportLiveFixture.Exec(c, "EXECUTE AS USER=" + ImportLiveFixture.L(user) + ";");
                Assert(Convert.ToInt64(ImportLiveFixture.Scalar(c, "SELECT COUNT_BIG(*) FROM sys.objects WHERE is_ms_shipped=0;")) == 0, "Real effective token filters existing hidden object to zero.");
                Assert(Convert.ToInt32(ImportLiveFixture.Scalar(c, "SELECT HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','ALTER');")) == 0, "Fixture user has no ALTER DATABASE permission.");
            } finally { if (c.State == ConnectionState.Open) ImportLiveFixture.Exec(c, "REVERT;"); }
            using (var session = sessions()) {
                var target = session.ReadTarget(CancellationToken.None);
                Assert(!target.MetadataVisible && !target.UserObjectCount.HasValue, "Filtered zero metadata is unknown for complete effective user/role/public token.");
            }
            var service = new MssqlImportService(sessions, job => new MssqlImportLog(job, Path.Combine(f.Root, "logs")));
            var nativeRequest = new MssqlImportRequest(Guid.NewGuid(), nativeScript, f.Target);
            using (var preflight = service.Preflight(nativeRequest, null, CancellationToken.None)) Assert(!preflight.Passed && preflight.Issues.Any(x => x.Code == "TargetVisibilityRequired"), "Native full import rejects incomplete metadata without claiming empty target.");
            string generic = f.WriteScript("limited-" + permissionMode + ".sql", "CREATE TABLE import_owned.VisibleFixture(id int);INSERT import_owned.VisibleFixture VALUES(7);\nGO\n");
            var request = new MssqlImportRequest(Guid.NewGuid(), generic, f.Target);
            using (var preflight = service.Preflight(request, null, CancellationToken.None)) {
                Assert(preflight.Passed && preflight.Issues.Any(x => x.Code == "TargetNotProvenEmpty"), "Generic import under same limited token is allowed with explicit visibility warning.");
                var result = service.Run(request, preflight, null, CancellationToken.None); ImportRecordResult(f, "limited-" + permissionMode, generic, result);
                Assert(result.Status == MssqlImportStatus.Completed, "Limited schema owner imports without ALTER DATABASE or broader permissions."); ImportFaultClean(result, true);
                using (var c = f.Open(f.Target)) Assert(Convert.ToInt64(ImportLiveFixture.Scalar(c, "SELECT COUNT_BIG(*) FROM import_owned.VisibleFixture WHERE id=7;")) == 1, "Independent admin reads actual limited-caller committed data.");
            }
        }
        f.ResetTarget();
    }
    private sealed class ImportFaultRevertOwner : IDisposable
    {
        private MssqlOwnedSession owned;
        internal ImportFaultRevertOwner(MssqlOwnedSession session) { owned = session; }
        public void Dispose() {
            var value = Interlocked.Exchange(ref owned, null); if (value == null) return;
            try { if (value.Connection.State == ConnectionState.Open) ImportLiveFixture.Exec(value.Connection, "REVERT;"); }
            finally { value.Dispose(); }
        }
    }
    private static void ImportLiveMemory(ImportLiveFixture f)
    {
        f.Case("memory.drain-million-rows",()=>{
            string path=f.WriteScript("million-results.sql","/*IMPORT_INTEGRATION_DRAIN*/ WITH n AS(SELECT TOP(2500000) ROW_NUMBER() OVER(ORDER BY(SELECT NULL)) n FROM sys.all_objects a CROSS JOIN sys.all_objects b) SELECT n FROM n;\r\nGO\r\n");
            using(var point=new ImportLiveCheckpoint("Drain"))using(var memory=new ImportDraftMemory()){
                var result=ImportDraftRunActual(f.Server,f.Target,f.Databases,path,f.Root,point:point,progress:p=>memory.Stage=p.Stage.ToString());ImportRecordResult(f,"million",path,result);
                File.WriteAllText(Path.Combine(f.Root,"memory-million.json"),JsonConvert.SerializeObject(memory.Snapshot(),Formatting.Indented));
                Assert(result.Status==MssqlImportStatus.Completed&&point.ReadCalls==2500001&&point.ValueGetterCalls==0,"Actual SqlClient discarded2.5million rows with complete result drain.");
            }
        });
        f.Case("memory.drain-64m-returned-lob",()=>{
            string path=f.WriteScript("returned-lob.sql","/*IMPORT_INTEGRATION_DRAIN*/ SELECT CONVERT(varbinary(max),REPLICATE(CONVERT(varchar(max),'z'),67108864));SELECT 2;\r\nGO\r\n");
            using(var point=new ImportLiveCheckpoint("Drain"))using(var memory=new ImportDraftMemory()){
                var result=ImportDraftRunActual(f.Server,f.Target,f.Databases,path,f.Root,point:point,progress:p=>memory.Stage=p.Stage.ToString());ImportRecordResult(f,"returned-lob",path,result);
                var values=memory.Snapshot();File.WriteAllText(Path.Combine(f.Root,"memory-returned-lob.json"),JsonConvert.SerializeObject(values,Formatting.Indented));
                Assert(result.Status==MssqlImportStatus.Completed&&point.ReadCalls==4&&point.NextCalls==2&&point.ValueGetterCalls==0,"Actual64MiB returned LOB and next result drained without getters/materialization.");
                long peak=values.Values.Max(x=>x.PeakPrivateBytes),baseline=values.Values.Min(x=>x.FirstPrivateBytes);
                Assert(peak-baseline<256L*1024*1024,"Returned64MiB result drain private memory remains bounded.");
            }
        });
        f.Case("memory.warmup-x64",()=>{Assert(IntPtr.Size==8,"Actual integration process is x64.");ImportMemoryWarmup(f);});
        JObject small=null,large=null;
        f.Case("memory.stream-256m",()=>{small=ImportRunMemoryChild(f,1);});
        f.Case("memory.stream-2g5",()=>{large=ImportRunMemoryChild(f,10);});
        f.Case("memory.compare-peaks",()=>{
            Assert((long)large["fileBytes"]==(long)small["fileBytes"]*10&&(long)large["batches"]==(long)small["batches"]*10,"Actual executed file bytes and batch counts scale exactly10x.");
            long smallDelta=(long)small["peakPrivateBytes"]-(long)small["baselinePrivateBytes"],largeDelta=(long)large["peakPrivateBytes"]-(long)large["baselinePrivateBytes"];
            long difference=largeDelta-smallDelta;
            File.WriteAllText(Path.Combine(f.Root,"memory-comparison.json"),JsonConvert.SerializeObject(new{small,large,smallDelta,largeDelta,difference,thresholdBytes=256L*1024*1024},Formatting.Indented));
            Assert(difference<=256L*1024*1024,"10x actual import cannot add>256MiB private bytes; investigate instead of raising cap.");
            Assert((long)large["peakPrivateBytes"]-(long)small["peakPrivateBytes"]<=256L*1024*1024,"Raw private peak difference also remains bounded.");
        });
    }
    private static void ImportMemoryWarmup(ImportLiveFixture f)
    {
        string path=Path.Combine(f.Root,"memory-warmup.sql");
        using(var writer=new StreamWriter(path,false,new UTF8Encoding(false),65536)){
            writer.Write("SELECT CAST(1 AS int); /*");string block=new string('m',65536);for(int i=0;i<16;i++)writer.Write(block);writer.Write("*/\r\nGO\r\n");
        }
        var result=ImportDraftRunActual(f.Server,f.Target,f.Databases,path,f.Root);Assert(result.Status==MssqlImportStatus.Completed&&result.SuccessfulBatches==1,"Actual importer warmup uses identical bounded batch shape.");
    }
    private static void ImportLiveMemoryChild(ImportLiveFixture f,int scale)
    {
        Assert(scale==1||scale==10,"Exact memory child scale only.");Assert(IntPtr.Size==8,"Memory child is x64.");
        string path=Path.Combine(f.Root,"memory-input.sql");long bytes=ImportDraftGenerateMemorySql(path,scale);
        f.Case("memory.warmup-x64",()=>ImportMemoryWarmup(f));
        f.Case(scale==1?"memory.stream-256m":"memory.stream-2g5",()=>{
            long baseline;using(var process=Process.GetCurrentProcess()){process.Refresh();baseline=process.PrivateMemorySize64;}
            MssqlImportResult result;Dictionary<string,ImportDraftMemoryStage> values;
            using(var memory=new ImportDraftMemory()){
                result=ImportDraftRunActual(f.Server,f.Target,f.Databases,path,f.Root,progress:p=>memory.Stage=p.Stage.ToString());values=memory.Snapshot();
            }
            ImportRecordResult(f,"memory-stream",path,result);
            Assert(result.Status==MssqlImportStatus.Completed&&result.SuccessfulBatches==256L*scale&&result.ProcessedBytes==bytes,"Actual importer executes all memory batches and full digest/EOF boundary.");
            Assert(bytes>=256L*1024*1024*scale&&bytes<256L*1024*1024*scale+1024*1024,"Actual streamed input matches planned size with only bounded per-batch framing.");
            Assert(values.Values.Sum(x=>x.Samples)>3,"Memory sampler captured actual scan/execute samples.");
            File.WriteAllText(Path.Combine(f.Root,"memory-measurement.json"),JsonConvert.SerializeObject(new{scale,processBits=IntPtr.Size*8,fileBytes=bytes,batches=result.SuccessfulBatches,
                baselinePrivateBytes=baseline,peakPrivateBytes=values.Values.Max(x=>x.PeakPrivateBytes),peakWorkingSet=values.Values.Max(x=>x.PeakWorkingSet),stages=values,elapsed=result.Elapsed},Formatting.Indented));
        });
    }
    private static JObject ImportRunMemoryChild(ImportLiveFixture f,int scale)
    {
        string parent=Path.Combine(f.Root,"child-"+scale),identity=Guid.NewGuid().ToString("N");
        var start=new ProcessStartInfo(typeof(SmokeTests).Assembly.Location){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        start.EnvironmentVariables["MYSQLPUNK_MSSQL_IMPORT_MEMORY_SCALE"]=scale.ToString();start.EnvironmentVariables["MYSQLPUNK_MSSQL_IMPORT_OUTPUT"]=parent;start.EnvironmentVariables["MYSQLPUNK_MSSQL_IMPORT_RUN_ID"]=identity;
        start.EnvironmentVariables["MYSQLPUNK_SMOKE_FILTER"]="MSSQL import integration";
        int exit=RunOwnedRestore(start,3600000,receipt=>File.WriteAllText(Path.Combine(f.Root,"memory-child-"+scale+".json"),JsonConvert.SerializeObject(receipt,Formatting.Indented)));
        Assert(exit==0,"Owned actual-importer memory child exits successfully.");
        string root=Path.Combine(parent,"Memory");var receipt=JObject.Parse(File.ReadAllText(Path.Combine(root,"profile-receipt.json")));
        Assert((bool)receipt["complete"]&&(int)receipt["processBits"]==64&&(string)receipt["runId"]==identity&&(string)receipt["cleanupStatus"]=="Confirmed","Actual child identity,completion,x64 and dual ownership cleanup confirmed.");
        var cases=(JArray)receipt["cases"];Assert(cases.Count==2&&cases.All(x=>(string)x["status"]=="Passed"&&(bool)x["executed"]),"Actual child warmup and requested stream cases both executed/passed.");
        foreach(var item in cases){string file=Path.Combine(root,(string)item["caseId"]+".json");using(var sha=SHA256.Create())using(var stream=File.OpenRead(file))Assert(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant()==(string)item["receiptSha256"],"Child case receipt hashes match actual files.");}
        return JObject.Parse(File.ReadAllText(Path.Combine(root,"memory-measurement.json")));
    }}