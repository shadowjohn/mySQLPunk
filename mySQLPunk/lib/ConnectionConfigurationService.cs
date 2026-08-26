using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Data.SqlClient;
using MySqlConnector;
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
            builder.TrustServerCertificate = true;
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
                SslMode = MySqlSslMode.Preferred,
                CharacterSet = "utf8",
                AllowZeroDateTime = true,
                ConnectionTimeout = 8
            };
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

        public static string BuildOracleConnectionString(Dictionary<string, object> connection)
        {
            string existing = GetValue(connection, "connString");
            if (!string.IsNullOrWhiteSpace(existing)) return existing;

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

        private static string BuildOracleBasicDataSource(Dictionary<string, object> connection)
        {
            string host = string.IsNullOrWhiteSpace(GetValue(connection, "host")) ? "localhost" : GetValue(connection, "host");
            string port = string.IsNullOrWhiteSpace(GetValue(connection, "port")) ? "1521" : GetValue(connection, "port");
            string value = GetValue(connection, "service_name");
            if (string.IsNullOrWhiteSpace(value)) value = GetValue(connection, "sid");
            string key = string.Equals(GetValue(connection, "oracle_identifier_type"), "sid", StringComparison.OrdinalIgnoreCase)
                ? "SID"
                : "SERVICE_NAME";
            return "(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=" + host + ")(PORT=" + port + "))" +
                   "(CONNECT_DATA=(" + key + "=" + value + ")))";
        }
    }
}
