using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace mySQLPunk.lib.PostgreSqlImport
{
    public sealed class PostgreSqlImportLog : IDisposable
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

        public PostgreSqlImportLog(Guid jobId, string directory = null)
        {
            directory = directory ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mySQLPunk", "ImportLogs");
            Directory.CreateDirectory(directory);
            string stem = "import-postgresql-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + jobId.ToString("N").Substring(0, 8);
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
                writer.WriteLine("mySQLPunk PostgreSQL / PostGIS Import Execution Log");
                writer.WriteLine(string.Format("Job ID    : {0}", jobId));
                writer.WriteLine(string.Format("Started   : {0:yyyy-MM-dd HH:mm:ss}", DateTime.Now));
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
                writer.WriteLine(string.Format("[{0:HH:mm:ss.fff}] [INFO] {1}", DateTime.Now, message));
            }
        }

        public void LogStatement(int lineNumber, string preview, bool success, string errorMessage = null, string action = null)
        {
            lock (gate)
            {
                if (disposed) return;
                string status = success ? "OK" : "ERROR";
                string extra = string.IsNullOrWhiteSpace(action) ? "" : " (" + action + ")";
                writer.WriteLine(string.Format("[{0:HH:mm:ss.fff}] [{1}] Line {2}{3}: {4}", DateTime.Now, status, lineNumber, extra, preview));
                if (!success && !string.IsNullOrWhiteSpace(errorMessage))
                {
                    writer.WriteLine(string.Format("             Error: {0}", errorMessage));
                    if (recentErrors.Count < 20)
                    {
                        recentErrors.Add(string.Format("Line {0}: {1}", lineNumber, errorMessage));
                    }
                }
            }
        }

        public void LogManifest(Dictionary<string, long> expected, Dictionary<string, long> actual, bool match)
        {
            lock (gate)
            {
                if (disposed) return;
                writer.WriteLine();
                writer.WriteLine("--- Manifest Verification ---");
                writer.WriteLine(string.Format("Result: {0}", match ? "PASSED" : "MISMATCH"));
                foreach (var kvp in expected)
                {
                    long actualRows = actual.ContainsKey(kvp.Key) ? actual[kvp.Key] : -1;
                    writer.WriteLine(string.Format("  Table {0}: Expected {1}, Actual {2}", kvp.Key, kvp.Value, actualRows));
                }
                writer.WriteLine("-----------------------------");
                writer.WriteLine();
            }
        }

        public void LogSummary(int executed, int failed, long rowsAffected, TimeSpan elapsed, bool success)
        {
            lock (gate)
            {
                if (disposed) return;
                writer.WriteLine();
                writer.WriteLine("================================================================================");
                writer.WriteLine(string.Format("Execution Finished: {0}", success ? "SUCCESS" : "FAILED"));
                writer.WriteLine(string.Format("Statements Executed: {0}", executed));
                writer.WriteLine(string.Format("Statements Failed  : {0}", failed));
                writer.WriteLine(string.Format("Rows Affected      : {0:N0}", rowsAffected));
                writer.WriteLine(string.Format("Duration           : {0:hh\\:mm\\:ss\\.fff}", elapsed));
                writer.WriteLine("================================================================================");
                writer.Flush();
            }
        }

        public void Flush()
        {
            lock (gate)
            {
                if (disposed) return;
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
