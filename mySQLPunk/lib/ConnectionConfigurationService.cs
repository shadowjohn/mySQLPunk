using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Data.SqlClient;
using MySqlConnector;
using MongoDB.Driver;
using MongoDB.Driver.Core.Configuration;
using Npgsql;
using Oracle.ManagedDataAccess.Client;

namespace mySQLPunk.lib
{
    /// <summary>
    /// 將已保存的連線設定轉成 provider 物件與 connection string。
    /// UI 與背景工作共用這裡，避免兩條路徑的連線行為逐漸不同。
    /// </summary>
    public static class ConnectionConfigurationService
    {
        public static IDatabase CreateDatabase(string provider)
        {
            switch (NormalizeProvider(provider))
            {
                case "mysql": return new my_mysql();
                case "postgresql": return new my_postgresql();
                case "mssql": return new my_mssql();
                case "oracle": return new my_oracle();
                case "sqlite": return new my_sqlite();
                case "mongodb": return new my_mongodb();
                case "redis": return new my_redis();
                default: throw new NotSupportedException(Localization.Format("Automation.UnsupportedProvider", provider ?? string.Empty));
            }
        }

        public static string BuildConnectionString(Dictionary<string, object> connection)
        {
            if (connection == null) throw new ArgumentNullException("connection");
            string provider = NormalizeProvider(GetValue(connection, "db_kind"));
            switch (provider)
            {
                case "mysql": return BuildMySqlConnectionString(connection);
                case "postgresql": return BuildPostgreSqlConnectionString(connection);
                case "mssql": return BuildSqlServerConnectionString(connection);
                case "oracle": return BuildOracleConnectionString(connection);
                case "sqlite": return BuildSqliteConnectionString(connection);
                case "mongodb": return BuildMongoDbConnectionString(connection);
                case "redis": return BuildRedisConnectionString(connection);
                default: throw new NotSupportedException(Localization.Format("Automation.UnsupportedProvider", provider));
            }
        }

        public static string BuildSqlServerConnectionString(Dictionary<string, object> connection)
        {
            SqlConnectionStringBuilder builder = new SqlConnectionStringBuilder();
            string host = GetValue(connection, "host");
            string port = GetValue(connection, "port");
            builder.DataSource = BuildSqlServerDataSource(host, port);
            builder.InitialCatalog = string.IsNullOrWhiteSpace(GetValue(connection, "initial_database"))
                ? "master"
                : GetValue(connection, "initial_database");
            bool trusted = string.Equals(GetValue(connection, "trusted_connection"), "T", StringComparison.OrdinalIgnoreCase);
            builder.IntegratedSecurity = trusted;
            string tlsMode = NormalizeTlsMode(connection, "Disabled");
            builder.Encrypt = !string.Equals(tlsMode, "Disabled", StringComparison.OrdinalIgnoreCase);
            builder.TrustServerCertificate = !string.Equals(tlsMode, "VerifyFull", StringComparison.OrdinalIgnoreCase);
            builder.MultipleActiveResultSets = true;
            builder.ConnectTimeout = 8;
            if (!trusted)
            {
                builder.UserID = GetValue(connection, "username");
                builder.Password = GetValue(connection, "pwd");
            }
            return builder.ConnectionString;
        }

