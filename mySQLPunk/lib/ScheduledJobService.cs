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
        Backup
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
        public string CreatedUtc { get; set; }
        public string UpdatedUtc { get; set; }
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
            return Execute(job, store, () =>
            {
                Dictionary<string, object> connection = AutomationConnectionProfileService.LoadConnection(job.ProfileName, job.ConnectionName, applicationDirectory);
                string provider = ConnectionConfigurationService.NormalizeProvider(ConnectionConfigurationService.GetValue(connection, "db_kind"));
                if (provider == "mysql" || provider == "postgresql" || provider == "mssql")
                {
                    connection["initial_database"] = job.DatabaseName;
                }
                return ConnectionOpenService.Open(connection, false).Database;
            });
        }

        public static ScheduledJobRunRecord Execute(ScheduledJobDefinition job, ScheduledJobStore store, Func<IDatabase> databaseFactory)
        {
            if (job == null) throw new ArgumentNullException("job");
            if (databaseFactory == null) throw new ArgumentNullException("databaseFactory");
            ScheduledJobValidator.Validate(job);
            store = store ?? new ScheduledJobStore();
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
            try
            {
                store.SaveRun(record);
                using (IDatabase database = databaseFactory())
                {
                    if (database == null) throw new InvalidOperationException(Localization.T("Connection.DatabaseFactoryReturnedNull"));
                    ExecuteCore(job, database, record);
                }
                record.Status = "Success";
                record.Message = Localization.T("Automation.RunSucceeded");
            }
            catch (Exception ex)
            {
                record.Status = "Failed";
                record.Message = ExceptionMessageService.GetReason(ex);
            }
            finally
            {
                stopwatch.Stop();
                record.ElapsedMilliseconds = stopwatch.ElapsedMilliseconds;
                record.FinishedUtc = DateTime.UtcNow.ToString("o");
                try { store.SaveRun(record); } catch { }
            }
            return record;
        }

        public static string ExpandOutputPath(ScheduledJobDefinition job, DateTime localTime)
        {
            string value = job == null ? string.Empty : job.OutputPath ?? string.Empty;
            string safeJobName = MakeSafeFileName(job == null ? string.Empty : job.Name);
            value = value.Replace("{yyyyMMdd_HHmmss}", localTime.ToString("yyyyMMdd_HHmmss"));
            value = value.Replace("{yyyyMMdd}", localTime.ToString("yyyyMMdd"));
            value = value.Replace("{job}", safeJobName);
            if (!Path.IsPathRooted(value))
            {
                string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (string.IsNullOrWhiteSpace(documents)) documents = ScheduledJobStore.GetDefaultRootDirectory();
                value = Path.Combine(documents, "mySQLPunk", "automation-output", value);
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
