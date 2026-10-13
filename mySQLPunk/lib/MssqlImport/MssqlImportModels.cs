using System;

namespace mySQLPunk.lib.MssqlImport
{
    internal enum MssqlImportScriptKind { Generic, NativeSchema, NativeData }

    internal sealed class MssqlImportBatch
    {
        public long Number { get; }
        public long StartByte { get; }
        public long EndByte { get; }
        public long StartLine { get; }
        public long EndLine { get; }
        public string Text { get; }
        internal string SourceEncodingName { get; }
        internal MssqlImportBatch(long number, long startByte, long endByte, long startLine, long endLine, string text, string sourceEncodingName = "UTF-8")
        { Number = number; StartByte = startByte; EndByte = endByte; StartLine = startLine; EndLine = endLine; Text = text; SourceEncodingName = sourceEncodingName; }
    }

    internal sealed class MssqlImportInputException : Exception
    {
        public string Code { get; }
        public long BatchNumber { get; }
        public long Line { get; }
        public long ByteOffset { get; }
        internal MssqlImportInputException(string code, long batchNumber, long line, long byteOffset, Exception inner = null)
            : base(code + (code == "EmbeddedNul" ? " (unsupported embedded NUL or possible BOM-less UTF-16)" : "") + " at batch " + batchNumber + ", line " + line + ", byte " + byteOffset + ".", inner)
        { Code = code; BatchNumber = batchNumber; Line = line; ByteOffset = byteOffset; }
    }

    internal sealed class MssqlImportScriptInfo
    {
        public MssqlImportScriptKind Kind { get; }
        public Guid? NativeJobId { get; }
        public long? ExpectedTables { get; }
        public long? ExpectedRows { get; }
        public string SourceReadMode { get; }
        public bool HasLobProtocol { get; }
        internal MssqlImportScriptInfo(MssqlImportScriptKind kind, Guid? job, long? tables, long? rows, string mode, bool lob)
        { Kind = kind; NativeJobId = job; ExpectedTables = tables; ExpectedRows = rows; SourceReadMode = mode; HasLobProtocol = lob; }
    }

    internal sealed class MssqlImportScan
    {
        public long FileBytes { get; }
        public long BatchCount { get; }
        public long MaxBatchCodeUnits { get; }
        public string EncodingName { get; }
        public string Sha256 { get; }
        public MssqlImportScriptInfo Script { get; }
        internal MssqlImportScan(long bytes, long batches, long max, string encoding, string sha256, MssqlImportScriptInfo script)
        { FileBytes = bytes; BatchCount = batches; MaxBatchCodeUnits = max; EncodingName = encoding; Sha256 = sha256; Script = script; }
    }
}
