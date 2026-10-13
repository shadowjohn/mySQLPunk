using System;
using System.Collections;
using System.Collections.Generic;
using System.Data.Common;
using System.Data.SqlTypes;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using mySQLPunk.lib.MssqlExport;

public static partial class SmokeTests
{
    private static MssqlColumnDefinition ValueColumn(string type, int id = 1, string name = "v", bool identity = false)
    { return new MssqlColumnDefinition(new Dictionary<string, object> { { "type_name", type }, { "column_id", id }, { "name", name }, { "is_identity", identity }, { "scale", 7 }, { "max_length", -1 } }); }
    private static MssqlTableDefinition ValueTable(params MssqlColumnDefinition[] columns)
    { return new MssqlTableDefinition(new MssqlObjectKey(1, "dbo", "t"), new Dictionary<string, object>(), columns, null, null); }
    private static void Values_Geometry()
    {
        var g = ValueColumn("geometry", 1, "g]x");
        AssertEquals("CONVERT(varbinary(max),[g]]x].Serialize())", MssqlValueCodec.SourceExpression(g), "Escaped source internal binary serialization");
        using (var reader = new ValueReader(new[] { new object[] { new byte[] { 1, 0, 255 } }, new object[] { null }, new object[] { new byte[0] } }))
        using (var output = new StringWriter()) {
            long bytes = -1;
            Assert(new MssqlDataScriptWriter().WriteTable(reader, ValueTable(g), output, (r,b) => bytes=b, CancellationToken.None) == 3, "Inline geometry rows");
            string sql = output.ToString();
            Assert(sql.Contains("VALUES (geometry::Deserialize(0x0100FF));") && sql.Contains("VALUES (NULL);") && sql.Contains("VALUES (geometry::Deserialize(0x));"), "Deserialize bytes; SQL NULL distinct from empty byte payload");
            Assert(bytes == 3, "Projected geometry byte progress");
        }
    }

