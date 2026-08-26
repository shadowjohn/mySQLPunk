using System;
using System.Collections.Generic;
using System.IO;
using MySqlConnector;

namespace mySQLPunk.lib
{
    public sealed class ConnectionOpenResult
    {
        public IDatabase Database { get; private set; }
        public List<string> Databases { get; private set; }
        public string ConnectionString { get; private set; }

        public ConnectionOpenResult(IDatabase database, List<string> databases, string connectionString = null)
        {
            Database = database;
            Databases = databases ?? new List<string>();
            ConnectionString = connectionString ?? string.Empty;
        }
    }

    public static class ConnectionOpenService
    {
        public static ConnectionOpenResult Open(Func<IDatabase> databaseFactory, string connectionString)
        {
            if (databaseFactory == null) throw new ArgumentNullException(nameof(databaseFactory));

            IDatabase db = databaseFactory();
            if (db == null) throw new InvalidOperationException(Localization.T("Connection.DatabaseFactoryReturnedNull"));

            try
            {
                db.SetConn(connectionString);
                db.Open();
                return new ConnectionOpenResult(db, db.GetDatabases(), connectionString);
            }
            catch
            {
                try { db.Dispose(); } catch { }
                throw;
            }
        }

        public static ConnectionOpenResult Open(Dictionary<string, object> connection, bool loadDatabases = true)
        {
            if (connection == null) throw new ArgumentNullException("connection");
            Dictionary<string, object> effective = new Dictionary<string, object>(connection);
            ConnectionSecuritySettingsService.Normalize(effective);
            ValidateSecurityCombination(effective);

            SshTunnelLease tunnel = null;
            IDatabase database = null;
            try
            {
                tunnel = SshTunnelLease.Start(effective);
                if (tunnel != null)
                {
                    effective["host"] = tunnel.LocalHost;
                    effective["port"] = tunnel.LocalPort.ToString();
                    effective["connString"] = string.Empty;
                }

                string provider = ConnectionConfigurationService.NormalizeProvider(ConnectionConfigurationService.GetValue(effective, "db_kind"));
                string connectionString = ConnectionConfigurationService.BuildConnectionString(effective);
                IDatabase rawDatabase = ConnectionConfigurationService.CreateDatabase(provider);
                database = tunnel == null ? rawDatabase : new SecuredDatabase(rawDatabase, tunnel);
                if (tunnel != null) tunnel = null;
                database.SetConn(connectionString);
                database.Open();
                List<string> databases = loadDatabases ? database.GetDatabases() : new List<string>();
                return new ConnectionOpenResult(database, databases, connectionString);
            }
            catch
            {
                try { if (database != null) database.Dispose(); } catch { }
                try { if (tunnel != null) tunnel.Dispose(); } catch { }
                throw;
            }
        }

        private static void ValidateSecurityCombination(Dictionary<string, object> connection)
        {
            if (!ConnectionSecuritySettingsService.IsTrue(connection, "ssh_enabled")) return;
            string tlsMode = ConnectionSecuritySettingsService.GetValue(connection, "tls_mode");
            if (string.Equals(tlsMode, "VerifyFull", StringComparison.OrdinalIgnoreCase))
            {
                string provider = ConnectionConfigurationService.NormalizeProvider(ConnectionConfigurationService.GetValue(connection, "db_kind"));
                string alternative = provider == "mysql" || provider == "postgresql" ? "VerifyCA" : "Required";
                throw new InvalidOperationException("SSH Tunnel 會把資料庫端點改成 127.0.0.1，無法正確比對 TLS 主機名稱；請改用 " + alternative + "，並以 SSH 主機金鑰指紋驗證 Tunnel 端點。");
            }
        }

        public static bool ShouldOfferRetry(Exception ex)
        {
            MySqlException mySqlEx = ex as MySqlException;
            if (mySqlEx != null)
            {
                if (mySqlEx.Number == 1045) return false;
                return mySqlEx.IsTransient;
            }

            string message = ex == null ? string.Empty : ex.Message ?? string.Empty;
            if (message.IndexOf("28P01", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (message.IndexOf("password authentication failed", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (message.IndexOf("login failed for user", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (message.IndexOf("ORA-01017", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (message.IndexOf("ORA-28000", StringComparison.OrdinalIgnoreCase) >= 0) return false;

            if (ex is TimeoutException) return true;
            if (ex is IOException) return true;
            if (ex is System.Net.Sockets.SocketException) return true;
            if (ex != null && ex.InnerException != null) return ShouldOfferRetry(ex.InnerException);
            return false;
        }
    }
}
