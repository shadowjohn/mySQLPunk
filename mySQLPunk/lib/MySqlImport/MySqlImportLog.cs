using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace mySQLPunk.lib.MySqlImport
{
    public sealed class MySqlImportLog : IDisposable
    {
        private readonly Stream stream;
        private readonly StreamWriter writer;
        private readonly object gate = new object();
        private readonly List<string> recentErrors = new List<string>();
        private bool disposed;

        public string Path { get; }
        public IReadOnlyList<string> RecentErrors
        {
            get { lock (gate) return recentErrors.ToArray(); }
        }

        public MySqlImportLog(Guid jobId, string directory = null)
        {
            directory = directory ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mySQLPunk", "ImportLogs");
            Directory.CreateDirectory(directory);
            string stem = "import-mysql-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + jobId.ToString("N").Substring(0, 8);
            Path = System.IO.Path.Combine(directory, stem + ".log");

            stream = new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.Read);
            writer = new StreamWriter(stream, Encoding.UTF8);
            WriteHeader(jobId);
        }

        private void WriteHeader(Guid jobId)
        {
            lock (gate)
            {
                writer.WriteLine("================================================================================");
                writer.WriteLine($"mySQLPunk MySQL Import Execution Log");
                writer.WriteLine($"Job ID    : {jobId}");
                writer.WriteLine($"Started   : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                writer.WriteLine("================================================================================");
                writer.WriteLine();
                writer.Flush();
            }
        }

        public void LogInfo(string message)
        {
            lock (gate)
            {
                if (disposed) return;
                writer.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [INFO] {message}");
            }
        }

        public void LogStatement(int lineNumber, string preview, bool success, string errorMessage = null, string action = null)
        {
            lock (gate)
            {
                if (disposed) return;
                if (success)
                {
                    string tag = string.IsNullOrWhiteSpace(action) ? "OK" : action;
                    writer.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [{tag}] Line {lineNumber}: {preview}");
                }
                else
                {
                    string errText = $"[{DateTime.Now:HH:mm:ss.fff}] [ERROR] Line {lineNumber}: {errorMessage} | Action: {action} | SQL: {preview}";
                    writer.WriteLine(errText);
                    recentErrors.Add($"Line {lineNumber}: {errorMessage}");
                    if (recentErrors.Count > 100) recentErrors.RemoveAt(0);
                }
            }
        }

        public void LogManifestVerification(string table, long expectedRows, long actualRows, bool match)
        {
            lock (gate)
            {
                if (disposed) return;
                string icon = match ? "[PASS]" : "[WARN]";
                writer.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {icon} Manifest Check: Table `{table}` -> Expected: {expectedRows}, Actual: {actualRows}");
            }
        }

        public void Flush()
        {
            lock (gate)
            {
                if (disposed) return;
                try { writer.Flush(); } catch { }
            }
        }

        public void WriteEnd(MySqlImportResult result)
        {
            lock (gate)
            {
                if (disposed) return;
                writer.WriteLine();
                writer.WriteLine("================================================================================");
                writer.WriteLine($"Execution Finished: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                writer.WriteLine($"Status    : {result.Status}");
                writer.WriteLine($"Executed  : {result.ExecutedStatements} statements");
                writer.WriteLine($"Failed    : {result.FailedStatements} statements");
                writer.WriteLine($"Skipped   : {result.SkippedStatements} statements");
                writer.WriteLine($"Rows      : {result.TotalRowsAffected:N0} rows affected");
                writer.WriteLine($"Duration  : {result.Elapsed.TotalSeconds:F2} seconds");
                writer.WriteLine($"Summary   : {result.Summary}");
                writer.WriteLine("================================================================================");
                writer.Flush();
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (!disposed)
                {
                    disposed = true;
                    try { writer?.Dispose(); } catch { }
                    try { stream?.Dispose(); } catch { }
                }
            }
        }
    }
}
