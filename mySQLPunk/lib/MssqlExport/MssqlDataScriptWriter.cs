using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace mySQLPunk.lib.MssqlExport
{
    // Task5 consumes this same reader synchronously, finishes exactly one row, and returns
    // additional source bytes consumed. It must not Read(), reopen, rewind, or own the reader.
    internal interface IMssqlLargeRowWriter
    {
        long WriteRow(DbDataReader reader, MssqlTableDefinition table, IReadOnlyList<MssqlColumnDefinition> columns,
            MssqlRowContinuation row, MssqlScriptBatchWriter output, CancellationToken cancellation);
    }
    internal sealed class MssqlRowContinuation
    {
        private readonly char[] buffer;
        private readonly int[] starts, lengths;
        internal int Ordinal { get; }
        internal long CurrentByteOffset { get; }
        internal bool CurrentValueStarted { get; }
        internal bool CurrentValueComplete { get; }
        internal int BufferBytes { get { return buffer.Length * sizeof(char); } }
        internal MssqlRowContinuation(char[] buffer, int[] starts, int[] lengths, int ordinal, long offset, bool started, bool complete)
        { this.buffer = buffer; this.starts = starts; this.lengths = lengths; Ordinal = ordinal; CurrentByteOffset = offset; CurrentValueStarted = started; CurrentValueComplete = complete; }
        // Completed columns contain full expressions; an open binary column contains prefix+hex,
        // without suffix. No source bytes are pending outside this buffer. Valid only during callback.
        internal int ValueLength(int ordinal)
        { CheckOrdinal(ordinal); return lengths[ordinal]; }
        internal int CopyValueChars(int ordinal, int sourceOffset, char[] destination, int destinationOffset, int count)
        {
            CheckOrdinal(ordinal);
            if (sourceOffset < 0 || sourceOffset > lengths[ordinal]) throw new ArgumentOutOfRangeException(nameof(sourceOffset));
            int copied = Math.Min(count, lengths[ordinal] - sourceOffset);
            Array.Copy(buffer, starts[ordinal] + sourceOffset, destination, destinationOffset, copied);
            return copied;
        }
        private void CheckOrdinal(int ordinal)
        { if (ordinal < 0 || ordinal > Ordinal) throw new ArgumentOutOfRangeException(nameof(ordinal)); }
    }
    internal sealed class MssqlScriptBatchWriter
    {
        private readonly TextWriter output;
        private readonly Encoding encoding;
        private int batchBytes;
        internal MssqlScriptBatchWriter(TextWriter output)
        { this.output = output ?? throw new ArgumentNullException(nameof(output)); encoding = output is StreamWriter ? output.Encoding : new UTF8Encoding(false, true); }
        internal int Bytes(string text) { return encoding.GetByteCount(text); }
        internal void WriteStatement(string statement)
        {
            int bytes = Bytes(statement) + Bytes("\r\n");
            if (bytes > MssqlExportLimits.StatementBytes) throw new InvalidOperationException("Statement exceeds the bounded script budget.");
            if (batchBytes + bytes + Bytes("GO\r\n") > MssqlExportLimits.BatchBytes) Flush();
            output.Write(statement); output.Write("\r\n"); batchBytes += bytes;
        }
        internal bool TryWriteRow(string header, char[] values, int[] starts, int[] lengths)
        {
            int bytes = Bytes(header) + Bytes(lengths.Length == 0 ? "\r\n" : ");\r\n");
            for (int i = 0; i < lengths.Length; i++) bytes += encoding.GetByteCount(values, starts[i], lengths[i]) + (i == 0 ? 0 : Bytes(","));
            if (bytes > MssqlExportLimits.StatementBytes) return false;
            if (batchBytes + bytes + Bytes("GO\r\n") > MssqlExportLimits.BatchBytes) Flush();
            output.Write(header);
            for (int i = 0; i < lengths.Length; i++) { if (i > 0) output.Write(","); output.Write(values, starts[i], lengths[i]); }
            output.Write(lengths.Length == 0 ? "\r\n" : ");\r\n");
            batchBytes += bytes;
            return true;
        }
        internal void Flush() { if (batchBytes == 0) return; output.Write("GO\r\n"); batchBytes = 0; }
    }
    internal sealed class MssqlDataScriptWriter
    {
        private readonly IMssqlLargeRowWriter largeRows;
        internal MssqlDataScriptWriter(IMssqlLargeRowWriter largeRows = null) { this.largeRows = largeRows; }
        internal long WriteTable(DbDataReader reader, MssqlTableDefinition table, TextWriter output,
            Action<long, long> onRowsAndSourceBytes, CancellationToken cancellation)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            if (table == null) throw new ArgumentNullException(nameof(table));
            var columns = table.Columns.Where(c => !c.Computed && !c.RowVersion).OrderBy(c => c.Ordinal).ToList().AsReadOnly();
            string target = MssqlValueCodec.QuoteIdentifier(table.Key.Schema) + "." + MssqlValueCodec.QuoteIdentifier(table.Key.Name);
            string header = "INSERT INTO " + target + (columns.Count == 0 ? " DEFAULT VALUES;" : " (" + string.Join(",", columns.Select(c => MssqlValueCodec.QuoteIdentifier(c.Name))) + ") VALUES (");
            var batches = new MssqlScriptBatchWriter(output);
            // Buffer plus scratch and column offsets stay under SmallRowBytes. The encoded
            // statement is separately measured; conservative capacity is not a source limit.
            int capacity = (MssqlExportLimits.SmallRowBytes - 8192 - columns.Count * 8) / 2;
            if (capacity < 1024) throw new NotSupportedException("Column metadata exceeds row buffer budget.");
            var buffer = new char[capacity]; var chunk = new byte[4096];
            var starts = new int[columns.Count]; var lengths = new int[columns.Count];
            long rows = 0, sourceBytes = 0; bool identity = columns.Any(c => c.Identity), identityOn = false;
            cancellation.ThrowIfCancellationRequested();
            while (reader.Read())
            {
                cancellation.ThrowIfCancellationRequested();
                if (identity && !identityOn) { batches.WriteStatement("SET IDENTITY_INSERT " + target + " ON;"); identityOn = true; }
                int used = 0; bool handedOff = false;
                for (int i = 0; i < columns.Count; i++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    starts[i] = used; lengths[i] = 0;
                    var column = columns[i];
                    bool binary = MssqlValueCodec.IsBinaryProjected(column);
                    // Fixed-width scalar expressions are bounded (<100 chars). Do not consume
                    // the next column when there is insufficient room; Task5 starts it itself.
                    if (capacity - used < 128)
                    { sourceBytes += Handoff(reader, table, columns, new MssqlRowContinuation(buffer, starts, lengths, i, 0, false, false), batches, cancellation); handedOff = true; break; }
                    if (reader.IsDBNull(i)) { Append(buffer, ref used, "NULL"); lengths[i] = used - starts[i]; continue; }
                    if (!binary)
                    {
                        Append(buffer, ref used, MssqlValueCodec.FormatScalar(MssqlValueCodec.ReadScalar(reader, i, column), column));
                        sourceBytes += MssqlValueCodec.ScalarBytes(column); lengths[i] = used - starts[i]; continue;
                    }
                    string suffix = MssqlValueCodec.BinarySuffix(column);
                    Append(buffer, ref used, MssqlValueCodec.BinaryPrefix(column));
                    long offset = 0;
                    while (true)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        int count = Math.Min(chunk.Length, (capacity - used - suffix.Length) / 2);
                        if (count == 0)
                        {
                            lengths[i] = used - starts[i];
                            sourceBytes += Handoff(reader, table, columns, new MssqlRowContinuation(buffer, starts, lengths, i, offset, true, false), batches, cancellation); handedOff = true; break;
                        }
                        int read = checked((int)reader.GetBytes(i, offset, chunk, 0, count));
                        if (read == 0) { Append(buffer, ref used, suffix); break; }
                        for (int j = 0; j < read; j++) { buffer[used++] = "0123456789ABCDEF"[chunk[j] >> 4]; buffer[used++] = "0123456789ABCDEF"[chunk[j] & 15]; }
                        offset += read; sourceBytes += read;
                    }
                    lengths[i] = used - starts[i];
                    if (handedOff) break;
                }
                if (!handedOff)
                {
                    if (!batches.TryWriteRow(header, buffer, starts, lengths))
                    {
                        int last = columns.Count - 1;
                        sourceBytes += Handoff(reader, table, columns, new MssqlRowContinuation(buffer, starts, lengths, last, 0, true, true), batches, cancellation);
                    }
                }
                rows++; onRowsAndSourceBytes?.Invoke(rows, sourceBytes);
                cancellation.ThrowIfCancellationRequested();
            }
            if (identityOn) batches.WriteStatement("SET IDENTITY_INSERT " + target + " OFF;");
            batches.Flush();
            return rows;
        }
        private long Handoff(DbDataReader reader, MssqlTableDefinition table, IReadOnlyList<MssqlColumnDefinition> columns, MssqlRowContinuation row, MssqlScriptBatchWriter output, CancellationToken cancellation)
        {
            if (largeRows == null) throw new NotImplementedException("Large row continuation requires the Task5 streaming staging writer; the export UI is not connected yet.");
            return largeRows.WriteRow(reader, table, columns, row, output, cancellation);
        }
        private static void Append(char[] buffer, ref int used, string value)
        { value.CopyTo(0, buffer, used, value.Length); used += value.Length; }
    }
}
