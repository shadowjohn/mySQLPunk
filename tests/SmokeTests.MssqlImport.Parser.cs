using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using mySQLPunk.lib.MssqlImport;

public static partial class SmokeTests
{
    // Breaks caught: GO split inside a lexical context, decoder boundary corruption,
    // accepting over-budget SQL, native downgrade, read-ahead progress and handle replacement.
    private static void MssqlImportParserPreservesContexts()
    {
        string first = "SELECT N'羽😀\r\nGO\r\n''x';\r\nSELECT \"a\"\"\r\nGO\r\nb\", [a]]\r\nGO\r\nb];\r\n/* outer\r\n/* nested */\r\nGO\r\n*/\r\n-- GO\r\n";
        string last = "SELECT 2;";
        foreach (Encoding encoding in new Encoding[] { new UTF8Encoding(false,true), new UTF8Encoding(true,true), new UnicodeEncoding(false,true,true), new UnicodeEncoding(true,true,true) })
        foreach (int buffer in new[] {1,2,3,7})
        {
            byte[] bytes = ImportBytes(first + "  gO -- end\r\n" + last, encoding);
            using (var stream = new MemoryStream(bytes))
            using (var reader = new MssqlGoBatchReader(stream, buffer))
            {
                var batches = reader.ReadBatches(CancellationToken.None).ToArray();
                Assert(batches.Length == 2, "Context-aware GO reader yields exactly two commands.");
                Assert(batches[0].Text == first && batches[1].Text == last, "Original SQL text survives tiny decode buffers and quote escapes.");
                Assert(batches[0].StartByte == 0 && batches[0].EndByte == ImportBytes(first + "  gO -- end\r\n",encoding).LongLength, "EndByte includes original encoded GO line and BOM.");
                Assert(batches[1].StartByte == batches[0].EndByte && batches[1].EndByte == bytes.LongLength, "Byte ranges cover last undelimited command.");
                Assert(batches[0].StartLine == 1 && batches[0].EndLine == 13 && batches[1].StartLine == 15 && batches[1].EndLine == 15, "Original line offsets exclude GO delimiter.");
            }
        }
        // A later malformed byte in one decoder buffer must not erase valid
        // preceding line/GO transitions, including EOF decoder flush errors.
        foreach(Encoding encoding in new Encoding[]{new UTF8Encoding(false,true),new UTF8Encoding(true,true),new UnicodeEncoding(false,true,true),new UnicodeEncoding(true,true,true)})
        foreach(int buffer in new[]{8192,1,2,3,7})
        foreach(int padding in new[]{0,9000})
        foreach(string newline in new[]{"\n","\r\n"})
        {
            string prefix=(padding==0?"":"--"+new string(' ',padding)+newline)+"SELECT 1;"+newline+"GO"+newline+"SELECT '"+newline;
            byte[] valid=ImportBytes(prefix,encoding);
            bool utf8=encoding.CodePage==65001, little=encoding.CodePage==1200;
            byte[][] invalid=utf8 ? new[]{new byte[]{0xff},new byte[]{0xe2,0x82}} : little ? new[]{new byte[]{0x00,0xdc},new byte[]{0x00,0xd8},new byte[]{0x41}} : new[]{new byte[]{0xdc,0x00},new byte[]{0xd8,0x00},new byte[]{0x41}};
            foreach(byte[] suffix in invalid)
            using(var stream=new MemoryStream(valid.Concat(suffix).ToArray()))
            using(var reader=new MssqlGoBatchReader(stream,buffer))
            {
                try {reader.ReadBatches(CancellationToken.None).ToArray();Assert(false,"Malformed encoding must reject after valid GO prefix.");}
                catch(MssqlImportInputException ex)
                {
                    Assert(ex.Code=="InvalidEncoding" && ex.BatchNumber==2 && ex.Line==(padding==0?4:5) && ex.ByteOffset==valid.LongLength,
                        "Decoder error must preserve actual batch/line/raw byte coordinates: "+encoding.CodePage+" buffer="+buffer+" padding="+padding+" got "+ex.Message);
                }
            }
        }
        using(var input = ImportInput("-- only\r\n/* nested /* ok */ end */\r\nGO\r\n-- tail"))
        {
            Assert(input.Scan(CancellationToken.None,null).BatchCount==0, "Only comments never become executable commands.");
            Assert(!input.ReadBatches(CancellationToken.None).Any() && input.ExecutionSha256!=null, "Comment-only EOF still finishes execution digest.");
        }
    }

