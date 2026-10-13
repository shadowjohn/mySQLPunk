using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace mySQLPunk.lib.MssqlImport
{
    internal interface IMssqlImportLog : IDisposable
    {
        string Path { get; }
        // Worker snapshots this tail before publishing immutable UI progress; UI never reads the mutable tail.
        MssqlImportLogTail Tail { get; }
        void WriteStart(MssqlImportRequest request, MssqlImportScan scan, MssqlImportTarget target);
        void WriteError(MssqlImportBatch batch, MssqlImportBatchException error, string action);
        void Flush();
        void WriteEnd(MssqlImportResult result);
    }
    internal sealed class MssqlImportLogTail
    {
        private readonly Queue<string> entries = new Queue<string>();
        private readonly object gate = new object();
        private int characters;
        private long totalErrors;
        private bool truncated;
        public long TotalErrors { get { lock (gate) return totalErrors; } }
        public bool Truncated { get { lock (gate) return truncated; } }
        public void Add(string text)
        {
            lock (gate)
            {
                totalErrors = checked(totalErrors + 1);
                text = text ?? string.Empty;
                if (text.Length > 32768) { text = text.Substring(text.Length - 32768); truncated = true; }
                entries.Enqueue(text); characters += text.Length;
                while (entries.Count > 100 || characters > 32768)
                { characters -= entries.Dequeue().Length; truncated = true; }
            }
        }
        public string Snapshot() { lock (gate) return string.Concat(entries); }
    }
    internal sealed class MssqlImportLog : IMssqlImportLog
    {
        private readonly Stream stream;
        private readonly StreamWriter writer;
        private readonly Func<DateTime> utcNow;
        private bool disposed;
        public string Path { get; }
        public MssqlImportLogTail Tail { get; } = new MssqlImportLogTail();
        internal MssqlImportLog(Guid jobId, string directory = null, Func<string, Stream> streamFactory = null, Func<DateTime> utcNow = null)
        {
            if (jobId == Guid.Empty) throw new ArgumentException("A job identity is required.", nameof(jobId));
            this.utcNow = utcNow ?? (() => DateTime.UtcNow);
            directory = directory ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mySQLPunk", "ImportLogs");
            directory = System.IO.Path.GetFullPath(directory);
            Directory.CreateDirectory(directory);
            string stem = "import-" + this.utcNow().ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture) + "-" + jobId.ToString("N");
            for (int collision = 0; ; collision++)
            {
                string suffix = collision == 0 ? string.Empty : "-" + collision.ToString(CultureInfo.InvariantCulture);
                string candidate = System.IO.Path.Combine(directory, stem + suffix + ".log");
                try
                {
                    stream = streamFactory != null ? streamFactory(candidate) : new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    Path = candidate;
                    break;
                }
                catch (IOException) when (streamFactory == null && File.Exists(candidate)) { }
            }
            try { writer = new StreamWriter(stream, new UTF8Encoding(false, true), 4096, true) { NewLine = "\r\n" }; }
            catch { stream.Dispose(); throw; }
        }
        public void WriteStart(MssqlImportRequest request, MssqlImportScan scan, MssqlImportTarget target)
        {
            EnsureOpen();
            Field("StartUtc", utcNow().ToString("O", CultureInfo.InvariantCulture));
            Field("Job", request.JobId); Field("LogPath", Path); Field("SourcePath", request.SourcePath);
            Field("FileBytes", scan.FileBytes); Field("Sha256", scan.Sha256); Field("Encoding", scan.EncodingName);
            Field("SkipSqlErrors", request.SkipSqlErrors); Field("ServerName", target.ServerName); Field("DatabaseName", target.DatabaseName);
            Field("DatabaseId", target.DatabaseId); Field("DatabaseCreateDate", target.DatabaseCreateDate.ToString("O", CultureInfo.InvariantCulture));
            Field("Version", target.Version); Field("Writable", target.Writable); Field("MetadataVisible", target.MetadataVisible);
            Field("UserObjectCount", target.UserObjectCount); Field("CanCreateTable", target.CanCreateTable); Field("CanCreateView", target.CanCreateView);
            Flush(); // The caller must not start DB writes until this durable header succeeds.
        }
        public void WriteError(MssqlImportBatch batch, MssqlImportBatchException error, string action)
        {
            EnsureOpen();
            string timestamp = utcNow().ToString("O", CultureInfo.InvariantCulture);
            // Build only a bounded UI suffix. The original provider message is streamed independently.
            var preview = new StringBuilder(32769);
            foreach (string part in ErrorParts(batch, error, action, timestamp))
            {
                int keep = Math.Min(part.Length, 32769);
                if (preview.Length + keep > 32769) preview.Remove(0, preview.Length + keep - 32769);
                preview.Append(part, part.Length - keep, keep);
            }
            Tail.Add(preview.ToString());
            foreach (string part in ErrorParts(batch, error, action, timestamp)) writer.Write(part);
        }
        private static IEnumerable<string> ErrorParts(MssqlImportBatch batch, MssqlImportBatchException error, string action, string timestamp)
        {
            yield return "ErrorUtc=" + timestamp + " Batch=" + Number(batch.Number) + " StartLine=" + Number(batch.StartLine) +
                " EndLine=" + Number(batch.EndLine) + " StartByte=" + Number(batch.StartByte) + " EndByte=" + Number(batch.EndByte) +
                " OutcomeUncertain=" + error.OutcomeUncertain + " Action=" + action + "\r\n";
            foreach (var detail in error.Errors)
            {
                long? sourceLine = detail.RelativeLine > 0 && batch.StartLine <= long.MaxValue - detail.RelativeLine + 1
                    ? (long?)(batch.StartLine + ((long)detail.RelativeLine - 1)) : null;
                yield return "Number=" + detail.Number.ToString(CultureInfo.InvariantCulture) + " State=" + detail.State.ToString(CultureInfo.InvariantCulture) +
                    " Class=" + detail.Class.ToString(CultureInfo.InvariantCulture) + " RelativeLine=" + detail.RelativeLine.ToString(CultureInfo.InvariantCulture) +
                    " SourceLine=" + (sourceLine.HasValue ? Number(sourceLine.Value) : "unknown") + " Message=";
                yield return detail.Message;
                yield return "\r\n";
            }
        }
        public void Flush()
        {
            EnsureOpen(); writer.Flush();
            var file = stream as FileStream;
            if (file != null) file.Flush(true); else stream.Flush();
        }
        public void WriteEnd(MssqlImportResult result)
        {
            EnsureOpen();
            Field("EndUtc", utcNow().ToString("O", CultureInfo.InvariantCulture)); Field("Job", result.JobId); Field("Status", result.Status);
            Field("FileBytes", result.FileBytes); Field("ProcessedBytes", result.ProcessedBytes); Field("SuccessfulBytes", result.SuccessfulBytes);
            Field("SkippedBytes", result.SkippedBytes); Field("SuccessfulBatches", result.SuccessfulBatches); Field("FailedBatches", result.FailedBatches);
            Field("LastAcknowledgedBatch", result.LastAcknowledgedBatch); Field("UncertainBatch", result.UncertainBatch); Field("TotalErrors", result.TotalErrors);
            Field("LogPath", Path); Field("Elapsed", result.Elapsed); Field("ValidationSummary", result.ValidationSummary);
            if (result.Cleanup != null)
            {
                Field("CommandEnded", result.Cleanup.CommandEnded); Field("SessionReleased", result.Cleanup.SessionReleased);
                Field("TransactionCleanupConfirmed", result.Cleanup.TransactionCleanupConfirmed); Field("CleanupIssueCode", result.Cleanup.IssueCode);
            }
            else Field("Cleanup", "unknown");
            Flush();
        }
        private static string Number(long value) { return value.ToString(CultureInfo.InvariantCulture); }
        private void Field(string name, object value)
        { writer.Write(name); writer.Write('='); writer.WriteLine(value == null ? "unknown" : Convert.ToString(value, CultureInfo.InvariantCulture)); }
        private void EnsureOpen() { if (disposed) throw new ObjectDisposedException(nameof(MssqlImportLog)); }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { writer.Dispose(); }
            finally { stream.Dispose(); }
        }
    }
}
