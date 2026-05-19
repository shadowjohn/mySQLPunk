using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace mySQLPunk.lib
{
    public sealed class DiagnosticQueryLogEntry
    {
        public string TimestampUtc { get; set; }
        public string Category { get; set; }
        public string DatabaseName { get; set; }
        public string SqlPreview { get; set; }
        public string SqlSha256 { get; set; }
        public string Status { get; set; }
        public int Rows { get; set; }
        public long ElapsedMilliseconds { get; set; }
        public bool IsQuery { get; set; }
    }

    public static class DiagnosticLogService
    {
        public static void AppendQueryHistory(string databaseName, string sql, string status, long elapsedMilliseconds, int rows, bool isQuery)
        {
            if (!ApplicationOptionSettings.GetBool("AdvancedEnableDiagnosticsLog")) return;
            if (string.IsNullOrWhiteSpace(sql)) return;

            try
            {
                string directory = GetLogDirectory();
                Directory.CreateDirectory(directory);
                string path = BuildLogPath(directory, DateTime.UtcNow);
                File.AppendAllText(path, BuildQueryHistoryJsonLine(databaseName, sql, status, elapsedMilliseconds, rows, isQuery) + Environment.NewLine, new UTF8Encoding(false));
            }
            catch
            {
            }
        }

        public static string BuildQueryHistoryJsonLine(string databaseName, string sql, string status, long elapsedMilliseconds, int rows, bool isQuery)
        {
            DiagnosticQueryLogEntry entry = new DiagnosticQueryLogEntry
            {
                TimestampUtc = DateTime.UtcNow.ToString("o"),
                Category = "query-history",
                DatabaseName = databaseName ?? string.Empty,
                SqlPreview = BuildSqlPreview(sql, 500),
                SqlSha256 = ComputeSha256(sql ?? string.Empty),
                Status = string.IsNullOrWhiteSpace(status) ? "OK" : status,
                Rows = rows,
                ElapsedMilliseconds = elapsedMilliseconds,
                IsQuery = isQuery
            };

            return JsonConvert.SerializeObject(entry, Formatting.None);
        }

        public static string GetLogDirectory()
        {
            string configured = ApplicationOptionSettings.GetString("FileLogDirectory");
            if (!string.IsNullOrWhiteSpace(configured)) return configured;

            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(documents)) documents = AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(documents, "mySQLPunk", "logs");
        }

        public static string BuildLogPath(string directory, DateTime timestampUtc)
        {
            return Path.Combine(directory, "diagnostics-" + timestampUtc.ToString("yyyyMMdd") + ".jsonl");
        }

        public static string BuildSqlPreview(string sql, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(sql)) return string.Empty;
            string preview = sql.Replace("\r", " ").Replace("\n", " ").Trim();
            while (preview.Contains("  ")) preview = preview.Replace("  ", " ");
            if (maxLength <= 0 || preview.Length <= maxLength) return preview;
            return preview.Substring(0, Math.Max(0, maxLength - 3)) + "...";
        }

        private static string ComputeSha256(string value)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
                StringBuilder sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash)
                {
                    sb.Append(b.ToString("x2"));
                }
                return sb.ToString();
            }
        }
    }
}
