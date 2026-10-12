using System;
using System.Collections;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using mySQLPunk.lib.MssqlExport;

public static partial class SmokeTests
{
    // Regressions: default missing implementation, backward handoff reads, duplicate/lost
    // prefix bytes, unbounded chunks, and premature target INSERT.
    private static void Lob_LazyLargeRow()
    {
        var lengths = new long[] { 64L*1024*1024, 18L*1024*1024+6, 0, -1 };
        using(var reader = new LobReader(lengths)) using(var sink = new LobSink(true)) {
            long source=0; int during=0;
            var table=ValueTable(ValueColumn("image",2,"b"),ValueColumn("ntext",7,"u"),ValueColumn("varbinary",11,"empty"),ValueColumn("nvarchar",19,"nil"));
            Assert(new MssqlDataScriptWriter().WriteTable(reader,table,sink,(r,b)=>{Assert(b>=source,"Monotonic source progress");source=b;if(r==0 && b>0 && b<lengths[0])during++;},CancellationToken.None)==1,"Large row restored once");
            Assert(during>10,"Progress while the first large row is still incomplete");
            Assert(source==lengths[0]+lengths[1],"Exact source-byte count across handoff");
            Assert(reader.ReadCalls==2 && reader.TotalBytes==source,"One sequential source pass");
            Assert(sink.Inserts==1 && sink.MaxStatement<=262144 && sink.MaxBatch<=4194304,"Bounded statements and exactly one target insert");
            Assert(sink.BinaryBytes==lengths[0] && sink.UnicodeBytes==lengths[1],"Every retained and new byte emitted exactly once");
        }
    }
    private static void Lob_ContinuationBranches()
    {
        var adapter=new LobContinuationObserver();
        using(var reader=new ValueReader(new[]{new object[]{new byte[63460],9}})) using(var sink=new LobSink()) {
            new MssqlDataScriptWriter(adapter).WriteTable(reader,ValueTable(ValueColumn("varbinary"),ValueColumn("int",7,"tail")),sink,null,CancellationToken.None);
            Assert(adapter.Untouched && sink.Inserts==1 && sink.BinaryBytes==63460,"Untouched current field is consumed once");
        }
        // SQL Server permits 1024 columns and 128 Unicode characters per identifier.
        // The legal column list alone exceeds the data statement limit; values stay staged.
        foreach(int count in new[]{512,1024}) {
        var columns=Enumerable.Range(0,count).Select(i=>ValueColumn(i==0 ? "varbinary" : "int",i*2+1,new string('\u4e2d',123)+i.ToString("D4"))).ToArray();
        var data=Enumerable.Range(0,count).Select(i=>i==0 ? (object)new byte[40000] : i).ToArray();
        adapter=new LobContinuationObserver();
        using(var reader=new ValueReader(new[]{data})) using(var sink=new LobSink()) {
            new MssqlDataScriptWriter(adapter).WriteTable(reader,ValueTable(columns),sink,null,CancellationToken.None);
            Assert((count==512 ? adapter.Completed : adapter.Untouched) && sink.Inserts==1 && sink.BinaryBytes==40000,"Completed or oversized header handoff never rereads source");
            Assert(sink.MaxBatch<=4194304,"Metadata-only insert fits bounded batch");
        }
        }
    }
    private sealed class LobContinuationObserver : IMssqlLargeRowWriter
    {
        private readonly MssqlLobScriptWriter writer=new MssqlLobScriptWriter(Guid.NewGuid());
        internal bool Untouched,Completed;
        public long WriteRow(DbDataReader reader,MssqlTableDefinition table,IReadOnlyList<MssqlColumnDefinition> columns,MssqlRowContinuation row,MssqlScriptBatchWriter output,CancellationToken cancellation,Action<long> onAdditionalBytes) {
            Untouched=!row.CurrentValueStarted;Completed=row.CurrentValueComplete;
            Assert(row.BufferBytes<=262144,"Small row buffer bounded");
            return writer.WriteRow(reader,table,columns,row,output,cancellation,onAdditionalBytes);
        }
    }
    private static void Lob_Utf16AndCancel()
    {
        using(var reader=new LobReader(new long[]{2000078})) using(var sink=new LobSink(true)) {
            new MssqlDataScriptWriter().WriteTable(reader,ValueTable(ValueColumn("nvarchar")),sink,null,CancellationToken.None);
            Assert(sink.UnicodeBytes==2000078 && sink.Inserts==1,"Partial UTF16 handoff has no data loss");
        }
        using(var cancel=new CancellationTokenSource()) using(var reader=new LobReader(new long[]{64L*1024*1024},cancel)) using(var sink=new LobSink()) {
            AssertThrows<OperationCanceledException>(()=>new MssqlDataScriptWriter().WriteTable(reader,ValueTable(ValueColumn("varbinary")),sink,null,cancel.Token),"Cancel stops streaming current LOB");
            Assert(sink.Inserts==0 && reader.TotalBytes<64L*1024*1024,"Canceled row cannot be inserted");
        }
    }
    // Keep only one bounded generated statement, never the large SQL document.
    private sealed class LobSink : TextWriter
    {
        private readonly StringBuilder line=new StringBuilder(); private int batch;
        private readonly bool verifyPattern; private readonly Dictionary<string,long> offsets=new Dictionary<string,long>();
        private readonly Dictionary<string,int> lastUnit=new Dictionary<string,int>();
        internal LobSink(bool verifyPattern=false){this.verifyPattern=verifyPattern;}
        internal long BinaryBytes,UnicodeBytes; internal int Inserts,MaxStatement,MaxBatch;
        public override Encoding Encoding { get { return new UTF8Encoding(false,true); } }
        public override void Write(char value) { if(value=='\n') { Consume(line.ToString().TrimEnd('\r'));line.Clear(); } else line.Append(value); }
        public override void Write(string value) { foreach(char c in value) Write(c); }
        public override void Write(char[] value,int start,int count) { for(int i=0;i<count;i++) Write(value[start+i]); }
        private void Consume(string sql) {
            int bytes=Encoding.GetByteCount(sql+"\r\n");
            if(sql=="GO") { MaxBatch=Math.Max(MaxBatch,batch+bytes);Assert(batch+bytes<=4194304,"GO budget");batch=0;return; }
            bool metadata=sql.Contains("CREATE TABLE") || sql.Contains("INSERT INTO [dbo].[t]");
            if(!metadata)MaxStatement=Math.Max(MaxStatement,bytes);
            batch+=bytes;Assert(bytes<=(metadata ? 2097152 : 262144),"Statement byte budget");
            if(sql.Contains("INSERT INTO [dbo].[t]")) Inserts++;
            int at=sql.IndexOf(".WRITE(",StringComparison.Ordinal);
            if(at<0)return;
            bool unicode=sql.Substring(at).StartsWith(".WRITE(CONVERT(nvarchar(max),0x",StringComparison.Ordinal);
            int hex=sql.IndexOf("0x",at,StringComparison.Ordinal)+2,end=hex;
            while(end<sql.Length && Uri.IsHexDigit(sql[end]))end++;
            int count=(end-hex)/2;
            Assert(count<=65536,"Binary chunk bound");
            if(unicode) { Assert(count%2==0 && count<=32768,"UTF16 code-unit bound");UnicodeBytes+=count; }
            else BinaryBytes+=count;
            if(verifyPattern) {
                int fieldStart=sql.LastIndexOf("[c",at,StringComparison.Ordinal);string field=sql.Substring(fieldStart,at-fieldStart);
                long offset;offsets.TryGetValue(field,out offset);
                byte[] expected={0x3d,0xd8,0x00,0xde,0,0,0x0a,0,0x20,0,0,0xd8,0x41,0};
                int first=0,last=0;
                for(int j=0;j<count;j++) {
                    int value=HexDigit(sql[hex+j*2])*16+HexDigit(sql[hex+j*2+1]);
                    Assert(value==expected[(offset+j)%expected.Length],"Exact native bytes across chunk/handoff boundary");
                    if(j<2)first|=value<<(j*8);if(j>=count-2)last|=value<<((j-(count-2))*8);
                }
                int prior;
                if(unicode && lastUnit.TryGetValue(field,out prior))Assert(!(prior>=0xd800 && prior<=0xdbff && first>=0xdc00 && first<=0xdfff),"No split surrogate pair");
                lastUnit[field]=last;offsets[field]=offset+count;
            }
        }
        private static int HexDigit(char value){return value<='9' ? value-'0' : value-'A'+10;}
    }
    private sealed class LobReader : DbDataReader
    {
        private readonly long[] lengths; private readonly CancellationTokenSource cancel;
        private int ordinal=-1; private long offset; internal long TotalBytes; internal int ReadCalls;
        internal LobReader(long[] lengths,CancellationTokenSource cancel=null){this.lengths=lengths;this.cancel=cancel;}
        private void At(int i){if(i<ordinal)throw new InvalidOperationException("Backward column");if(i>ordinal){ordinal=i;offset=0;}}
        public override bool Read(){ReadCalls++;ordinal=-1;offset=0;return ReadCalls==1;}
        public override bool IsDBNull(int i){At(i);return lengths[i]<0;}
        public override long GetBytes(int i,long start,byte[] buffer,int index,int count){
            At(i);if(buffer==null || start!=offset || count>65536)throw new InvalidOperationException("Nonsequential or unbounded read");
            int n=(int)Math.Min(count,lengths[i]-start);
            // Repeated valid surrogate pair, NUL, control, spaces, then unpaired high surrogate.
            byte[] pattern={0x3d,0xd8,0x00,0xde,0,0,0x0a,0,0x20,0,0,0xd8,0x41,0};
            for(int j=0;j<n;j++)buffer[index+j]=pattern[(start+j)%pattern.Length];
            offset+=n;TotalBytes+=n;if(cancel!=null && TotalBytes>200000)cancel.Cancel();return n;
        }
        public override object GetValue(int i){throw new InvalidOperationException("Whole LOB");}
        public override string GetString(int i){throw new InvalidOperationException("Whole string");}
        public override long GetChars(int i,long a,char[] b,int c,int d){throw new NotSupportedException();}
        public override int GetInt32(int i){throw new NotSupportedException();}
        public override bool GetBoolean(int i){throw new NotSupportedException();}
        public override byte GetByte(int i){throw new NotSupportedException();}
        public override short GetInt16(int i){throw new NotSupportedException();}
        public override long GetInt64(int i){throw new NotSupportedException();}
        public override decimal GetDecimal(int i){throw new NotSupportedException();}
        public override float GetFloat(int i){throw new NotSupportedException();}
        public override double GetDouble(int i){throw new NotSupportedException();}
        public override Guid GetGuid(int i){throw new NotSupportedException();}
        public override DateTime GetDateTime(int i){throw new NotSupportedException();}
        public override char GetChar(int i){throw new NotSupportedException();}
        public override int FieldCount{get{return lengths.Length;}}
        public override bool HasRows{get{return true;}}
        public override bool IsClosed{get{return false;}}
        public override int RecordsAffected{get{return -1;}}
        public override int Depth{get{return 0;}}
        public override object this[int i]{get{return GetValue(i);}}
        public override object this[string name]{get{throw new NotSupportedException();}}
        public override string GetName(int i){throw new NotSupportedException();}
        public override int GetOrdinal(string name){throw new NotSupportedException();}
        public override string GetDataTypeName(int i){throw new NotSupportedException();}
        public override Type GetFieldType(int i){throw new NotSupportedException();}
        public override int GetValues(object[] values){throw new NotSupportedException();}
        public override IEnumerator GetEnumerator(){throw new NotSupportedException();}
        public override bool NextResult(){return false;}
    }
}
