using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

namespace mySQLPunk.lib.MssqlExport
{
    // One instance per export job, reused across tables. Projection ordinals are zero-based;
    // the monotonically increasing protocol sequence is independent of source row/column IDs.
    internal sealed class MssqlLobScriptWriter : IMssqlLargeRowWriter
    {
        private readonly string state, values, fields;
        private long sequence;
        private MssqlTableDefinition table;
        private IReadOnlyList<MssqlColumnDefinition> columns;
        private string Identity { get { return "[Sequence]=" + N(sequence) + " AND [TableId]=" + N(table.Key.ObjectId); } }
        internal MssqlLobScriptWriter(Guid jobId)
        {
            if(jobId==Guid.Empty) throw new ArgumentException("A job identity is required.",nameof(jobId));
            string root="#mp_"+jobId.ToString("N");state=root+"_s";values=root+"_v";fields=root+"_c";
        }
        public long WriteRow(DbDataReader reader, MssqlTableDefinition table, IReadOnlyList<MssqlColumnDefinition> columns,
            MssqlRowContinuation row, MssqlScriptBatchWriter output, CancellationToken cancellation, Action<long> onAdditionalBytes)
        {
            cancellation.ThrowIfCancellationRequested();
            BeginRow(table,columns,output);
            long additional=0;
            for(int i=0;i<columns.Count;i++) {
                cancellation.ThrowIfCancellationRequested();
                bool retained=row.CurrentValueComplete || i<row.Ordinal;
                bool partial=!retained && i==row.Ordinal && row.CurrentValueStarted;
                if(retained && IsNull(row,i)) { CompleteColumn(i,true,0,0,output);continue; }
                if(!retained && !partial && reader.IsDBNull(i)) { CompleteColumn(i,true,0,0,output);continue; }
                if(!MssqlValueCodec.IsBinaryProjected(columns[i])) {
                    string scalar;
                    if(retained) { var chars=new char[row.ValueLength(i)];row.CopyValueChars(i,0,chars,0,chars.Length);scalar=new string(chars); }
                    else { scalar=MssqlValueCodec.FormatScalar(MssqlValueCodec.ReadScalar(reader,i,columns[i]),columns[i]);additional+=MssqlValueCodec.ScalarBytes(columns[i]);onAdditionalBytes?.Invoke(additional); }
                    WriteScalar(i,scalar,output);continue;
                }
                bool unicode=Unicode(columns[i]);
                var buffer=new byte[unicode ? MssqlExportLimits.TextChunkCodeUnits*2 : MssqlExportLimits.BinaryChunkBytes];
                var hex=new char[4096];
                int prefix=MssqlValueCodec.BinaryPrefix(columns[i]).Length;
                int retainedBytes=(retained || partial) ? (row.ValueLength(i)-prefix-(retained ? MssqlValueCodec.BinarySuffix(columns[i]).Length : 0))/2 : 0;
                int prefixRead=0,used=0;long offset=partial ? row.CurrentByteOffset : 0, index=0,total=0;
                bool eof=retained;
                while(true) {
                    cancellation.ThrowIfCancellationRequested();
                    while(used<buffer.Length && prefixRead<retainedBytes) {
                        int count=Math.Min(Math.Min(hex.Length/2,buffer.Length-used),retainedBytes-prefixRead);
                        row.CopyValueChars(i,prefix+prefixRead*2,hex,0,count*2);
                        for(int j=0;j<count;j++)buffer[used+j]=(byte)((Nibble(hex[j*2])<<4)|Nibble(hex[j*2+1]));
                        prefixRead+=count;used+=count;
                    }
                    while(used<buffer.Length && !eof) {
                        cancellation.ThrowIfCancellationRequested();
                        int read=checked((int)reader.GetBytes(i,offset,buffer,used,buffer.Length-used));
                        if(read==0) { eof=true;break; }
                        offset+=read;additional+=read;used+=read;onAdditionalBytes?.Invoke(additional);
                    }
                    bool end=eof && prefixRead==retainedBytes;
                    if(end && used==0)break;
                    int emit=used;
                    if(unicode) {
                        if(end && (emit&1)!=0)throw new InvalidOperationException("Invalid odd UTF-16 source byte count.");
                        emit-=emit&1;
                        if(!end && emit>=2 && buffer[emit-1]>=0xD8 && buffer[emit-1]<=0xDB)emit-=2;
                    }
                    if(emit==0)throw new InvalidOperationException("Cannot advance LOB chunk.");
                    WriteChunk(i,index++,buffer,emit,output);total+=emit;
                    Buffer.BlockCopy(buffer,emit,buffer,0,used-emit);used-=emit;
                }
                CompleteColumn(i,false,index,total,output);
            }
            cancellation.ThrowIfCancellationRequested();CommitRow(output);return additional;
        }
        internal void BeginRow(MssqlTableDefinition table,IReadOnlyList<MssqlColumnDefinition> columns,MssqlScriptBatchWriter output)
        {
            this.table=table;this.columns=columns;sequence=checked(sequence+1);
            output.Flush();
            if(sequence==1) {
                output.WriteStatement("IF OBJECT_ID('tempdb.."+state+"') IS NOT NULL THROW 51000,'LOB job already exists',1; CREATE TABLE "+state+" ([Sequence] bigint NOT NULL,[TableId] int NOT NULL,[ConsumedThrough] bigint NOT NULL,[Consumed] bit NOT NULL,[Failed] bit NOT NULL); INSERT INTO "+state+" VALUES(0,0,0,1,0);");output.Flush();
            }
            string definition=string.Join(",",columns.Select((c,i)=>Field(i)+" "+StageType(c)+" NULL"));
            string initial=string.Join(",",columns.Select(c=>MssqlValueCodec.IsBinaryProjected(c) ? (Unicode(c) ? "CONVERT(nvarchar(max),0x)" : "0x") : "NULL"));
            // Compile the initial INSERT after CREATE: the reusable temp table may still
            // have the previous table's shape when this batch is first compiled. Only
            // generated empty/NULL constants enter EXEC, never source values or SQL names.
            string body="IF NOT EXISTS(SELECT 1 FROM "+state+" WHERE [Failed]=0 AND [Consumed]=1 AND [ConsumedThrough]="+N(sequence-1)+") THROW 51000,'LOB prior row not consumed',1; "+
                "UPDATE "+state+" SET [Sequence]="+N(sequence)+",[TableId]="+N(table.Key.ObjectId)+",[Consumed]=0; "+
                "IF OBJECT_ID('tempdb.."+values+"') IS NOT NULL DROP TABLE "+values+"; IF OBJECT_ID('tempdb.."+fields+"') IS NOT NULL DROP TABLE "+fields+"; "+
                "CREATE TABLE "+values+" ("+definition+"); EXEC(N'INSERT INTO "+values+" VALUES("+initial+");'); "+
                "CREATE TABLE "+fields+" ([Ordinal] int NOT NULL PRIMARY KEY,[Chunks] bigint NOT NULL,[Bytes] bigint NOT NULL,[ExpectedChunks] bigint NULL,[ExpectedBytes] bigint NULL,[IsNull] bit NOT NULL,[Completed] bit NOT NULL); "+
                string.Join(" ",columns.Select((c,i)=>"INSERT INTO "+fields+" VALUES("+N(i)+",0,0,NULL,NULL,0,0);"));
            Guarded(body,output,false,true);
        }
        internal void WriteScalar(int ordinal,string scalarExpression,MssqlScriptBatchWriter output)
        {
            Guarded(OpenField(ordinal)+"UPDATE "+values+" SET "+Field(ordinal)+"="+scalarExpression+"; UPDATE "+fields+" SET [Completed]=1,[IsNull]="+(scalarExpression=="NULL" ? "1" : "0")+",[ExpectedChunks]=0,[ExpectedBytes]=COALESCE((SELECT CONVERT(bigint,DATALENGTH("+Field(ordinal)+")) FROM "+values+"),0),[Bytes]=COALESCE((SELECT CONVERT(bigint,DATALENGTH("+Field(ordinal)+")) FROM "+values+"),0) WHERE [Ordinal]="+N(ordinal)+";",output);
        }
        internal void WriteChunk(int ordinal,long chunkIndex,byte[] encodedBytes,int count,MssqlScriptBatchWriter output)
        {
            if(count<=0 || count>encodedBytes.Length || count>MssqlExportLimits.BinaryChunkBytes || (Unicode(columns[ordinal]) && ((count&1)!=0 || count>MssqlExportLimits.TextChunkCodeUnits*2)))throw new ArgumentOutOfRangeException(nameof(count));
            var hex=new StringBuilder(2+count*2);hex.Append("0x");for(int j=0;j<count;j++)hex.Append(encodedBytes[j].ToString("X2",CultureInfo.InvariantCulture));
            string value=Unicode(columns[ordinal]) ? "CONVERT(nvarchar(max),"+hex+")" : hex.ToString();
            Guarded(OpenField(ordinal)+"IF NOT EXISTS(SELECT 1 FROM "+fields+" WHERE [Ordinal]="+N(ordinal)+" AND [Chunks]="+N(chunkIndex)+" AND [IsNull]=0) THROW 51000,'LOB chunk order',1; "+
                "UPDATE "+values+" SET "+Field(ordinal)+".WRITE("+value+",NULL,0); UPDATE "+fields+" SET [Chunks]=[Chunks]+1,[Bytes]=[Bytes]+"+N(count)+" WHERE [Ordinal]="+N(ordinal)+"; "+LengthGuard(ordinal),output);
        }
        internal void CompleteColumn(int ordinal,bool isNull,long chunkCount,long expectedBytes,MssqlScriptBatchWriter output)
        {
            Guarded(OpenField(ordinal)+"IF NOT EXISTS(SELECT 1 FROM "+fields+" WHERE [Ordinal]="+N(ordinal)+" AND [Chunks]="+N(chunkCount)+" AND [Bytes]="+N(expectedBytes)+") THROW 51000,'LOB totals',1; "+
                (isNull ? "IF "+N(chunkCount)+"<>0 OR "+N(expectedBytes)+"<>0 THROW 51000,'LOB null totals',1; UPDATE "+values+" SET "+Field(ordinal)+"=NULL; " : LengthGuard(ordinal))+
                "UPDATE "+fields+" SET [Completed]=1,[IsNull]="+(isNull ? "1" : "0")+",[ExpectedChunks]="+N(chunkCount)+",[ExpectedBytes]="+N(expectedBytes)+" WHERE [Ordinal]="+N(ordinal)+";",output);
        }
        internal void CommitRow(MssqlScriptBatchWriter output)
        {
            string checks="IF (SELECT COUNT(*) FROM "+fields+")<>"+N(columns.Count)+" OR EXISTS(SELECT 1 FROM "+fields+" WHERE [Completed]<>1 OR [ExpectedChunks] IS NULL OR [ExpectedBytes] IS NULL OR [Chunks]<>[ExpectedChunks] OR [Bytes]<>[ExpectedBytes]) THROW 51000,'LOB incomplete row',1; ";
            string lengths=string.Join(",",columns.Select((c,i)=>"("+N(i)+",CONVERT(bigint,DATALENGTH("+Field(i)+")))"));
            checks+="IF EXISTS(SELECT 1 FROM "+values+" CROSS APPLY(VALUES "+lengths+") AS actual([Ordinal],[Bytes]) LEFT JOIN "+fields+" AS expected ON actual.[Ordinal]=expected.[Ordinal] WHERE expected.[Ordinal] IS NULL OR COALESCE(actual.[Bytes],0)<>expected.[Bytes] OR expected.[IsNull]<>CASE WHEN actual.[Bytes] IS NULL THEN 1 ELSE 0 END) THROW 51000,'LOB final length or null mismatch',1; ";
            string target=MssqlValueCodec.QuoteIdentifier(table.Key.Schema)+"."+MssqlValueCodec.QuoteIdentifier(table.Key.Name);
            Guarded(checks+"INSERT INTO "+target+" ("+string.Join(",",columns.Select(c=>MssqlValueCodec.QuoteIdentifier(c.Name)))+") SELECT "+string.Join(",",columns.Select((c,i)=>c.NativeType=="xml" ? "CONVERT(xml,"+Field(i)+",1)" : c.NativeType=="geometry" ? "geometry::Deserialize("+Field(i)+")" : Field(i)))+" FROM "+values+"; "+
                "UPDATE "+state+" SET [Consumed]=1,[ConsumedThrough]="+N(sequence)+"; DELETE FROM "+values+"; DELETE FROM "+fields+";",output,true,true);
        }
        private string OpenField(int ordinal)
        { return "IF NOT EXISTS(SELECT 1 FROM "+fields+" WHERE [Ordinal]="+N(ordinal)+" AND [Completed]=0) THROW 51000,'LOB column completed or missing',1; "; }
        private string LengthGuard(int ordinal)
        { return "IF NOT EXISTS(SELECT 1 FROM "+fields+" WHERE [Ordinal]="+N(ordinal)+" AND [Bytes]=COALESCE((SELECT CONVERT(bigint,DATALENGTH("+Field(ordinal)+")) FROM "+values+"),0)) THROW 51000,'LOB byte length',1; "; }
        private void Guarded(string body,MssqlScriptBatchWriter output,bool active=true,bool metadataOnly=false)
        {
            string check=active ? "IF NOT EXISTS(SELECT 1 FROM "+state+" WHERE "+Identity+" AND [Failed]=0 AND [Consumed]=0 AND [ConsumedThrough]="+N(sequence-1)+") THROW 51000,'LOB inactive or consumed row',1; IF (SELECT COUNT(*) FROM "+values+")<>1 THROW 51000,'LOB missing stage',1; " : "";
            string batch="BEGIN TRY BEGIN TRANSACTION; "+check+body+" COMMIT TRANSACTION; END TRY BEGIN CATCH IF XACT_STATE()<>0 ROLLBACK TRANSACTION; UPDATE "+state+" SET [Failed]=1; THROW; END CATCH;";
            if(metadataOnly)output.WriteMetadataStatement(batch);else output.WriteStatement(batch);
            // Each short transaction is a self-contained batch. Failed clients that continue at
            // the next GO encounter the durable Failed bit before any further target mutation.
            output.Flush();
        }
        private static string StageType(MssqlColumnDefinition c)
        {
            if(Unicode(c))return "nvarchar(max)";
            if(c.NativeType=="varchar" || c.NativeType=="char" || c.NativeType=="text") {
                if(string.IsNullOrEmpty(c.Collation) || c.Collation.Any(ch=>!(char.IsLetterOrDigit(ch) && ch<128) && ch!='_'))throw new InvalidOperationException("Native ANSI collation is required.");
                return "varchar(max) COLLATE "+c.Collation;
            }
            if(MssqlValueCodec.IsBinaryProjected(c))return "varbinary(max)";
            switch(c.NativeType) {
                case "decimal":case "numeric":return c.NativeType+"("+N(c.Precision)+","+N(c.Scale)+")";
                case "time":case "datetime2":case "datetimeoffset":return c.NativeType+"("+N(c.Scale)+")";
                case "bit":case "tinyint":case "smallint":case "int":case "bigint":case "money":case "smallmoney":case "real":case "float":case "date":case "datetime":case "smalldatetime":case "uniqueidentifier":return c.NativeType;
                default:throw new NotSupportedException("No LOB staging type for "+c.NativeType);
            }
        }
        private static bool Unicode(MssqlColumnDefinition c) { return c.NativeType=="nvarchar" || c.NativeType=="nchar" || c.NativeType=="ntext" || c.NativeType=="xml"; }
        private static bool IsNull(MssqlRowContinuation row,int ordinal) { if(row.ValueLength(ordinal)!=4)return false;var b=new char[4];row.CopyValueChars(ordinal,0,b,0,4);return new string(b)=="NULL"; }
        private static int Nibble(char c) { return c<='9' ? c-'0' : c-'A'+10; }
        private static string Field(int ordinal) { return "[c"+N(ordinal)+"]"; }
        private static string N(long value) { return value.ToString(CultureInfo.InvariantCulture); }
    }
}
