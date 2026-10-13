using System;
using System.Collections.Generic;
using System.Linq;

namespace mySQLPunk.lib.MssqlImport
{
    internal enum MssqlImportScriptKind { Generic, NativeSchema, NativeData }
    internal enum MssqlImportStage { Scanning, Executing, Verifying, Stopping, Finished }
    internal enum MssqlImportPhase { None, Schema, Data, Indexes, Constraints }
    internal enum MssqlImportStatus { Completed, CompletedWithErrors, Failed, Cancelled, CleanupUncertain }

    internal sealed class MssqlImportRequest
    {
        public Guid JobId { get; }
        public string SourcePath { get; }
        public string DatabaseName { get; }
        public bool SkipSqlErrors { get; }
        internal MssqlImportRequest(Guid jobId, string sourcePath, string databaseName, bool skipSqlErrors = false)
        {
            if (jobId == Guid.Empty) throw new ArgumentException("A job identity is required.", nameof(jobId));
            JobId = jobId; SourcePath = sourcePath ?? string.Empty; DatabaseName = databaseName; SkipSqlErrors = skipSqlErrors;
        }
    }

    internal sealed class MssqlImportTarget
    {
        public string ServerName { get; }
        public string DatabaseName { get; }
        public int DatabaseId { get; }
        public DateTime DatabaseCreateDate { get; }
        public string Version { get; }
        public bool Writable { get; }
        public bool MetadataVisible { get; }
        public long? UserObjectCount { get; }
        public bool? CanCreateTable { get; }
        public bool? CanCreateView { get; }
        internal MssqlImportTarget(string serverName, string databaseName, int databaseId, DateTime databaseCreateDate,
            string version, bool writable, bool metadataVisible, long? userObjectCount, bool? canCreateTable = null, bool? canCreateView = null)
        {
            ServerName = serverName; DatabaseName = databaseName; DatabaseId = databaseId; DatabaseCreateDate = databaseCreateDate;
            Version = version; Writable = writable; MetadataVisible = metadataVisible; UserObjectCount = userObjectCount;
            CanCreateTable = canCreateTable; CanCreateView = canCreateView;
        }
    }

    internal sealed class MssqlImportSessionState
    {
        public bool IsOpen { get; }
        public int DatabaseId { get; }
        public int TransactionCount { get; }
        public int XactState { get; }
        internal MssqlImportSessionState(bool isOpen, int databaseId, int transactionCount, int xactState)
        { IsOpen = isOpen; DatabaseId = databaseId; TransactionCount = transactionCount; XactState = xactState; }
    }

    internal sealed class MssqlImportLobState
    {
        public bool Exists { get; }
        public long RowCount { get; }
        public long Sequence { get; }
        public long ConsumedThrough { get; }
        public bool Consumed { get; }
        public bool Failed { get; }
        internal MssqlImportLobState(bool exists, long rowCount, long sequence, long consumedThrough, bool consumed, bool failed)
        { Exists = exists; RowCount = rowCount; Sequence = sequence; ConsumedThrough = consumedThrough; Consumed = consumed; Failed = failed; }
    }

    internal sealed class MssqlImportCounts
    {
        public long Tables { get; }
        public long Rows { get; }
        internal MssqlImportCounts(long tables, long rows) { Tables = tables; Rows = rows; }
    }

    internal sealed class MssqlImportCleanup
    {
        public bool CommandEnded { get; }
        public bool SessionReleased { get; }
        public bool TransactionCleanupConfirmed { get; }
        public string IssueCode { get; }
        internal MssqlImportCleanup(bool commandEnded, bool sessionReleased, bool transactionCleanupConfirmed, string issueCode)
        { CommandEnded = commandEnded; SessionReleased = sessionReleased; TransactionCleanupConfirmed = transactionCleanupConfirmed; IssueCode = issueCode; }
    }

    internal sealed class MssqlImportSqlError
    {
        public int Number { get; }
        public int State { get; }
        public int Class { get; }
        public int RelativeLine { get; }
        public string Message { get; }
        internal MssqlImportSqlError(int number, int state, int @class, int relativeLine, string message)
        { Number = number; State = state; Class = @class; RelativeLine = relativeLine; Message = message ?? string.Empty; }
    }

    internal sealed class MssqlImportBatchException : Exception
    {
        public IReadOnlyList<MssqlImportSqlError> Errors { get; }
        public bool OutcomeUncertain { get; }
        internal MssqlImportBatchException(IEnumerable<MssqlImportSqlError> errors, bool outcomeUncertain = false)
            : base("MSSQL batch execution failed.")
        { Errors = Array.AsReadOnly((errors ?? Enumerable.Empty<MssqlImportSqlError>()).ToArray()); OutcomeUncertain = outcomeUncertain; }
    }