    private static void MssqlImportParserLimits()
    {
        const int limit = 16777216;
        string sql = "SELECT '" + new string('x',limit-10) + "';";
        Assert(sql.Length==limit, "Fixture reaches the documented code-unit boundary.");
        using(var input=ImportInput(sql+"\r\nGO\r\n"))
        {
            // The CRLF preceding GO is SQL text and counts toward the batch budget.
            ImportReject(input,"BatchTooLarge");
        }
        using(var input=ImportInput(sql))
        {
            var scan=input.Scan(CancellationToken.None,null);
            Assert(scan.BatchCount==1 && scan.MaxBatchCodeUnits==limit,"Exactly 16,777,216 code units scan successfully.");
        }
        using(var input=ImportInput(sql+" ")) ImportReject(input,"BatchTooLarge");
        string framed = "SELECT '" + new string('x',limit-12) + "';\r\n";
        using(var input=ImportInput(framed+"GO --delimiter\r\n"))
            Assert(input.Scan(CancellationToken.None,null).MaxBatchCodeUnits==limit,"Exact cap remains valid with a GO delimiter excluded from SQL.");
        using(var input=ImportInput("SELECT 1;\n"+new string(' ',limit+5)+"GO --"+new string('x',limit+5)+"\nSELECT 2;"))
            Assert(input.Scan(CancellationToken.None,null).BatchCount==2,"Oversized GO whitespace/comment candidates are discarded without losing commands.");
        using(var input=ImportInput(new string(' ',limit+5)+"SELECT 1;")) ImportReject(input,"BatchTooLarge");
        using(var input=ImportInput("--"+new string(' ',limit-2)))
            Assert(input.Scan(CancellationToken.None,null).BatchCount==0,"Huge single comment line is bounded without an empty command.");
    }

    private static void MssqlImportParserUnsupported()
    {
        foreach(string sql in new[]{"SELECT 1\nGO 2\n","SELECT 1\nGO /*comment*/\n","SELECT 1\nGO--ok\n:r file.sql","/*ok*/ :setvar a b","!! whoami","SELECT 1; USE [other];","USE [transfer];"})
            using(var input=ImportInput(sql,"Transfer")) ImportReject(input,sql.Contains("GO 2")||sql.Contains("GO /*")?"UnsupportedGo":sql.Contains("USE")?"DatabaseChange":"SqlCmdUnsupported");
        using(var input=ImportInput("/* same-line comment */ GO\n")) ImportReject(input,"UnsupportedGo");
        foreach(string sql in new[]{"SELECT '\nGO","SELECT [abc","SELECT \"abc","/* /* nested */"})
            using(var input=ImportInput(sql)) ImportReject(input,"UnclosedContext");
        using(var input=ImportInput("USE [Tran]]sfer]; SELECT ':r file.sql GO 2 !!'; -- USE other\n","Tran]sfer"))
            Assert(input.Scan(CancellationToken.None,null).BatchCount==1,"Exact decoded same-database USE succeeds; string/comment directives are inert.");
        using(var input=ImportInput("USE \"Transfer\";", "Transfer")) Assert(input.Scan(CancellationToken.None,null).BatchCount==1,"Double-quoted exact USE succeeds.");
        using(var input=ImportInput("USE Transfer;")) ImportReject(input,"DatabaseChange");
        foreach(Encoding encoding in new Encoding[]{new UTF8Encoding(true,true),new UnicodeEncoding(false,true,true),new UnicodeEncoding(true,true,true)})
        {
            string prefix="SELECT N'\u7fbd\U0001f600';\r\n";
            using(var input=ImportInput(ImportBytes(prefix+"USE [other];",encoding),"Transfer"))
            {
                try {input.Scan(CancellationToken.None,null);Assert(false,"Cross-database USE must reject.");}
                catch(MssqlImportInputException ex) {Assert(ex.Code=="DatabaseChange" && ex.Line==2 && ex.ByteOffset==ImportBytes(prefix+"USE ",encoding).LongLength,"Error source byte/line coordinates use original encoding and BOM.");}
            }
        }
        foreach(byte[] bytes in new[]{new byte[]{0xef,0xbb},new byte[]{0xc0,0xaf},new byte[]{0xe2,0x82},new byte[]{0xff,0xfe,0x00},new byte[]{0xff,0xfe,0x00,0xd8},new byte[]{0xfe,0xff,0xdc,0x00},new byte[]{0xff,0xfe,0,0,0,0,0,0}})
            using(var input=ImportInput(bytes)) ImportReject(input,"InvalidEncoding");
        foreach(byte[] bytes in new[]{new byte[]{0x53,0,0x45,0},ImportBytes("SELECT '\0';",new UTF8Encoding(false,true)),ImportBytes("SELECT '\0';",new UnicodeEncoding(false,true,true))})
            using(var input=ImportInput(bytes)) ImportReject(input,"EmbeddedNul");
        foreach(string sql in new[]{"SELECT col$(suffix) FROM dbo.t;","SELECT dbo.table$(suffix).col FROM dbo.t;","USE Transfer$(suffix);","SELECT @col$(suffix);","SELECT #tmp$(suffix);","SELECT "+new string('x',600)+"$(suffix);","SELECT 1;\nGO\nSELECT col$(suffix);"})
            using(var input=ImportInput(sql,"Transfer")) ImportReject(input,"SqlCmdUnsupported");
        using(var input=ImportInput("SELECT N'col$(suffix)', \"col$(suffix)\", [col$(suffix)], [a]]$(suffix)]; -- col$(suffix)\n/* outer /* col$(suffix) */ end */"))
            Assert(input.Scan(CancellationToken.None,null).Script.Kind==MssqlImportScriptKind.Generic,"Embedded SQLCMD text remains inert in all protected lexical contexts.");
        using(var input=ImportInput("SELECT '$(name) :connect !!';")) Assert(input.Scan(CancellationToken.None,null).BatchCount==1,"No SQLCMD expansion in strings.");
    }