        public static string BuildMySqlConnectionString(Dictionary<string, object> connection)
        {
            uint port = 3306;
            uint parsedPort;
            string portText = GetValue(connection, "port");
            if (!string.IsNullOrWhiteSpace(portText) && uint.TryParse(portText.Trim(), out parsedPort) && parsedPort > 0)
            {
                port = parsedPort;
            }

            MySqlConnectionStringBuilder builder = new MySqlConnectionStringBuilder
            {
                Server = GetValue(connection, "host"),
                Port = port,
                UserID = GetValue(connection, "username"),
                Password = GetValue(connection, "pwd"),
                Database = string.IsNullOrWhiteSpace(GetValue(connection, "initial_database"))
                    ? string.Empty
                    : GetValue(connection, "initial_database").Trim(),
                // 伺服器支援時使用 TLS；不支援時維持既有連線相容性。
                SslMode = ParseMySqlSslMode(NormalizeTlsMode(connection, "Preferred")),
                CharacterSet = "utf8",
                AllowZeroDateTime = true,
                ConnectionTimeout = 8
            };
            builder.SslCa = GetValue(connection, "tls_ca_path");
            string clientCertificate = GetValue(connection, "tls_client_certificate_path");
            string certificateExtension = System.IO.Path.GetExtension(clientCertificate);
            if (string.Equals(certificateExtension, ".pfx", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(certificateExtension, ".p12", StringComparison.OrdinalIgnoreCase))
            {
                builder.CertificateFile = clientCertificate;
                builder.CertificatePassword = GetValue(connection, "tls_certificate_password");
            }
            else
            {
                builder.SslCert = clientCertificate;
                builder.SslKey = GetValue(connection, "tls_client_key_path");
            }
            return builder.ConnectionString;
        }

        public static string BuildPostgreSqlConnectionString(Dictionary<string, object> connection)
        {
            int port = 5432;
            int parsedPort;
            string portText = GetValue(connection, "port");
            if (!string.IsNullOrWhiteSpace(portText) && int.TryParse(portText.Trim(), out parsedPort) && parsedPort > 0)
            {
                port = parsedPort;
            }

            NpgsqlConnectionStringBuilder builder = new NpgsqlConnectionStringBuilder
            {
                Host = GetValue(connection, "host"),
                Port = port,
                Username = GetValue(connection, "username"),
                Password = GetValue(connection, "pwd"),
                Database = string.IsNullOrWhiteSpace(GetValue(connection, "initial_database"))
                    ? "postgres"
                    : GetValue(connection, "initial_database").Trim(),
                // 只限制建立連線時間，查詢保留 Npgsql 的預設 command timeout。
                Timeout = 8
            };
            builder.SslMode = ParseNpgsqlSslMode(NormalizeTlsMode(connection, "Prefer"));
            builder.RootCertificate = GetValue(connection, "tls_ca_path");
            builder.SslCertificate = GetValue(connection, "tls_client_certificate_path");
            builder.SslKey = GetValue(connection, "tls_client_key_path");
            builder.SslPassword = GetValue(connection, "tls_certificate_password");
            builder.CheckCertificateRevocation = ConnectionSecuritySettingsService.IsTrue(connection, "tls_check_revocation");
            return builder.ConnectionString;
        }

        public static string BuildSqliteConnectionString(Dictionary<string, object> connection)
        {
            return new SQLiteConnectionStringBuilder
            {
                DataSource = GetValue(connection, "path"),
                Version = 3
            }.ConnectionString;
        }

        public static string BuildMongoDbConnectionString(Dictionary<string, object> connection)
        {
            if (connection == null) throw new ArgumentNullException("connection");
            bool useSrv = IsTrue(connection, "mongo_srv");
            string host = GetValue(connection, "host").Trim();
            if (string.IsNullOrWhiteSpace(host)) throw new InvalidOperationException(Localization.T("Connection.EnterHost"));

            MongoUrlBuilder builder = new MongoUrlBuilder
            {
                Scheme = useSrv ? ConnectionStringScheme.MongoDBPlusSrv : ConnectionStringScheme.MongoDB,
                Server = useSrv
                    ? new MongoServerAddress(host)
                    : new MongoServerAddress(host, ParsePort(GetValue(connection, "port"), 27017)),
                DatabaseName = NullIfWhiteSpace(GetValue(connection, "initial_database")),
                Username = NullIfWhiteSpace(GetValue(connection, "username")),
                Password = GetValue(connection, "pwd"),
                AuthenticationSource = NullIfWhiteSpace(GetValue(connection, "mongo_auth_source")),
                ReplicaSetName = NullIfWhiteSpace(GetValue(connection, "mongo_replica_set")),
                DirectConnection = IsTrue(connection, "mongo_direct_connection"),
                RetryWrites = !connection.ContainsKey("mongo_retry_writes") || IsTrue(connection, "mongo_retry_writes"),
                UseTls = useSrv || IsTrue(connection, "mongo_tls"),
                ConnectTimeout = TimeSpan.FromSeconds(8),
                ServerSelectionTimeout = TimeSpan.FromSeconds(8),
                ApplicationName = "mySQLPunk"
            };
            return builder.ToString();
        }

        public static string BuildRedisConnectionString(Dictionary<string, object> connection)
        {
            if (connection == null) throw new ArgumentNullException("connection");
            string host = GetValue(connection, "host").Trim();
            if (string.IsNullOrWhiteSpace(host)) throw new InvalidOperationException(Localization.T("Connection.EnterHost"));
            int databaseIndex;
            string indexText = GetValue(connection, "initial_database").Trim();
            if (string.IsNullOrWhiteSpace(indexText)) databaseIndex = 0;
            else if (!int.TryParse(indexText, out databaseIndex) || databaseIndex < 0)
                throw new InvalidOperationException(Localization.T("Redis.InvalidDatabaseIndex"));
            return my_redis.BuildConnectionString(
                host,
                ParsePort(GetValue(connection, "port"), 6379),
                GetValue(connection, "username").Trim(),
                GetValue(connection, "pwd"),
                IsTrue(connection, "redis_tls"),
                databaseIndex);
        }

        public static string BuildOracleConnectionString(Dictionary<string, object> connection)
        {
            OracleConnectionStringBuilder builder = new OracleConnectionStringBuilder
            {
                UserID = GetValue(connection, "username"),
                Password = GetValue(connection, "pwd"),
                DataSource = string.Equals(GetValue(connection, "connection_type"), "TNS", StringComparison.OrdinalIgnoreCase)
                    ? GetValue(connection, "tns_name")
                    : BuildOracleBasicDataSource(connection)
            };
            return builder.ConnectionString;
        }

        public static string NormalizeProvider(string provider)
        {
            string value = (provider ?? string.Empty).Trim().ToLowerInvariant();
            if (value == "postgres" || value == "pgsql" || value == "npgsql") return "postgresql";
            if (value == "sqlserver" || value == "sql server") return "mssql";
            if (value == "mariadb") return "mysql";
            if (value == "mongo") return "mongodb";
            if (value == "garnet") return "redis";
            return value;
        }

        public static string GetValue(Dictionary<string, object> connection, string key)
        {
            if (connection != null && connection.ContainsKey(key) && connection[key] != null)
            {
                return connection[key].ToString();
            }
            return string.Empty;
        }

        private static string BuildSqlServerDataSource(string host, string port)
        {
            if (string.IsNullOrWhiteSpace(port)) return host;
            if ((host ?? string.Empty).Contains(",") || (host ?? string.Empty).Contains("\\")) return host;
            return host + "," + port;
        }

        private static int ParsePort(string value, int fallback)
        {
            int port;
            return int.TryParse((value ?? string.Empty).Trim(), out port) && port > 0 && port <= 65535 ? port : fallback;
        }

        private static bool IsTrue(Dictionary<string, object> connection, string key)
        {
            string value = GetValue(connection, key);
            return string.Equals(value, "T", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                   value == "1";
        }

        private static string NullIfWhiteSpace(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static string BuildOracleBasicDataSource(Dictionary<string, object> connection)
        {
            string host = string.IsNullOrWhiteSpace(GetValue(connection, "host")) ? "localhost" : GetValue(connection, "host");
            string port = string.IsNullOrWhiteSpace(GetValue(connection, "port")) ? "1521" : GetValue(connection, "port");
            string value = GetValue(connection, "service_name");
            if (string.IsNullOrWhiteSpace(value)) value = GetValue(connection, "sid");
            string key = string.Equals(GetValue(connection, "oracle_identifier_type"), "sid", StringComparison.OrdinalIgnoreCase)
                ? "SID"
                : "SERVICE_NAME";
            string tlsMode = NormalizeTlsMode(connection, "Disabled");
            string protocol = string.Equals(tlsMode, "Disabled", StringComparison.OrdinalIgnoreCase) ? "TCP" : "TCPS";
            string walletPath = GetValue(connection, "tls_wallet_path").Trim();
            string security = string.Empty;
            if (!string.IsNullOrWhiteSpace(walletPath) || string.Equals(tlsMode, "VerifyFull", StringComparison.OrdinalIgnoreCase))
            {
                if (walletPath.IndexOf('(') >= 0 || walletPath.IndexOf(')') >= 0)
                    throw new InvalidOperationException("Oracle Wallet 路徑不能包含括號。");
                security = "(SECURITY=" +
                           (string.IsNullOrWhiteSpace(walletPath) ? string.Empty : "(MY_WALLET_DIRECTORY=" + walletPath + ")") +
                           (string.Equals(tlsMode, "VerifyFull", StringComparison.OrdinalIgnoreCase) ? "(SSL_SERVER_DN_MATCH=YES)" : string.Empty) +
                           ")";
            }
            return "(DESCRIPTION=(ADDRESS=(PROTOCOL=" + protocol + ")(HOST=" + host + ")(PORT=" + port + "))" +
                   security + "(CONNECT_DATA=(" + key + "=" + value + ")))";
        }

        private static string NormalizeTlsMode(Dictionary<string, object> connection, string fallback)
        {
            string value = GetValue(connection, "tls_mode").Trim();
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        private static MySqlSslMode ParseMySqlSslMode(string value)
        {
            MySqlSslMode mode;
            return Enum.TryParse(value, true, out mode) ? mode : MySqlSslMode.Preferred;
        }

        private static SslMode ParseNpgsqlSslMode(string value)
        {
            SslMode mode;
            return Enum.TryParse(value, true, out mode) ? mode : SslMode.Prefer;
        }
    }
}
