using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace mySQLPunk.lib
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum ScheduledJobType
    {
        Query,
        Export,
        Backup,
        Import,
        Transfer
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum ScheduledJobScheduleKind
    {
        /// <summary>每天 DailyTime。</summary>
        Daily,

        /// <summary>每週 WeekDays 的 DailyTime。</summary>
        Weekly,

        /// <summary>從 DailyTime 開始每 IntervalHours 小時。</summary>
        Hourly,

        /// <summary>目前使用者登入 Windows 時（延遲一分鐘）。</summary>
        Logon
    }

    /// <summary>傳輸作業的一張表；Mode 為 create／append／replace。</summary>
    public sealed class ScheduledTransferTable
    {
        public string Source { get; set; }
        public string Target { get; set; }
        [JsonConverter(typeof(StringEnumConverter))]
        public TransferMode Mode { get; set; }
    }

    /// <summary>傳輸作業表格清單的文字格式：每行「來源 => 目標 : create／append／replace」，目標與方式可省略。</summary>
    public static class ScheduledTransferTableText
    {
        public static List<ScheduledTransferTable> Parse(string text)
        {
            List<ScheduledTransferTable> tables = new List<ScheduledTransferTable>();
            string[] lines = (text ?? string.Empty).Replace("\r\n", "\n").Split('\n');
            for (int index = 0; index < lines.Length; index++)
            {
                string line = lines[index].Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                TransferMode mode = TransferMode.Append;
                bool explicitMode = false;
                int colon = line.LastIndexOf(':');
                if (colon > 0)
                {
                    string word = line.Substring(colon + 1).Trim().ToLowerInvariant();
                    if (word == "create" || word == "建立") mode = TransferMode.CreateNew;
                    else if (word == "append" || word == "附加") mode = TransferMode.Append;
                    else if (word == "replace" || word == "取代") mode = TransferMode.ReplaceData;
                    else throw new FormatException(Localization.Format("Automation.TransferLineInvalid", index + 1));
                    explicitMode = true;
                    line = line.Substring(0, colon).Trim();
                }
                string[] parts = line.Split(new[] { "=>" }, StringSplitOptions.None);
                if (parts.Length > 2 || parts[0].Trim().Length == 0) throw new FormatException(Localization.Format("Automation.TransferLineInvalid", index + 1));
                tables.Add(new ScheduledTransferTable
                {
                    Source = parts[0].Trim(),
                    Target = parts.Length == 2 && parts[1].Trim().Length > 0 ? parts[1].Trim() : parts[0].Trim(),
                    Mode = explicitMode ? mode : TransferMode.Append
                });
            }
            return tables;
        }

        public static string Format(IEnumerable<ScheduledTransferTable> tables)
        {
            return string.Join(Environment.NewLine, (tables ?? Enumerable.Empty<ScheduledTransferTable>()).Select(table =>
                table.Source + (string.Equals(table.Source, table.Target, StringComparison.Ordinal) ? string.Empty : " => " + table.Target) + " : " +
                (table.Mode == TransferMode.CreateNew ? "create" : table.Mode == TransferMode.ReplaceData ? "replace" : "append")));
        }
    }

    public sealed class ScheduledJobDefinition
    {
        public int Version { get; set; } = 1;
        public string Id { get; set; }
        public string Name { get; set; }
        public ScheduledJobType Type { get; set; }
        public string ProfileName { get; set; }
        public string ConnectionName { get; set; }
        public string DatabaseName { get; set; }
        public string Sql { get; set; }
        public string OutputPath { get; set; }

        [JsonConverter(typeof(StringEnumConverter))]
        public QueryResultExportFormat ExportFormat { get; set; } = QueryResultExportFormat.Csv;

        public string DailyTime { get; set; } = "02:00";
        public bool ScheduleEnabled { get; set; }
        public ScheduledJobScheduleKind ScheduleKind { get; set; }
        /// <summary>每週排程的星期（依 DayOfWeek 名稱，例如 Monday）。</summary>
        public List<DayOfWeek> WeekDays { get; set; } = new List<DayOfWeek>();
        public int IntervalHours { get; set; } = 1;
        public string CreatedUtc { get; set; }
        public string UpdatedUtc { get; set; }

        /// <summary>匯入：CSV 路徑（可用 {yyyyMMdd} 等替代文字）、目標資料表與格式。</summary>
        public string InputPath { get; set; }
        public string TargetTable { get; set; }
        public string CsvDelimiter { get; set; } = ",";
        public bool CsvHasHeader { get; set; } = true;

        /// <summary>傳輸：目標連線（同一個設定檔）與資料表清單；空清單代表來源所有資料表。</summary>
        public string TargetConnectionName { get; set; }
        public string TargetDatabaseName { get; set; }
        public List<ScheduledTransferTable> TransferTables { get; set; } = new List<ScheduledTransferTable>();
        /// <summary>含「取代資料」時必須等於目標資料庫名稱，代表建立作業時已確認會刪除目標資料。</summary>
        public string ConfirmedTargetDatabase { get; set; }

        /// <summary>失敗後重試次數（0–5）與間隔秒數（0–3600）。</summary>
        public int RetryCount { get; set; }
        public int RetryDelaySeconds { get; set; } = 60;

        /// <summary>完成後 POST 執行結果 JSON 的網址；https，或僅限本機的 http。</summary>
        public string WebhookUrl { get; set; }
        public bool NotifyOnlyOnFailure { get; set; }

        /// <summary>通知信收件人（逗號分隔，最多 10 位）；SMTP 設定由所有作業共用。</summary>
        public string EmailTo { get; set; }
    }

    public sealed class ScheduledJobConnectionOption
    {
        public string Name { get; set; }
        public string Provider { get; set; }
        public string InitialDatabase { get; set; }

        public string DisplayName
        {
            get
            {
                return string.IsNullOrWhiteSpace(Provider) ? Name : Name + " (" + Provider + ")";
            }
        }

        public override string ToString()
        {
            return DisplayName;
        }
    }

    public sealed class ScheduledJobRunRecord
    {
        public string ExecutionId { get; set; }
        public string JobId { get; set; }
        public string JobName { get; set; }

        [JsonConverter(typeof(StringEnumConverter))]
        public ScheduledJobType JobType { get; set; }

        public string StartedUtc { get; set; }
        public string FinishedUtc { get; set; }
        public string Status { get; set; }
        public long ElapsedMilliseconds { get; set; }
        public long Rows { get; set; }
        public string OutputPath { get; set; }
        public string Message { get; set; }
        public string RecordPath { get; set; }
        public int Attempts { get; set; }
        public string Notification { get; set; }
    }

    public sealed class ScheduledJobStoreSnapshot
    {
        public List<ScheduledJobDefinition> Jobs { get; } = new List<ScheduledJobDefinition>();
        public List<string> Warnings { get; } = new List<string>();
    }

    public sealed class ScheduledJobCliResult
    {
        public bool Handled { get; set; }
        public int ExitCode { get; set; }
        public string Message { get; set; }
        public ScheduledJobRunRecord RunRecord { get; set; }
    }

    public static class ScheduledJobValidator
    {
        private static readonly HashSet<string> ReadOnlyLeadingKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SELECT", "SHOW", "EXPLAIN", "DESC", "DESCRIBE"
        };

        private static readonly HashSet<string> MutatingKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "INSERT", "UPDATE", "DELETE", "MERGE", "REPLACE", "CREATE", "ALTER", "DROP", "TRUNCATE",
            "GRANT", "REVOKE", "CALL", "EXEC", "EXECUTE", "VACUUM", "ATTACH", "DETACH"
        };

        public static void Validate(ScheduledJobDefinition job)
        {
            if (job == null) throw new ArgumentNullException("job");
            if (job.Version != 1) throw new InvalidOperationException(Localization.Format("Automation.UnsupportedJobVersion", job.Version));
            if (!Enum.IsDefined(typeof(ScheduledJobType), job.Type)) throw new InvalidOperationException(Localization.T("Automation.InvalidJobType"));

            if (string.IsNullOrWhiteSpace(job.Id)) job.Id = Guid.NewGuid().ToString("N");
            Guid parsedId;
            if (!Guid.TryParse(job.Id, out parsedId)) throw new InvalidOperationException(Localization.T("Automation.InvalidJobId"));
            job.Id = parsedId.ToString("N");

            job.Name = (job.Name ?? string.Empty).Trim();
            if (job.Name.Length == 0) throw new InvalidOperationException(Localization.T("Automation.JobNameRequired"));
            if (job.Name.Length > 80) throw new InvalidOperationException(Localization.T("Automation.JobNameTooLong"));
            if (string.IsNullOrWhiteSpace(job.ProfileName)) job.ProfileName = "default";
            else job.ProfileName = job.ProfileName.Trim();
            if (string.IsNullOrWhiteSpace(job.ConnectionName)) throw new InvalidOperationException(Localization.T("Automation.ConnectionRequired"));
            job.ConnectionName = job.ConnectionName.Trim();
            if (string.IsNullOrWhiteSpace(job.DatabaseName)) throw new InvalidOperationException(Localization.T("Automation.DatabaseRequired"));

            DateTime parsedTime;
            if (!DateTime.TryParseExact(job.DailyTime ?? string.Empty, "HH:mm", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out parsedTime))
            {
                throw new InvalidOperationException(Localization.T("Automation.InvalidDailyTime"));
            }
            job.DailyTime = parsedTime.ToString("HH:mm", CultureInfo.InvariantCulture);

            if (job.Type == ScheduledJobType.Query || job.Type == ScheduledJobType.Export)
            {
                if (string.IsNullOrWhiteSpace(job.Sql)) throw new InvalidOperationException(Localization.T("Automation.SqlRequired"));
                string reason;
                if (!IsReadOnlySql(job.Sql, out reason))
                {
                    throw new InvalidOperationException(Localization.Format("Automation.ReadOnlySqlRequired", reason));
                }
            }

            if ((job.Type == ScheduledJobType.Export || job.Type == ScheduledJobType.Backup) &&
                string.IsNullOrWhiteSpace(job.OutputPath))
            {
                throw new InvalidOperationException(Localization.T("Automation.OutputPathRequired"));
            }

            if (job.Type == ScheduledJobType.Export && !Enum.IsDefined(typeof(QueryResultExportFormat), job.ExportFormat))
            {
                throw new InvalidOperationException(Localization.T("Automation.InvalidExportFormat"));
            }

            if (job.Type == ScheduledJobType.Import)
            {
                if (string.IsNullOrWhiteSpace(job.InputPath)) throw new InvalidOperationException(Localization.T("Automation.InputPathRequired"));
                if (string.IsNullOrWhiteSpace(job.TargetTable)) throw new InvalidOperationException(Localization.T("Automation.TargetTableRequired"));
                job.TargetTable = job.TargetTable.Trim();
                if (job.CsvDelimiter != "," && job.CsvDelimiter != ";" && job.CsvDelimiter != "\\t" && job.CsvDelimiter != "|")
                {
                    throw new InvalidOperationException(Localization.T("Automation.InvalidDelimiter"));
                }
            }

            if (job.Type == ScheduledJobType.Transfer)
            {
                if (string.IsNullOrWhiteSpace(job.TargetConnectionName)) throw new InvalidOperationException(Localization.T("Automation.TargetConnectionRequired"));
                if (string.IsNullOrWhiteSpace(job.TargetDatabaseName)) throw new InvalidOperationException(Localization.T("Automation.TargetDatabaseRequired"));
                job.TargetConnectionName = job.TargetConnectionName.Trim();
                job.TargetDatabaseName = job.TargetDatabaseName.Trim();
                job.TransferTables = (job.TransferTables ?? new List<ScheduledTransferTable>())
                    .Where(table => table != null && !string.IsNullOrWhiteSpace(table.Source)).ToList();
                foreach (ScheduledTransferTable table in job.TransferTables)
                {
                    table.Source = table.Source.Trim();
                    table.Target = string.IsNullOrWhiteSpace(table.Target) ? table.Source : table.Target.Trim();
                    if (!Enum.IsDefined(typeof(TransferMode), table.Mode)) throw new InvalidOperationException(Localization.T("Automation.InvalidTransferMode"));
                }
                if (string.Equals(job.ConnectionName, job.TargetConnectionName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(job.DatabaseName, job.TargetDatabaseName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(Localization.T("Automation.TransferSameDatabase"));
                }
                if (job.TransferTables.Any(table => table.Mode == TransferMode.ReplaceData) &&
                    !string.Equals(job.ConfirmedTargetDatabase, job.TargetDatabaseName, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(Localization.T("Automation.ReplaceNeedsConfirmation"));
                }
            }

            if (!Enum.IsDefined(typeof(ScheduledJobScheduleKind), job.ScheduleKind)) throw new InvalidOperationException(Localization.T("Automation.InvalidScheduleKind"));
            job.WeekDays = (job.WeekDays ?? new List<DayOfWeek>()).Where(day => Enum.IsDefined(typeof(DayOfWeek), day)).Distinct().OrderBy(day => day).ToList();
            if (job.ScheduleKind == ScheduledJobScheduleKind.Weekly && job.WeekDays.Count == 0) throw new InvalidOperationException(Localization.T("Automation.WeekDaysRequired"));
            if (job.ScheduleKind == ScheduledJobScheduleKind.Hourly && (job.IntervalHours < 1 || job.IntervalHours > 24))
            {
                throw new InvalidOperationException(Localization.T("Automation.InvalidIntervalHours"));
            }

            if (job.RetryCount < 0 || job.RetryCount > 5) throw new InvalidOperationException(Localization.T("Automation.InvalidRetryCount"));
            if (job.RetryDelaySeconds < 0 || job.RetryDelaySeconds > 3600) throw new InvalidOperationException(Localization.T("Automation.InvalidRetryDelay"));
            if (!string.IsNullOrWhiteSpace(job.EmailTo))
            {
                job.EmailTo = string.Join(", ", AutomationEmailService.ParseRecipients(job.EmailTo).Select(address => address.Address));
            }
            if (!string.IsNullOrWhiteSpace(job.WebhookUrl))
            {
                job.WebhookUrl = job.WebhookUrl.Trim();
                Uri webhook;
                if (!Uri.TryCreate(job.WebhookUrl, UriKind.Absolute, out webhook) ||
                    !(webhook.Scheme == Uri.UriSchemeHttps || webhook.Scheme == Uri.UriSchemeHttp && webhook.IsLoopback) ||
                    !string.IsNullOrEmpty(webhook.UserInfo))
                {
                    throw new InvalidOperationException(Localization.T("Automation.InvalidWebhook"));
                }
            }
        }

        public static bool IsReadOnlySql(string sql, out string reason)
        {
            reason = string.Empty;
            List<string> allTokens;
            List<string> topLevelTokens;
            bool multipleStatements;
            Tokenize(sql, out allTokens, out topLevelTokens, out multipleStatements);
            if (allTokens.Count == 0)
            {
                reason = Localization.T("Automation.EmptySql");
                return false;
            }
            if (multipleStatements)
            {
                reason = Localization.T("Automation.MultipleStatementsNotAllowed");
                return false;
            }

            foreach (string token in allTokens)
            {
                if (MutatingKeywords.Contains(token))
                {
                    reason = Localization.Format("Automation.MutatingKeywordFound", token.ToUpperInvariant());
                    return false;
                }
            }

            string leading = topLevelTokens.Count == 0 ? string.Empty : topLevelTokens[0];
            if (string.Equals(leading, "WITH", StringComparison.OrdinalIgnoreCase))
            {
                leading = topLevelTokens.FirstOrDefault(token => ReadOnlyLeadingKeywords.Contains(token) || MutatingKeywords.Contains(token)) ?? string.Empty;
            }
            if (!ReadOnlyLeadingKeywords.Contains(leading))
            {
                reason = Localization.Format("Automation.UnsupportedReadOnlyStatement", string.IsNullOrWhiteSpace(leading) ? "?" : leading.ToUpperInvariant());
                return false;
            }
            if (topLevelTokens.Any(token => string.Equals(token, "INTO", StringComparison.OrdinalIgnoreCase)))
            {
                reason = Localization.T("Automation.SelectIntoNotAllowed");
                return false;
            }
            if (string.Equals(leading, "EXPLAIN", StringComparison.OrdinalIgnoreCase) &&
                allTokens.Any(token => string.Equals(token, "ANALYZE", StringComparison.OrdinalIgnoreCase)))
            {
                reason = Localization.T("Automation.ExplainAnalyzeNotAllowed");
                return false;
            }
            return true;
        }

        /// <summary>
        /// 讓其他安全分類器（如 AI 代為操作的 SQL 分級）共用同一套註解/字串感知的 tokenizer，
        /// 避免兩套解析器對同一句 SQL 給出不同判定。
        /// </summary>
        public static void TryGetSqlTokens(string sql, out List<string> allTokens, out List<string> topLevelTokens, out bool multipleStatements)
        {
            Tokenize(sql, out allTokens, out topLevelTokens, out multipleStatements);
        }

        private static void Tokenize(string sql, out List<string> allTokens, out List<string> topLevelTokens, out bool multipleStatements)
        {
            allTokens = new List<string>();
            topLevelTokens = new List<string>();
            multipleStatements = false;
            string value = sql ?? string.Empty;
            int depth = 0;
            bool statementEnded = false;
            int index = 0;
            while (index < value.Length)
            {
                char current = value[index];
                if (char.IsWhiteSpace(current))
                {
                    index++;
                    continue;
                }
                if (current == '-' && index + 1 < value.Length && value[index + 1] == '-')
                {
                    index += 2;
                    while (index < value.Length && value[index] != '\r' && value[index] != '\n') index++;
                    continue;
                }
                if (current == '/' && index + 1 < value.Length && value[index + 1] == '*')
                {
                    // MySQL 的 /*! ... */ 不是一般註解，伺服器可能執行其中內容；保守視為可寫入語句。
                    if (index + 2 < value.Length && value[index + 2] == '!')
                    {
                        allTokens.Add("EXECUTE");
                        if (depth == 0) topLevelTokens.Add("EXECUTE");
                    }
                    int end = value.IndexOf("*/", index + 2, StringComparison.Ordinal);
                    index = end < 0 ? value.Length : end + 2;
                    continue;
                }
                if (current == '\'' || current == '"' || current == '`')
                {
                    index = SkipQuoted(value, index, current);
                    if (statementEnded) multipleStatements = true;
                    continue;
                }
                if (current == '[')
                {
                    index++;
                    while (index < value.Length)
                    {
                        if (value[index] == ']' && index + 1 < value.Length && value[index + 1] == ']') { index += 2; continue; }
                        if (value[index++] == ']') break;
                    }
                    if (statementEnded) multipleStatements = true;
                    continue;
                }
                if (current == '$')
                {
                    int tagEnd = value.IndexOf('$', index + 1);
                    if (tagEnd >= 0)
                    {
                        string tag = value.Substring(index, tagEnd - index + 1);
                        if (tag.Skip(1).Take(tag.Length - 2).All(ch => char.IsLetterOrDigit(ch) || ch == '_'))
                        {
                            int quoteEnd = value.IndexOf(tag, tagEnd + 1, StringComparison.Ordinal);
                            index = quoteEnd < 0 ? value.Length : quoteEnd + tag.Length;
                            if (statementEnded) multipleStatements = true;
                            continue;
                        }
                    }
                }
                if (current == '(') { depth++; index++; if (statementEnded) multipleStatements = true; continue; }
                if (current == ')') { if (depth > 0) depth--; index++; if (statementEnded) multipleStatements = true; continue; }
                if (current == ';') { statementEnded = true; index++; continue; }
                if (char.IsLetter(current) || current == '_')
                {
                    int start = index++;
                    while (index < value.Length && (char.IsLetterOrDigit(value[index]) || value[index] == '_' || value[index] == '$')) index++;
                    string token = value.Substring(start, index - start);
                    allTokens.Add(token);
                    if (depth == 0) topLevelTokens.Add(token);
                    if (statementEnded) multipleStatements = true;
                    continue;
                }
                if (statementEnded) multipleStatements = true;
                index++;
            }
        }

        private static int SkipQuoted(string value, int start, char quote)
        {
            int index = start + 1;
            while (index < value.Length)
            {
                if (value[index] == quote)
                {
                    if (index + 1 < value.Length && value[index + 1] == quote) { index += 2; continue; }
                    return index + 1;
                }
                if (value[index] == '\\' && index + 1 < value.Length) index += 2;
                else index++;
            }
            return value.Length;
        }
    }

    public sealed class ScheduledJobStore
    {
        private static readonly JsonSerializerSettings JsonSettings = BuildJsonSettings();
        private readonly string rootDirectory;

        public ScheduledJobStore(string rootDirectory = null)
        {
            this.rootDirectory = string.IsNullOrWhiteSpace(rootDirectory) ? GetDefaultRootDirectory() : Path.GetFullPath(rootDirectory);
        }

        public string RootDirectory { get { return rootDirectory; } }
        public string JobsDirectory { get { return Path.Combine(rootDirectory, "jobs"); } }
        public string RunsDirectory { get { return Path.Combine(rootDirectory, "runs"); } }

        public static string GetDefaultRootDirectory()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local)) local = AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(local, "mySQLPunk", "automation");
        }

        public string GetJobPath(string jobId)
        {
            Guid parsed;
            if (!Guid.TryParse(jobId, out parsed)) throw new InvalidOperationException(Localization.T("Automation.InvalidJobId"));
            return Path.Combine(JobsDirectory, parsed.ToString("N") + ".json");
        }

        public string SaveJob(ScheduledJobDefinition job)
        {
            ScheduledJobValidator.Validate(job);
            DateTime now = DateTime.UtcNow;
            if (string.IsNullOrWhiteSpace(job.CreatedUtc)) job.CreatedUtc = now.ToString("o");
            job.UpdatedUtc = now.ToString("o");
            Directory.CreateDirectory(JobsDirectory);
            string path = GetJobPath(job.Id);
            WriteJsonAtomic(path, JsonConvert.SerializeObject(job, Formatting.Indented, JsonSettings));
            return path;
        }

        public ScheduledJobDefinition LoadJob(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException(Localization.T("Automation.JobPathRequired"), "path");
            string fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath)) throw new FileNotFoundException(Localization.Format("Automation.JobFileNotFound", fullPath), fullPath);
            ScheduledJobDefinition job = JsonConvert.DeserializeObject<ScheduledJobDefinition>(File.ReadAllText(fullPath, Encoding.UTF8), JsonSettings);
            ScheduledJobValidator.Validate(job);
            return job;
        }

        public ScheduledJobStoreSnapshot LoadJobs()
        {
            ScheduledJobStoreSnapshot snapshot = new ScheduledJobStoreSnapshot();
            if (!Directory.Exists(JobsDirectory)) return snapshot;
            foreach (string path in Directory.GetFiles(JobsDirectory, "*.json").OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
            {
                try { snapshot.Jobs.Add(LoadJob(path)); }
                catch (Exception ex) { snapshot.Warnings.Add(Path.GetFileName(path) + ": " + ExceptionMessageService.GetReason(ex)); }
            }
            snapshot.Jobs.Sort((left, right) => StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name));
            return snapshot;
        }

        public void DeleteJob(string jobId)
        {
            string path = GetJobPath(jobId);
            if (File.Exists(path)) File.Delete(path);
        }

        public string SaveRun(ScheduledJobRunRecord record)
        {
            if (record == null) throw new ArgumentNullException("record");
            Guid jobId;
            if (!Guid.TryParse(record.JobId, out jobId)) throw new InvalidOperationException(Localization.T("Automation.InvalidJobId"));
            Guid executionId;
            if (!Guid.TryParse(record.ExecutionId, out executionId)) throw new InvalidOperationException(Localization.T("Automation.InvalidExecutionId"));

            string directory = Path.Combine(RunsDirectory, jobId.ToString("N"));
            Directory.CreateDirectory(directory);
            DateTime started;
            if (!DateTime.TryParse(record.StartedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out started)) started = DateTime.UtcNow;
            string path = Path.Combine(directory, started.ToUniversalTime().ToString("yyyyMMddTHHmmssfff") + "-" + executionId.ToString("N") + ".json");
            record.RecordPath = path;
            WriteJsonAtomic(path, JsonConvert.SerializeObject(record, Formatting.Indented, JsonSettings));
            return path;
        }

        public List<ScheduledJobRunRecord> LoadRecentRuns(string jobId, int maximum = 50)
        {
            Guid parsed;
            if (!Guid.TryParse(jobId, out parsed)) return new List<ScheduledJobRunRecord>();
            string directory = Path.Combine(RunsDirectory, parsed.ToString("N"));
            if (!Directory.Exists(directory)) return new List<ScheduledJobRunRecord>();
            List<ScheduledJobRunRecord> output = new List<ScheduledJobRunRecord>();
            foreach (string path in Directory.GetFiles(directory, "*.json").OrderByDescending(item => item, StringComparer.OrdinalIgnoreCase).Take(Math.Max(1, maximum)))
            {
                try
                {
                    ScheduledJobRunRecord record = JsonConvert.DeserializeObject<ScheduledJobRunRecord>(File.ReadAllText(path, Encoding.UTF8), JsonSettings);
                    if (record != null) { record.RecordPath = path; output.Add(record); }
                }
                catch { }
            }
            return output;
        }

        private static JsonSerializerSettings BuildJsonSettings()
        {
            JsonSerializerSettings settings = new JsonSerializerSettings
            {
                MissingMemberHandling = MissingMemberHandling.Ignore,
                NullValueHandling = NullValueHandling.Ignore
            };
            settings.Converters.Add(new StringEnumConverter());
            return settings;
        }

        private static void WriteJsonAtomic(string path, string json)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            string tempPath = path + ".writing";
            try
            {
                File.WriteAllText(tempPath, json, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(tempPath, path, null);
                else File.Move(tempPath, path);
            }
            finally
            {
                if (File.Exists(tempPath)) { try { File.Delete(tempPath); } catch { } }
            }
        }
    }

    public static class AutomationConnectionProfileService
    {
        public static List<string> GetProfileNames(string applicationDirectory = null)
        {
            string baseDirectory = ResolveApplicationDirectory(applicationDirectory);
            List<string> output = new List<string> { "default" };
            string directory = Path.Combine(baseDirectory, "connection_profiles");
            if (!Directory.Exists(directory)) return output;
            foreach (string path in Directory.GetFiles(directory, "*.json"))
            {
                string name;
                try { name = Uri.UnescapeDataString(Path.GetFileNameWithoutExtension(path)); }
                catch { name = Path.GetFileNameWithoutExtension(path); }
                if (!string.IsNullOrWhiteSpace(name) && !output.Contains(name, StringComparer.OrdinalIgnoreCase)) output.Add(name);
            }
            return output.OrderBy(name => string.Equals(name, "default", StringComparison.OrdinalIgnoreCase) ? string.Empty : name,
                StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        public static List<ScheduledJobConnectionOption> LoadConnectionOptions(string profileName, string applicationDirectory = null)
        {
            return LoadConnectionDictionaries(profileName, applicationDirectory, false)
                .Select(connection => new ScheduledJobConnectionOption
                {
                    Name = GetValue(connection, "conn_name"),
                    Provider = ConnectionConfigurationService.NormalizeProvider(GetValue(connection, "db_kind")),
                    InitialDatabase = BuildInitialDatabase(connection)
                })
                .Where(option => !string.IsNullOrWhiteSpace(option.Name))
                .OrderBy(option => option.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        public static Dictionary<string, object> LoadConnection(string profileName, string connectionName, string applicationDirectory = null)
        {
            List<Dictionary<string, object>> matches = LoadConnectionDictionaries(profileName, applicationDirectory, true)
                .Where(connection => string.Equals(GetValue(connection, "conn_name"), connectionName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 0) throw new InvalidOperationException(Localization.Format("Automation.ConnectionNotFound", profileName ?? "default", connectionName ?? string.Empty));
            if (matches.Count > 1) throw new InvalidOperationException(Localization.Format("Automation.DuplicateConnectionName", profileName ?? "default", connectionName ?? string.Empty));
            return matches[0];
        }

        private static List<Dictionary<string, object>> LoadConnectionDictionaries(string profileName, string applicationDirectory, bool includeCredential)
        {
            string baseDirectory = ResolveApplicationDirectory(applicationDirectory);
            string normalizedProfile = string.IsNullOrWhiteSpace(profileName) ? "default" : profileName.Trim();
            string path = string.Equals(normalizedProfile, "default", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(baseDirectory, "setting.ini")
                : Path.Combine(baseDirectory, "connection_profiles", Uri.EscapeDataString(normalizedProfile) + ".json");
            if (!File.Exists(path)) throw new FileNotFoundException(Localization.Format("Automation.ProfileFileNotFound", normalizedProfile), path);

            JToken root = JToken.Parse(File.ReadAllText(path, Encoding.UTF8));
            JArray array = root.Type == JTokenType.Array ? (JArray)root : root["connections"] as JArray;
            if (array == null) return new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> output = new List<Dictionary<string, object>>();
            foreach (JToken token in array)
            {
                Dictionary<string, object> connection = token.ToObject<Dictionary<string, object>>() ?? new Dictionary<string, object>();
                NormalizeConnection(connection);
                connection["username"] = Crypto.Decrypt(GetValue(connection, "username"));
                connection["pwd"] = includeCredential ? LoadPassword(connection) : string.Empty;
                if (includeCredential) LoadSecuritySecrets(connection);
                output.Add(connection);
            }
            return output;
        }

        private static string LoadPassword(Dictionary<string, object> connection)
        {
            string target = GetValue(connection, "credential_target");
            string password;
            if (!string.IsNullOrWhiteSpace(target))
            {
                if (WindowsCredentialService.TryReadPassword(target, out password)) return password;
                throw new InvalidOperationException(Localization.Format("Automation.CredentialUnavailable", GetValue(connection, "conn_name")));
            }
            return Crypto.Decrypt(GetValue(connection, "pwd"));
        }

        private static void LoadSecuritySecrets(Dictionary<string, object> connection)
        {
            string target = GetValue(connection, "security_credential_target");
            string payload;
            if (string.IsNullOrWhiteSpace(target))
            {
                ConnectionSecuritySettingsService.ApplySerializedSecrets(connection, string.Empty);
                return;
            }
            if (!WindowsCredentialService.TryReadPassword(target, out payload))
                throw new InvalidOperationException(Localization.Format("Automation.CredentialUnavailable", GetValue(connection, "conn_name")));
            ConnectionSecuritySettingsService.ApplySerializedSecrets(connection, payload);
        }

        private static string BuildInitialDatabase(Dictionary<string, object> connection)
        {
            if (ConnectionConfigurationService.NormalizeProvider(GetValue(connection, "db_kind")) == "sqlite") return "main";
            return GetValue(connection, "initial_database");
        }

        private static void NormalizeConnection(Dictionary<string, object> connection)
        {
            CopyIfMissing(connection, "name", "conn_name");
            CopyIfMissing(connection, "ip", "host");
            CopyIfMissing(connection, "kind", "db_kind");
            CopyIfMissing(connection, "login_id", "username");
            foreach (string key in new[] { "pwd", "credential_target", "initial_database", "trusted_connection", "path", "port", "service_name", "sid", "tns_name", "connection_type", "oracle_identifier_type" })
            {
                if (!connection.ContainsKey(key)) connection[key] = string.Empty;
            }
            ConnectionSecuritySettingsService.Normalize(connection);
        }

        private static void CopyIfMissing(Dictionary<string, object> connection, string oldKey, string newKey)
        {
            if (!connection.ContainsKey(newKey) && connection.ContainsKey(oldKey)) connection[newKey] = connection[oldKey];
        }

        private static string GetValue(Dictionary<string, object> connection, string key)
        {
            return ConnectionConfigurationService.GetValue(connection, key);
        }

        private static string ResolveApplicationDirectory(string applicationDirectory)
        {
            return string.IsNullOrWhiteSpace(applicationDirectory)
                ? Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory)
                : Path.GetFullPath(applicationDirectory);
        }
    }

    public static class ScheduledJobExecutionService
    {
        public static ScheduledJobRunRecord ExecuteFromProfile(ScheduledJobDefinition job, ScheduledJobStore store = null, string applicationDirectory = null)
        {
            Func<string, string, IDatabase> open = (connectionName, databaseName) =>
            {
                Dictionary<string, object> connection = AutomationConnectionProfileService.LoadConnection(job.ProfileName, connectionName, applicationDirectory);
                string provider = ConnectionConfigurationService.NormalizeProvider(ConnectionConfigurationService.GetValue(connection, "db_kind"));
                if (provider == "mysql" || provider == "postgresql" || provider == "mssql")
                {
                    connection["initial_database"] = databaseName;
                }
                return ConnectionOpenService.Open(connection, false).Database;
            };
            return Execute(job, store, () => open(job.ConnectionName, job.DatabaseName), () => open(job.TargetConnectionName, job.TargetDatabaseName));
        }

        public static ScheduledJobRunRecord Execute(ScheduledJobDefinition job, ScheduledJobStore store, Func<IDatabase> databaseFactory)
        {
            return Execute(job, store, databaseFactory, null, null);
        }

        /// <summary>
        /// 執行作業；失敗時依 RetryCount 重試（傳輸會從檢查點續傳；匯入若已寫入部分資料則不重試，以免重複），
        /// 最後寫入執行紀錄並視設定呼叫 Webhook。delay 供測試略過等待。
        /// </summary>
        public static ScheduledJobRunRecord Execute(ScheduledJobDefinition job, ScheduledJobStore store, Func<IDatabase> databaseFactory,
            Func<IDatabase> targetFactory, Action<TimeSpan> delay = null)
        {
            if (job == null) throw new ArgumentNullException("job");
            if (databaseFactory == null) throw new ArgumentNullException("databaseFactory");
            ScheduledJobValidator.Validate(job);
            store = store ?? new ScheduledJobStore();
            delay = delay ?? System.Threading.Thread.Sleep;
            ScheduledJobRunRecord record = new ScheduledJobRunRecord
            {
                ExecutionId = Guid.NewGuid().ToString("N"),
                JobId = job.Id,
                JobName = job.Name,
                JobType = job.Type,
                StartedUtc = DateTime.UtcNow.ToString("o"),
                Status = "Running",
                Rows = -1,
                Message = Localization.T("Automation.RunStarted")
            };
            Stopwatch stopwatch = Stopwatch.StartNew();
            try { store.SaveRun(record); } catch { }
            for (int attempt = 1; ; attempt++)
            {
                record.Attempts = attempt;
                try
                {
                    using (IDatabase database = databaseFactory())
                    {
                        if (database == null) throw new InvalidOperationException(Localization.T("Connection.DatabaseFactoryReturnedNull"));
                        if (job.Type == ScheduledJobType.Transfer)
                        {
                            if (targetFactory == null) throw new InvalidOperationException(Localization.T("Automation.TargetConnectionRequired"));
                            using (IDatabase target = targetFactory())
                            {
                                if (target == null) throw new InvalidOperationException(Localization.T("Connection.DatabaseFactoryReturnedNull"));
                                ExecuteTransfer(job, database, target, store, record);
                            }
                        }
                        else
                        {
                            ExecuteCore(job, database, record);
                        }
                    }
                    record.Status = "Success";
                    record.Message = attempt > 1
                        ? Localization.Format("Automation.RunSucceededAfterRetry", attempt)
                        : Localization.T("Automation.RunSucceeded");
                    break;
                }
                catch (Exception ex)
                {
                    record.Status = "Failed";
                    record.Message = ExceptionMessageService.GetReason(ex);
                    bool partialImport = ex.Data.Contains(CsvImportService.ImportedRowsKey) && Convert.ToInt64(ex.Data[CsvImportService.ImportedRowsKey], CultureInfo.InvariantCulture) > 0;
                    if (partialImport)
                    {
                        record.Message = Localization.Format("Automation.ImportPartial", ex.Data[CsvImportService.ImportedRowsKey], record.Message);
                        break;
                    }
                    if (attempt > job.RetryCount) break;
                    record.Message = Localization.Format("Automation.RetryScheduled", attempt, job.RetryCount + 1, record.Message);
                    try { store.SaveRun(record); } catch { }
                    delay(TimeSpan.FromSeconds(job.RetryDelaySeconds));
                }
            }

            stopwatch.Stop();
            record.ElapsedMilliseconds = stopwatch.ElapsedMilliseconds;
            record.FinishedUtc = DateTime.UtcNow.ToString("o");
            record.Notification = string.Join("；", new[] { Notify(job, record), AutomationEmailService.Notify(store, job, record) }.Where(item => item != null));
            if (record.Notification.Length == 0) record.Notification = null;
            try { store.SaveRun(record); } catch { }
            return record;
        }

        /// <summary>傳輸作業：同一作業未完成的檢查點會自動續傳；任何一張表失敗或列數不一致都視為失敗。</summary>
        private static void ExecuteTransfer(ScheduledJobDefinition job, IDatabase source, IDatabase target, ScheduledJobStore store, ScheduledJobRunRecord record)
        {
            string checkpoints = Path.Combine(store.RootDirectory, "transfers", job.Id);
            List<string> tables = job.TransferTables.Count > 0
                ? job.TransferTables.Select(table => table.Source).ToList()
                : source.GetTables(job.DatabaseName);
            TransferPlan fresh = DataTransferService.BuildPlan(source, job.DatabaseName, job.ConnectionName, target, job.TargetDatabaseName, job.TargetConnectionName, tables);
            foreach (ScheduledTransferTable setting in job.TransferTables)
            {
                TransferItem item = fresh.Items.First(entry => string.Equals(entry.SourceTable, setting.Source, StringComparison.OrdinalIgnoreCase));
                item.TargetTable = setting.Target;
                item.Mode = setting.Mode;
            }
            fresh.ContinueOnError = true;

            TransferPlan plan = DataTransferService.LoadUnfinished(checkpoints, fresh);
            // 作業設定改過之後，舊檢查點的表與模式不再適用，改用新的計畫。
            if (plan == null || plan.Items.Count != fresh.Items.Count ||
                plan.Items.Zip(fresh.Items, (left, right) => left.SourceTable == right.SourceTable && left.TargetTable == right.TargetTable && left.Mode == right.Mode).Any(same => !same))
            {
                plan = fresh;
            }
            else
            {
                foreach (TransferItem item in plan.Items.Where(item => item.Status == TransferItemStatus.Failed)) item.Status = TransferItemStatus.Pending;
            }

            HashSet<string> keyed = new HashSet<string>(
                SchemaModelService.Load(source, job.DatabaseName).Tables.Where(table => table.Columns.Any(column => column.IsPrimaryKey)).Select(table => table.Name),
                StringComparer.OrdinalIgnoreCase);
            DataTransferService.Run(plan, source, target, keyed, null, current => DataTransferService.SaveCheckpoint(current, checkpoints), System.Threading.CancellationToken.None);
            record.Rows = plan.Items.Where(item => item.Include).Sum(item => item.CopiedRows);
            List<TransferItem> problems = plan.Items.Where(item => item.Include && (item.Status != TransferItemStatus.Done || item.Verified != true)).ToList();
            if (problems.Count > 0)
            {
                throw new InvalidOperationException(Localization.Format("Automation.TransferProblems", problems.Count,
                    string.Join("; ", problems.Select(item => item.SourceTable + ": " + (item.Error ?? item.Warning ?? DataTransferService.ResultText(item))))));
            }
            DataTransferService.DeleteCheckpoint(plan, checkpoints);
        }

        /// <summary>POST 執行結果到 Webhook；只送作業名稱、狀態、列數、訊息與時間，不含 SQL 或認證。</summary>
        private static string Notify(ScheduledJobDefinition job, ScheduledJobRunRecord record)
        {
            if (string.IsNullOrWhiteSpace(job.WebhookUrl)) return null;
            bool success = string.Equals(record.Status, "Success", StringComparison.OrdinalIgnoreCase);
            if (job.NotifyOnlyOnFailure && success) return Localization.T("Automation.NotifySkipped");
            JObject payload = new JObject
            {
                { "job", record.JobName },
                { "jobId", record.JobId },
                { "type", record.JobType.ToString() },
                { "status", record.Status },
                { "rows", record.Rows },
                { "attempts", record.Attempts },
                { "message", record.Message },
                { "startedUtc", record.StartedUtc },
                { "finishedUtc", record.FinishedUtc },
                { "elapsedMs", record.ElapsedMilliseconds }
            };
            try
            {
                System.Net.HttpWebRequest request = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(job.WebhookUrl);
                request.Method = "POST";
                request.ContentType = "application/json; charset=utf-8";
                request.Timeout = 10000;
                request.ReadWriteTimeout = 10000;
                request.AllowAutoRedirect = false;
                request.KeepAlive = false;
                // 不等 100-continue：許多 Webhook 端點不回應它，會讓每次通知多等一段時間甚至逾時。
                request.ServicePoint.Expect100Continue = false;
                byte[] body = new UTF8Encoding(false).GetBytes(payload.ToString(Formatting.None));
                request.ContentLength = body.Length;
                using (Stream stream = request.GetRequestStream()) stream.Write(body, 0, body.Length);
                using (System.Net.HttpWebResponse response = (System.Net.HttpWebResponse)request.GetResponse())
                {
                    return Localization.Format("Automation.NotifySent", (int)response.StatusCode);
                }
            }
            catch (System.Net.WebException ex)
            {
                System.Net.HttpWebResponse response = ex.Response as System.Net.HttpWebResponse;
                return Localization.Format("Automation.NotifyFailed", response != null ? "HTTP " + (int)response.StatusCode : ex.Message);
            }
            catch (Exception ex)
            {
                return Localization.Format("Automation.NotifyFailed", ex.Message);
            }
        }

        public static string ExpandOutputPath(ScheduledJobDefinition job, DateTime localTime)
        {
            return ExpandPath(job, job == null ? string.Empty : job.OutputPath, "automation-output", localTime);
        }

        public static string ExpandPath(ScheduledJobDefinition job, string template, string defaultFolder, DateTime localTime)
        {
            string value = template ?? string.Empty;
            string safeJobName = MakeSafeFileName(job == null ? string.Empty : job.Name);
            value = value.Replace("{yyyyMMdd_HHmmss}", localTime.ToString("yyyyMMdd_HHmmss"));
            value = value.Replace("{yyyyMMdd}", localTime.ToString("yyyyMMdd"));
            value = value.Replace("{job}", safeJobName);
            if (!Path.IsPathRooted(value))
            {
                string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (string.IsNullOrWhiteSpace(documents)) documents = ScheduledJobStore.GetDefaultRootDirectory();
                value = Path.Combine(documents, "mySQLPunk", defaultFolder, value);
            }
            return Path.GetFullPath(value);
        }

        private static void ExecuteCore(ScheduledJobDefinition job, IDatabase database, ScheduledJobRunRecord record)
        {
            if (job.Type == ScheduledJobType.Query)
            {
                DataTable result = database.SelectSQL(job.Sql);
                ThrowIfQueryFailed(result);
                record.Rows = result == null ? 0 : result.Rows.Count;
                return;
            }

            if (job.Type == ScheduledJobType.Import)
            {
                string inputPath = ExpandPath(job, job.InputPath, "automation-input", DateTime.Now);
                if (!File.Exists(inputPath)) throw new FileNotFoundException(Localization.Format("Automation.InputMissing", inputPath), inputPath);
                record.OutputPath = inputPath;
                char delimiter = job.CsvDelimiter == "\\t" ? '\t' : job.CsvDelimiter[0];
                CsvImportResult imported = CsvImportService.Import(database, job.DatabaseName, job.TargetTable, inputPath, delimiter, job.CsvHasHeader, true);
                record.Rows = imported.Rows;
                return;
            }

            string outputPath = ExpandOutputPath(job, DateTime.Now);
            string directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            record.OutputPath = outputPath;

            if (job.Type == ScheduledJobType.Backup)
            {
                DatabaseDumpService.WriteDatabaseDump(database, job.DatabaseName, outputPath);
                record.Rows = -1;
                return;
            }

            if (QueryResultExportService.CanStreamFormat(job.ExportFormat))
            {
                QueryResultStreamingExportResult exported = QueryResultExportService.WriteStreaming(database, job.Sql, null, outputPath, job.ExportFormat);
                record.Rows = exported.Rows;
            }
            else
            {
                DataTable result = database.SelectSQL(job.Sql);
                ThrowIfQueryFailed(result);
                QueryResultExportService.Write(result ?? new DataTable(), outputPath, job.ExportFormat);
                record.Rows = result == null ? 0 : result.Rows.Count;
            }
        }

        private static void ThrowIfQueryFailed(DataTable result)
        {
            if (result == null || !result.ExtendedProperties.ContainsKey(my_sqlite.QueryErrorExtendedProperty)) return;
            string message = Convert.ToString(result.ExtendedProperties[my_sqlite.QueryErrorExtendedProperty]);
            if (!string.IsNullOrWhiteSpace(message)) throw new InvalidOperationException(message);
        }

        private static string MakeSafeFileName(string value)
        {
            string output = string.IsNullOrWhiteSpace(value) ? "job" : value.Trim();
            foreach (char invalid in Path.GetInvalidFileNameChars()) output = output.Replace(invalid, '_');
            return output;
        }
    }

    public static class ScheduledJobCliService
    {
        public const string RunJobCommand = "--run-scheduled-job";

        public static ScheduledJobCliResult TryRun(string[] args)
        {
            args = args ?? new string[0];
            if (args.Length == 0 || !string.Equals(args[0], RunJobCommand, StringComparison.OrdinalIgnoreCase))
            {
                return new ScheduledJobCliResult { Handled = false };
            }

            try { Localization.Load(); } catch { }
            if (args.Length != 2 || string.IsNullOrWhiteSpace(args[1]))
            {
                return new ScheduledJobCliResult
                {
                    Handled = true,
                    ExitCode = 2,
                    Message = Localization.Format("Automation.CliUsage", RunJobCommand)
                };
            }

            try
            {
                ScheduledJobStore store = new ScheduledJobStore();
                ScheduledJobDefinition job = store.LoadJob(args[1]);
                ScheduledJobRunRecord record = ScheduledJobExecutionService.ExecuteFromProfile(job, store);
                return new ScheduledJobCliResult
                {
                    Handled = true,
                    ExitCode = string.Equals(record.Status, "Success", StringComparison.OrdinalIgnoreCase) ? 0 : 1,
                    Message = Localization.Format("Automation.CliResult", record.JobName, record.Status, record.Message),
                    RunRecord = record
                };
            }
            catch (Exception ex)
            {
                return new ScheduledJobCliResult
                {
                    Handled = true,
                    ExitCode = 1,
                    Message = Localization.Format("Automation.CliFailed", ExceptionMessageService.GetReason(ex))
                };
            }
        }
    }
}
