using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace mySQLPunk.lib
{
    /// <summary>編輯基準載入後 key 被其他連線改動或刪除；呼叫端應重新載入再決定是否重試。</summary>
    public sealed class RedisEditConflictException : Exception
    {
        public RedisEditConflictException(string message) : base(message) { }
    }

    public sealed class RedisMonitorMetric
    {
        public string Section = string.Empty;
        public string Name = string.Empty;
        public string Value = string.Empty;
    }

    public sealed class RedisCommandMetric
    {
        public string Command = string.Empty;
        public long Calls;
        public double MicrosecondsPerCall;
        public long FailedCalls;
        public long RejectedCalls;
    }

    public sealed class RedisMonitorSnapshot
    {
        public DateTime CapturedAtUtc;
        public readonly List<RedisMonitorMetric> Metrics = new List<RedisMonitorMetric>();
        public readonly List<RedisCommandMetric> Commands = new List<RedisCommandMetric>();
    }

    /// <summary>
    /// Redis／Garnet 第一階段 provider：連線、db 清單、keys 瀏覽與受限唯讀查詢。
    /// 與 my_mongodb 相同原則：不假裝相容關聯式 DDL／寫入，避免 RDBMS UI 誤送命令。
    /// </summary>
    public sealed class my_redis : IDatabase
    {
        private const int DefaultQueryLimit = 100;
        private const int MaxQueryLimit = 10000;
        private const int ScanBatchSize = 500;
        private const int PreviewLength = 256;
        private const int ConnectTimeoutMs = 8000;

        private readonly object _sync = new object();
        private string host = string.Empty;
        private int port = 6379;
        private string username = string.Empty;
        private string password = string.Empty;
        private bool useTls;
        private int initialDatabaseIndex;
        private string mode = ModeStandalone;
        private List<string> seeds = new List<string>();
        private string masterName = string.Empty;
        private bool sentinelAuth;
        private string connectedHost = string.Empty;
        private int connectedPort;
        private RedisCommandRouter client;

        public const string ModeStandalone = "standalone";
        public const string ModeCluster = "cluster";
        public const string ModeSentinel = "sentinel";

        /// <summary>Redis Cluster key slot（CRC16，支援 {hash tag}）。</summary>
        public static int KeySlot(string key)
        {
            return RedisCommandRouter.KeySlot(key);
        }

        /// <summary>standalone、cluster 或 sentinel。</summary>
        public string Mode { get { return mode; } }

        /// <summary>實際連上的節點（Sentinel 為解析出的 master；Cluster 為種子節點）。</summary>
        public string ConnectedAddress { get { return open ? RedisCommandRouter.FormatAddress(connectedHost, connectedPort) : string.Empty; } }
        private int selectedDatabase = -1;
        private bool open;

        public string ProviderName => "redis";
        public ConnectionState State => open ? ConnectionState.Open : ConnectionState.Closed;

        public void SetConn(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException(Localization.T("Redis.ConnectionStringRequired"), "value");
            Uri uri;
            if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out uri) ||
                (!string.Equals(uri.Scheme, "redis", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(uri.Scheme, "rediss", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException(Localization.T("Redis.ConnectionStringInvalid"), "value");

            host = uri.Host;
            port = uri.Port > 0 ? uri.Port : 6379;
            useTls = string.Equals(uri.Scheme, "rediss", StringComparison.OrdinalIgnoreCase);
            username = string.Empty;
            password = string.Empty;
            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                int split = uri.UserInfo.IndexOf(':');
                if (split >= 0)
                {
                    username = Uri.UnescapeDataString(uri.UserInfo.Substring(0, split));
                    password = Uri.UnescapeDataString(uri.UserInfo.Substring(split + 1));
                }
                else
                {
                    // redis URI 慣例：只有一段時視為密碼（預設使用者）。
                    password = Uri.UnescapeDataString(uri.UserInfo);
                }
            }
            ParseTopology(uri.Query);
            string path = (uri.AbsolutePath ?? string.Empty).Trim('/');
            initialDatabaseIndex = 0;
            if (!string.IsNullOrWhiteSpace(path))
            {
                int parsed;
                if (!int.TryParse(path, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) || parsed < 0)
                    throw new ArgumentException(Localization.T("Redis.InvalidDatabaseIndex"), "value");
                initialDatabaseIndex = parsed;
            }
            if (mode == ModeCluster && initialDatabaseIndex != 0) throw new ArgumentException(Localization.T("Redis.ClusterDatabaseZero"), "value");
        }

        /// <summary>
        /// 解析 ?mode=cluster|sentinel&amp;seeds=host:port,…&amp;master=名稱&amp;sentinelAuth=1。未知參數一律拒絕，
        /// 避免打錯字時默默退回獨立模式。
        /// </summary>
        private void ParseTopology(string query)
        {
            mode = ModeStandalone;
            seeds = new List<string>();
            masterName = string.Empty;
            sentinelAuth = false;
            string text = (query ?? string.Empty).TrimStart('?');
            if (text.Length == 0) return;
            foreach (string pair in text.Split('&'))
            {
                if (pair.Length == 0) continue;
                int equals = pair.IndexOf('=');
                string name = Uri.UnescapeDataString(equals < 0 ? pair : pair.Substring(0, equals));
                string value = equals < 0 ? string.Empty : Uri.UnescapeDataString(pair.Substring(equals + 1).Replace('+', ' '));
                switch (name)
                {
                    case "mode":
                        if (value != ModeStandalone && value != ModeCluster && value != ModeSentinel)
                            throw new ArgumentException(Localization.Format("Redis.InvalidMode", value), "value");
                        mode = value;
                        break;
                    case "seeds":
                        foreach (string item in value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            KeyValuePair<string, int> endpoint = RedisCommandRouter.SplitAddress(item.Trim());
                            seeds.Add(RedisCommandRouter.FormatAddress(endpoint.Key, endpoint.Value));
                        }
                        break;
                    case "master":
                        masterName = value.Trim();
                        break;
                    case "sentinelAuth":
                        sentinelAuth = value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
                        break;
                    default:
                        throw new ArgumentException(Localization.Format("Redis.UnknownConnectionOption", name), "value");
                }
            }
            if (mode == ModeSentinel && string.IsNullOrWhiteSpace(masterName)) throw new ArgumentException(Localization.T("Redis.SentinelMasterRequired"), "value");
        }

        public static string BuildConnectionString(string host, int port, string username, string password, bool useTls, int databaseIndex,
            string mode, IEnumerable<string> seeds, string masterName, bool sentinelAuth)
        {
            string baseText = BuildConnectionString(host, port, username, password, useTls, databaseIndex);
            string normalized = string.IsNullOrWhiteSpace(mode) ? ModeStandalone : mode.Trim().ToLowerInvariant();
            if (normalized == ModeStandalone) return baseText;
            List<string> query = new List<string> { "mode=" + Uri.EscapeDataString(normalized) };
            List<string> seedList = (seeds ?? Enumerable.Empty<string>()).Select(item => item.Trim()).Where(item => item.Length > 0).ToList();
            if (seedList.Count > 0) query.Add("seeds=" + Uri.EscapeDataString(string.Join(",", seedList)));
            if (normalized == ModeSentinel)
            {
                query.Add("master=" + Uri.EscapeDataString(masterName ?? string.Empty));
                if (sentinelAuth) query.Add("sentinelAuth=1");
            }
            return baseText + "?" + string.Join("&", query);
        }

        public static string BuildConnectionString(string host, int port, string username, string password, bool useTls, int databaseIndex)
        {
            string scheme = useTls ? "rediss" : "redis";
            string auth = string.Empty;
            if (!string.IsNullOrEmpty(username) || !string.IsNullOrEmpty(password))
            {
                auth = string.IsNullOrEmpty(username)
                    ? ":" + Uri.EscapeDataString(password ?? string.Empty) + "@"
                    : Uri.EscapeDataString(username) + ":" + Uri.EscapeDataString(password ?? string.Empty) + "@";
            }
            string uriHost = (host ?? string.Empty).Trim();
            if (uriHost.IndexOf(':') >= 0 && !(uriHost.StartsWith("[", StringComparison.Ordinal) && uriHost.EndsWith("]", StringComparison.Ordinal)))
                uriHost = "[" + uriHost + "]";
            return scheme + "://" + auth + uriHost + ":" + port.ToString(CultureInfo.InvariantCulture)
                + "/" + Math.Max(0, databaseIndex).ToString(CultureInfo.InvariantCulture);
        }

        public void Open()
        {
            if (string.IsNullOrWhiteSpace(host)) throw new InvalidOperationException(Localization.T("Redis.ConnectionStringRequired"));
            lock (_sync)
            {
                if (mode == ModeCluster)
                {
                    OpenCluster();
                }
                else
                {
                    KeyValuePair<string, int> target = mode == ModeSentinel ? ResolveSentinelMaster() : new KeyValuePair<string, int>(host, port);
                    RedisRespClient candidate = ConnectNode(target.Key, target.Value, initialDatabaseIndex);
                    try
                    {
                        if (mode == ModeSentinel) EnsureMasterRole(candidate, target);
                        client = mode == ModeSentinel
                            ? RedisCommandRouter.ForSentinel(candidate, ReconnectToCurrentMaster, () => RedisCommandRouter.FormatAddress(connectedHost, connectedPort), ConsumeMasterSwitch)
                            : RedisCommandRouter.ForStandalone(candidate);
                        if (mode == ModeSentinel) StartSentinelWatcher();
                        connectedHost = target.Key;
                        connectedPort = target.Value;
                    }
                    catch
                    {
                        candidate.Dispose();
                        throw;
                    }
                }
                selectedDatabase = initialDatabaseIndex;
                open = true;
            }
        }

        private void OpenCluster()
        {
            List<string> candidates = new List<string> { RedisCommandRouter.FormatAddress(host, port) };
            candidates.AddRange(seeds.Where(item => !candidates.Contains(item, StringComparer.OrdinalIgnoreCase)));
            List<string> failures = new List<string>();
            foreach (string address in candidates)
            {
                KeyValuePair<string, int> endpoint = RedisCommandRouter.SplitAddress(address);
                RedisRespClient seed = null;
                try
                {
                    seed = ConnectNode(endpoint.Key, endpoint.Value, 0);
                    client = RedisCommandRouter.ForCluster(endpoint.Key, endpoint.Value, seed, (nodeHost, nodePort) => ConnectNode(nodeHost, nodePort, 0));
                    connectedHost = endpoint.Key;
                    connectedPort = endpoint.Value;
                    return;
                }
                catch (Exception exception) when (exception is RedisServerException || exception is System.IO.IOException ||
                                                  exception is System.Net.Sockets.SocketException || exception is TimeoutException)
                {
                    if (seed != null) seed.Dispose();
                    failures.Add(address + ": " + exception.Message);
                }
            }
            throw new InvalidOperationException(Localization.Format("Redis.ClusterUnreachable", string.Join("; ", failures)));
        }

        /// <summary>依序詢問每個 Sentinel 目前的 master 位址；Sentinel 本身的驗證可選擇沿用資料節點帳密。</summary>
        private KeyValuePair<string, int> ResolveSentinelMaster()
        {
            List<string> sentinels = new List<string> { RedisCommandRouter.FormatAddress(host, port) };
            sentinels.AddRange(seeds.Where(item => !sentinels.Contains(item, StringComparer.OrdinalIgnoreCase)));
            List<string> failures = new List<string>();
            foreach (string address in sentinels)
            {
                KeyValuePair<string, int> endpoint = RedisCommandRouter.SplitAddress(address);
                try
                {
                    using (RedisRespClient sentinel = RedisRespClient.Connect(endpoint.Key, endpoint.Value, useTls, ConnectTimeoutMs))
                    {
                        if (sentinelAuth)
                        {
                            if (!string.IsNullOrEmpty(username)) sentinel.Execute("AUTH", username, password ?? string.Empty);
                            else if (!string.IsNullOrEmpty(password)) sentinel.Execute("AUTH", password);
                        }
                        object[] reply = sentinel.Execute("SENTINEL", "get-master-addr-by-name", masterName) as object[];
                        int masterPort;
                        if (reply != null && reply.Length == 2 &&
                            int.TryParse(Convert.ToString(reply[1], CultureInfo.InvariantCulture), NumberStyles.None, CultureInfo.InvariantCulture, out masterPort))
                        {
                            return new KeyValuePair<string, int>(Convert.ToString(reply[0], CultureInfo.InvariantCulture), masterPort);
                        }
                        failures.Add(address + ": " + Localization.Format("Redis.SentinelMasterUnknown", masterName));
                    }
                }
                catch (Exception exception) when (exception is RedisServerException || exception is System.IO.IOException ||
                                                  exception is System.Net.Sockets.SocketException || exception is TimeoutException)
                {
                    failures.Add(address + ": " + exception.Message);
                }
            }
            throw new InvalidOperationException(Localization.Format("Redis.SentinelUnreachable", masterName, string.Join("; ", failures)));
        }

        private volatile bool masterSwitchPending;
        private RedisRespClient sentinelWatcher;
        private System.Threading.Thread sentinelWatcherThread;

        /// <summary>
        /// 背景訂閱 Sentinel 的 +switch-master：Sentinel 宣布切換後，下一個命令前就改連新 master，
        /// 避免在舊 master 被降級前的空窗寫進即將被覆蓋的舊節點。Sentinel 無法連線時不影響一般操作。
        /// </summary>
        private void StartSentinelWatcher()
        {
            StopSentinelWatcher();
            List<string> sentinels = new List<string> { RedisCommandRouter.FormatAddress(host, port) };
            sentinels.AddRange(seeds.Where(item => !sentinels.Contains(item, StringComparer.OrdinalIgnoreCase)));
            foreach (string address in sentinels)
            {
                KeyValuePair<string, int> endpoint = RedisCommandRouter.SplitAddress(address);
                RedisRespClient watcher = null;
                try
                {
                    watcher = RedisRespClient.Connect(endpoint.Key, endpoint.Value, useTls, ConnectTimeoutMs);
                    if (sentinelAuth)
                    {
                        if (!string.IsNullOrEmpty(username)) watcher.Execute("AUTH", username, password ?? string.Empty);
                        else if (!string.IsNullOrEmpty(password)) watcher.Execute("AUTH", password);
                    }
                    watcher.Execute("SUBSCRIBE", "+switch-master");
                    watcher.SetReceiveTimeout(0);
                }
                catch (Exception exception) when (exception is RedisServerException || exception is System.IO.IOException ||
                                                  exception is System.Net.Sockets.SocketException || exception is TimeoutException)
                {
                    if (watcher != null) watcher.Dispose();
                    continue;
                }

                RedisRespClient active = watcher;
                sentinelWatcher = active;
                sentinelWatcherThread = new System.Threading.Thread(() => WatchSentinel(active)) { IsBackground = true, Name = "mySQLPunk Redis Sentinel watcher" };
                sentinelWatcherThread.Start();
                return;
            }
        }

        private void WatchSentinel(RedisRespClient watcher)
        {
            try
            {
                while (ReferenceEquals(sentinelWatcher, watcher))
                {
                    object[] reply = watcher.ReadReply() as object[];
                    if (reply == null || reply.Length != 3 || !string.Equals(Convert.ToString(reply[0], CultureInfo.InvariantCulture), "message", StringComparison.OrdinalIgnoreCase)) continue;
                    // 內容為「master 名稱 舊 IP 舊 port 新 IP 新 port」。
                    string[] parts = Convert.ToString(reply[2], CultureInfo.InvariantCulture).Split(' ');
                    if (parts.Length >= 1 && string.Equals(parts[0], masterName, StringComparison.Ordinal)) masterSwitchPending = true;
                }
            }
            catch (Exception)
            {
                // 監看連線中斷時退回「命令失敗才換線」的行為。
            }
        }

        private void StopSentinelWatcher()
        {
            RedisRespClient watcher = sentinelWatcher;
            sentinelWatcher = null;
            sentinelWatcherThread = null;
            masterSwitchPending = false;
            if (watcher != null) watcher.Dispose();
        }

        private bool ConsumeMasterSwitch()
        {
            if (!masterSwitchPending) return false;
            masterSwitchPending = false;
            return true;
        }

        /// <summary>容錯切換後重新向 Sentinel 解析 master，連上並確認角色，沿用目前選取的 logical database。</summary>
        private RedisRespClient ReconnectToCurrentMaster()
        {
            List<string> failures = new List<string>();
            // 切換剛發生時 Sentinel 可能還回報舊 master，短暫重試幾次。
            for (int attempt = 0; attempt < 6; attempt++)
            {
                KeyValuePair<string, int> target;
                try
                {
                    target = ResolveSentinelMaster();
                }
                catch (InvalidOperationException exception)
                {
                    failures.Add(exception.Message);
                    System.Threading.Thread.Sleep(500 * (attempt + 1));
                    continue;
                }

                RedisRespClient candidate = null;
                try
                {
                    candidate = ConnectNode(target.Key, target.Value, Math.Max(0, selectedDatabase));
                    EnsureMasterRole(candidate, target);
                    connectedHost = target.Key;
                    connectedPort = target.Value;
                    return candidate;
                }
                catch (Exception exception) when (exception is RedisServerException || exception is System.IO.IOException ||
                                                  exception is System.Net.Sockets.SocketException || exception is TimeoutException ||
                                                  exception is InvalidOperationException)
                {
                    if (candidate != null) candidate.Dispose();
                    failures.Add(RedisCommandRouter.FormatAddress(target.Key, target.Value) + ": " + exception.Message);
                    System.Threading.Thread.Sleep(500 * (attempt + 1));
                }
            }
            throw new InvalidOperationException(Localization.Format("Redis.SentinelUnreachable", masterName, string.Join("; ", failures)));
        }

        /// <summary>Sentinel 回報的位址可能落後於實際切換；連上後以 ROLE 確認真的是 master。</summary>
        private static void EnsureMasterRole(RedisRespClient candidate, KeyValuePair<string, int> target)
        {
            object[] role = candidate.Execute("ROLE") as object[];
            string name = role != null && role.Length > 0 ? Convert.ToString(role[0], CultureInfo.InvariantCulture) : string.Empty;
            if (!string.Equals(name, "master", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(Localization.Format("Redis.SentinelNotMaster", RedisCommandRouter.FormatAddress(target.Key, target.Value), name));
            }
        }

        private RedisRespClient ConnectNode(string nodeHost, int nodePort, int databaseIndex)
        {
            RedisRespClient candidate = RedisRespClient.Connect(nodeHost, nodePort, useTls, ConnectTimeoutMs);
            try
            {
                if (!string.IsNullOrEmpty(username)) candidate.Execute("AUTH", username, password ?? string.Empty);
                else if (!string.IsNullOrEmpty(password)) candidate.Execute("AUTH", password);
                candidate.Execute("PING");
                if (databaseIndex > 0) candidate.Execute("SELECT", databaseIndex.ToString(CultureInfo.InvariantCulture));
                return candidate;
            }
            catch
            {
                candidate.Dispose();
                throw;
            }
        }

        public void Close()
        {
            lock (_sync)
            {
                StopSentinelWatcher();
                if (client != null) client.Dispose();
                client = null;
                selectedDatabase = -1;
                open = false;
            }
        }

        public void Dispose()
        {
            Close();
        }

        public List<string> GetDatabases()
        {
            lock (_sync)
            {
                EnsureOpen();
                if (client.IsCluster) return new List<string> { "db0" };
                int count = 16;
                try
                {
                    // Garnet 未必支援 CONFIG GET databases，失敗就退回預設 16 個。
                    object[] reply = client.Execute("CONFIG", "GET", "databases") as object[];
                    if (reply != null && reply.Length == 2)
                    {
                        int parsed;
                        if (int.TryParse(Convert.ToString(reply[1], CultureInfo.InvariantCulture), out parsed) && parsed > 0)
                            count = parsed;
                    }
                }
                catch (RedisServerException) { }

                if (initialDatabaseIndex >= count) count = initialDatabaseIndex + 1;
                List<string> result = new List<string>();
                for (int i = 0; i < count; i++) result.Add("db" + i.ToString(CultureInfo.InvariantCulture));
                return result;
            }
        }

        public List<string> GetTables(string databaseName)
        {
            EnsureOpen();
            return new List<string> { "keys" };
        }

        public List<string> GetViews(string databaseName)
        {
            EnsureOpen();
            return new List<string>();
        }

        public DataTable GetColumns(string databaseName, string tableName)
        {
            EnsureOpen();
            DataTable result = new DataTable();
            result.Columns.Add("Field");
            result.Columns.Add("Type");
            result.Columns.Add("Null");
            result.Columns.Add("Key");
            result.Columns.Add("Default");
            result.Columns.Add("Extra");
            result.Columns.Add("Comment");
            AddColumnRow(result, "key", "string", "PRI", Localization.T("Redis.ColumnKeyComment"));
            AddColumnRow(result, "type", "string", string.Empty, Localization.T("Redis.ColumnTypeComment"));
            AddColumnRow(result, "ttl", "string", string.Empty, Localization.T("Redis.ColumnTtlComment"));
            AddColumnRow(result, "preview", "string", string.Empty, Localization.T("Redis.ColumnPreviewComment"));
            return result;
        }

        public DataTable GetIndexes(string databaseName, string tableName)
        {
            EnsureOpen();
            DataTable result = new DataTable();
            result.Columns.Add("Key_name");
            result.Columns.Add("Column_name");
            result.Columns.Add("Non_unique", typeof(int));
            result.Columns.Add("Seq_in_index", typeof(int));
            result.Columns.Add("Index_type");
            return result;
        }

        public DataTable GetTableStatus(string databaseName)
        {
            lock (_sync)
            {
                EnsureOpen();
                SelectDatabase(databaseName);
                DataTable result = new DataTable();
                result.Columns.Add("Name");
                result.Columns.Add("Rows", typeof(long));
                result.Columns.Add("Data_length", typeof(long));
                result.Columns.Add("Index_length", typeof(long));
                result.Columns.Add("Engine");
                result.Columns.Add("Update_time");
                result.Columns.Add("Comment");
                DataRow row = result.NewRow();
                row["Name"] = "keys";
                row["Rows"] = Convert.ToInt64(client.Execute("DBSIZE"), CultureInfo.InvariantCulture);
                row["Data_length"] = 0L;
                row["Index_length"] = 0L;
                row["Engine"] = "Redis";
                row["Update_time"] = string.Empty;
                row["Comment"] = Localization.T("Redis.KeysPseudoTable");
                result.Rows.Add(row);
                return result;
            }
        }

        public Dictionary<string, string> GetDatabaseInfo(string databaseName)
        {
            lock (_sync)
            {
                EnsureOpen();
                SelectDatabase(databaseName);
                Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "Provider", "Redis" },
                    { "Database", databaseName ?? string.Empty },
                    { "topology", mode },
                    { "node", ConnectedAddress },
                    { "keys", Convert.ToString(client.Execute("DBSIZE"), CultureInfo.InvariantCulture) }
                };
                string info = string.Empty;
                try { info = client.Execute("INFO", "server") as string ?? string.Empty; }
                catch (RedisServerException) { }
                foreach (string name in new[] { "redis_version", "garnet_version", "redis_mode", "os", "uptime_in_seconds", "tcp_port" })
                {
                    string value = ParseInfoValue(info, name);
                    if (!string.IsNullOrWhiteSpace(value)) result[name] = value;
                }
                try
                {
                    string memory = client.Execute("INFO", "memory") as string ?? string.Empty;
                    string used = ParseInfoValue(memory, "used_memory_human");
                    if (!string.IsNullOrWhiteSpace(used)) result["used_memory_human"] = used;
                }
                catch (RedisServerException) { }
                return result;
            }
        }

        /// <summary>
        /// 讀取 Redis／Garnet 即時監控快照。優先合併預設 INFO，缺少的區段才個別補讀；
        /// 伺服器不支援其中一段時只略過該段，避免版本或 Garnet 欄位差異讓整頁失效。
        /// </summary>
        public RedisMonitorSnapshot GetMonitorSnapshot(string databaseName)
        {
            lock (_sync)
            {
                EnsureOpen();
                SelectDatabase(databaseName);

                RedisMonitorSnapshot snapshot = new RedisMonitorSnapshot { CapturedAtUtc = DateTime.UtcNow };
                Dictionary<string, string> fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    MergeInfoFields(fields, client.Execute("INFO") as string);
                }
                catch (RedisServerException) { }

                ReadInfoSectionWhenMissing(fields, "server", "redis_version", "garnet_version");
                ReadInfoSectionWhenMissing(fields, "clients", "connected_clients");
                ReadInfoSectionWhenMissing(fields, "memory", "used_memory", "used_memory_human");
                ReadInfoSectionWhenMissing(fields, "persistence", "rdb_last_bgsave_status", "aof_enabled");
                ReadInfoSectionWhenMissing(fields, "stats", "total_commands_processed", "instantaneous_ops_per_sec");
                ReadInfoSectionWhenMissing(fields, "replication", "role");
                ReadInfoSectionWhenMissing(fields, "cpu", "used_cpu_sys", "used_cpu_user");
                ReadInfoSectionWhenMissing(fields, "commandstats", "cmdstat_");

                AddMonitorMetric(snapshot, "database", "database", databaseName ?? string.Empty);
                AddMonitorMetric(snapshot, "database", "keys", Convert.ToString(client.Execute("DBSIZE"), CultureInfo.InvariantCulture));
                AddMonitorMetric(snapshot, fields, "server", "redis_version");
                AddMonitorMetric(snapshot, fields, "server", "garnet_version");
                AddMonitorMetric(snapshot, fields, "server", "redis_mode");
                AddMonitorMetric(snapshot, fields, "server", "uptime_in_seconds");
                AddMonitorMetric(snapshot, fields, "clients", "connected_clients");
                AddMonitorMetric(snapshot, fields, "clients", "blocked_clients");
                AddMonitorMetric(snapshot, fields, "memory", "used_memory_human", "used_memory");
                AddMonitorMetric(snapshot, fields, "memory", "used_memory_peak_human", "used_memory_peak");
                AddMonitorMetric(snapshot, fields, "memory", "used_memory_rss_human", "used_memory_rss");
                AddMonitorMetric(snapshot, fields, "memory", "mem_fragmentation_ratio");
                AddMonitorMetric(snapshot, fields, "activity", "instantaneous_ops_per_sec");
                AddMonitorMetric(snapshot, fields, "activity", "total_commands_processed");
                AddMonitorMetric(snapshot, fields, "activity", "keyspace_hits");
                AddMonitorMetric(snapshot, fields, "activity", "keyspace_misses");
                AddMonitorMetric(snapshot, fields, "activity", "evicted_keys");
                AddMonitorMetric(snapshot, fields, "activity", "rejected_connections");
                AddMonitorMetric(snapshot, fields, "network", "instantaneous_input_kbps");
                AddMonitorMetric(snapshot, fields, "network", "instantaneous_output_kbps");
                AddMonitorMetric(snapshot, fields, "network", "total_net_input_bytes");
                AddMonitorMetric(snapshot, fields, "network", "total_net_output_bytes");
                AddMonitorMetric(snapshot, fields, "cpu", "used_cpu_sys");
                AddMonitorMetric(snapshot, fields, "cpu", "used_cpu_user");
                AddMonitorMetric(snapshot, fields, "persistence", "rdb_last_bgsave_status");
                AddMonitorMetric(snapshot, fields, "persistence", "aof_enabled");
                AddMonitorMetric(snapshot, fields, "persistence", "aof_last_bgrewrite_status");
                AddMonitorMetric(snapshot, fields, "replication", "role");
                AddMonitorMetric(snapshot, fields, "replication", "connected_slaves");
                AddMonitorMetric(snapshot, fields, "replication", "master_link_status");
                AddMonitorMetric(snapshot, "topology", "mode", mode);
                AddMonitorMetric(snapshot, "topology", "node", ConnectedAddress);
                if (client.IsCluster)
                {
                    AddMonitorMetric(snapshot, "topology", "masters", client.Masters.Count.ToString(CultureInfo.InvariantCulture));
                    AddMonitorMetric(snapshot, "topology", "slots_covered", client.CoveredSlots.ToString(CultureInfo.InvariantCulture) + "/" + RedisCommandRouter.SlotCount.ToString(CultureInfo.InvariantCulture));
                    try
                    {
                        Dictionary<string, string> cluster = ParseInfoFields(client.Execute("CLUSTER", "INFO") as string ?? string.Empty);
                        foreach (string name in new[] { "cluster_state", "cluster_known_nodes", "cluster_size", "cluster_slots_fail" })
                        {
                            string value;
                            if (cluster.TryGetValue(name, out value)) AddMonitorMetric(snapshot, "topology", name, value);
                        }
                    }
                    catch (RedisServerException) { }
                }

                long hits, misses;
                if (TryReadLong(fields, "keyspace_hits", out hits) && TryReadLong(fields, "keyspace_misses", out misses))
                {
                    double total = (double)hits + misses;
                    if (total > 0d)
                    {
                        double rate = hits / total;
                        AddMonitorMetric(snapshot, "activity", "keyspace_hit_rate", rate.ToString("P1", CultureInfo.InvariantCulture));
                    }
                }

                foreach (KeyValuePair<string, string> pair in fields)
                {
                    if (!pair.Key.StartsWith("cmdstat_", StringComparison.OrdinalIgnoreCase)) continue;
                    RedisCommandMetric command = ParseCommandMetric(pair.Key.Substring("cmdstat_".Length), pair.Value);
                    if (command != null) snapshot.Commands.Add(command);
                }
                snapshot.Commands.Sort((left, right) =>
                {
                    int byCalls = right.Calls.CompareTo(left.Calls);
                    return byCalls != 0 ? byCalls : string.Compare(left.Command, right.Command, StringComparison.OrdinalIgnoreCase);
                });
                return snapshot;
            }
        }

        public RedisPubSubSubscription CreatePubSubSubscription(string databaseName, string topic, bool pattern)
        {
            return CreatePubSubSubscription(databaseName, topic, pattern ? RedisPubSubKind.Pattern : RedisPubSubKind.Channel);
        }

        /// <summary>
        /// 以專用連線訂閱 channel、pattern 或分片 channel；呼叫端掛上事件後需呼叫 Start。
        /// Sentinel 模式每次都重新解析目前的 master（容錯切換後可直接重新訂閱）；Cluster 的分片 channel 連到負責該 slot 的 master。
        /// </summary>
        public RedisPubSubSubscription CreatePubSubSubscription(string databaseName, string topic, RedisPubSubKind kind)
        {
            if (string.IsNullOrWhiteSpace(topic))
                throw new ArgumentException(Localization.T("Redis.PubSubTopicRequired"), "topic");

            lock (_sync)
            {
                EnsureOpen();
                int databaseIndex = string.IsNullOrWhiteSpace(databaseName)
                    ? initialDatabaseIndex
                    : ParseDatabaseIndex(databaseName);
                KeyValuePair<string, int> endpoint;
                RedisRespClient subscriptionClient;
                if (mode == ModeSentinel)
                {
                    endpoint = ResolveSentinelMaster();
                    subscriptionClient = ConnectNode(endpoint.Key, endpoint.Value, databaseIndex);
                    try
                    {
                        EnsureMasterRole(subscriptionClient, endpoint);
                    }
                    catch
                    {
                        subscriptionClient.Dispose();
                        throw;
                    }
                }
                else if (mode == ModeCluster && kind == RedisPubSubKind.Shard)
                {
                    endpoint = RedisCommandRouter.SplitAddress(client.OwnerAddress(topic));
                    subscriptionClient = ConnectNode(endpoint.Key, endpoint.Value, 0);
                }
                else
                {
                    endpoint = new KeyValuePair<string, int>(connectedHost, connectedPort);
                    subscriptionClient = OpenAuthenticatedClient(databaseIndex);
                }
                try
                {
                    RedisPubSubSubscription subscription = RedisPubSubSubscription.Create(subscriptionClient, topic, kind, RedisCommandRouter.FormatAddress(endpoint.Key, endpoint.Value));
                    subscriptionClient.SetReceiveTimeout(0);
                    return subscription;
                }
                catch
                {
                    subscriptionClient.Dispose();
                    throw;
                }
            }
        }

        /// <summary>在一般 provider 連線發布訊息，不會占用訂閱連線；sharded 為 Redis 7 的 SPUBLISH（Cluster 送到負責該 slot 的 master）。</summary>
        public long Publish(string channel, string message, bool sharded = false)
        {
            if (string.IsNullOrWhiteSpace(channel))
                throw new ArgumentException(Localization.T("Redis.PubSubChannelRequired"), "channel");
            lock (_sync)
            {
                EnsureOpen();
                return Convert.ToInt64(client.Execute(sharded ? "SPUBLISH" : "PUBLISH", channel, message ?? string.Empty), CultureInfo.InvariantCulture);
            }
        }

        /// <summary>Sentinel 模式下發生過的容錯切換次數。</summary>
        public int FailoverCount
        {
            get
            {
                lock (_sync)
                {
                    return client == null ? 0 : client.Failovers;
                }
            }
        }

        public string GetTableCreateStatement(string databaseName, string tableName)
        {
            return string.Empty;
        }

        public bool TableExists(string databaseName, string tableName)
        {
            return string.Equals(tableName, "keys", StringComparison.OrdinalIgnoreCase);
        }

        public bool ViewExists(string databaseName, string viewName)
        {
            return false;
        }

        public long CountRows(string databaseName, string tableName)
        {
            lock (_sync)
            {
                EnsureOpen();
                SelectDatabase(databaseName);
                return Convert.ToInt64(client.Execute("DBSIZE"), CultureInfo.InvariantCulture);
            }
        }

        public DataTable SelectTablePage(string databaseName, string tableName, long offset, int limit)
        {
            lock (_sync)
            {
                EnsureOpen();
                SelectDatabase(databaseName);
                List<string> keys = ScanKeys("*", offset, NormalizeLimit(limit), string.Empty);
                return BuildKeyListTable(keys);
            }
        }

        /// <summary>執行查詢分頁的受限唯讀 JSON 規格：pattern 掃描或單一 key 內容。</summary>
        public DataTable SelectJsonQuery(string databaseName, string query)
        {
            RedisReadQuery request = RedisReadQuery.Parse(query);
            lock (_sync)
            {
                EnsureOpen();
                SelectDatabase(databaseName);
                if (!string.IsNullOrEmpty(request.Key)) return BuildKeyDetailTable(request.Key, request.Limit);
                List<string> keys = ScanKeys(
                    string.IsNullOrEmpty(request.Pattern) ? "*" : request.Pattern,
                    0,
                    request.Limit,
                    request.Type);
                return BuildKeyListTable(keys);
            }
        }

        /// <summary>讀取單一 string key 的編輯基準：值、剩餘 TTL 與是否含無法以 UTF-8 呈現的位元組。</summary>
        public RedisStringEditContext GetStringForEdit(string databaseName, string key)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentException(Localization.T("Redis.EditKeyRequired"), "key");
            lock (_sync)
            {
                EnsureOpen();
                SelectDatabase(databaseName);
                string type = GetKeyType(key);
                if (type == "none") throw new InvalidOperationException(Localization.T("Redis.EditKeyDeleted"));
                if (!string.Equals(type, "string", StringComparison.OrdinalIgnoreCase))
                    throw new NotSupportedException(Localization.Format("Redis.EditStringOnly", type));
                string value = Convert.ToString(client.Execute("GET", key), CultureInfo.InvariantCulture);
                long ttlMs = Convert.ToInt64(client.Execute("PTTL", key), CultureInfo.InvariantCulture);
                return new RedisStringEditContext
                {
                    Key = key,
                    Value = value,
                    TtlMs = ttlMs,
                    // RESP 讀取一律以 UTF-8 解碼；出現替換字元代表原值不是合法 UTF-8，
                    // 寫回會把原始位元組換成 U+FFFD 而損毀資料，因此標成唯讀。
                    IsBinaryUnsafe = value != null && value.IndexOf('\uFFFD') >= 0
                };
            }
        }

        /// <summary>
        /// 以 WATCH＋MULTI／EXEC 儲存 string 值：載入後被其他連線改過（比對值不同或 EXEC 落空）就丟
        /// RedisEditConflictException，不會蓋掉別人的寫入。preserveTtl 會在同一交易內以 PEXPIRE 保留剩餘 TTL。
        /// </summary>
        public void SaveStringValue(string databaseName, string key, string expectedValue, string newValue, bool preserveTtl)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentException(Localization.T("Redis.EditKeyRequired"), "key");
            if (newValue == null) newValue = string.Empty;
            if (newValue.IndexOf('\uFFFD') >= 0)
                throw new NotSupportedException(Localization.T("Redis.EditBinaryUnsupported"));
            lock (_sync)
            {
                EnsureOpen();
                SelectDatabase(databaseName);
                client.Execute("WATCH", key);
                bool inMulti = false;
                try
                {
                    string type = GetKeyType(key);
                    if (type == "none") throw new RedisEditConflictException(Localization.T("Redis.EditKeyDeleted"));
                    if (!string.Equals(type, "string", StringComparison.OrdinalIgnoreCase))
                        throw new NotSupportedException(Localization.Format("Redis.EditStringOnly", type));
                    string current = Convert.ToString(client.Execute("GET", key), CultureInfo.InvariantCulture);
                    if (!string.Equals(current, expectedValue, StringComparison.Ordinal))
                        throw new RedisEditConflictException(Localization.T("Redis.EditConflict"));
                    if (current != null && current.IndexOf('\uFFFD') >= 0)
                        throw new NotSupportedException(Localization.T("Redis.EditBinaryUnsupported"));
                    // SET 會清掉 TTL，保留時要在同一交易內補 PEXPIRE；WATCH 保證讀到的 TTL 沒被其他寫入動過。
                    long ttlMs = preserveTtl ? Convert.ToInt64(client.Execute("PTTL", key), CultureInfo.InvariantCulture) : -1;
                    client.Execute("MULTI");
                    inMulti = true;
                    client.Execute("SET", key, newValue);
                    if (preserveTtl && ttlMs > 0)
                        client.Execute("PEXPIRE", key, ttlMs.ToString(CultureInfo.InvariantCulture));
                    object execReply = client.Execute("EXEC");
                    inMulti = false;
                    if (execReply == null) throw new RedisEditConflictException(Localization.T("Redis.EditConflict"));
                }
                catch
                {
                    if (inMulti) { try { client.Execute("DISCARD"); } catch { } }
                    else { try { client.Execute("UNWATCH"); } catch { } }
                    throw;
                }
            }
        }

        /// <summary>取得 key 的實際型別；key 不存在時擲回錯誤，供編輯器決定顯示模式。</summary>
        public string GetKeyTypeForEdit(string databaseName, string key)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentException(Localization.T("Redis.EditKeyRequired"), "key");
            lock (_sync)
            {
                EnsureOpen();
                SelectDatabase(databaseName);
                string type = GetKeyType(key);
                if (type == "none") throw new InvalidOperationException(Localization.T("Redis.EditKeyDeleted"));
                return type;
            }
        }

        /// <summary>以單鍵內容表載入集合項目（與查詢分頁同一種表結構），供編輯器顯示。</summary>
        public DataTable GetKeyDetailForEdit(string databaseName, string key, int limit)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentException(Localization.T("Redis.EditKeyRequired"), "key");
            lock (_sync)
            {
                EnsureOpen();
                SelectDatabase(databaseName);
                return BuildKeyDetailTable(key, NormalizeLimit(limit));
            }
        }

        /// <summary>取得 key 的剩餘 TTL（毫秒；-1 代表不會過期），供編輯器顯示各型別的 TTL。</summary>
        public long GetKeyTtlMs(string databaseName, string key)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentException(Localization.T("Redis.EditKeyRequired"), "key");
            lock (_sync)
            {
                EnsureOpen();
                SelectDatabase(databaseName);
                long ttlMs = Convert.ToInt64(client.Execute("PTTL", key), CultureInfo.InvariantCulture);
                if (ttlMs == -2) throw new InvalidOperationException(Localization.T("Redis.EditKeyDeleted"));
                return ttlMs;
            }
        }

        /// <summary>新增或更新 hash 欄位；expectExisting 決定「必須已存在且值相符」或「必須不存在」。</summary>
        public void SaveHashField(string databaseName, string key, string field, string expectedValue, bool expectExisting, string newValue)
        {
            if (string.IsNullOrEmpty(field)) throw new ArgumentException(Localization.T("Redis.EntryRequired"), "field");
            RejectBinary(newValue);
            RunWatchedWrite(databaseName, key, "hash",
                () =>
                {
                    string current = client.Execute("HGET", key, field) as string;
                    ValidateEntryExpectation(current, expectedValue, expectExisting);
                },
                () => client.Execute("HSET", key, field, newValue ?? string.Empty));
        }

        /// <summary>刪除 hash 欄位；欄位值已被改過或欄位已消失時回報衝突。</summary>
        public void DeleteHashField(string databaseName, string key, string field, string expectedValue)
        {
            if (string.IsNullOrEmpty(field)) throw new ArgumentException(Localization.T("Redis.EntryRequired"), "field");
            RunWatchedWrite(databaseName, key, "hash",
                () =>
                {
                    string current = client.Execute("HGET", key, field) as string;
                    ValidateEntryExpectation(current, expectedValue, true);
                },
                () => client.Execute("HDEL", key, field));
        }

        /// <summary>更新 list 指定索引的元素；索引超出範圍或元素已被改過時回報衝突。</summary>
        public void SaveListElement(string databaseName, string key, long index, string expectedValue, string newValue)
        {
            RejectBinary(newValue);
            RunWatchedWrite(databaseName, key, "list",
                () =>
                {
                    string current = client.Execute("LINDEX", key, index.ToString(CultureInfo.InvariantCulture)) as string;
                    ValidateEntryExpectation(current, expectedValue, true);
                },
                () => client.Execute("LSET", key, index.ToString(CultureInfo.InvariantCulture), newValue ?? string.Empty));
        }

        /// <summary>在 list 尾端加入元素（RPUSH）；只驗證型別，不需比對舊值。</summary>
        public void AppendListElement(string databaseName, string key, string newValue)
        {
            RejectBinary(newValue);
            RunWatchedWrite(databaseName, key, "list", null,
                () => client.Execute("RPUSH", key, newValue ?? string.Empty));
        }

        /// <summary>
        /// 刪除 list 指定索引的元素；先比對載入值，再於同一交易以唯一標記取代並移除，
        /// 避免 LREM 直接依值刪除時誤傷內容相同的其他元素。
        /// </summary>
        public void DeleteListElement(string databaseName, string key, long index, string expectedValue)
        {
            if (index < 0) throw new ArgumentOutOfRangeException("index");
            RejectBinary(expectedValue);
            string marker = "\0mysqlpunk:list-delete:" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            RunWatchedWrite(databaseName, key, "list",
                () =>
                {
                    string current = client.Execute("LINDEX", key, index.ToString(CultureInfo.InvariantCulture)) as string;
                    ValidateEntryExpectation(current, expectedValue, true);
                },
                () =>
                {
                    client.Execute("LSET", key, index.ToString(CultureInfo.InvariantCulture), marker);
                    client.Execute("LREM", key, "1", marker);
                });
        }

        /// <summary>加入 set 成員（SADD；已存在時為 no-op）。</summary>
        public void AddSetMember(string databaseName, string key, string member)
        {
            if (string.IsNullOrEmpty(member)) throw new ArgumentException(Localization.T("Redis.EntryRequired"), "member");
            RejectBinary(member);
            RunWatchedWrite(databaseName, key, "set", null,
                () => client.Execute("SADD", key, member));
        }

        /// <summary>移除 set 成員；成員已不存在時回報衝突。</summary>
        public void RemoveSetMember(string databaseName, string key, string member)
        {
            if (string.IsNullOrEmpty(member)) throw new ArgumentException(Localization.T("Redis.EntryRequired"), "member");
            RunWatchedWrite(databaseName, key, "set",
                () =>
                {
                    long exists = Convert.ToInt64(client.Execute("SISMEMBER", key, member), CultureInfo.InvariantCulture);
                    if (exists != 1) throw new RedisEditConflictException(Localization.T("Redis.EditEntryMissing"));
                },
                () => client.Execute("SREM", key, member));
        }

        /// <summary>新增或更新 zset 成員分數；expectExisting 語意同 hash 欄位。</summary>
        public void SaveZSetMember(string databaseName, string key, string member, string expectedScore, bool expectExisting, string newScore)
        {
            if (string.IsNullOrEmpty(member)) throw new ArgumentException(Localization.T("Redis.EntryRequired"), "member");
            RejectBinary(member);
            double parsedScore;
            if (!double.TryParse((newScore ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsedScore))
                throw new ArgumentException(Localization.T("Redis.ScoreInvalid"), "newScore");
            RunWatchedWrite(databaseName, key, "zset",
                () =>
                {
                    string current = client.Execute("ZSCORE", key, member) as string;
                    ValidateEntryExpectation(current, expectedScore, expectExisting, ScoresEqual);
                },
                () => client.Execute("ZADD", key, parsedScore.ToString("R", CultureInfo.InvariantCulture), member));
        }

        /// <summary>移除 zset 成員；分數已被改過或成員已消失時回報衝突。</summary>
        public void RemoveZSetMember(string databaseName, string key, string member, string expectedScore)
        {
            if (string.IsNullOrEmpty(member)) throw new ArgumentException(Localization.T("Redis.EntryRequired"), "member");
            RunWatchedWrite(databaseName, key, "zset",
                () =>
                {
                    string current = client.Execute("ZSCORE", key, member) as string;
                    ValidateEntryExpectation(current, expectedScore, true, ScoresEqual);
                },
                () => client.Execute("ZREM", key, member));
        }

        /// <summary>
        /// 集合寫入共用交易：WATCH key → 型別檢查 → 呼叫端驗證 → MULTI／queue → EXEC。
        /// EXEC 落空（其他連線改過 key）或驗證失敗都不會寫入。
        /// </summary>
        private void RunWatchedWrite(string databaseName, string key, string expectedType, Action validate, Action queueCommands)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentException(Localization.T("Redis.EditKeyRequired"), "key");
            lock (_sync)
            {
                EnsureOpen();
                SelectDatabase(databaseName);
                client.Execute("WATCH", key);
                bool inMulti = false;
                try
                {
                    string type = GetKeyType(key);
                    if (type == "none") throw new RedisEditConflictException(Localization.T("Redis.EditKeyDeleted"));
                    if (!string.Equals(type, expectedType, StringComparison.OrdinalIgnoreCase))
                        throw new RedisEditConflictException(Localization.Format("Redis.EditTypeChanged", expectedType, type));
                    if (validate != null) validate();
                    client.Execute("MULTI");
                    inMulti = true;
                    queueCommands();
                    object execReply = client.Execute("EXEC");
                    inMulti = false;
                    if (execReply == null) throw new RedisEditConflictException(Localization.T("Redis.EditConflict"));
                }
                catch
                {
                    if (inMulti) { try { client.Execute("DISCARD"); } catch { } }
                    else { try { client.Execute("UNWATCH"); } catch { } }
                    throw;
                }
            }
        }

        /// <summary>驗證項目現值符合編輯基準：新增時必須不存在，更新／刪除時必須存在且值相符。</summary>
        private static void ValidateEntryExpectation(string current, string expected, bool expectExisting, Func<string, string, bool> equals = null)
        {
            if (!expectExisting)
            {
                if (current != null) throw new RedisEditConflictException(Localization.T("Redis.EditEntryExists"));
                return;
            }
            if (current == null) throw new RedisEditConflictException(Localization.T("Redis.EditEntryMissing"));
            bool match = equals != null ? equals(current, expected ?? string.Empty) : string.Equals(current, expected, StringComparison.Ordinal);
            if (!match) throw new RedisEditConflictException(Localization.T("Redis.EditConflict"));
        }

        /// <summary>zset 分數以數值比較：伺服器可能把 1.5 正規化成不同字串表示。</summary>
        private static bool ScoresEqual(string current, string expected)
        {
            double currentScore, expectedScore;
            if (double.TryParse(current, NumberStyles.Float, CultureInfo.InvariantCulture, out currentScore) &&
                double.TryParse(expected, NumberStyles.Float, CultureInfo.InvariantCulture, out expectedScore))
                return currentScore.Equals(expectedScore);
            return string.Equals(current, expected, StringComparison.Ordinal);
        }

        private static void RejectBinary(string value)
        {
            if (value != null && value.IndexOf('\uFFFD') >= 0)
                throw new NotSupportedException(Localization.T("Redis.EditBinaryUnsupported"));
        }

        /// <summary>設定 key 的存活時間（秒）；key 不存在時擲回錯誤而不是默默略過。</summary>
        public void SetKeyTtl(string databaseName, string key, long ttlSeconds)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentException(Localization.T("Redis.EditKeyRequired"), "key");
            if (ttlSeconds <= 0) throw new ArgumentException(Localization.T("Redis.TtlInvalid"), "ttlSeconds");
            lock (_sync)
            {
                EnsureOpen();
                SelectDatabase(databaseName);
                long applied = Convert.ToInt64(client.Execute("EXPIRE", key, ttlSeconds.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
                if (applied != 1) throw new InvalidOperationException(Localization.T("Redis.EditKeyDeleted"));
            }
        }

        /// <summary>移除 key 的存活時間（PERSIST）；key 不存在時擲回錯誤。</summary>
        public void RemoveKeyTtl(string databaseName, string key)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentException(Localization.T("Redis.EditKeyRequired"), "key");
            lock (_sync)
            {
                EnsureOpen();
                SelectDatabase(databaseName);
                if (Convert.ToInt64(client.Execute("EXISTS", key), CultureInfo.InvariantCulture) != 1)
                    throw new InvalidOperationException(Localization.T("Redis.EditKeyDeleted"));
                client.Execute("PERSIST", key);
            }
        }

        /// <summary>刪除單一 key；回傳是否真的刪掉（false 代表 key 已不存在）。</summary>
        public bool DeleteKey(string databaseName, string key)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentException(Localization.T("Redis.EditKeyRequired"), "key");
            lock (_sync)
            {
                EnsureOpen();
                SelectDatabase(databaseName);
                return Convert.ToInt64(client.Execute("DEL", key), CultureInfo.InvariantCulture) > 0;
            }
        }

        public DataTable SelectSQL(string sql, Dictionary<string, object> parameters = null)
        {
            return SelectJsonQuery("db" + initialDatabaseIndex.ToString(CultureInfo.InvariantCulture), sql);
        }

        public Task<DataTable> SelectSQLAsync(string sql, Dictionary<string, object> parameters = null)
        {
            return Task.Run(() => SelectSQL(sql, parameters));
        }

        public Dictionary<string, string> ExecSQL(string sql, Dictionary<string, object> parameters = null)
        {
            return new Dictionary<string, string>
            {
                { "status", "ERROR" },
                { "reason", Localization.T("Redis.ReadOnlyFirstPhase") }
            };
        }

        public Task<Dictionary<string, string>> ExecSQLAsync(string sql, Dictionary<string, object> parameters = null)
        {
            return Task.FromResult(ExecSQL(sql, parameters));
        }

        public DataTable GetCopyColumns(string databaseName, string tableName) { throw UnsupportedWrite(); }
        public DataTable GetCopyIndexes(string databaseName, string tableName) { throw UnsupportedWrite(); }
        public void CreateTableForCopy(string databaseName, string tableName, DataTable sourceColumns, string sourceProvider) { throw UnsupportedWrite(); }
        public void DropTableForCopy(string databaseName, string tableName) { throw UnsupportedWrite(); }
        public void CreateIndexesForCopy(string databaseName, string tableName, DataTable sourceIndexes, string sourceProvider) { throw UnsupportedWrite(); }
        public void InsertTableBatch(string databaseName, string tableName, DataTable rows) { throw UnsupportedWrite(); }
        public void RenameTable(string databaseName, string oldTableName, string newTableName) { throw UnsupportedWrite(); }
        public void RenameView(string databaseName, string oldViewName, string newViewName) { throw UnsupportedWrite(); }
        public string GetViewCreateStatement(string databaseName, string viewName) { return string.Empty; }
        public void CreateViewFromStatement(string databaseName, string viewName, string sourceViewSql) { throw UnsupportedWrite(); }

        public static string BuildQueryTemplate()
        {
            return "{\r\n  \"pattern\": \"*\",\r\n  \"limit\": " + DefaultQueryLimit.ToString(CultureInfo.InvariantCulture) + "\r\n}";
        }

        /// <summary>把 db 節點名稱（db0、db3 或純數字）轉成資料庫索引。</summary>
        public static int ParseDatabaseIndex(string databaseName)
        {
            string value = (databaseName ?? string.Empty).Trim();
            if (value.StartsWith("db", StringComparison.OrdinalIgnoreCase)) value = value.Substring(2);
            int index;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out index) || index < 0)
                throw new ArgumentException(Localization.Format("Redis.InvalidDatabaseName", databaseName ?? string.Empty));
            return index;
        }

        private void SelectDatabase(string databaseName)
        {
            int index = string.IsNullOrWhiteSpace(databaseName) ? initialDatabaseIndex : ParseDatabaseIndex(databaseName);
            if (index == selectedDatabase) return;
            client.Execute("SELECT", index.ToString(CultureInfo.InvariantCulture));
            selectedDatabase = index;
        }

        private List<string> ScanKeys(string pattern, long offset, int limit, string typeFilter)
        {
            List<string> keys = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            long skipped = 0;
            string cursor = "0";
            // Cluster 的 SCAN 只涵蓋單一節點，必須逐一掃描每個 master。
            foreach (Func<string[], object> target in client.ScanTargets())
            {
                cursor = "0";
                do
                {
                    object[] reply = target(new[] { "SCAN", cursor, "MATCH", pattern, "COUNT",
                        ScanBatchSize.ToString(CultureInfo.InvariantCulture) }) as object[];
                    if (reply == null || reply.Length != 2) break;
                    cursor = Convert.ToString(reply[0], CultureInfo.InvariantCulture);
                    object[] batch = reply[1] as object[] ?? new object[0];
                    foreach (object item in batch)
                    {
                        string key = Convert.ToString(item, CultureInfo.InvariantCulture);
                        if (!seen.Add(key)) continue;
                        if (!string.IsNullOrEmpty(typeFilter) &&
                            !string.Equals(GetKeyType(key), typeFilter, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (skipped < offset) { skipped++; continue; }
                        if (keys.Count >= limit) return keys;
                        keys.Add(key);
                    }
                } while (cursor != "0");
            }
            return keys;
        }

        private DataTable BuildKeyListTable(List<string> keys)
        {
            DataTable result = new DataTable();
            result.Columns.Add("key", typeof(string));
            result.Columns.Add("type", typeof(string));
            result.Columns.Add("ttl", typeof(string));
            result.Columns.Add("preview", typeof(string));
            foreach (string key in keys)
            {
                DataRow row = result.NewRow();
                row["key"] = key;
                string type = GetKeyType(key);
                row["type"] = type;
                row["ttl"] = DescribeTtl(key);
                row["preview"] = BuildPreview(key, type);
                result.Rows.Add(row);
            }
            return result;
        }

        private DataTable BuildKeyDetailTable(string key, int limit)
        {
            string type = GetKeyType(key);
            DataTable result = new DataTable();
            switch (type)
            {
                case "none":
                    result.Columns.Add("key", typeof(string));
                    result.Columns.Add("status", typeof(string));
                    result.Rows.Add(key, Localization.T("Redis.KeyNotFound"));
                    return result;
                case "string":
                    {
                        result.Columns.Add("key", typeof(string));
                        result.Columns.Add("value", typeof(string));
                        result.Rows.Add(key, Convert.ToString(client.Execute("GET", key), CultureInfo.InvariantCulture));
                        return result;
                    }
                case "list":
                    {
                        result.Columns.Add("index", typeof(long));
                        result.Columns.Add("value", typeof(string));
                        object[] items = client.Execute("LRANGE", key, "0", (limit - 1).ToString(CultureInfo.InvariantCulture)) as object[] ?? new object[0];
                        for (int i = 0; i < items.Length; i++) result.Rows.Add((long)i, Convert.ToString(items[i], CultureInfo.InvariantCulture));
                        return result;
                    }
                case "hash":
                    {
                        result.Columns.Add("field", typeof(string));
                        result.Columns.Add("value", typeof(string));
                        foreach (KeyValuePair<string, string> pair in ScanPairs("HSCAN", key, limit))
                            result.Rows.Add(pair.Key, pair.Value);
                        return result;
                    }
                case "set":
                    {
                        result.Columns.Add("member", typeof(string));
                        foreach (string member in ScanMembers("SSCAN", key, limit)) result.Rows.Add(member);
                        return result;
                    }
                case "zset":
                    {
                        result.Columns.Add("member", typeof(string));
                        result.Columns.Add("score", typeof(string));
                        object[] items = client.Execute("ZRANGE", key, "0", (limit - 1).ToString(CultureInfo.InvariantCulture), "WITHSCORES") as object[] ?? new object[0];
                        for (int i = 0; i + 1 < items.Length; i += 2)
                            result.Rows.Add(Convert.ToString(items[i], CultureInfo.InvariantCulture), Convert.ToString(items[i + 1], CultureInfo.InvariantCulture));
                        return result;
                    }
                default:
                    {
                        // stream 等其他型別：第一期先給型別與長度摘要，不嘗試展開內容。
                        result.Columns.Add("key", typeof(string));
                        result.Columns.Add("type", typeof(string));
                        result.Columns.Add("summary", typeof(string));
                        result.Rows.Add(key, type, BuildPreview(key, type));
                        return result;
                    }
            }
        }

        private IEnumerable<KeyValuePair<string, string>> ScanPairs(string command, string key, int limit)
        {
            List<KeyValuePair<string, string>> pairs = new List<KeyValuePair<string, string>>();
            string cursor = "0";
            do
            {
                object[] reply = client.Execute(command, key, cursor, "COUNT", ScanBatchSize.ToString(CultureInfo.InvariantCulture)) as object[];
                if (reply == null || reply.Length != 2) break;
                cursor = Convert.ToString(reply[0], CultureInfo.InvariantCulture);
                object[] batch = reply[1] as object[] ?? new object[0];
                for (int i = 0; i + 1 < batch.Length; i += 2)
                {
                    if (pairs.Count >= limit) return pairs;
                    pairs.Add(new KeyValuePair<string, string>(
                        Convert.ToString(batch[i], CultureInfo.InvariantCulture),
                        Convert.ToString(batch[i + 1], CultureInfo.InvariantCulture)));
                }
            } while (cursor != "0");
            return pairs;
        }

        private IEnumerable<string> ScanMembers(string command, string key, int limit)
        {
            List<string> members = new List<string>();
            string cursor = "0";
            do
            {
                object[] reply = client.Execute(command, key, cursor, "COUNT", ScanBatchSize.ToString(CultureInfo.InvariantCulture)) as object[];
                if (reply == null || reply.Length != 2) break;
                cursor = Convert.ToString(reply[0], CultureInfo.InvariantCulture);
                object[] batch = reply[1] as object[] ?? new object[0];
                foreach (object item in batch)
                {
                    if (members.Count >= limit) return members;
                    members.Add(Convert.ToString(item, CultureInfo.InvariantCulture));
                }
            } while (cursor != "0");
            return members;
        }

        private string GetKeyType(string key)
        {
            return Convert.ToString(client.Execute("TYPE", key), CultureInfo.InvariantCulture);
        }

        private string DescribeTtl(string key)
        {
            long ttlMs = Convert.ToInt64(client.Execute("PTTL", key), CultureInfo.InvariantCulture);
            if (ttlMs == -1) return string.Empty;
            if (ttlMs < 0) return Localization.T("Redis.KeyNotFound");
            return TimeSpan.FromMilliseconds(ttlMs).ToString("g", CultureInfo.InvariantCulture);
        }

        private string BuildPreview(string key, string type)
        {
            try
            {
                switch (type)
                {
                    case "string":
                        {
                            string value = Convert.ToString(client.Execute("GETRANGE", key, "0", (PreviewLength - 1).ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
                            long length = Convert.ToInt64(client.Execute("STRLEN", key), CultureInfo.InvariantCulture);
                            return length > PreviewLength ? value + "…" : value;
                        }
                    case "list": return Localization.Format("Redis.CollectionSummary", type, client.Execute("LLEN", key));
                    case "hash": return Localization.Format("Redis.CollectionSummary", type, client.Execute("HLEN", key));
                    case "set": return Localization.Format("Redis.CollectionSummary", type, client.Execute("SCARD", key));
                    case "zset": return Localization.Format("Redis.CollectionSummary", type, client.Execute("ZCARD", key));
                    case "stream": return Localization.Format("Redis.CollectionSummary", type, client.Execute("XLEN", key));
                    default: return type;
                }
            }
            catch (RedisServerException ex)
            {
                return ex.Message;
            }
        }

        private static string ParseInfoValue(string info, string name)
        {
            string value;
            return ParseInfoFields(info).TryGetValue(name, out value) ? value : string.Empty;
        }

        private static Dictionary<string, string> ParseInfoFields(string info)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in (info ?? string.Empty).Split('\n'))
            {
                string trimmed = line.TrimEnd('\r').Trim();
                if (trimmed.Length == 0 || trimmed[0] == '#') continue;
                int separator = trimmed.IndexOf(':');
                if (separator <= 0) continue;
                string key = trimmed.Substring(0, separator).Trim();
                if (key.Length == 0) continue;
                result[key] = trimmed.Substring(separator + 1).Trim();
            }
            return result;
        }

        private void ReadInfoSectionWhenMissing(Dictionary<string, string> fields, string section, params string[] expectedNames)
        {
            foreach (string expectedName in expectedNames ?? new string[0])
            {
                if (expectedName.EndsWith("_", StringComparison.Ordinal))
                {
                    foreach (string name in fields.Keys)
                        if (name.StartsWith(expectedName, StringComparison.OrdinalIgnoreCase)) return;
                }
                else if (fields.ContainsKey(expectedName)) return;
            }

            try { MergeInfoFields(fields, client.Execute("INFO", section) as string); }
            catch (RedisServerException) { }
        }

        private static void MergeInfoFields(Dictionary<string, string> target, string info)
        {
            if (target == null) return;
            foreach (KeyValuePair<string, string> pair in ParseInfoFields(info)) target[pair.Key] = pair.Value;
        }

        private static void AddMonitorMetric(RedisMonitorSnapshot snapshot, string section, string name, string value)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(value)) return;
            snapshot.Metrics.Add(new RedisMonitorMetric { Section = section, Name = name, Value = value });
        }

        private static void AddMonitorMetric(
            RedisMonitorSnapshot snapshot,
            Dictionary<string, string> fields,
            string section,
            string name,
            string fallbackName = null)
        {
            string value;
            if (fields != null && fields.TryGetValue(name, out value) && !string.IsNullOrWhiteSpace(value))
            {
                AddMonitorMetric(snapshot, section, name, value);
                return;
            }
            if (!string.IsNullOrWhiteSpace(fallbackName) && fields != null && fields.TryGetValue(fallbackName, out value))
                AddMonitorMetric(snapshot, section, fallbackName, value);
        }

        private static bool TryReadLong(Dictionary<string, string> fields, string name, out long value)
        {
            value = 0;
            string raw;
            return fields != null && fields.TryGetValue(name, out raw) &&
                long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private static RedisCommandMetric ParseCommandMetric(string commandName, string value)
        {
            if (string.IsNullOrWhiteSpace(commandName)) return null;
            Dictionary<string, string> parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string item in (value ?? string.Empty).Split(','))
            {
                int separator = item.IndexOf('=');
                if (separator <= 0) continue;
                parts[item.Substring(0, separator).Trim()] = item.Substring(separator + 1).Trim();
            }

            long calls;
            if (!parts.TryGetValue("calls", out value) || !long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out calls))
                return null;
            double average = 0d;
            long failed = 0;
            long rejected = 0;
            if (parts.TryGetValue("usec_per_call", out value))
                double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out average);
            if (parts.TryGetValue("failed_calls", out value))
                long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out failed);
            if (parts.TryGetValue("rejected_calls", out value))
                long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out rejected);
            return new RedisCommandMetric
            {
                Command = commandName.Replace('|', ' '),
                Calls = calls,
                MicrosecondsPerCall = average,
                FailedCalls = failed,
                RejectedCalls = rejected
            };
        }

        private static void AddColumnRow(DataTable table, string field, string type, string key, string comment)
        {
            DataRow row = table.NewRow();
            row["Field"] = field;
            row["Type"] = type;
            row["Null"] = "NO";
            row["Key"] = key;
            row["Default"] = string.Empty;
            row["Extra"] = string.Empty;
            row["Comment"] = comment;
            table.Rows.Add(row);
        }

        private void EnsureOpen()
        {
            if (!open || client == null) throw new InvalidOperationException(Localization.T("Redis.ConnectionNotOpen"));
        }

        /// <summary>額外的專用連線（Pub/Sub）連到實際使用中的節點：Sentinel 為解析出的 master。</summary>
        private RedisRespClient OpenAuthenticatedClient(int databaseIndex)
        {
            return ConnectNode(connectedHost, connectedPort, databaseIndex);
        }

        private static Exception UnsupportedWrite()
        {
            return new NotSupportedException(Localization.T("Redis.ReadOnlyFirstPhase"));
        }

        private static int NormalizeLimit(int limit)
        {
            if (limit <= 0) return DefaultQueryLimit;
            return Math.Min(limit, MaxQueryLimit);
        }

        /// <summary>string key 的編輯基準快照。</summary>
        public sealed class RedisStringEditContext
        {
            public string Key = string.Empty;
            public string Value = string.Empty;
            /// <summary>剩餘毫秒；-1 代表不會過期。</summary>
            public long TtlMs = -1;
            /// <summary>值含無法以 UTF-8 呈現的位元組時只允許檢視，避免寫回損毀資料。</summary>
            public bool IsBinaryUnsafe;
        }

        /// <summary>查詢分頁接受的受限唯讀規格；未知欄位一律拒絕。</summary>
        public sealed class RedisReadQuery
        {
            public string Pattern = string.Empty;
            public string Key = string.Empty;
            public string Type = string.Empty;
            public int Limit = DefaultQueryLimit;

            private static readonly HashSet<string> AllowedTypes = new HashSet<string>(
                new[] { "string", "list", "hash", "set", "zset", "stream" }, StringComparer.OrdinalIgnoreCase);

            public static RedisReadQuery Parse(string query)
            {
                if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException(Localization.T("Redis.QueryRequired"), "query");
                string trimmed = query.Trim();
                // 開啟 keys 虛擬資料表時程式會產生 SELECT * FROM keys；等同全部掃描。
                if (Regex.IsMatch(trimmed, @"^SELECT\s+\*\s+FROM\s+keys\s*;?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    return new RedisReadQuery { Pattern = "*", Limit = DefaultQueryLimit };
                }
                if (trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
                    throw new FormatException(Localization.T("Redis.InvalidSelectQuery"));
                JObject document;
                try { document = JObject.Parse(trimmed); }
                catch (Exception ex) { throw new FormatException(Localization.Format("Redis.InvalidJsonQuery", ex.Message), ex); }

                HashSet<string> allowed = new HashSet<string>(new[] { "pattern", "key", "type", "limit" }, StringComparer.OrdinalIgnoreCase);
                foreach (JProperty property in document.Properties())
                {
                    if (!allowed.Contains(property.Name))
                        throw new FormatException(Localization.Format("Redis.UnsupportedQueryField", property.Name));
                }

                RedisReadQuery result = new RedisReadQuery
                {
                    Pattern = ReadString(document, "pattern"),
                    Key = ReadString(document, "key"),
                    Type = ReadString(document, "type"),
                    Limit = ReadLimit(document)
                };
                if (!string.IsNullOrEmpty(result.Key) && !string.IsNullOrEmpty(result.Pattern))
                    throw new FormatException(Localization.T("Redis.KeyPatternConflict"));
                if (!string.IsNullOrEmpty(result.Type) && !AllowedTypes.Contains(result.Type))
                    throw new FormatException(Localization.Format("Redis.UnsupportedQueryType", result.Type));
                return result;
            }

            private static string ReadString(JObject document, string name)
            {
                JToken token = document.GetValue(name, StringComparison.OrdinalIgnoreCase);
                if (token == null || token.Type == JTokenType.Null) return string.Empty;
                if (token.Type != JTokenType.String)
                    throw new FormatException(Localization.Format("Redis.QueryFieldMustBeString", name));
                return token.Value<string>().Trim();
            }

            private static int ReadLimit(JObject document)
            {
                JToken token = document.GetValue("limit", StringComparison.OrdinalIgnoreCase);
                if (token == null || token.Type == JTokenType.Null) return DefaultQueryLimit;
                if (token.Type != JTokenType.Integer)
                    throw new FormatException(Localization.Format("Redis.QueryFieldMustBeInteger", "limit"));
                int value = token.Value<int>();
                if (value <= 0) throw new FormatException(Localization.Format("Redis.QueryFieldNonNegative", "limit"));
                return Math.Min(value, MaxQueryLimit);
            }
        }
    }
}