    private static void Values_Scalars()
    {
        var previous = CultureInfo.CurrentCulture;
        try {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            AssertEquals("[a]]b]", MssqlValueCodec.QuoteIdentifier("a]b"), "Escape identifiers");
            AssertEquals("1234567890123456789012345678.1234567890", MssqlValueCodec.FormatScalar(SqlDecimal.Parse("1234567890123456789012345678.1234567890"), ValueColumn("decimal")), "38 decimal digits");
            AssertEquals("12.3400", MssqlValueCodec.FormatScalar(new SqlMoney(12.34m), ValueColumn("money")), "Money precision");
            AssertEquals("'2026-10-12T03:04:05.1234567+08:00'", MssqlValueCodec.FormatScalar(new DateTimeOffset(2026,10,12,3,4,5,TimeSpan.FromHours(8)).AddTicks(1234567), ValueColumn("datetimeoffset")), "Offset and 7 digits");
            AssertEquals("'03:04:05.1234567'", MssqlValueCodec.FormatScalar(new TimeSpan(0,3,4,5).Add(TimeSpan.FromTicks(1234567)), ValueColumn("time")), "Time ticks");
            AssertEquals("'2026-10-12'", MssqlValueCodec.FormatScalar(new DateTime(2026,10,12), ValueColumn("date")), "ISO date");
            AssertEquals("'2026-10-12T03:04:05.1234567'", MssqlValueCodec.FormatScalar(new DateTime(2026,10,12,3,4,5).AddTicks(1234567), ValueColumn("datetime2")), "Date ticks");
            AssertEquals("'2026-10-12T03:04:05.123'", MssqlValueCodec.FormatScalar(new DateTime(2026,10,12,3,4,5).AddMilliseconds(123), ValueColumn("datetime")), "Legacy datetime literal uses SQL-compatible 3 digits");
            AssertEquals("'2026-10-12T03:04:00.000'", MssqlValueCodec.FormatScalar(new DateTime(2026,10,12,3,4,0), ValueColumn("smalldatetime")), "Legacy smalldatetime literal uses SQL-compatible 3 digits");
            foreach (var legacy in new[]{new DateTime(1753,1,1),new DateTime(2026,10,12,3,4,5).AddMilliseconds(3),new DateTime(2026,10,12,3,4,5).AddMilliseconds(7),new DateTime(9999,12,31,23,59,59).AddMilliseconds(997)}) {
                var stored=new SqlDateTime(legacy);
                string literal=MssqlValueCodec.FormatScalar(stored.Value,ValueColumn("datetime")).Trim('\'');
                Assert(stored.Equals(new SqlDateTime(DateTime.ParseExact(literal,"yyyy-MM-dd'T'HH:mm:ss.fff",CultureInfo.InvariantCulture))),"Legacy datetime stored ticks survive 3-digit serialization");
            }
            AssertEquals("'00112233-4455-6677-8899-aabbccddeeff'", MssqlValueCodec.FormatScalar(new Guid("00112233-4455-6677-8899-aabbccddeeff"), ValueColumn("uniqueidentifier")), "GUID");
            double number = 1.2345678901234567;
            Assert(double.Parse(MssqlValueCodec.FormatScalar(number, ValueColumn("float")), CultureInfo.InvariantCulture) == number, "Float roundtrip");
            AssertEquals("NULL", MssqlValueCodec.FormatScalar(DBNull.Value, ValueColumn("int")), "Null");
        } finally { CultureInfo.CurrentCulture = previous; }
    }
    private static void Values_ProjectionAndStreaming()
    {
        Values_TinyLegacyText();
        AssertEquals("CONVERT(varbinary(max),CONVERT(varchar(max),[v]))", MssqlValueCodec.SourceExpression(ValueColumn("text")), "Legacy ANSI bytes");
        AssertEquals("CONVERT(varbinary(max),CONVERT(nvarchar(max),[v]))", MssqlValueCodec.SourceExpression(ValueColumn("xml")), "XML serialized UTF16");
        var data = new byte[] { 0x47,0,0x4f,0,0x0a,0,0x3a,0,0x72,0,0,0,0x24,0,0x28,0,0x78,0,0x29,0,0,0xd8 };
        var table = ValueTable(ValueColumn("int", 2,"id",true), ValueColumn("nvarchar",7,"u"),ValueColumn("varchar",8,"a"),ValueColumn("xml",12,"x"),ValueColumn("decimal",15,"d"));
        using(var reader = new ValueReader(new object[][] { new object[] { 4,data,new byte[]{255,0},new byte[]{60,0,114,0,47,0,62,0},SqlDecimal.Parse("1234567890123456789012345678.1234567890") } }))
        using(var output = new StringWriter(CultureInfo.InvariantCulture)) {
            long progressRows=0, progressBytes=0;
            Assert(new MssqlDataScriptWriter().WriteTable(reader,table,output,(r,b)=>{ progressRows=r; progressBytes=b; },CancellationToken.None)==1,"One row");
            string sql=output.ToString();
            Assert(sql.Contains("SET IDENTITY_INSERT [dbo].[t] ON;") && sql.Contains("SET IDENTITY_INSERT [dbo].[t] OFF;"),"Identity lifetime");
            Assert(sql.Contains("CONVERT(nvarchar(max),0x47004F000A003A0072000000240028007800290000D8)"),"Exact UTF16 including unpaired surrogate");
            Assert(sql.Contains(",0xFF00,CONVERT(xml,CONVERT(nvarchar(max),0x3C0072002F003E00),1),"),"Direct ANSI binary and XML style1");
            Assert(sql.Contains("1234567890123456789012345678.1234567890"),"Provider decimal path");
            Assert(!sql.Contains("$(") && !sql.Contains(":r"),"Data cannot escape literal");
            Assert(progressRows==1 && progressBytes==53,"Source bytes count includes scalars and raw projection");
        }
        using(var reader = new ValueReader(new object[][] { new object[]{null,new byte[0]},new object[]{new byte[0],null} }))
        using(var output = new StringWriter()) {
            new MssqlDataScriptWriter().WriteTable(reader,ValueTable(ValueColumn("varchar",1,"a"),ValueColumn("nvarchar",2,"b")),output,null,CancellationToken.None);
            Assert(output.ToString().Contains("VALUES (NULL,CONVERT(nvarchar(max),0x));") && output.ToString().Contains("VALUES (0x,NULL);"),"Empty differs from NULL");
        }
    }
    private static void Values_TinyLegacyText()
    {
        var text = new MssqlColumnDefinition(new Dictionary<string,object> { {"type_name","text"}, {"column_id",1}, {"name","v"}, {"collation_name","Chinese_Taiwan_Stroke_CI_AS"}, {"max_length",-1} });
        using(var reader = new ValueReader(new[] { new object[] { new byte[] { 0xA4,0xA4,0,32,32 } }, new object[] { new byte[0] }, new object[] { null } }))
        using(var output = new StringWriter()) {
            long bytes=0;
            Assert(new MssqlDataScriptWriter().WriteTable(reader,ValueTable(text),output,(r,b)=>bytes=b,CancellationToken.None)==3,"Tiny legacy text rows consumed once");
            string sql=output.ToString();
            Assert(sql.Contains("varchar(max) COLLATE Chinese_Taiwan_Stroke_CI_AS") && sql.Contains(".WRITE(0xA4A4002020,"),"Tiny text uses typed raw-byte staging instead of illegal varbinary assignment");
            Assert(bytes==5,"Legacy text exact bytes, empty and NULL accounting");
        }
    }
    private static void Values_BudgetsAndHandoff()
    {
        var rows=Enumerable.Range(0,80).Select(i=>new object[]{new byte[40000]}).ToArray();
        using(var reader=new ValueReader(rows)) using(var output=new StringWriter()) {
            Assert(new MssqlDataScriptWriter().WriteTable(reader,ValueTable(ValueColumn("varbinary")),output,null,CancellationToken.None)==80,"Batch rows");
            foreach(string batch in output.ToString().Split(new[]{"GO\r\n"},StringSplitOptions.RemoveEmptyEntries)) {
                Assert(Encoding.UTF8.GetByteCount(batch)<=4194304,"Actual batch budget");
                foreach(string statement in batch.Split(new[]{"\r\n"},StringSplitOptions.RemoveEmptyEntries)) Assert(Encoding.UTF8.GetByteCount(statement+"\r\n")<=262144,"Actual statement budget");
            }
        }
        using(var reader=new ValueReader(new[]{new object[]{5,new byte[300000],9}})) using(var output=new StringWriter()) {
            var sink=new ValueContinuationSink();
            Assert(new MssqlDataScriptWriter(sink).WriteTable(reader,ValueTable(ValueColumn("int",1,"a"),ValueColumn("varbinary",4,"b"),ValueColumn("int",9,"c")),output,null,CancellationToken.None)==1,"Handoff finishes source row");
            Assert(sink.Bytes==300000 && sink.PrefixValue=="5" && sink.Tail==9,"Handoff consumes retained prefix and forward-only remainder");
        }
        using(var reader=new ValueReader(new[]{new object[]{new byte[300000]}})) using(var output=new StringWriter())
            Assert(new MssqlDataScriptWriter().WriteTable(reader,ValueTable(ValueColumn("varbinary")),output,null,CancellationToken.None)==1,"Default large support");
    }
    private static void Values_DefaultAndCancel()
    {
        using(var reader=new ValueReader(new[]{new object[]{true},new object[]{true}})) using(var output=new StringWriter()) {
            Assert(new MssqlDataScriptWriter().WriteTable(reader,ValueTable(),output,null,CancellationToken.None)==2,"Default rows");
            Assert(output.ToString().Split(new[]{"INSERT INTO [dbo].[t] DEFAULT VALUES;"},StringSplitOptions.None).Length==3,"Each default row");
        }
        var computed=new MssqlColumnDefinition(new Dictionary<string,object>{{"type_name","int"},{"column_id",1},{"name","computed"},{"is_computed",true}});
        using(var reader=new ValueReader(new[]{new object[]{7}})) using(var output=new StringWriter()) {
            new MssqlDataScriptWriter().WriteTable(reader,ValueTable(computed,ValueColumn("rowversion",2,"rv"),ValueColumn("int",8,"kept")),output,null,CancellationToken.None);
            Assert(output.ToString().Contains("([kept]) VALUES (7);") && !output.ToString().Contains("[computed]") && !output.ToString().Contains("[rv]"),"Exclude computed and rowversion; contiguous projection ordinals");
        }
        using(var reader=new ValueReader(new object[0][])) using(var output=new StringWriter()) Assert(new MssqlDataScriptWriter().WriteTable(reader,ValueTable(ValueColumn("int")),output,null,CancellationToken.None)==0 && output.ToString()=="","Empty table");
        using(var reader=new ValueReader(new[]{new object[]{1}})) using(var output=new StringWriter()) using(var cancel=new CancellationTokenSource()) { cancel.Cancel(); AssertThrows<OperationCanceledException>(()=>new MssqlDataScriptWriter().WriteTable(reader,ValueTable(ValueColumn("int")),output,null,cancel.Token),"Cancel before read"); }
    }
    private sealed class ValueContinuationSink : IMssqlLargeRowWriter
    {
        internal long Bytes; internal string PrefixValue; internal int Tail;
        public long WriteRow(DbDataReader reader, MssqlTableDefinition table, IReadOnlyList<MssqlColumnDefinition> columns, MssqlRowContinuation row, MssqlScriptBatchWriter output, CancellationToken cancellation, Action<long> onAdditionalBytes) {
            var prefix=new char[row.ValueLength(0)]; row.CopyValueChars(0,0,prefix,0,prefix.Length); PrefixValue=new string(prefix); Bytes=row.CurrentByteOffset;
            Assert(row.BufferBytes<=262144,"Bounded retained row buffer");
            var bytes=new byte[4096]; int n;
            while((n=(int)reader.GetBytes(row.Ordinal,Bytes,bytes,0,bytes.Length))!=0) Bytes+=n;
            Tail=reader.GetInt32(row.Ordinal+1);
            output.WriteStatement("SELECT 1;");
            return 300004-row.CurrentByteOffset;
        }
    }
    // SQL projection exposes variable-size fields as binary. Reject whole-field and backward access.
    private sealed class ValueReader : DbDataReader
    {
        private readonly object[][] rows; private int row=-1, ordinal=-1; private long offset;
        internal ValueReader(object[][] rows) { this.rows=rows; }
        private object At(int i) { if(i<ordinal) throw new InvalidOperationException("Backward ordinal"); if(i>ordinal){ordinal=i;offset=0;} return rows[row][i]; }
        public override bool Read(){row++;ordinal=-1;offset=0;return row<rows.Length;}
        public override bool IsDBNull(int i){return At(i)==null;}
        public override object GetValue(int i){var v=At(i);if(v is byte[] || v is SqlDecimal)throw new InvalidOperationException("Whole field or decimal narrowing");return v;}
        public override object GetProviderSpecificValue(int i){return At(i);}
        public override long GetBytes(int i,long start,byte[] buffer,int index,int count){var b=(byte[])At(i);if(buffer==null || start!=offset || count>65536)throw new InvalidOperationException("Unbounded or nonsequential");int n=(int)Math.Min(count,b.LongLength-start);Array.Copy(b,start,buffer,index,n);offset+=n;return n;}
        public override string GetString(int i){throw new InvalidOperationException("Whole string");}
        public override long GetChars(int i,long a,char[] b,int c,int d){throw new NotSupportedException();}
        public override int GetInt32(int i){return (int)GetValue(i);}
        public override bool GetBoolean(int i){return (bool)GetValue(i);}
        public override byte GetByte(int i){return (byte)GetValue(i);}
        public override short GetInt16(int i){return (short)GetValue(i);}
        public override long GetInt64(int i){return (long)GetValue(i);}
        public override decimal GetDecimal(int i){throw new InvalidOperationException("Decimal narrowing");}
        public override float GetFloat(int i){return (float)GetValue(i);}
        public override double GetDouble(int i){return (double)GetValue(i);}
        public override Guid GetGuid(int i){return (Guid)GetValue(i);}
        public override DateTime GetDateTime(int i){return (DateTime)GetValue(i);}
        public override char GetChar(int i){throw new NotSupportedException();}
        public override int FieldCount{get{return rows.Length==0?0:rows[0].Length;}}
        public override bool HasRows{get{return rows.Length>0;}}
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