    internal sealed class MssqlImportProgress
    {
        public Guid JobId { get; }
        public long FileBytes { get; }
        public MssqlImportStage Stage { get; }
        public MssqlImportPhase Phase { get; }
        public long ScannedBytes { get; }
        public long ProcessedBytes { get; }
        public long SuccessfulBytes { get; }
        public long SkippedBytes { get; }
        public long SuccessfulBatches { get; }
        public long FailedBatches { get; }
        public long BatchNumber { get; }
        public long StartLine { get; }
        public long EndLine { get; }
        public long StartByte { get; }
        public long EndByte { get; }
        public string TableName { get; }
        public TimeSpan Elapsed { get; }
        public TimeSpan? Remaining { get; }
        public DateTime? EstimatedFinish { get; }
        public string RecentErrorText { get; }
        public string LogPath { get; }
        public long TotalErrors { get; }
        public bool LogTruncated { get; }
        internal MssqlImportProgress(Guid jobId, long fileBytes, MssqlImportStage stage, long scannedBytes = 0,
            long successfulBytes = 0, long skippedBytes = 0, long successfulBatches = 0, long failedBatches = 0,
            long batchNumber = 0, long startLine = 0, long endLine = 0, long startByte = 0, long endByte = 0,
            MssqlImportPhase phase = MssqlImportPhase.None, string tableName = null, TimeSpan elapsed = default(TimeSpan),
            TimeSpan? remaining = null, DateTime? estimatedFinish = null, string recentErrorText = null,
            string logPath = null, long totalErrors = 0, bool logTruncated = false)
        {
            JobId = jobId; FileBytes = fileBytes; Stage = stage; ScannedBytes = scannedBytes;
            SuccessfulBytes = successfulBytes; SkippedBytes = skippedBytes; ProcessedBytes = checked(successfulBytes + skippedBytes);
            SuccessfulBatches = successfulBatches; FailedBatches = failedBatches; BatchNumber = batchNumber;
            StartLine = startLine; EndLine = endLine; StartByte = startByte; EndByte = endByte; Phase = phase; TableName = tableName;
            Elapsed = elapsed; Remaining = remaining; EstimatedFinish = estimatedFinish; LogPath = logPath; TotalErrors = totalErrors;
            string text = recentErrorText ?? string.Empty;
            RecentErrorText = text.Length <= 32768 ? text : text.Substring(text.Length - 32768);
            LogTruncated = logTruncated || text.Length > 32768;
        }
    }

    internal sealed class MssqlImportResult
    {
        public Guid JobId { get; }
        public MssqlImportStatus Status { get; }
        public long FileBytes { get; }
        public long ProcessedBytes { get; }
        public long SuccessfulBytes { get; }
        public long SkippedBytes { get; }
        public long SuccessfulBatches { get; }
        public long FailedBatches { get; }
        public long LastAcknowledgedBatch { get; }
        public long? UncertainBatch { get; }
        public string LogPath { get; }
        public MssqlImportCleanup Cleanup { get; }
        public string ValidationSummary { get; }
        public long TotalErrors { get; }
        public TimeSpan Elapsed { get; }
        internal MssqlImportResult(Guid jobId, MssqlImportStatus status, long fileBytes, long successfulBytes = 0, long skippedBytes = 0,
            long successfulBatches = 0, long failedBatches = 0, long lastAcknowledgedBatch = 0, long? uncertainBatch = null,
            string logPath = null, MssqlImportCleanup cleanup = null, string validationSummary = null, long totalErrors = 0,
            TimeSpan elapsed = default(TimeSpan))
        {
            JobId = jobId; Status = status; FileBytes = fileBytes; SuccessfulBytes = successfulBytes; SkippedBytes = skippedBytes;
            ProcessedBytes = checked(successfulBytes + skippedBytes); SuccessfulBatches = successfulBatches; FailedBatches = failedBatches;
            LastAcknowledgedBatch = lastAcknowledgedBatch; UncertainBatch = uncertainBatch; LogPath = logPath; Cleanup = cleanup;
            ValidationSummary = validationSummary; TotalErrors = totalErrors; Elapsed = elapsed;
        }
    }

    internal sealed class MssqlImportEstimate
    {
        public TimeSpan? Remaining { get; }
        public DateTime? EstimatedFinish { get; }
        public bool IsReliable { get; }
        internal MssqlImportEstimate(TimeSpan? remaining, DateTime? estimatedFinish, bool isReliable)
        { Remaining = remaining; EstimatedFinish = estimatedFinish; IsReliable = isReliable; }
    }

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
