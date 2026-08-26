using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Renci.SshNet;

namespace mySQLPunk.lib
{
    public static class ConnectionSecuritySettingsService
    {
        public static readonly string[] PersistedKeys =
        {
            "tls_mode", "tls_ca_path", "tls_client_certificate_path", "tls_client_key_path",
            "tls_check_revocation", "tls_wallet_path", "ssh_enabled", "ssh_host", "ssh_port",
            "ssh_username", "ssh_private_key_path", "ssh_host_key_fingerprint", "security_credential_target"
        };

        public static readonly string[] SecretKeys =
        {
            "ssh_password", "ssh_key_passphrase", "tls_certificate_password"
        };

        public static void Normalize(Dictionary<string, object> connection)
        {
            if (connection == null) return;
            string provider = ConnectionConfigurationService.NormalizeProvider(GetValue(connection, "db_kind"));
            SetDefault(connection, "tls_mode", GetDefaultTlsMode(provider));
            SetDefault(connection, "tls_ca_path", string.Empty);
            SetDefault(connection, "tls_client_certificate_path", string.Empty);
            SetDefault(connection, "tls_client_key_path", string.Empty);
            SetDefault(connection, "tls_certificate_password", string.Empty);
            SetDefault(connection, "tls_check_revocation", "F");
            SetDefault(connection, "tls_wallet_path", string.Empty);
            SetDefault(connection, "ssh_enabled", "F");
            SetDefault(connection, "ssh_host", string.Empty);
            SetDefault(connection, "ssh_port", "22");
            SetDefault(connection, "ssh_username", string.Empty);
            SetDefault(connection, "ssh_password", string.Empty);
            SetDefault(connection, "ssh_private_key_path", string.Empty);
            SetDefault(connection, "ssh_key_passphrase", string.Empty);
            SetDefault(connection, "ssh_host_key_fingerprint", string.Empty);
            SetDefault(connection, "security_credential_target", string.Empty);
        }

        public static void Copy(Dictionary<string, object> source, Dictionary<string, object> destination)
        {
            if (destination == null) throw new ArgumentNullException("destination");
            if (source != null)
            {
                foreach (string key in PersistedKeys.Concat(SecretKeys))
                {
                    if (source.ContainsKey(key)) destination[key] = source[key];
                }
            }
            Normalize(destination);
        }

        public static string SerializeSecrets(Dictionary<string, object> connection)
        {
            Dictionary<string, string> secrets = SecretKeys.ToDictionary(key => key, key => GetValue(connection, key));
            if (secrets.Values.All(string.IsNullOrEmpty)) return string.Empty;
            return JsonConvert.SerializeObject(secrets);
        }

        public static void ApplySerializedSecrets(Dictionary<string, object> connection, string payload)
        {
            if (connection == null) return;
            Dictionary<string, string> secrets = string.IsNullOrWhiteSpace(payload)
                ? null
                : JsonConvert.DeserializeObject<Dictionary<string, string>>(payload);
            foreach (string key in SecretKeys)
            {
                string value;
                connection[key] = secrets != null && secrets.TryGetValue(key, out value) ? value ?? string.Empty : string.Empty;
            }
        }

        public static string GetSummary(Dictionary<string, object> connection)
        {
            Normalize(connection);
            string tls = GetValue(connection, "tls_mode");
            string ssh = IsTrue(connection, "ssh_enabled")
                ? GetValue(connection, "ssh_host") + ":" + GetValue(connection, "ssh_port")
                : "關閉";
            return "TLS: " + tls + "；SSH: " + ssh;
        }

        public static string NormalizeSshHostKeyFingerprint(string value)
        {
            return SshTunnelLease.NormalizeFingerprint(value);
        }

        public static bool IsTrue(Dictionary<string, object> connection, string key)
        {
            string value = GetValue(connection, key).Trim();
            return value == "T" || value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        public static string GetValue(Dictionary<string, object> connection, string key)
        {
            return ConnectionConfigurationService.GetValue(connection, key);
        }

        public static string GetDefaultTlsMode(string provider)
        {
            switch (ConnectionConfigurationService.NormalizeProvider(provider))
            {
                case "mysql": return "Preferred";
                case "postgresql": return "Prefer";
                default: return "Disabled";
            }
        }

        private static void SetDefault(Dictionary<string, object> connection, string key, string value)
        {
            if (!connection.ContainsKey(key) || connection[key] == null ||
                (key == "ssh_port" && string.IsNullOrWhiteSpace(connection[key].ToString())))
            {
                connection[key] = value;
            }
        }
    }

