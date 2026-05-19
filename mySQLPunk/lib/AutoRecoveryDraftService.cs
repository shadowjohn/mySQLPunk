using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace mySQLPunk.lib
{
    public sealed class QueryAutoRecoveryDraft
    {
        public int Version { get; set; }
        public string SavedAtUtc { get; set; }
        public string DatabaseName { get; set; }
        public string ConnectionHost { get; set; }
        public string Title { get; set; }
        public string Sql { get; set; }
        public string SqlSha256 { get; set; }
    }

    public static class AutoRecoveryDraftService
    {
        public static bool IsQueryAutoRecoveryEnabled()
        {
            return ApplicationOptionSettings.GetBool("AutoRecoveryQueryEnabled");
        }

        public static int GetQueryAutoRecoveryIntervalMilliseconds()
        {
            int seconds = ApplicationOptionSettings.GetInt("AutoRecoveryIntervalSeconds");
            if (seconds < 5) seconds = 5;
            if (seconds > 3600) seconds = 3600;
            return seconds * 1000;
        }

        public static string WriteQueryDraft(string databaseName, string connectionHost, string title, string sql)
        {
            if (!IsQueryAutoRecoveryEnabled()) return string.Empty;
            if (string.IsNullOrWhiteSpace(sql)) return string.Empty;

            string directory = GetQueryDraftDirectory();
            Directory.CreateDirectory(directory);
            string path = BuildQueryDraftPath(directory, databaseName, connectionHost);
            File.WriteAllText(path, BuildQueryDraftJson(databaseName, connectionHost, title, sql), new UTF8Encoding(false));
            return path;
        }

        public static string BuildQueryDraftJson(string databaseName, string connectionHost, string title, string sql)
        {
            QueryAutoRecoveryDraft draft = new QueryAutoRecoveryDraft
            {
                Version = 1,
                SavedAtUtc = DateTime.UtcNow.ToString("o"),
                DatabaseName = databaseName ?? string.Empty,
                ConnectionHost = connectionHost ?? string.Empty,
                Title = title ?? string.Empty,
                Sql = sql ?? string.Empty,
                SqlSha256 = ComputeSha256(sql ?? string.Empty)
            };

            return JsonConvert.SerializeObject(draft, Formatting.Indented);
        }

        public static string GetQueryDraftDirectory()
        {
            string queryDirectory = ApplicationOptionSettings.GetString("FileQueryDirectory");
            if (string.IsNullOrWhiteSpace(queryDirectory))
            {
                queryDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "mySQLPunk", "queries");
            }

            return Path.Combine(queryDirectory, "auto-recovery");
        }

        public static string BuildQueryDraftPath(string directory, string databaseName, string connectionHost)
        {
            string scope = (connectionHost ?? string.Empty).Trim() + "_" + (databaseName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(scope)) scope = "query";
            string hash = ComputeSha256(scope).Substring(0, 12);
            return Path.Combine(directory, "query-draft-" + SanitizeFileName(scope, 48) + "-" + hash + ".json");
        }

        private static string SanitizeFileName(string value, int maxLength)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char ch in value ?? string.Empty)
            {
                bool invalid = Array.IndexOf(Path.GetInvalidFileNameChars(), ch) >= 0;
                sb.Append(invalid || char.IsControl(ch) ? '_' : ch);
                if (maxLength > 0 && sb.Length >= maxLength) break;
            }

            string sanitized = sb.ToString().Trim(' ', '.', '_');
            return string.IsNullOrWhiteSpace(sanitized) ? "query" : sanitized;
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