    private const string ImportJob="123456781234123412341234567890ab";
    private const string ImportHeader="-- Source read mode: READ COMMITTED\r\n";
    private const string ImportHint="-- Restore into an empty, compatible target selected by the caller; existing objects fail.\r\n";
    private const string ImportFooter="-- mysqlpunk-export-complete job="+ImportJob+" tables=1 rows=2\r\n";
    private static void MssqlImportParserNative()
    {
        string schema=ImportHeader+ImportHint+"CREATE TABLE [dbo].[x] ([a] int);\r\nGO\r\n"+ImportFooter;
        using(var input=ImportInput(schema))
        {
            var script=input.Scan(CancellationToken.None,null).Script;
            Assert(script.Kind==MssqlImportScriptKind.NativeSchema && script.NativeJobId==Guid.ParseExact(ImportJob,"N") && script.ExpectedTables==1 && script.ExpectedRows==2 && !script.HasLobProtocol && script.SourceReadMode=="READ COMMITTED","Native schema metadata is exact without requiring LOB.");
        }
        using(var input=ImportInput(ImportHeader+"INSERT INTO dbo.x VALUES(1);\r\nGO\r\n"+ImportFooter))
            Assert(input.Scan(CancellationToken.None,null).Script.Kind==MssqlImportScriptKind.NativeData,"Existing DataOnly header is accepted without a new mode manifest.");
        using(var input=ImportInput(ImportHeader+"CREATE TABLE #mp_"+ImportJob+"_s ([Sequence] bigint);\r\nGO\r\n"+ImportFooter))
            Assert(input.Scan(CancellationToken.None,null).Script.HasLobProtocol,"Actual validated native temporary identifiers mark LOB.");
        foreach(string sql in new[]{ImportHeader+ImportHint+"CREATE TABLE x(a int);",schema+ImportFooter,schema.Replace("rows=2","rows=9223372036854775808"),schema.Replace("tables=1","tables=-1"),schema.Replace(ImportJob,"bad"),ImportFooter,ImportHeader+"CREATE TABLE x(a int);\nGO\n"+ImportFooter,ImportHeader+ImportHeader+ImportFooter,ImportHeader.Replace("READ COMMITTED","unknown")+ImportFooter,ImportHeader+"CREATE TABLE #mp_ffffffffffffffffffffffffffffffff_s (a int);\nGO\n"+ImportFooter,ImportHeader+"CREATE TABLE #mp_bad_s (a int);\nGO\n"+ImportFooter,"CREATE TABLE #mp_"+ImportJob+"_s (a int);"})
            using(var input=ImportInput(sql)) ImportReject(input,"NativeFormat");
        using(var input=ImportInput("SELECT '-- Source read mode: READ COMMITTED', '-- mysqlpunk-export-complete job=bad', [#mp_bad_s];\nGO\n-- regular comment"))
            Assert(input.Scan(CancellationToken.None,null).Script.Kind==MssqlImportScriptKind.Generic,"String/ordinary identifier decoys do not create native markers.");
        using(var input=ImportInput("SELECT 1;\n")) Assert(input.Scan(CancellationToken.None,null).Script.Kind==MssqlImportScriptKind.Generic,"Generic SQL requires no native footer.");
        using(var input=ImportInput("-- Source read mode READ COMMITTED\n")) ImportReject(input,"NativeFormat");
        string emptyFooter=ImportFooter.Replace("tables=1 rows=2","tables=0 rows=0");
        using(var input=ImportInput(ImportHeader+ImportHint+emptyFooter))
            Assert(input.Scan(CancellationToken.None,null).Script.Kind==MssqlImportScriptKind.NativeSchema,"Existing zero-object schema export remains valid.");
        using(var input=ImportInput(ImportHeader.Replace("READ COMMITTED","Schema only (no data transaction)")+ImportHint+"CREATE TABLE x(a int);\nGO\n"+ImportFooter.Replace("rows=2","rows=0")))
            Assert(input.Scan(CancellationToken.None,null).Script.Kind==MssqlImportScriptKind.NativeSchema,"SchemaOnly source mode and zero row footer are coherent.");
        foreach(string malformed in new[]{ImportHeader+ImportHint+"SELECT 'CREATE TABLE x(a int)';\nGO\n"+ImportFooter,"CREATE TABLE x(a int);\nGO\n"+ImportHeader+ImportHint+ImportFooter,ImportHeader.Replace("READ COMMITTED","Schema only (no data transaction)")+ImportHint+"CREATE TABLE x(a int); INSERT INTO x VALUES(1);\nGO\n"+ImportFooter})
            using(var input=ImportInput(malformed)) ImportReject(input,"NativeFormat");
    }

