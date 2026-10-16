using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace mySQLPunk.lib.SqliteImport
{
    public sealed class SqliteImportLog : IDisposable
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

        public SqliteImportLog(Guid jobId, string directory = null)
        {
            directory = directory ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mySQLPunk", "ImportLogs");
            Directory.CreateDirectory(directory);
            string stem = "import-sqlite-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + jobId.ToString("N").Substring(0, 8);
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
                writer.WriteLine("mySQLPunk SQLite Import Execution Log");
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
                writer.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {icon} Manifest Check: Table \"{table}\" -> Expected: {expectedRows}, Actual: {actualRows}");
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

        public void WriteEnd(SqliteImportResult result)
        {
            lock (gate)
            {
                if (disposed) return;
                writer.WriteLine();
                writer.WriteLine("================================================================================");
                writer.WriteLine($"Import Execution Completed: Status={result.Status}");
                writer.WriteLine($"Elapsed     : {result.Elapsed}");
                writer.WriteLine($"Statements  : Executed={result.ExecutedStatements}, Failed={result.FailedStatements}, Skipped={result.SkippedStatements}");
                writer.WriteLine($"RowsAffected: {result.TotalRowsAffected:N0}");
                writer.WriteLine($"Manifest    : Verified={result.ManifestVerified}");
                if (!string.IsNullOrWhiteSpace(result.ManifestReport))
                {
                    writer.WriteLine($"Manifest Details: {result.ManifestReport}");
                }
                writer.WriteLine("================================================================================");
                writer.Flush();
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                try { writer.Flush(); writer.Dispose(); } catch { }
                try { stream.Dispose(); } catch { }
            }
        }
    }
}
