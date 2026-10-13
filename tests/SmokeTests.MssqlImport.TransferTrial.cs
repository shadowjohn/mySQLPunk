// SQL2019 Transfer acceptance harness. Isolated fixture authority never applies here.
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using mySQLPunk.lib;
using mySQLPunk.lib.MssqlImport;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public static partial class SmokeTests
{
    private const string TransferInput = @"C:\Users\johnho\Desktop\Transfer_dump_20261013_165730.sql";
    private const string TransferAttempt = @"C:\Users\johnho\Documents\mySQLPunk-worktrees\mssql-import\.superpowers\sdd\implementation-plan\ImportTrials\JOHN_MSSQL2019-Transfer-Transfer_dump_20261013_165730.attempt.json";

    private const string TransferProductExecutable = @"C:\Users\johnho\Documents\mySQLPunk-worktrees\mssql-import\mySQLPunk\bin\CodexVerify\mySQLPunk.exe";
    private const long TransferEntryFree = 68719476736L, TransferCancelFree = 21474836480L;
    private const string TransferPin = @"C:\Users\johnho\Documents\mySQLPunk-worktrees\mssql-import\.superpowers\sdd\implementation-plan\task-7-build-pin.json";
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern bool GetVolumePathName(string fileName, StringBuilder volumePathName, uint length);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, StringBuilder volumeName, uint length);
    private static string TransferHash(string path)
    { using(var hash=SHA256.Create())using(var input=File.OpenRead(path))return BitConverter.ToString(hash.ComputeHash(input)).Replace("-","").ToLowerInvariant(); }
    private static JObject TransferReadBounded(string path)
    {
        TransferPathNoReparse(path);byte[] bytes;
        using(var input=File.OpenRead(path))
        {
            TransferRequire(input.Length>0&&input.Length<=1048576,"TrialJsonSize");bytes=new byte[(int)input.Length];int read=0;
            while(read<bytes.Length){int n=input.Read(bytes,read,bytes.Length-read);TransferRequire(n>0,"TrialJsonTruncated");read+=n;}
            TransferRequire(input.ReadByte()==-1,"TrialJsonChanged");
        }
        string text=new UTF8Encoding(false,true).GetString(bytes);var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using(var scan=new JsonTextReader(new StringReader(text)){MaxDepth=4,DateParseHandling=DateParseHandling.None})
        {
            TransferRequire(scan.Read()&&scan.TokenType==JsonToken.StartObject,"TrialJsonShape");
            while(scan.Read())
            {
                TransferRequire(scan.TokenType!=JsonToken.Comment&&scan.TokenType!=JsonToken.StartObject&&scan.TokenType!=JsonToken.StartArray,"TrialJsonShape");
                if(scan.TokenType==JsonToken.PropertyName)TransferRequire(names.Add((string)scan.Value),"TrialJsonDuplicate");
            }
        }
        var settings=new JsonLoadSettings{DuplicatePropertyNameHandling=DuplicatePropertyNameHandling.Error};
        using(var reader=new JsonTextReader(new StringReader(text)){MaxDepth=4,DateParseHandling=DateParseHandling.None})
        {var value=JObject.Load(reader,settings);TransferRequire(!reader.Read(),"TrialJsonTrailingData");foreach(var property in value.Properties())TransferRequire(property.Value is JValue,"TrialJsonShape");return value;}
    }
    private static void TransferValidateRequest(JObject value,string output)
    {
        string[] names={"SchemaVersion","Kind","Nonce","JobId","Server","DatabaseName","InputCanonicalPath","OutputCanonicalPath","RequiredFreeBytes","CancelBelowBytes","SourceCommit","SourceSnapshotSha256","ExecutableSha256","ProductExecutableSha256","ProductExecutablePath","RegistrationSha256"};
        TransferRequire(value.Count==names.Length,"TrialRequestShape");
        foreach(string name in names)TransferRequire(value[name]!=null&&value[name] is JValue,"TrialRequestShape");
        foreach(string name in new[]{"SchemaVersion","RequiredFreeBytes","CancelBelowBytes"})TransferRequire(value[name].Type==JTokenType.Integer,"TrialRequestType");
        foreach(string name in names)if(name!="SchemaVersion"&&name!="RequiredFreeBytes"&&name!="CancelBelowBytes")TransferRequire(value[name].Type==JTokenType.String,"TrialRequestType");
        TransferRequire((int)value["SchemaVersion"]==2&&(string)value["Kind"]=="Request"&&(string)value["Server"]=="localhost"&&(string)value["DatabaseName"]=="Transfer"&&(string)value["InputCanonicalPath"]==TransferInput&&(string)value["OutputCanonicalPath"]==output&&(long)value["RequiredFreeBytes"]==TransferEntryFree&&(long)value["CancelBelowBytes"]==TransferCancelFree&&(string)value["ProductExecutablePath"]==TransferProductExecutable,"TrialRequestPolicy");
        Guid nonce,job;TransferRequire(Guid.TryParseExact((string)value["Nonce"],"N",out nonce)&&nonce!=Guid.Empty&&Guid.TryParseExact((string)value["JobId"],"D",out job)&&job!=Guid.Empty,"TrialRequestIdentity");
        foreach(string name in new[]{"SourceSnapshotSha256","ExecutableSha256","ProductExecutableSha256","RegistrationSha256"})TransferRequire(System.Text.RegularExpressions.Regex.IsMatch((string)value[name],"^[a-f0-9]{64}$"),"TrialBuildPinInvalid");
        TransferRequire(System.Text.RegularExpressions.Regex.IsMatch((string)value["SourceCommit"],"^[a-f0-9]{40}$"),"TrialBuildPinInvalid");
    }
    private static void TransferValidateBuildValues(JObject request,JObject pin,string smokeHash,string productPath,string productHash)
    {
        TransferRequire(pin["FinalPinAccepted"]!=null&&pin["FinalPinAccepted"].Type==JTokenType.Boolean&&(bool)pin["FinalPinAccepted"]&&pin["TransferTrialRegistered"]!=null&&pin["TransferTrialRegistered"].Type==JTokenType.Boolean&&(bool)pin["TransferTrialRegistered"],"TrialBuildPinNotReleased");
        foreach(string name in new[]{"SourceCommit","SourceSnapshotSha256","ExecutableSha256","ProductExecutableSha256","ProductExecutablePath","RegistrationSha256"})TransferRequire(JToken.DeepEquals(pin[name],request[name]),"TrialBuildPinMismatch");
        TransferRequire(smokeHash==(string)pin["ExecutableSha256"],"TrialExecutableChanged");
        TransferRequire(string.Equals(productPath,TransferProductExecutable,StringComparison.OrdinalIgnoreCase)&&(string)pin["ProductExecutablePath"]==TransferProductExecutable,"TrialProductAssemblyPathChanged");
        TransferRequire(productHash==(string)pin["ProductExecutableSha256"],"TrialProductExecutableChanged");
    }
    private static void TransferValidateBuild(JObject request)
    {
        var pin=TransferReadBounded(TransferPin);
        string executable=Process.GetCurrentProcess().MainModule.FileName,product=Path.GetFullPath(typeof(MssqlImportService).Assembly.Location);
        TransferPathNoReparse(product);
        TransferValidateBuildValues(request,pin,TransferHash(executable),product,TransferHash(product));
        foreach(string manifestName in new[]{"SourceSnapshot","Registration"})
        {
            string manifest=(string)pin[manifestName+"Path"];TransferPathNoReparse(manifest);
            TransferRequire(TransferHash(manifest)==(string)pin[manifestName+"Sha256"],"TrialSourceSnapshotChanged");
            var files=TransferReadBounded(manifest);
            foreach(var property in files.Properties())
            {TransferPathNoReparse(property.Name);TransferRequire(property.Value.Type==JTokenType.String&&TransferHash(property.Name)==(string)property.Value,"TrialPinnedFileChanged");}
        }
    }
    private static string TransferFixedFailure(Exception error)
    {
        if(error is OperationCanceledException)return "TrialCancelled";
        if(error is InvalidOperationException&&error.Message.Length<160&&System.Text.RegularExpressions.Regex.IsMatch(error.Message,"^(Trial|Monitor|Reparse|COnly)[A-Za-z0-9:]+$"))return error.Message;
        return "TrialFailure:"+error.GetType().Name;
    }
    private static Dictionary<string, object> TransferTrialSettings()
    {
        // Explicit ephemeral snapshot, never saved settings or account credentials. No SSH/session borrowing.
        return new Dictionary<string, object> {
            { "db_kind", "mssql" }, { "host", "localhost" }, { "port", "" },
            { "initial_database", "Transfer" }, { "trusted_connection", "T" }, { "tls_mode", "Disabled" }
        };
    }
    private static volatile bool TransferOfflineGuard;
    private static int TransferOfflineSqlAttempts;
    private static void TransferRequireEntryFreeSpace(long bytes)
    { TransferRequire(bytes>=TransferEntryFree,"TrialInsufficientSpace"); }
    private static void TransferNoOfflineSql()
    { if(TransferOfflineGuard){Interlocked.Increment(ref TransferOfflineSqlAttempts);throw new InvalidOperationException("TrialOfflineSqlOpenBlocked");} }
    private static IMssqlImportSession TransferOpenSession()
    { TransferNoOfflineSql();return new MssqlImportSqlSession(ConnectionOpenService.OpenMssqlImportSession(TransferTrialSettings())); }
    private static SqlConnection TransferOpenProbe()
    {
        TransferNoOfflineSql();
        var connection = new SqlConnection(ConnectionConfigurationService.BuildMssqlImportConnectionString(TransferTrialSettings()));
        try { connection.Open(); return connection; } catch { connection.Dispose(); throw; }
    }
    private static void TransferRequire(bool condition, string code)
    { if (!condition) throw new InvalidOperationException(code); }
    private static void TransferPathNoReparse(string path)
    {
        string full = Path.GetFullPath(path);
        TransferRequire(full.StartsWith(@"C:\", StringComparison.OrdinalIgnoreCase) && !full.StartsWith(@"\\", StringComparison.Ordinal), "COnlyPathRequired");
        for (string current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if (File.Exists(current) || Directory.Exists(current))
                TransferRequire((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0, "ReparsePathRejected");
                string existing=full;while(!Directory.Exists(existing)&&!File.Exists(existing))existing=Path.GetDirectoryName(existing);
        var mount=new StringBuilder(1024);var localVolume=new StringBuilder(1024);var actualVolume=new StringBuilder(1024);
        TransferRequire(new DriveInfo("C").DriveType==DriveType.Fixed&&GetVolumePathName(existing,mount,1024)&&string.Equals(mount.ToString(),@"C:\",StringComparison.OrdinalIgnoreCase)&&GetVolumeNameForVolumeMountPoint(@"C:\",localVolume,1024)&&GetVolumeNameForVolumeMountPoint(mount.ToString(),actualVolume,1024)&&localVolume.ToString()==actualVolume.ToString(),"TrialLocalCVolumeRequired");
    }
    private static JObject TransferIdentity(CancellationToken cancellation)
    {
        using (var connection = TransferOpenProbe())
        using (var command = connection.CreateCommand())
        {
            command.CommandTimeout = 5;
            command.CommandText = @"SELECT CONVERT(nvarchar(128),SERVERPROPERTY('ServerName')),DB_NAME(),DB_ID(),d.create_date,
CONVERT(nvarchar(128),SERVERPROPERTY('ProductVersion')),d.compatibility_level,d.state_desc,d.is_read_only,d.recovery_model_desc,
ORIGINAL_LOGIN(),CONVERT(nvarchar(128),SUSER_SNAME()),CONVERT(nvarchar(32),CONNECTIONPROPERTY('auth_scheme')),SUSER_SID(ORIGINAL_LOGIN()),SUSER_SID()
FROM sys.databases AS d WHERE d.database_id=DB_ID();";
            cancellation.ThrowIfCancellationRequested();
            using(var registration=cancellation.Register(()=>{try{command.Cancel();}catch{}}))
            using (var reader = command.ExecuteReader(CommandBehavior.SingleRow))
            {
                TransferRequire(reader.Read(), "TrialIdentityUnavailable");
                string login = reader.IsDBNull(9) ? "" : reader.GetString(9);
                string current = reader.IsDBNull(10) ? "" : reader.GetString(10);
                var row = new JObject {
                    ["server"] = reader.GetString(0), ["database"] = reader.GetString(1), ["databaseId"] = reader.GetInt32(2),
                    ["createDateTicks"] = reader.GetDateTime(3).Ticks, ["version"] = reader.GetString(4),
                    ["compatibility"] = Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture), ["state"] = reader.GetString(6),
                    ["readOnly"] = reader.GetBoolean(7), ["recovery"] = reader.GetString(8),
                    ["originalLogin"] = login, ["currentLogin"] = current, ["authScheme"] = reader.IsDBNull(11) ? "" : reader.GetString(11)
                };
                TransferRequire(string.Equals((string)row["server"], @"JOHN\MSSQL2019", StringComparison.OrdinalIgnoreCase) && (string)row["database"] == "Transfer", "TrialWrongIdentity");
                TransferRequire(((string)row["version"]).StartsWith("15.", StringComparison.Ordinal) && (int)row["compatibility"] == 150 &&
                    (string)row["state"] == "ONLINE" && !(bool)row["readOnly"] && (string)row["recovery"] == "FULL", "TrialWrongState");
                using(var identity=WindowsIdentity.GetCurrent())
                {
                    TransferRequire(identity.User!=null&&identity.Name.Split((char)92)[identity.Name.Split((char)92).Length-1].Equals("johnho",StringComparison.OrdinalIgnoreCase),"TrialWrongWindowsIdentity");
                    byte[] sid=new byte[identity.User.BinaryLength];identity.User.GetBinaryForm(sid,0);
                    string expected=BitConverter.ToString(sid).Replace("-","").ToLowerInvariant();
                    string original=reader.IsDBNull(12)?"":BitConverter.ToString((byte[])reader.GetValue(12)).Replace("-","").ToLowerInvariant();
                    string currentSid=reader.IsDBNull(13)?"":BitConverter.ToString((byte[])reader.GetValue(13)).Replace("-","").ToLowerInvariant();
                    TransferValidateSidNames(identity.Name,expected,login,original,current,currentSid,(string)row["authScheme"]);
                    row["windowsSid"]=expected;row["windowsName"]=identity.Name;
                }
                return row;
            }
        }
    }
    private static void TransferSameIdentity(JObject a, JObject b)
    {
        foreach (string name in new [] { "server", "database", "databaseId", "createDateTicks", "version", "compatibility", "state", "readOnly", "recovery", "originalLogin", "currentLogin", "windowsSid", "windowsName" })
            TransferRequire(JToken.DeepEquals(a[name], b[name]), "TrialIdentityChanged:" + name);
    }
    private static void TransferWriteNewReceipt(string path, JObject value)
    {
        // Append-only bounded stage receipts. No overwrite hides an earlier attempted/failed state.
        string json=value.ToString(Formatting.Indented);TransferRequire(new UTF8Encoding(false).GetByteCount(json)<=1048576,"TrialReceiptSize");
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true))
        { writer.Write(json); writer.Flush(); stream.Flush(true); }
    }
    private static void TransferClaimAttempt(JObject attempt)
    {
        TransferClaimAttemptAt(TransferAttempt,attempt,TransferPathNoReparse);
    }
    private static void TransferClaimAttemptAt(string path,JObject attempt,Action<string> validate)
    {
        validate(path);Directory.CreateDirectory(Path.GetDirectoryName(path));validate(Path.GetDirectoryName(path));validate(path);
        // CreateNew is the actual race-safe authorization claim; any existing file, even corrupt, stops.
        TransferWriteNewReceipt(path, attempt);
        // A failure/crash here may consume authority before SQL starts. Preserve marker; no implicit retry.
    }
    private static string TransferQuote(string name) { return "[" + name.Replace("]", "]]") + "]"; }

    private static JObject TransferIndependentVerification(string output, CancellationToken cancellation)
    {
        long tables=0, rows=0;
        using (var connection = TransferOpenProbe())
        using (var detail = new StreamWriter(new FileStream(Path.Combine(output,"independent-counts.jsonl"),FileMode.CreateNew,FileAccess.Write,FileShare.Read),new UTF8Encoding(false)))
        {
            int lastObjectId=0;
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                int id; string schema, table;
                using (var next=connection.CreateCommand())
                {
                    next.CommandTimeout=5;
                    next.CommandText="SELECT TOP(1) t.object_id,s.name,t.name FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id WHERE t.is_ms_shipped=0 AND t.object_id>@id ORDER BY t.object_id;";
                    next.Parameters.Add("@id",SqlDbType.Int).Value=lastObjectId;
                    using(var reader=next.ExecuteReader(CommandBehavior.SingleRow))
                    { if(!reader.Read())break;id=reader.GetInt32(0);schema=reader.GetString(1);table=reader.GetString(2); }
                }
                using(var count=connection.CreateCommand())
                {
                    count.CommandTimeout=0;count.CommandText="SELECT COUNT_BIG(*) FROM "+TransferQuote(schema)+"."+TransferQuote(table)+";";
                    using(cancellation.Register(()=>{try{count.Cancel();}catch{}}))
                    {
                        long n=Convert.ToInt64(count.ExecuteScalar(),CultureInfo.InvariantCulture);rows=checked(rows+n);tables++;
                        detail.WriteLine(new JObject{["schema"]=schema,["table"]=table,["rows"]=n}.ToString(Formatting.None));detail.Flush();
                    }
                }
                lastObjectId=id;
            }
        }
        TransferRequire(tables==126 && rows==3247785,"TrialIndependentCountMismatch");
        // Catalog inventory is separate evidence; no unsupported equality claim versus absent source object manifest.
        using(var connection=TransferOpenProbe())
        using(var command=connection.CreateCommand())
        using(var stream=new StreamWriter(new FileStream(Path.Combine(output,"independent-catalog.jsonl"),FileMode.CreateNew,FileAccess.Write,FileShare.Read),new UTF8Encoding(false)))
        {
            command.CommandTimeout=5;
            command.CommandText=@"SELECT o.type_desc,s.name,o.name,CONVERT(nvarchar(100),NULL),CONVERT(nvarchar(100),NULL)
FROM sys.objects o JOIN sys.schemas s ON s.schema_id=o.schema_id WHERE o.is_ms_shipped=0
UNION ALL SELECT N'INDEX',s.name,t.name,i.name,CONCAT(N'disabled=',i.is_disabled,N';hypothetical=',i.is_hypothetical,N';unique=',i.is_unique) FROM sys.indexes i JOIN sys.tables t ON t.object_id=i.object_id JOIN sys.schemas s ON s.schema_id=t.schema_id WHERE t.is_ms_shipped=0 AND i.index_id>0
UNION ALL SELECT N'FOREIGN_KEY',s.name,t.name,f.name,CONCAT(N'disabled=',f.is_disabled,N';not_trusted=',f.is_not_trusted) FROM sys.foreign_keys f JOIN sys.tables t ON t.object_id=f.parent_object_id JOIN sys.schemas s ON s.schema_id=t.schema_id WHERE t.is_ms_shipped=0
UNION ALL SELECT N'CHECK',s.name,t.name,c.name,CONCAT(N'disabled=',c.is_disabled,N';not_trusted=',c.is_not_trusted) FROM sys.check_constraints c JOIN sys.tables t ON t.object_id=c.parent_object_id JOIN sys.schemas s ON s.schema_id=t.schema_id WHERE t.is_ms_shipped=0
UNION ALL SELECT N'COMMENT_NAME',CONVERT(nvarchar(128),e.class),CONVERT(nvarchar(128),e.major_id),e.name,CONVERT(nvarchar(100),e.minor_id) FROM sys.extended_properties e;";
            using(var reader=command.ExecuteReader(CommandBehavior.SequentialAccess))
                while(reader.Read())
                { cancellation.ThrowIfCancellationRequested();var record=new JObject();for(int i=0;i<reader.FieldCount;i++)record["c"+i]=reader.IsDBNull(i)?JValue.CreateNull():new JValue(reader.GetString(i));stream.WriteLine(record.ToString(Formatting.None)); }
        }
        return new JObject{["tables"]=tables,["rows"]=rows,["countsMatchFooter"]=true,["catalogInventoryCaptured"]=true,
            ["objectDefinitionEqualityVerified"]=false,["fieldHashEqualityVerified"]=false};
    }

    private static JObject TransferReadUsedLog(JObject expectedIdentity)
    {
        var snapshot=new JObject{["Bytes"]=JValue.CreateNull(),["Availability"]="LogUsedUnavailable",["Utc"]=DateTime.UtcNow.ToString("o")};
        try
        {
            using(var connection=TransferOpenProbe())using(var command=connection.CreateCommand())
            {
                command.CommandTimeout=5;
                command.CommandText=@"SELECT CONVERT(bigint,used_log_space_in_bytes),DB_ID(),
(SELECT create_date FROM sys.databases WHERE database_id=DB_ID()),
CONVERT(nvarchar(128),SERVERPROPERTY('ServerName')),DB_NAME()
FROM sys.dm_db_log_space_usage;";
                using(var reader=command.ExecuteReader(CommandBehavior.SingleRow))
                {
                    TransferRequire(reader.Read()&&!reader.IsDBNull(0),"TrialLogUsedUnavailable");
                    TransferRequire(string.Equals(reader.GetString(3),@"JOHN\MSSQL2019",StringComparison.OrdinalIgnoreCase)&&reader.GetString(4)=="Transfer","TrialLogUsedIdentityChanged");
                    if(expectedIdentity!=null)TransferRequire(reader.GetInt32(1)==(long)expectedIdentity["DatabaseId"]&&reader.GetDateTime(2).Ticks==(long)expectedIdentity["DatabaseCreateDateTicks"],"TrialLogUsedIdentityChanged");
                    long bytes=reader.GetInt64(0);TransferRequire(bytes>=0,"TrialLogUsedInvalid");
                    snapshot["Bytes"]=bytes;snapshot["Availability"]="LogUsedAvailable";
                }
            }
        }
        catch
        {
            // Optional read-only evidence: permission/transport/identity failure remains null with one fixed code.
            // Never convert unknown to zero, GRANT permissions, change recovery or replace the primary import failure.
            snapshot["Bytes"]=JValue.CreateNull();snapshot["Availability"]="LogUsedUnavailable";
        }
        return snapshot;
    }
    private static void TransferCaptureUsedLog(JObject receipt,string phase)
    {
        var snapshot=receipt["DatabaseId"]!=null&&receipt["DatabaseCreateDateTicks"]!=null?TransferReadUsedLog(receipt):new JObject{["Bytes"]=JValue.CreateNull(),["Availability"]="LogUsedUnavailable",["Utc"]=DateTime.UtcNow.ToString("o")};
        receipt[phase+"LogUsedBytes"]=snapshot["Bytes"].DeepClone();
        receipt[phase+"LogUsedAvailability"]=(string)snapshot["Availability"];
        receipt[phase+"LogUsedUtc"]=(string)snapshot["Utc"];
    }
    private static void TransferValidateSidNames(string windowsName,string sid,string originalName,string originalSid,string currentName,string currentSid,string auth)
    { TransferRequire(!string.IsNullOrEmpty(sid)&&sid==originalSid&&sid==currentSid&&string.Equals(originalName,windowsName,StringComparison.OrdinalIgnoreCase)&&string.Equals(currentName,windowsName,StringComparison.OrdinalIgnoreCase)&&(auth=="NTLM"||auth=="KERBEROS"),"TrialWrongLogin"); }
    private static void TransferRetainFailure(ref Exception primary,Exception failure,ref string secondary,JObject receipt)
    { if(primary==null){primary=failure;receipt["FailureCode"]=TransferFixedFailure(failure);}else secondary=TransferFixedFailure(failure); }
    private static JObject TransferReadAllocation()
    {
        var value=new JObject{["dataAllocatedBytes"]=JValue.CreateNull(),["logAllocatedBytes"]=JValue.CreateNull(),["dbMetricAvailability"]="Unavailable"};
        try{
            using(var connection=TransferOpenProbe())using(var command=connection.CreateCommand()){
                command.CommandTimeout=5;command.CommandText="SELECT SUM(CASE WHEN type=0 THEN CONVERT(bigint,size)*8192 ELSE 0 END),SUM(CASE WHEN type=1 THEN CONVERT(bigint,size)*8192 ELSE 0 END) FROM sys.database_files;";
                using(var reader=command.ExecuteReader(CommandBehavior.SingleRow)){TransferRequire(reader.Read(),"MonitorMetricsMissing");value["dataAllocatedBytes"]=reader.GetInt64(0);value["logAllocatedBytes"]=reader.GetInt64(1);value["dbMetricAvailability"]="AllocationAvailable";}
            }
        }catch{ }
        return value;
    }
    private sealed class TransferTrialMonitor : IDisposable
    {
        private readonly string output;
        private readonly ManualResetEventSlim stop = new ManualResetEventSlim(false);
        private readonly ManualResetEventSlim ready = new ManualResetEventSlim(false);
        private readonly CancellationTokenSource cancellation;
        private readonly string nonce;
        private readonly Func<long> freeSpace;
        private readonly Func<JObject> readUsed,readAllocation;
        private readonly Func<Stream> openStream;
        private readonly bool pollOperator;
        private readonly long baselineCpu;
        private readonly Thread thread;
        private readonly Stopwatch elapsed = Stopwatch.StartNew();
        private long sequence, peakPrivate;
        internal volatile Exception Failure;
        internal MssqlImportProgress Latest; // Interlocked reference publication only; immutable DTO.
        internal TransferTrialMonitor(string directory,CancellationTokenSource ownedCancellation,string runNonce)
            : this(directory,ownedCancellation,runNonce,()=>new DriveInfo("C").AvailableFreeSpace,()=>TransferReadUsedLog(null),TransferReadAllocation,
                ()=>new FileStream(Path.Combine(directory,"monitor.jsonl"),FileMode.CreateNew,FileAccess.Write,FileShare.Read),true) { }
        internal TransferTrialMonitor(string directory,CancellationTokenSource ownedCancellation,string runNonce,Func<long> free,Func<JObject> used,Func<JObject> allocation,Func<Stream> stream,bool operatorProbe)
        { output=directory;cancellation=ownedCancellation;nonce=runNonce;freeSpace=free;readUsed=used;readAllocation=allocation;openStream=stream;pollOperator=operatorProbe;using(var p=Process.GetCurrentProcess())baselineCpu=(long)p.TotalProcessorTime.TotalMilliseconds;thread=new Thread(Loop){IsBackground=true,Name="Transfer trial local evidence"};thread.Start(); }
        internal void RequireReady(){TransferRequire(ready.Wait(5000),"TrialMonitorNotReady");TransferRequire(Failure==null,"TrialMonitorIoFailure");}
        private void Loop()
        {
            try
            {
                using(var writer=new StreamWriter(openStream(),new UTF8Encoding(false)))
                { ready.Set();while(!stop.IsSet)
                {
                    using(var process=Process.GetCurrentProcess())
                    {
                        process.Refresh();peakPrivate=Math.Max(peakPrivate,process.PrivateMemorySize64);
                        var p=Interlocked.CompareExchange(ref Latest,null,null);
                        var record=new JObject{["schemaVersion"]=1,["utc"]=DateTime.UtcNow.ToString("o"),["sequence"]=sequence++,
                            ["nonce"]=nonce,["processId"]=process.Id,["elapsedMs"]=elapsed.ElapsedMilliseconds,["cpuMs"]=(long)process.TotalProcessorTime.TotalMilliseconds-baselineCpu,
                            ["peakSampledPrivateBytes"]=peakPrivate,["peakWorkingSetBytes"]=process.PeakWorkingSet64,
                            ["successfulBatches"]=p==null?0:p.SuccessfulBatches,["failedBatches"]=p==null?0:p.FailedBatches,["stage"]=p==null?"Scanning":p.Stage.ToString(),
                            ["cFreeBytes"]=freeSpace()};
                        if(pollOperator&&File.Exists(Path.Combine(output,"cancel.request"))){record["cancelCode"]="TrialOperatorStop";cancellation.Cancel();}
                        if((long)record["cFreeBytes"]<TransferCancelFree){record["cancelCode"]="TrialLowSpace";cancellation.Cancel();}
                        foreach(var property in readAllocation().Properties())record[property.Name]=property.Value.DeepClone();
                        var usedLog=readUsed();record["logUsedBytes"]=usedLog["Bytes"].DeepClone();record["logUsedAvailability"]=(string)usedLog["Availability"];record["logUsedUtc"]=(string)usedLog["Utc"];
                        writer.WriteLine(record.ToString(Formatting.None));writer.Flush();var file=writer.BaseStream as FileStream;if(file!=null)file.Flush(true);else writer.BaseStream.Flush();
                    }
                    stop.Wait(1000);
                }
            }
            }
            catch(Exception error){Failure=error;cancellation.Cancel();ready.Set();}
        }
        public void Dispose()
        { stop.Set();if(!thread.Join(15000))throw new InvalidOperationException("TrialMonitorTerminationUnconfirmed");stop.Dispose();ready.Dispose(); }
        // Monitor has the run nonce, baseline CPU and owned cancellation route. Used-log permission gaps remain explicit null.
        // Optional used-log DMV samples record actual values or null+LogUsedUnavailable; no GRANT/recovery change/payload/error.Message.
    }

    private static void MssqlImportTransferTrial()
    {
        if(Environment.GetEnvironmentVariable("MYSQLPUNK_MSSQL_IMPORT_TRIAL")!="1")
        {Console.WriteLine("[SKIP] MSSQL import Transfer trial: opt-in absent.");return;}
        foreach(string name in new[]{"MYSQLPUNK_MSSQL_IMPORT_INTEGRATION","MYSQLPUNK_MSSQL_IMPORT_AUTH","MYSQLPUNK_MSSQL_IMPORT_PROFILE","MYSQLPUNK_MSSQL_IMPORT_FIXTURES","MYSQLPUNK_MSSQL_IMPORT_MEMORY_SCALE","MYSQLPUNK_MSSQL_IMPORT_RUN_ID","MYSQLPUNK_MSSQL_IMPORT_WIZARD_PREVIEW","MYSQLPUNK_MSSQL_WIZARD_PREVIEW"})TransferRequire(string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)),"TrialForeignOptIn");
        string server=Environment.GetEnvironmentVariable("MYSQLPUNK_MSSQL_IMPORT_SERVER"),database=Environment.GetEnvironmentVariable("MYSQLPUNK_MSSQL_IMPORT_DATABASE"),input=Environment.GetEnvironmentVariable("MYSQLPUNK_MSSQL_IMPORT_INPUT"),output=Environment.GetEnvironmentVariable("MYSQLPUNK_MSSQL_IMPORT_OUTPUT");
        TransferRequire(server=="localhost"&&database=="Transfer"&&input==TransferInput&&!string.IsNullOrWhiteSpace(output),"TrialInvocationMismatch");
        output=Path.GetFullPath(output);TransferPathNoReparse(output);TransferPathNoReparse(TransferAttempt);
        var envelope=TransferReadBounded(Path.Combine(output,"request.json"));TransferValidateRequest(envelope,output);TransferValidateBuild(envelope);
        TransferPathNoReparse(TransferInput);TransferRequire(new FileInfo(TransferInput).Length==4478414151L,"TrialInputLengthMismatch");
        TransferRequireEntryFreeSpace(new DriveInfo("C").AvailableFreeSpace);TransferRequire(!File.Exists(TransferAttempt),"TrialAttemptAlreadyClaimed");
        foreach(var entry in Directory.GetFileSystemEntries(output))TransferRequire(File.Exists(entry)&&(Path.GetFileName(entry)=="request.json"||Path.GetFileName(entry)=="stdout.log"||Path.GetFileName(entry)=="stderr.log"),"TrialOutputNotFresh");
        var cancellation=new CancellationTokenSource();TransferTrialMonitor monitor=null;Exception primary=null;string secondary="";bool monitorJoined=true;
        var receipt=(JObject)envelope.DeepClone();receipt["Kind"]="Final";receipt["ProcessId"]=Process.GetCurrentProcess().Id;receipt["RequestSha256"]=TransferHash(Path.Combine(output,"request.json"));receipt["Executed"]=false;receipt["AttemptMarkerDurable"]=false;receipt["Status"]="NotStarted";receipt["FailureCode"]="";receipt["SecondaryFailureCode"]="";receipt["StartedUtc"]=DateTime.UtcNow.ToString("o");
        ConsoleCancelEventHandler cancel=(sender,args)=>{args.Cancel=true;cancellation.Cancel();};Console.CancelKeyPress+=cancel;
        try
        {
            var before=TransferIdentity(cancellation.Token);receipt["ServerName"]=(string)before["server"];receipt["DatabaseId"]=(long)before["databaseId"];receipt["DatabaseCreateDateTicks"]=(long)before["createDateTicks"];receipt["WindowsSid"]=(string)before["windowsSid"];receipt["WindowsName"]=(string)before["windowsName"];
            monitor=new TransferTrialMonitor(output,cancellation,(string)envelope["Nonce"]);monitor.RequireReady();
            Action<MssqlImportProgress> progress=p=>Interlocked.Exchange(ref monitor.Latest,p);
            var service=new MssqlImportService(TransferOpenSession,id=>new MssqlImportLog(id,output));
            var request=new MssqlImportRequest(Guid.Parse((string)envelope["JobId"]),TransferInput,"Transfer",false);
            MssqlImportResult result;
            using(var preflight=service.Preflight(request,progress,cancellation.Token))
            {
                TransferRequire(preflight.Passed,"TrialPreflightRejected");var scan=preflight.Scan;
                TransferRequire(scan.Script.Kind==MssqlImportScriptKind.NativeSchema&&scan.Script.ExpectedTables==126&&scan.Script.ExpectedRows==3247785&&scan.FileBytes==4478414151L&&scan.MaxBatchCodeUnits<=16777216L&&scan.EncodingName=="UTF-8"&&scan.Script.SourceReadMode=="READ COMMITTED","TrialNativeIdentityMismatch");
                receipt["SourceSha256"]=scan.Sha256.ToLowerInvariant();receipt["ScanBatchCount"]=scan.BatchCount;receipt["MaxBatchCodeUnits"]=scan.MaxBatchCodeUnits;receipt["NativeJobId"]=scan.Script.NativeJobId.ToString();receipt["EncodingName"]=scan.EncodingName;receipt["SourceReadMode"]=scan.Script.SourceReadMode;receipt["LogPath"]=preflight.LogPath;
                TransferSameIdentity(before,TransferIdentity(cancellation.Token));TransferValidateBuild(envelope);TransferCaptureUsedLog(receipt,"PreRun");
                cancellation.Token.ThrowIfCancellationRequested();TransferRequire(monitor.Failure==null,"TrialMonitorIoFailure");TransferRequireEntryFreeSpace(new DriveInfo("C").AvailableFreeSpace);
                var marker=(JObject)receipt.DeepClone();marker["Kind"]="Attempt";
                // Marker carries only immutable request + actual identity/source chain, not fake terminal proof fields.
                foreach(string key in new[]{"Executed","AttemptMarkerDurable","Status","FailureCode","SecondaryFailureCode","LogPath","ScanBatchCount","MaxBatchCodeUnits","NativeJobId","EncodingName","SourceReadMode","PreRunLogUsedBytes","PreRunLogUsedAvailability","PreRunLogUsedUtc"})marker.Remove(key);
                TransferClaimAttempt(marker);receipt["MarkerSha256"]=TransferHash(TransferAttempt);receipt["AttemptMarkerDurable"]=true;
                var started=(JObject)receipt.DeepClone();started["Kind"]="Started";TransferWriteNewReceipt(Path.Combine(output,"started.json"),started);
                cancellation.Token.ThrowIfCancellationRequested();TransferRequire(monitor.Failure==null,"TrialMonitorIoFailure");TransferRequireEntryFreeSpace(new DriveInfo("C").AvailableFreeSpace);
                receipt["Executed"]=true;
                result=service.Run(request,preflight,progress,cancellation.Token); // Sole authorized Run. No retry path.
            }
            receipt["Status"]=result.Status.ToString();receipt["FailedBatches"]=result.FailedBatches;receipt["SkippedBytes"]=result.SkippedBytes;receipt["TotalErrors"]=result.TotalErrors;receipt["UncertainBatch"]=result.UncertainBatch.HasValue?new JValue(result.UncertainBatch.Value):JValue.CreateNull();receipt["LastAcknowledgedBatch"]=result.LastAcknowledgedBatch;receipt["SuccessfulBatches"]=result.SuccessfulBatches;receipt["ProcessedBytes"]=result.ProcessedBytes;
            receipt["CommandEnded"]=result.Cleanup!=null&&result.Cleanup.CommandEnded;receipt["SessionReleased"]=result.Cleanup!=null&&result.Cleanup.SessionReleased;receipt["TransactionCleanupConfirmed"]=result.Cleanup!=null&&result.Cleanup.TransactionCleanupConfirmed;receipt["CleanupErrors"]=result.Cleanup==null?0:result.Cleanup.Errors.Count;
            TransferRequire(result.Status==MssqlImportStatus.Completed&&result.FailedBatches==0&&result.TotalErrors==0&&result.SkippedBytes==0&&result.UncertainBatch==null,"TrialSutNotCompleted");
            TransferRequire((bool)receipt["CommandEnded"]&&(bool)receipt["SessionReleased"]&&(bool)receipt["TransactionCleanupConfirmed"]&&(long)receipt["CleanupErrors"]==0,"TrialCleanupUnconfirmed");
            // Accepted Service Completed semantics include EOF digest/session/transaction/native-LOB checks and log durability.
            receipt["ServiceCompletionValidated"]=true;receipt["LogComplete"]=true;
            TransferSameIdentity(before,TransferIdentity(cancellation.Token));using(var proof=TransferOpenSession())TransferRequire(proof.ReadTarget(cancellation.Token).MetadataVisible,"TrialIndependentMetadataUnknown");
            var verification=TransferIndependentVerification(output,cancellation.Token);receipt["ActualTables"]=(long)verification["tables"];receipt["ActualRows"]=(long)verification["rows"];receipt["IndependentVerificationCompleted"]=true;receipt["IndependentCatalogCaptured"]=(bool)verification["catalogInventoryCaptured"];receipt["ObjectVerificationBoundary"]="InventoryOnly;SourceDefinitionEqualityUnverified";receipt["FieldHashEqualityVerified"]=false;
            TransferSameIdentity(before,TransferIdentity(cancellation.Token));cancellation.Token.ThrowIfCancellationRequested();
        }
        catch(Exception error){primary=error;receipt["FailureCode"]=TransferFixedFailure(error);}
        finally
        {
            Console.CancelKeyPress-=cancel;
            try{if(monitor!=null){monitor.Dispose();if(monitor.Failure!=null)throw new InvalidOperationException("TrialMonitorIoFailure");}receipt["MonitorComplete"]=monitor!=null;}
            catch(Exception error){monitorJoined=false;receipt["MonitorComplete"]=false;TransferRetainFailure(ref primary,error,ref secondary,receipt);}
            if(cancellation.IsCancellationRequested&&primary==null){primary=new OperationCanceledException();receipt["FailureCode"]="TrialCancelled";}
            TransferCaptureUsedLog(receipt,"Terminal");
            receipt["SecondaryFailureCode"]=secondary;receipt["FinishedUtc"]=DateTime.UtcNow.ToString("o");
            try{TransferWriteNewReceipt(Path.Combine(output,"final.json"),receipt);}
            catch(Exception error){if(primary==null)primary=error;else Console.Error.WriteLine("TrialFinalReceiptIncomplete;primary="+TransferFixedFailure(primary)+";secondary="+TransferFixedFailure(error));}
            if(monitorJoined)cancellation.Dispose();
        }
        if(primary!=null)ExceptionDispatchInfo.Capture(primary).Throw();
    }
    private static int TransferGuardChecks;
    private static void TransferGuardCheck(bool condition,string message)
    { TransferGuardChecks++;Assert(condition,message); }
    private static void TransferExpectGuard(Action action,string expected=null)
    {
        Exception failure=null;try{action();}catch(Exception error){failure=error;}
        TransferGuardCheck(failure!=null&&(expected==null||failure is InvalidOperationException&&failure.Message==expected),"Transfer guard expected "+expected+"; actual "+(failure==null?"none":failure.Message));
    }
    private sealed class TransferMonitorWriteFault : MemoryStream
    { public override void Write(byte[] bytes,int offset,int count){throw new IOException("offline monitor write fault");} }
    private static JObject TransferGuardRequest(string output)
    {
        return new JObject{["SchemaVersion"]=2,["Kind"]="Request",["Nonce"]=new Guid("11111111-1111-1111-1111-111111111111").ToString("N"),["JobId"]="22222222-2222-2222-2222-222222222222",["Server"]="localhost",["DatabaseName"]="Transfer",["InputCanonicalPath"]=TransferInput,["OutputCanonicalPath"]=output,["RequiredFreeBytes"]=TransferEntryFree,["CancelBelowBytes"]=TransferCancelFree,["SourceCommit"]=new string('a',40),["SourceSnapshotSha256"]=new string('b',64),["ExecutableSha256"]=new string('c',64),["ProductExecutableSha256"]=new string('d',64),["ProductExecutablePath"]=TransferProductExecutable,["RegistrationSha256"]=new string('e',64)};
    }
    private static void MssqlImportTransferGuards()
    {
        TransferOfflineGuard=true;TransferOfflineSqlAttempts=0;TransferGuardChecks=0;
        string[] environmentNames={"MYSQLPUNK_MSSQL_IMPORT_TRIAL","MYSQLPUNK_MSSQL_IMPORT_SERVER","MYSQLPUNK_MSSQL_IMPORT_DATABASE","MYSQLPUNK_MSSQL_IMPORT_INPUT","MYSQLPUNK_MSSQL_IMPORT_OUTPUT","MYSQLPUNK_MSSQL_IMPORT_INTEGRATION","MYSQLPUNK_MSSQL_IMPORT_AUTH","MYSQLPUNK_MSSQL_IMPORT_PROFILE","MYSQLPUNK_MSSQL_IMPORT_FIXTURES","MYSQLPUNK_MSSQL_IMPORT_MEMORY_SCALE","MYSQLPUNK_MSSQL_IMPORT_RUN_ID","MYSQLPUNK_MSSQL_IMPORT_WIZARD_PREVIEW","MYSQLPUNK_MSSQL_WIZARD_PREVIEW"};
        var saved=new Dictionary<string,string>();foreach(string name in environmentNames){saved[name]=Environment.GetEnvironmentVariable(name);Environment.SetEnvironmentVariable(name,null);}
        string fixture=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"mySQLPunk","ImportTrialGuardFixtures",Guid.NewGuid().ToString("N"));
        try
        {
            TransferExpectGuard(()=>TransferOpenProbe(),"TrialOfflineSqlOpenBlocked");
            MssqlImportTransferTrial(); // absent opt-in returns before source/target IO
            TransferGuardCheck(TransferOfflineSqlAttempts==1,"NoRun does not call SQL factory.");
            Environment.SetEnvironmentVariable("MYSQLPUNK_MSSQL_IMPORT_TRIAL","1");
            foreach(string name in environmentNames)if(name.EndsWith("INTEGRATION",StringComparison.Ordinal)||name.EndsWith("AUTH",StringComparison.Ordinal)||name.EndsWith("PROFILE",StringComparison.Ordinal)||name.EndsWith("FIXTURES",StringComparison.Ordinal)||name.EndsWith("MEMORY_SCALE",StringComparison.Ordinal)||name.EndsWith("RUN_ID",StringComparison.Ordinal)||name.EndsWith("PREVIEW",StringComparison.Ordinal))
            {Environment.SetEnvironmentVariable(name,"1");TransferExpectGuard(MssqlImportTransferTrial,"TrialForeignOptIn");Environment.SetEnvironmentVariable(name,null);}
            Environment.SetEnvironmentVariable("MYSQLPUNK_MSSQL_IMPORT_SERVER","not-authorized");TransferExpectGuard(MssqlImportTransferTrial,"TrialInvocationMismatch");
            TransferRequireEntryFreeSpace(TransferEntryFree);TransferExpectGuard(()=>TransferRequireEntryFreeSpace(TransferEntryFree-1),"TrialInsufficientSpace");
            var request=TransferGuardRequest(fixture);TransferValidateRequest(request,fixture);
            foreach(string name in new[]{"Server","DatabaseName","InputCanonicalPath","ProductExecutablePath","OutputCanonicalPath"})
            {var bad=(JObject)request.DeepClone();bad[name]="wrong";TransferExpectGuard(()=>TransferValidateRequest(bad,fixture),"TrialRequestPolicy");}
            foreach(string name in new[]{"SourceSnapshotSha256","ExecutableSha256","ProductExecutableSha256","RegistrationSha256","SourceCommit"})
            {var bad=(JObject)request.DeepClone();bad[name]="bad";TransferExpectGuard(()=>TransferValidateRequest(bad,fixture),"TrialBuildPinInvalid");}
            foreach(string name in new[]{"SchemaVersion","RequiredFreeBytes","CancelBelowBytes"})
            {var bad=(JObject)request.DeepClone();bad[name]="2";TransferExpectGuard(()=>TransferValidateRequest(bad,fixture),"TrialRequestType");}
            var extra=(JObject)request.DeepClone();extra["Extra"]=true;TransferExpectGuard(()=>TransferValidateRequest(extra,fixture),"TrialRequestShape");
            var pin=(JObject)request.DeepClone();pin["FinalPinAccepted"]=true;pin["TransferTrialRegistered"]=true;
            TransferValidateBuildValues(request,pin,new string('c',64),TransferProductExecutable,new string('d',64));
            TransferExpectGuard(()=>TransferValidateBuildValues(request,pin,new string('c',64),TransferProductExecutable,new string('f',64)),"TrialProductExecutableChanged");
            TransferExpectGuard(()=>TransferValidateBuildValues(request,pin,new string('c',64),@"C:\other.exe",new string('d',64)),"TrialProductAssemblyPathChanged");
            var unaccepted=(JObject)pin.DeepClone();unaccepted["FinalPinAccepted"]="true";TransferExpectGuard(()=>TransferValidateBuildValues(request,unaccepted,new string('c',64),TransferProductExecutable,new string('d',64)),"TrialBuildPinNotReleased");
            TransferValidateSidNames(@"JOHN\johnho","aabb",@"JOHN\johnho","aabb",@"JOHN\johnho","aabb","NTLM");
            TransferExpectGuard(()=>TransferValidateSidNames(@"JOHN\johnho","aabb",@"OTHER\johnho","aabb",@"JOHN\johnho","aabb","NTLM"),"TrialWrongLogin");
            TransferExpectGuard(()=>TransferValidateSidNames(@"JOHN\johnho","aabb",@"JOHN\johnho","aabb",@"JOHN\johnho","ccdd","NTLM"),"TrialWrongLogin");
            TransferExpectGuard(()=>TransferValidateSidNames(@"JOHN\johnho","aabb",@"JOHN\johnho","aabb",@"JOHN\johnho","aabb","SQL"),"TrialWrongLogin");
            TransferPathNoReparse(fixture);Directory.CreateDirectory(fixture);TransferPathNoReparse(fixture);
            foreach(string malformed in new[]{"{\"A\":1,\"A\":2}","{\"A\":1,\"a\":2}","{/*comment*/\"A\":1}","{\"A\":{\"B\":1}}","{\"A\":1} {\"B\":2}"})
            {string file=Path.Combine(fixture,Guid.NewGuid().ToString("N")+".json");File.WriteAllText(file,malformed,new UTF8Encoding(false));TransferExpectGuard(()=>TransferReadBounded(file));}
            string oversize=Path.Combine(fixture,"oversize.json");using(var file=new FileStream(oversize,FileMode.CreateNew)){file.SetLength(1048577);}TransferExpectGuard(()=>TransferReadBounded(oversize),"TrialJsonSize");
            string invalid=Path.Combine(fixture,"invalid-utf8.json");File.WriteAllBytes(invalid,new byte[]{255});TransferExpectGuard(()=>TransferReadBounded(invalid));
            string valid=Path.Combine(fixture,"request.json");TransferWriteNewReceipt(valid,request);TransferGuardCheck(JToken.DeepEquals(request,TransferReadBounded(valid)),"Strict request round-trip remains exact.");
            string marker=Path.Combine(fixture,"attempt.json");int checks=0;TransferClaimAttemptAt(marker,request,p=>{checks++;TransferPathNoReparse(p);});
            string markerHash=TransferHash(marker);TransferExpectGuard(()=>TransferClaimAttemptAt(marker,request,TransferPathNoReparse));TransferGuardCheck(checks==3&&TransferHash(marker)==markerHash,"Existing marker blocks and remains unchanged.");
            string concurrent=Path.Combine(fixture,"raced-attempt.json");int winners=0;
            Action claim=()=>{try{TransferClaimAttemptAt(concurrent,request,TransferPathNoReparse);Interlocked.Increment(ref winners);}catch(IOException){}};
            System.Threading.Tasks.Task.WaitAll(System.Threading.Tasks.Task.Run(claim),System.Threading.Tasks.Task.Run(claim));TransferGuardCheck(winners==1,"Atomic CreateNew permits one claimant only.");
            Func<JObject> used=()=>new JObject{["Bytes"]=JValue.CreateNull(),["Availability"]="LogUsedUnavailable",["Utc"]="2026-10-14T00:00:00Z"};
            Func<JObject> allocation=()=>new JObject{["dataAllocatedBytes"]=0L,["logAllocatedBytes"]=0L,["dbMetricAvailability"]="OfflineFixture"};
            using(var cancellation=new CancellationTokenSource())using(var monitor=new TransferTrialMonitor(fixture,cancellation,"offline-low-space",()=>TransferCancelFree-1,used,allocation,()=>new MemoryStream(),false))
            {monitor.RequireReady();TransferGuardCheck(cancellation.Token.WaitHandle.WaitOne(2000),"Low-space monitor cancels the owned token.");}
            using(var cancellation=new CancellationTokenSource())using(var monitor=new TransferTrialMonitor(fixture,cancellation,"offline-io",()=>TransferEntryFree,used,allocation,()=>new TransferMonitorWriteFault(),false))
            {try{monitor.RequireReady();}catch(InvalidOperationException error){TransferGuardCheck(error.Message=="TrialMonitorIoFailure","Monitor reports fixed IO failure.");}TransferGuardCheck(cancellation.Token.WaitHandle.WaitOne(2000),"Monitor IO fault cancels the owned token.");}
            Exception original=new InvalidOperationException("TrialSutNotCompleted"),primary=original;string secondary="";var receipt=new JObject();TransferRetainFailure(ref primary,new IOException("secondary"),ref secondary,receipt);
            TransferGuardCheck(object.ReferenceEquals(primary,original)&&secondary=="TrialFailure:IOException","Secondary error cannot replace the original failure.");
            TransferGuardCheck(TransferOfflineSqlAttempts==1,"All negative/monitor/JSON/marker guards stop before SQL factory; the sole blocked call was deliberate.");
            Console.WriteLine("Transfer C# offline guard assertions: "+TransferGuardChecks+"; SQL opens0; fixture="+fixture);
        }
        finally{foreach(string name in environmentNames)Environment.SetEnvironmentVariable(name,saved[name]);TransferOfflineGuard=false;TransferOfflineSqlAttempts=0;}
    }
}