    private static void MssqlImportParserFileIdentity()
    {
        byte[] bytes=ImportBytes("SELECT N'羽😀';\r\nGO\r\n-- footer only\r\n",new UTF8Encoding(true,true));
        string path=ImportFixture(bytes);
        try
        {
            using(var input=MssqlImportInput.Open(path))
            {
                long progress=0;
                var scan=input.Scan(CancellationToken.None,p=>{Assert(p>=progress&&p<=bytes.LongLength,"Scan progress is monotonic bytes.");progress=p;});
                string expected;
                using(var hash=SHA256.Create()) expected=BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();
                Assert(scan.Sha256==expected && scan.FileBytes==bytes.LongLength && progress==bytes.LongLength,"Preflight hashes all original bytes including BOM and comment tail.");
                Assert(input.ExecutionSha256==null,"Scan cannot claim execution digest.");
                var commands=input.ReadBatches(CancellationToken.None).ToArray();
                Assert(commands.Length==1 && commands[0].EndByte<scan.FileBytes && input.ExecutionSha256==scan.Sha256,"Tail bytes are EOF-only coverage, never an empty command.");
                AssertThrows<IOException>(()=>{using(var writer=new FileStream(path,FileMode.Open,FileAccess.Write,FileShare.ReadWrite)){}},"Held input blocks general writers.");
                AssertThrows<IOException>(()=>File.Delete(path),"Held input blocks delete.");
            }
            using(var writer=new FileStream(path,FileMode.Open,FileAccess.Write,FileShare.None)) writer.WriteByte(0x20);
        }
        finally {File.Delete(path);}
        using(var input=ImportInput("SELECT 1;\nGO\nSELECT 2;"))
        {
            var cancel=new CancellationTokenSource();cancel.Cancel();
            AssertThrows<OperationCanceledException>(()=>input.Scan(cancel.Token,null),"Preflight cancellation performs no full scan.");
            input.Scan(CancellationToken.None,null);
            using(var enumerator=input.ReadBatches(CancellationToken.None).GetEnumerator()) {Assert(enumerator.MoveNext(),"First command exists.");}
            Assert(input.ExecutionSha256==null,"Abandoned enumeration never claims EOF digest.");
        }
    }

    private static byte[] ImportBytes(string sql,Encoding encoding) {return encoding.GetPreamble().Concat(encoding.GetBytes(sql)).ToArray();}
    private static string ImportFixture(byte[] bytes) {string path=Path.Combine(Path.GetTempPath(),"mysqlpunk-import-parser-"+Guid.NewGuid().ToString("N")+".sql");File.WriteAllBytes(path,bytes);return path;}
    private static ImportParserFixture ImportInput(string sql,string database=null) {return ImportInput(new UTF8Encoding(false,true).GetBytes(sql),database);}
    private static ImportParserFixture ImportInput(byte[] bytes,string database=null) {return new ImportParserFixture(bytes,database);}
    private sealed class ImportParserFixture : IDisposable
    {
        private readonly string path;
        private readonly MssqlImportInput input;
        internal ImportParserFixture(byte[] bytes,string database) {path=ImportFixture(bytes);try {input=MssqlImportInput.Open(path,database);}catch {File.Delete(path);throw;}}
        internal MssqlImportScan Scan(CancellationToken token,Action<long> progress) {return input.Scan(token,progress);}
        internal IEnumerable<MssqlImportBatch> ReadBatches(CancellationToken token) {return input.ReadBatches(token);}
        internal string ExecutionSha256 {get {return input.ExecutionSha256;}}
        public void Dispose() {input.Dispose();File.Delete(path);}
    }
    private static void ImportReject(ImportParserFixture input,string code)
    {
        try {input.Scan(CancellationToken.None,null);Assert(false,"Expected preflight rejection: "+code);}
        catch(MssqlImportInputException ex) {Assert(ex.Code==code,"Stable preflight code: expected "+code+", got "+ex.Code);Assert(ex.Line>=1&&ex.ByteOffset>=0&&ex.BatchNumber>=1,"Rejected input has source coordinates.");}
    }
}
