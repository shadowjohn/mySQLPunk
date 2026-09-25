using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace mySQLPunk.lib
{
    /// <summary>
    /// my_redis 所有命令的出口。獨立模式直接轉給單一連線；Cluster 模式依 CRC16 key slot 送到負責的 master，
    /// 遇到 MOVED 更新 slot 對應後重送、遇到 ASK 以 ASKING 單次轉送；WATCH 之後到 EXEC／DISCARD／UNWATCH 為止
    /// 固定在同一個節點；DBSIZE 加總所有 master；SCAN 由呼叫端逐一 master 執行。
    /// </summary>
    internal sealed class RedisCommandRouter : IDisposable
    {
        public const int SlotCount = 16384;
        private const int MaximumRedirects = 5;

        /// <summary>容錯切換後可以安全重送的唯讀命令；寫入與交易不自動重送，避免重複或半套執行。</summary>
        private static readonly HashSet<string> ReadOnlyCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "GET", "MGET", "STRLEN", "GETRANGE", "EXISTS", "TYPE", "TTL", "PTTL", "EXPIRETIME", "PEXPIRETIME", "DUMP",
            "HGET", "HGETALL", "HMGET", "HLEN", "HKEYS", "HVALS", "HSCAN", "HSTRLEN", "HEXISTS",
            "LRANGE", "LLEN", "LINDEX", "SMEMBERS", "SCARD", "SISMEMBER", "SSCAN",
            "ZRANGE", "ZCARD", "ZSCORE", "ZSCAN", "ZRANGEBYSCORE", "ZREVRANGE", "ZREVRANGEBYSCORE", "ZCOUNT", "ZRANK",
            "XRANGE", "XREVRANGE", "XLEN", "XINFO", "SCAN", "KEYS", "DBSIZE", "INFO", "PING", "ROLE", "TIME",
            "MEMORY", "OBJECT", "COMMAND", "SELECT", "ECHO"
        };

        private RedisRespClient standalone;
        private readonly Func<RedisRespClient> reconnect;
        private readonly Func<string> describeEndpoint;
        private readonly Func<bool> consumeMasterSwitch;
        private bool inTransaction;
        private readonly Func<string, int, RedisRespClient> connect;
        private readonly Dictionary<string, RedisRespClient> nodes = new Dictionary<string, RedisRespClient>(StringComparer.OrdinalIgnoreCase);
        private readonly string[] slotOwners = new string[SlotCount];
        private readonly string seedHost;
        private string pinned;

        private RedisCommandRouter(RedisRespClient standalone, Func<RedisRespClient> reconnect = null, Func<string> describeEndpoint = null, Func<bool> consumeMasterSwitch = null)
        {
            this.standalone = standalone;
            this.reconnect = reconnect;
            this.describeEndpoint = describeEndpoint;
            this.consumeMasterSwitch = consumeMasterSwitch;
        }

        private RedisCommandRouter(string seedAddress, RedisRespClient seed, Func<string, int, RedisRespClient> connect)
        {
            this.connect = connect;
            nodes[seedAddress] = seed;
            seedHost = SplitAddress(seedAddress).Key;
            RefreshSlots(seed);
        }

        public bool IsCluster { get { return standalone == null; } }

        public static RedisCommandRouter ForStandalone(RedisRespClient client)
        {
            return new RedisCommandRouter(client);
        }

        /// <summary>
        /// Sentinel 模式：連線中斷或寫到已降級的舊 master（READONLY）時重新向 Sentinel 解析 master 並換線；
        /// 唯讀命令在新 master 上重送一次，寫入與交易中的命令則回報已切換、不自動重送。
        /// </summary>
        public static RedisCommandRouter ForSentinel(RedisRespClient client, Func<RedisRespClient> reconnect, Func<string> describeEndpoint, Func<bool> consumeMasterSwitch = null)
        {
            if (reconnect == null) throw new ArgumentNullException("reconnect");
            return new RedisCommandRouter(client, reconnect, describeEndpoint, consumeMasterSwitch);
        }

        /// <summary>容錯切換的次數（測試與狀態列使用）。</summary>
        public int Failovers { get; private set; }

        public static RedisCommandRouter ForCluster(string seedHost, int seedPort, RedisRespClient seed, Func<string, int, RedisRespClient> connect)
        {
            return new RedisCommandRouter(FormatAddress(seedHost, seedPort), seed, connect);
        }

        /// <summary>目前 slot 對應中的 master 位址（依 slot 順序、不重複）。</summary>
        public List<string> Masters
        {
            get { return IsCluster ? slotOwners.Where(owner => owner != null).Distinct(StringComparer.OrdinalIgnoreCase).ToList() : new List<string>(); }
        }

        public int CoveredSlots { get { return slotOwners.Count(owner => owner != null); } }

        /// <summary>Cluster 模式中負責這個 key（或分片 channel）slot 的 master 位址。</summary>
        public string OwnerAddress(string key)
        {
            if (!IsCluster) throw new InvalidOperationException();
            return OwnerOf(key);
        }

        public object Execute(params string[] args)
        {
            if (!IsCluster) return reconnect == null ? standalone.Execute(args) : ExecuteWithFailover(args);
            if (args == null || args.Length == 0) throw new ArgumentException("args");
            string command = args[0].ToUpperInvariant();
            switch (command)
            {
                case "SELECT":
                    if (args.Length > 1 && args[1] != "0") throw new RedisServerException(Localization.T("Redis.ClusterDatabaseZero"));
                    return "OK";
                case "DBSIZE":
                    return Masters.Sum(address => Convert.ToInt64(Node(address).Execute("DBSIZE"), CultureInfo.InvariantCulture));
                case "SCAN":
                case "KEYS":
                    throw new InvalidOperationException(Localization.T("Redis.ClusterScanPerNode"));
                case "WATCH":
                    pinned = OwnerOf(args[1]);
                    return Node(pinned).Execute(args);
                case "MULTI":
                    return Node(pinned ?? FirstMaster()).Execute(args);
                case "EXEC":
                case "DISCARD":
                case "UNWATCH":
                    try
                    {
                        return Node(pinned ?? FirstMaster()).Execute(args);
                    }
                    finally
                    {
                        pinned = null;
                    }
                case "PING":
                case "INFO":
                case "CONFIG":
                case "PUBLISH":
                case "CLUSTER":
                case "COMMAND":
                case "ROLE":
                    return Node(FirstMaster()).Execute(args);
            }

            if (args.Length < 2) return Node(FirstMaster()).Execute(args);
            // 交易進行中的命令必須留在同一個節點，由伺服器排入佇列；此時不做重導。
            if (pinned != null) return Node(pinned).Execute(args);
            return ExecuteWithRedirects(OwnerOf(args[1]), args);
        }

        /// <summary>SCAN 需要逐一執行的目標：獨立模式只有一個，Cluster 為每個 master。</summary>
        public IEnumerable<Func<string[], object>> ScanTargets()
        {
            if (!IsCluster)
            {
                yield return args => standalone.Execute(args);
                yield break;
            }
            foreach (string address in Masters)
            {
                string target = address;
                yield return args => Node(target).Execute(args);
            }
        }

        /// <summary>Redis Cluster 的 key slot：CRC16（XMODEM）對 16384 取餘，若有非空的 {hash tag} 只計算其內容。</summary>
        public static int KeySlot(string key)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(key ?? string.Empty);
            int start = Array.IndexOf(bytes, (byte)'{');
            if (start >= 0)
            {
                int end = Array.IndexOf(bytes, (byte)'}', start + 1);
                if (end > start + 1)
                {
                    byte[] tag = new byte[end - start - 1];
                    Array.Copy(bytes, start + 1, tag, 0, tag.Length);
                    bytes = tag;
                }
            }
            return Crc16(bytes) % SlotCount;
        }

        public void Dispose()
        {
            if (standalone != null) standalone.Dispose();
            foreach (RedisRespClient node in nodes.Values) node.Dispose();
            nodes.Clear();
        }

        private object ExecuteWithFailover(string[] args)
        {
            string command = args == null || args.Length == 0 ? string.Empty : args[0].ToUpperInvariant();
            bool transactional = inTransaction || command == "WATCH" || command == "MULTI" || command == "EXEC" || command == "DISCARD" || command == "UNWATCH";
            if (consumeMasterSwitch != null && consumeMasterSwitch())
            {
                // Sentinel 已宣布切換：先換到新 master 再送命令。交易進行中則中止，因為 WATCH 綁在舊連線上。
                bool wasInTransaction = inTransaction;
                SwitchConnection();
                if (wasInTransaction)
                {
                    throw new RedisFailoverException(Localization.Format("Redis.SentinelFailedOver", describeEndpoint == null ? string.Empty : describeEndpoint(), command), null);
                }
            }
            try
            {
                object result = standalone.Execute(args);
                if (command == "WATCH" || command == "MULTI") inTransaction = true;
                else if (command == "EXEC" || command == "DISCARD" || command == "UNWATCH") inTransaction = false;
                return result;
            }
            catch (Exception exception) when (IsFailoverError(exception))
            {
                SwitchConnection();

                if (ReadOnlyCommands.Contains(command) && !transactional) return standalone.Execute(args);
                throw new RedisFailoverException(Localization.Format("Redis.SentinelFailedOver", describeEndpoint == null ? string.Empty : describeEndpoint(), command), exception);
            }
        }

        private void SwitchConnection()
        {
            inTransaction = false;
            RedisRespClient replacement = reconnect();
            RedisRespClient previous = standalone;
            standalone = replacement;
            Failovers++;
            try
            {
                previous.Dispose();
            }
            catch (Exception)
            {
                // 舊連線可能已經斷掉，釋放失敗不影響換線。
            }
        }

        private static bool IsFailoverError(Exception exception)
        {
            if (exception is System.IO.IOException || exception is System.Net.Sockets.SocketException || exception is ObjectDisposedException) return true;
            RedisServerException server = exception as RedisServerException;
            return server != null && (server.Message ?? string.Empty).StartsWith("READONLY", StringComparison.Ordinal);
        }

        private object ExecuteWithRedirects(string address, string[] args)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    return Node(address).Execute(args);
                }
                catch (RedisServerException exception)
                {
                    string message = exception.Message ?? string.Empty;
                    bool moved = message.StartsWith("MOVED ", StringComparison.Ordinal);
                    bool ask = message.StartsWith("ASK ", StringComparison.Ordinal);
                    if (!moved && !ask || attempt >= MaximumRedirects) throw;
                    string[] parts = message.Split(' ');
                    if (parts.Length < 3) throw;
                    string target = NormalizeAddress(parts[2]);
                    if (ask)
                    {
                        // ASK 只代表這一次（slot 遷移中），不更新對應表。
                        RedisRespClient node = Node(target);
                        node.Execute("ASKING");
                        return node.Execute(args);
                    }

                    int slot;
                    if (int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out slot) && slot >= 0 && slot < SlotCount) slotOwners[slot] = target;
                    try
                    {
                        RefreshSlots(Node(target));
                    }
                    catch (RedisServerException)
                    {
                        // 重新整理失敗仍可依 MOVED 指示的節點重送。
                    }
                    address = target;
                }
            }
        }

        private void RefreshSlots(RedisRespClient source)
        {
            object[] ranges = source.Execute("CLUSTER", "SLOTS") as object[];
            if (ranges == null) throw new RedisServerException(Localization.T("Redis.ClusterSlotsUnavailable"));
            for (int index = 0; index < SlotCount; index++) slotOwners[index] = null;
            foreach (object item in ranges)
            {
                object[] range = item as object[];
                if (range == null || range.Length < 3) continue;
                object[] master = range[2] as object[];
                if (master == null || master.Length < 2) continue;
                int start = Convert.ToInt32(range[0], CultureInfo.InvariantCulture);
                int end = Convert.ToInt32(range[1], CultureInfo.InvariantCulture);
                string ip = Convert.ToString(master[0], CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(ip) || ip == "?") ip = seedHost;
                string address = FormatAddress(ip, Convert.ToInt32(master[1], CultureInfo.InvariantCulture));
                for (int slot = Math.Max(0, start); slot <= end && slot < SlotCount; slot++) slotOwners[slot] = address;
            }
            if (CoveredSlots == 0) throw new RedisServerException(Localization.T("Redis.ClusterSlotsUnavailable"));
        }

        private string OwnerOf(string key)
        {
            string owner = slotOwners[KeySlot(key)];
            if (owner == null) throw new RedisServerException(Localization.Format("Redis.ClusterSlotUncovered", KeySlot(key)));
            return owner;
        }

        private string FirstMaster()
        {
            string first = slotOwners.FirstOrDefault(owner => owner != null);
            if (first == null) throw new RedisServerException(Localization.T("Redis.ClusterSlotsUnavailable"));
            return first;
        }

        private RedisRespClient Node(string address)
        {
            RedisRespClient node;
            if (nodes.TryGetValue(address, out node)) return node;
            KeyValuePair<string, int> endpoint = SplitAddress(address);
            node = connect(endpoint.Key, endpoint.Value);
            nodes[address] = node;
            return node;
        }

        private string NormalizeAddress(string address)
        {
            KeyValuePair<string, int> endpoint = SplitAddress(address);
            return FormatAddress(string.IsNullOrWhiteSpace(endpoint.Key) ? seedHost : endpoint.Key, endpoint.Value);
        }

        public static string FormatAddress(string host, int port)
        {
            string value = (host ?? string.Empty).Trim();
            if (value.IndexOf(':') >= 0 && !value.StartsWith("[", StringComparison.Ordinal)) value = "[" + value + "]";
            return value + ":" + port.ToString(CultureInfo.InvariantCulture);
        }

        public static KeyValuePair<string, int> SplitAddress(string address)
        {
            string value = (address ?? string.Empty).Trim();
            int split = value.LastIndexOf(':');
            int port;
            if (split < 0 || !int.TryParse(value.Substring(split + 1), NumberStyles.None, CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535)
            {
                throw new FormatException(Localization.Format("Redis.InvalidNodeAddress", address));
            }
            string host = value.Substring(0, split).Trim('[', ']');
            return new KeyValuePair<string, int>(host, port);
        }

        private static int Crc16(byte[] bytes)
        {
            int crc = 0;
            foreach (byte value in bytes)
            {
                crc ^= value << 8;
                for (int bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1;
                    crc &= 0xFFFF;
                }
            }
            return crc;
        }
    }

    /// <summary>Sentinel 已把連線切到新的 master，但這個命令沒有重送（寫入或交易），結果未知，請重新整理後再試。</summary>
    public sealed class RedisFailoverException : InvalidOperationException
    {
        public RedisFailoverException(string message, Exception inner) : base(message, inner) { }
    }
}
