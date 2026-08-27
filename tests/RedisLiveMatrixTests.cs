using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Net.Sockets;
using mySQLPunk;
using mySQLPunk.lib;

/// <summary>
/// Redis／Garnet standalone 實機矩陣：以真正的伺服器驗證唯讀瀏覽、受限查詢與 string 安全編輯。
/// 用法：RedisLiveMatrixTests.exe <port> <label>；所有測試資料使用 mtx: 前綴並於結束時清除。
/// </summary>
internal static class RedisLiveMatrixTests
{
    private static int _checks;

    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: RedisLiveMatrixTests.exe <port> <label>");
            return 2;
        }
        int port = int.Parse(args[0], CultureInfo.InvariantCulture);
        string label = args[1];
        try
        {
            using (RawRespClient raw = RawRespClient.Connect("127.0.0.1", port))
            {
                RunMatrix(port, label, raw);
            }
            Console.WriteLine(label + " live matrix passed: " + _checks + " checks");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(label + " live matrix failed: " + ex.Message);
            return 1;
        }
    }

    private static void RunMatrix(int port, string label, RawRespClient raw)
    {
        CleanupKeys(raw);
        raw.Execute("SET", "mtx:string", "hello world");
        raw.Execute("SET", "mtx:ttl", "expiring");
        raw.Execute("PEXPIRE", "mtx:ttl", "600000");
        raw.Execute("RPUSH", "mtx:list", "a", "b", "c");
        raw.Execute("HSET", "mtx:hash", "f1", "v1", "f2", "v2");
        raw.Execute("SADD", "mtx:set", "m1", "m2");
        raw.Execute("ZADD", "mtx:zset", "1.5", "z1", "2.5", "z2");

        using (my_redis provider = new my_redis())
        {
            provider.SetConn(my_redis.BuildConnectionString("127.0.0.1", port, "", "", false, 0));
            provider.Open();
            Check(provider.State == ConnectionState.Open, "provider opens over RESP");

            Dictionary<string, string> info = provider.GetDatabaseInfo("db0");
            Check(info.ContainsKey("redis_version") || info.ContainsKey("garnet_version"),
                "INFO server exposes a version");
            Check(provider.CountRows("db0", "keys") >= 6, "DBSIZE counts the seeded keys");

            DataTable page = provider.SelectJsonQuery("db0", "{ \"pattern\": \"mtx:*\", \"limit\": 100 }");
            Check(page.Rows.Count == 6, "pattern scan returns exactly the seeded keys");
            Dictionary<string, string> types = page.Rows.Cast<DataRow>()
                .ToDictionary(r => Convert.ToString(r["key"]), r => Convert.ToString(r["type"]), StringComparer.Ordinal);
            Check(types["mtx:string"] == "string" && types["mtx:list"] == "list" && types["mtx:hash"] == "hash"
                && types["mtx:set"] == "set" && types["mtx:zset"] == "zset", "TYPE reports every seeded kind");
            Check(!string.IsNullOrEmpty(Convert.ToString(
                page.Rows.Cast<DataRow>().First(r => Convert.ToString(r["key"]) == "mtx:ttl")["ttl"])),
                "TTL column is populated for expiring keys");

            DataTable hashes = provider.SelectJsonQuery("db0", "{ \"pattern\": \"mtx:*\", \"type\": \"hash\", \"limit\": 10 }");
            Check(hashes.Rows.Count == 1 && Convert.ToString(hashes.Rows[0]["key"]) == "mtx:hash",
                "type filters narrow the scan");

            DataTable stringDetail = provider.SelectJsonQuery("db0", "{ \"key\": \"mtx:string\" }");
            Check(Convert.ToString(stringDetail.Rows[0]["value"]) == "hello world", "single-key string detail");
            DataTable listDetail = provider.SelectJsonQuery("db0", "{ \"key\": \"mtx:list\" }");
            Check(listDetail.Rows.Count == 3 && Convert.ToString(listDetail.Rows[1]["value"]) == "b",
                "LRANGE detail preserves order");
            DataTable hashDetail = provider.SelectJsonQuery("db0", "{ \"key\": \"mtx:hash\" }");
            Check(hashDetail.Rows.Count == 2, "HSCAN detail returns all fields");
            DataTable setDetail = provider.SelectJsonQuery("db0", "{ \"key\": \"mtx:set\" }");
            Check(setDetail.Rows.Count == 2, "SSCAN detail returns all members");
            DataTable zsetDetail = provider.SelectJsonQuery("db0", "{ \"key\": \"mtx:zset\" }");
            Check(zsetDetail.Rows.Count == 2 && Convert.ToString(zsetDetail.Rows[0]["member"]) == "z1",
                "ZRANGE WITHSCORES detail is ordered by score");

            my_redis.RedisStringEditContext context = provider.GetStringForEdit("db0", "mtx:ttl");
            Check(context.Value == "expiring" && context.TtlMs > 0 && !context.IsBinaryUnsafe,
                "edit context loads value and remaining TTL");

            provider.SaveStringValue("db0", "mtx:ttl", "expiring", "expiring v2", true);
            Check(Convert.ToString(raw.Execute("GET", "mtx:ttl")) == "expiring v2", "save writes the new value");
            long ttlAfter = Convert.ToInt64(raw.Execute("PTTL", "mtx:ttl"), CultureInfo.InvariantCulture);
            Check(ttlAfter > 0 && ttlAfter <= 600000, "save with preserveTtl keeps the key expiring");

            provider.SaveStringValue("db0", "mtx:ttl", "expiring v2", "expiring v3", false);
            Check(Convert.ToInt64(raw.Execute("PTTL", "mtx:ttl"), CultureInfo.InvariantCulture) == -1,
                "save without preserveTtl clears the TTL");

            my_redis.RedisStringEditContext stale = provider.GetStringForEdit("db0", "mtx:string");
            raw.Execute("SET", "mtx:string", "changed by someone else");
            bool conflict = false;
            try { provider.SaveStringValue("db0", "mtx:string", stale.Value, "stale write", true); }
            catch (RedisEditConflictException) { conflict = true; }
            Check(conflict, "stale saves raise an edit conflict");
            Check(Convert.ToString(raw.Execute("GET", "mtx:string")) == "changed by someone else",
                "conflicts never overwrite the concurrent value");
            provider.SaveStringValue("db0", "mtx:string", "changed by someone else", "recovered", true);
            Check(Convert.ToString(raw.Execute("GET", "mtx:string")) == "recovered",
                "saving after reload succeeds on the same connection");

            bool notSupported = false;
            try { provider.SaveStringValue("db0", "mtx:hash", "x", "y", false); }
            catch (NotSupportedException) { notSupported = true; }
            Check(notSupported, "non-string keys reject value editing");

            provider.SetKeyTtl("db0", "mtx:string", 300);
            long applied = Convert.ToInt64(raw.Execute("PTTL", "mtx:string"), CultureInfo.InvariantCulture);
            Check(applied > 0 && applied <= 300000, "SetKeyTtl applies EXPIRE seconds");
            provider.RemoveKeyTtl("db0", "mtx:string");
            Check(Convert.ToInt64(raw.Execute("PTTL", "mtx:string"), CultureInfo.InvariantCulture) == -1,
                "RemoveKeyTtl persists the key");
            bool missing = false;
            try { provider.SetKeyTtl("db0", "mtx:absent", 30); }
            catch (InvalidOperationException) { missing = true; }
            Check(missing, "TTL updates on missing keys fail loudly");

            Check(provider.DeleteKey("db0", "mtx:zset"), "DeleteKey removes an existing key");
            Check(!provider.DeleteKey("db0", "mtx:zset"), "DeleteKey reports false when already gone");
            Check(Convert.ToInt64(raw.Execute("EXISTS", "mtx:zset"), CultureInfo.InvariantCulture) == 0,
                "deleted keys are gone on the server");

            provider.Close();
        }
        CleanupKeys(raw);
    }

    private static void CleanupKeys(RawRespClient raw)
    {
        foreach (string key in new[] { "mtx:string", "mtx:ttl", "mtx:list", "mtx:hash", "mtx:set", "mtx:zset" })
            raw.Execute("DEL", key);
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("check failed: " + description);
        _checks++;
        Console.WriteLine("  [ok] " + description);
    }

    /// <summary>測試端自備的 RESP 連線，用來播種資料與從 provider 外部驗證伺服器狀態。</summary>
    private sealed class RawRespClient : IDisposable
    {
        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;

        private RawRespClient(TcpClient tcp)
        {
            _tcp = tcp;
            _stream = tcp.GetStream();
        }

        public static RawRespClient Connect(string host, int port)
        {
            // 容器剛啟動時 port 可能已映射但服務尚未受理連線；重試最多約 15 秒。
            Exception last = null;
            for (int attempt = 0; attempt < 30; attempt++)
            {
                TcpClient tcp = new TcpClient();
                try
                {
                    tcp.ReceiveTimeout = 8000;
                    tcp.SendTimeout = 8000;
                    tcp.Connect(host, port);
                    RawRespClient client = new RawRespClient(tcp);
                    client.Execute("PING");
                    return client;
                }
                catch (Exception ex)
                {
                    last = ex;
                    try { tcp.Close(); } catch { }
                    System.Threading.Thread.Sleep(500);
                }
            }
            throw new Exception("Unable to reach the Redis server on port " + port, last);
        }

        public object Execute(params string[] args)
        {
            byte[] command = RedisRespProtocol.BuildCommand(args);
            _stream.Write(command, 0, command.Length);
            _stream.Flush();
            return RedisRespProtocol.ReadReply(_stream);
        }

        public void Dispose()
        {
            try { _stream.Dispose(); } catch { }
            try { _tcp.Close(); } catch { }
        }
    }
}
