using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace mySQLPunk.lib.OracleImport
{
    public sealed class OracleImportLog : IDisposable
    {
        private readonly string logPath;
        private readonly StreamWriter writer;

        public string LogPath => logPath;

        public OracleImportLog(string targetDatabase)
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string logDir = Path.Combine(appData, "mySQLPunk", "ImportLogs");
            if (!Directory.Exists(logDir))
            {
                Directory.CreateDirectory(logDir);
            }

            string dbSanitized = string.IsNullOrWhiteSpace(targetDatabase) ? "default" : targetDatabase.Replace(':', '_').Replace('/', '_').Replace('\\', '_');
            string fileName = string.Format("import-oracle-{0}-{1:yyyyMMdd_HHmmss}.log", dbSanitized, DateTime.Now);
            logPath = Path.Combine(logDir, fileName);

            writer = new StreamWriter(new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read), Encoding.UTF8);
            writer.WriteLine("====================================================================");
            writer.WriteLine(" mySQLPunk Oracle Import Execution Log");
            writer.WriteLine(string.Format(" Target Schema/Owner : {0}", targetDatabase));
            writer.WriteLine(string.Format(" Start Time          : {0:yyyy-MM-dd HH:mm:ss}", DateTime.Now));
            writer.WriteLine(" Architecture        : Streaming State Machine Lexer with PL/SQL Block Delimiters");
            writer.WriteLine(" Spatial Support     : MDSYS.SDO_GEOMETRY, WKB & USER_SDO_GEOM_METADATA");
            writer.WriteLine("====================================================================");
            writer.WriteLine();
            writer.Flush();
        }

        public void LogInfo(string message)
        {
            if (writer == null) return;
            writer.WriteLine(string.Format("[{0:HH:mm:ss.fff}] [INFO] {1}", DateTime.Now, message));
        }

        public void LogStatement(int line, string sqlPreview, bool success, string error = null)
        {
            if (writer == null) return;
            string status = success ? "OK" : "ERROR";
            writer.WriteLine(string.Format("[{0:HH:mm:ss.fff}] [{1}] Line {2}: {3}", DateTime.Now, status, line, sqlPreview));
            if (!success && !string.IsNullOrWhiteSpace(error))
            {
                writer.WriteLine(string.Format("                 └─ Detail: {0}", error));
            }
        }

        public void LogManifest(Dictionary<string, long> expected, Dictionary<string, long> actual, bool allMatched)
        {
            if (writer == null) return;
            writer.WriteLine();
            writer.WriteLine("--------------------------------------------------------------------");
            writer.WriteLine(" Native Manifest Verification Summary");
            writer.WriteLine(string.Format(" Overall Result: {0}", allMatched ? "PASS (All tables match declared rows)" : "FAIL (Discrepancy detected)"));
            writer.WriteLine("--------------------------------------------------------------------");
            foreach (var kvp in expected)
            {
                long act = actual.ContainsKey(kvp.Key) ? actual[kvp.Key] : -1;
                string status = (act == kvp.Value) ? "[MATCH]" : "[MISMATCH]";
                writer.WriteLine(string.Format("  {0,-10} Table [{1}] -> Expected: {2:N0}, Restored: {3:N0}", status, kvp.Key, kvp.Value, act));
            }
            writer.WriteLine("--------------------------------------------------------------------");
            writer.WriteLine();
        }

        public void LogSummary(int executed, int failed, long affectedRows, TimeSpan elapsed, bool success)
        {
            if (writer == null) return;
            writer.WriteLine();
            writer.WriteLine("====================================================================");
            writer.WriteLine(" Oracle Import Completed Summary");
            writer.WriteLine(string.Format(" Status             : {0}", success ? "SUCCESS" : "FAILED / COMPLETED WITH ERRORS"));
            writer.WriteLine(string.Format(" Statements Executed: {0:N0}", executed));
            writer.WriteLine(string.Format(" Statements Failed  : {0:N0}", failed));
            writer.WriteLine(string.Format(" Total Rows Affected: {0:N0}", affectedRows));
            writer.WriteLine(string.Format(" Elapsed Time       : {0:hh\\:mm\\:ss\\.fff}", elapsed));
            writer.WriteLine("====================================================================");
            writer.Flush();
        }

        public void Flush()
        {
            try { writer?.Flush(); } catch { }
        }

        public void Dispose()
        {
            try
            {
                writer?.Flush();
                writer?.Dispose();
            }
            catch { }
        }
    }
}