    internal sealed class SshTunnelLease : IDisposable
    {
        private SshClient client;
        private ForwardedPortLocal forwardedPort;

        public string LocalHost { get { return "127.0.0.1"; } }
        public uint LocalPort { get { return forwardedPort == null ? 0 : forwardedPort.BoundPort; } }

        public static SshTunnelLease Start(Dictionary<string, object> connection)
        {
            if (!ConnectionSecuritySettingsService.IsTrue(connection, "ssh_enabled")) return null;

            string sshHost = Require(connection, "ssh_host", "SSH 主機");
            string sshUser = Require(connection, "ssh_username", "SSH 使用者名稱");
            string fingerprint = NormalizeFingerprint(Require(connection, "ssh_host_key_fingerprint", "SSH 主機金鑰 SHA256 指紋"));
            int sshPort = ParsePort(ConnectionSecuritySettingsService.GetValue(connection, "ssh_port"), 22, "SSH 連接埠");
            string remoteHost = Require(connection, "host", "資料庫主機");
            int remotePort = ParsePort(ConnectionSecuritySettingsService.GetValue(connection, "port"), 0, "資料庫連接埠");

            if (string.Equals(ConnectionSecuritySettingsService.GetValue(connection, "connection_type"), "TNS", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Oracle TNS 模式無法判斷轉送目的地；請改用 Basic 連線再啟用 SSH Tunnel。");
            }

            List<AuthenticationMethod> methods = new List<AuthenticationMethod>();
            string password = ConnectionSecuritySettingsService.GetValue(connection, "ssh_password");
            if (!string.IsNullOrEmpty(password)) methods.Add(new PasswordAuthenticationMethod(sshUser, password));

            string keyPath = ConnectionSecuritySettingsService.GetValue(connection, "ssh_private_key_path").Trim();
            if (!string.IsNullOrWhiteSpace(keyPath))
            {
                if (!File.Exists(keyPath)) throw new FileNotFoundException("找不到 SSH 私鑰檔案。", keyPath);
                string passphrase = ConnectionSecuritySettingsService.GetValue(connection, "ssh_key_passphrase");
                PrivateKeyFile keyFile = string.IsNullOrEmpty(passphrase)
                    ? new PrivateKeyFile(keyPath)
                    : new PrivateKeyFile(keyPath, passphrase);
                methods.Add(new PrivateKeyAuthenticationMethod(sshUser, keyFile));
            }

            if (methods.Count == 0) throw new InvalidOperationException("SSH Tunnel 至少要填密碼或選擇私鑰。");

            ConnectionInfo info = new ConnectionInfo(sshHost, sshPort, sshUser, methods.ToArray());
            info.Timeout = TimeSpan.FromSeconds(12);
            SshTunnelLease lease = new SshTunnelLease();
            lease.client = new SshClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
            lease.client.HostKeyReceived += (sender, args) =>
            {
                args.CanTrust = string.Equals("SHA256:" + args.FingerPrintSHA256, fingerprint, StringComparison.Ordinal);
            };

            try
            {
                lease.client.Connect();
                lease.forwardedPort = new ForwardedPortLocal("127.0.0.1", 0, remoteHost, (uint)remotePort);
                lease.client.AddForwardedPort(lease.forwardedPort);
                lease.forwardedPort.Start();
                return lease;
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }

        public static string NormalizeFingerprint(string value)
        {
            string fingerprint = (value ?? string.Empty).Trim();
            if (!fingerprint.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase)) fingerprint = "SHA256:" + fingerprint;
            return "SHA256:" + fingerprint.Substring(7).Trim().TrimEnd('=');
        }

        public void Dispose()
        {
            if (forwardedPort != null)
            {
                try { if (forwardedPort.IsStarted) forwardedPort.Stop(); } catch { }
                try { forwardedPort.Dispose(); } catch { }
                forwardedPort = null;
            }
            if (client != null)
            {
                try { if (client.IsConnected) client.Disconnect(); } catch { }
                try { client.Dispose(); } catch { }
                client = null;
            }
        }

        private static string Require(Dictionary<string, object> connection, string key, string label)
        {
            string value = ConnectionSecuritySettingsService.GetValue(connection, key).Trim();
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException(label + "不能空白。");
            return value;
        }

        private static int ParsePort(string value, int fallback, string label)
        {
            int port;
            if (string.IsNullOrWhiteSpace(value) && fallback > 0) return fallback;
            if (!int.TryParse(value, out port) || port < 1 || port > 65535)
                throw new InvalidOperationException(label + "必須介於 1 到 65535。");
            return port;
        }
    }

    internal sealed class SecuredDatabase : IDatabase
    {
        private IDatabase inner;
        private IDisposable transport;

        public SecuredDatabase(IDatabase innerDatabase, IDisposable transportLease)
        {
            inner = innerDatabase ?? throw new ArgumentNullException("innerDatabase");
            transport = transportLease;
        }

        public ConnectionState State { get { return inner.State; } }
        public string ProviderName { get { return inner.ProviderName; } }
        public void SetConn(string value) { inner.SetConn(value); }
        public void Open() { inner.Open(); }
        public void Close()
        {
            try { inner.Close(); }
            finally
            {
                IDisposable lease = transport;
                transport = null;
                if (lease != null) lease.Dispose();
            }
        }
        public System.Data.DataTable SelectSQL(string sql, Dictionary<string, object> parameters = null) { return inner.SelectSQL(sql, parameters); }
        public Dictionary<string, string> ExecSQL(string sql, Dictionary<string, object> parameters = null) { return inner.ExecSQL(sql, parameters); }
        public Task<System.Data.DataTable> SelectSQLAsync(string sql, Dictionary<string, object> parameters = null) { return inner.SelectSQLAsync(sql, parameters); }
        public Task<Dictionary<string, string>> ExecSQLAsync(string sql, Dictionary<string, object> parameters = null) { return inner.ExecSQLAsync(sql, parameters); }
        public List<string> GetDatabases() { return inner.GetDatabases(); }
        public List<string> GetTables(string databaseName) { return inner.GetTables(databaseName); }
        public List<string> GetViews(string databaseName) { return inner.GetViews(databaseName); }
        public System.Data.DataTable GetColumns(string databaseName, string tableName) { return inner.GetColumns(databaseName, tableName); }
        public System.Data.DataTable GetIndexes(string databaseName, string tableName) { return inner.GetIndexes(databaseName, tableName); }
        public System.Data.DataTable GetTableStatus(string databaseName) { return inner.GetTableStatus(databaseName); }
        public Dictionary<string, string> GetDatabaseInfo(string databaseName) { return inner.GetDatabaseInfo(databaseName); }
        public string GetTableCreateStatement(string databaseName, string tableName) { return inner.GetTableCreateStatement(databaseName, tableName); }
        public bool TableExists(string databaseName, string tableName) { return inner.TableExists(databaseName, tableName); }
        public bool ViewExists(string databaseName, string viewName) { return inner.ViewExists(databaseName, viewName); }
        public void RenameTable(string databaseName, string oldTableName, string newTableName) { inner.RenameTable(databaseName, oldTableName, newTableName); }
        public void RenameView(string databaseName, string oldViewName, string newViewName) { inner.RenameView(databaseName, oldViewName, newViewName); }
        public long CountRows(string databaseName, string tableName) { return inner.CountRows(databaseName, tableName); }
        public System.Data.DataTable GetCopyColumns(string databaseName, string tableName) { return inner.GetCopyColumns(databaseName, tableName); }
        public System.Data.DataTable GetCopyIndexes(string databaseName, string tableName) { return inner.GetCopyIndexes(databaseName, tableName); }
        public void CreateTableForCopy(string databaseName, string tableName, System.Data.DataTable sourceColumns, string sourceProvider) { inner.CreateTableForCopy(databaseName, tableName, sourceColumns, sourceProvider); }
        public void DropTableForCopy(string databaseName, string tableName) { inner.DropTableForCopy(databaseName, tableName); }
        public void CreateIndexesForCopy(string databaseName, string tableName, System.Data.DataTable sourceIndexes, string sourceProvider) { inner.CreateIndexesForCopy(databaseName, tableName, sourceIndexes, sourceProvider); }
        public System.Data.DataTable SelectTablePage(string databaseName, string tableName, long offset, int limit) { return inner.SelectTablePage(databaseName, tableName, offset, limit); }
        public void InsertTableBatch(string databaseName, string tableName, System.Data.DataTable rows) { inner.InsertTableBatch(databaseName, tableName, rows); }
        public string GetViewCreateStatement(string databaseName, string viewName) { return inner.GetViewCreateStatement(databaseName, viewName); }
        public void CreateViewFromStatement(string databaseName, string viewName, string sourceViewSql) { inner.CreateViewFromStatement(databaseName, viewName, sourceViewSql); }

        public void Dispose()
        {
            IDatabase database = inner;
            inner = null;
            try { if (database != null) database.Dispose(); }
            finally
            {
                IDisposable lease = transport;
                transport = null;
                if (lease != null) lease.Dispose();
            }
        }
    }
}
