using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;

namespace mySQLPunk.lib
{
    public enum MySqlPrivilegeTargetType
    {
        TableOrView,
        Function,
        Procedure
    }

    public sealed class MySqlUserProviderAdapter
    {
        private readonly HashSet<string> userColumns;

        public MySqlUserProviderAdapter(string version, IEnumerable<string> userColumns, bool hasGlobalPrivTable)
        {
            Version = version ?? string.Empty;
            this.userColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (userColumns != null)
            {
                foreach (string column in userColumns)
                {
                    if (!string.IsNullOrWhiteSpace(column)) this.userColumns.Add(column.Trim());
                }
            }
            HasGlobalPrivTable = hasGlobalPrivTable;
        }

        public string Version { get; private set; }
        public bool IsMariaDb { get { return Version.IndexOf("MariaDB", StringComparison.OrdinalIgnoreCase) >= 0; } }
        public bool HasGlobalPrivTable { get; private set; }
        public IEnumerable<string> UserColumns { get { return userColumns; } }

        public bool HasUserColumn(string columnName)
        {
            return !string.IsNullOrWhiteSpace(columnName) && userColumns.Contains(columnName.Trim());
        }

        public static MySqlUserProviderAdapter Detect(IDatabase db)
        {
            string version = MySqlUserManagerService.TryReadScalar(db, "SELECT VERSION();");
            List<string> columns = new List<string>();
            DataTable columnTable = MySqlUserManagerService.TrySelect(db, "SHOW COLUMNS FROM mysql.user;");
            if (columnTable != null)
            {
                foreach (DataRow row in columnTable.Rows)
                {
                    string field = MySqlUserManagerService.GetColumnValue(row, "Field");
                    if (field.Length == 0 && columnTable.Columns.Count > 0 && row[0] != DBNull.Value) field = row[0].ToString();
                    if (field.Length > 0) columns.Add(field);
                }
            }

            bool hasGlobalPrivTable = false;
            DataTable globalPriv = MySqlUserManagerService.TrySelect(db,
                "SELECT COUNT(*) AS Cnt FROM information_schema.tables WHERE table_schema = 'mysql' AND table_name = 'global_priv';");
            if (globalPriv != null && globalPriv.Rows.Count > 0)
            {
                long count;
                if (long.TryParse(Convert.ToString(globalPriv.Rows[0][0]), out count)) hasGlobalPrivTable = count > 0;
            }

            return new MySqlUserProviderAdapter(version, columns, hasGlobalPrivTable);
        }
    }

    public sealed class MySqlCreateUserOptions
    {
        public string User { get; set; }
        public string Host { get; set; }
        public string Password { get; set; }
        public string Plugin { get; set; }
        public bool RequireSsl { get; set; }
        public bool ExpirePassword { get; set; }
        public bool LockAccount { get; set; }
    }

    public sealed class MySqlAlterUserOptions
    {
        public string User { get; set; }
        public string Host { get; set; }
        public bool RenameUser { get; set; }
        public string NewUser { get; set; }
        public string NewHost { get; set; }
        public bool ChangePassword { get; set; }
        public string Password { get; set; }
        public string Plugin { get; set; }
        public bool? LockAccount { get; set; }
        public bool ExpirePassword { get; set; }
        public bool RequireSsl { get; set; }
        public bool ClearSslRequirement { get; set; }
        public int? MaxQuestionsPerHour { get; set; }
        public int? MaxUpdatesPerHour { get; set; }
        public int? MaxConnectionsPerHour { get; set; }
        public int? MaxUserConnections { get; set; }
    }

    public static class MySqlUserManagerService
    {
        public const string NotSupported = "N/A";

        private static readonly string[,] PrivilegeColumns = new string[,]
        {
            { "Select_priv", "SELECT" },
            { "Insert_priv", "INSERT" },
            { "Update_priv", "UPDATE" },
            { "Delete_priv", "DELETE" },
            { "Create_priv", "CREATE" },
            { "Drop_priv", "DROP" },
            { "Reload_priv", "RELOAD" },
            { "Shutdown_priv", "SHUTDOWN" },
            { "Process_priv", "PROCESS" },
            { "File_priv", "FILE" },
            { "Grant_priv", "GRANT OPTION" },
            { "References_priv", "REFERENCES" },
            { "Index_priv", "INDEX" },
            { "Alter_priv", "ALTER" },
            { "Show_db_priv", "SHOW DATABASES" },
            { "Super_priv", "SUPER" },
            { "Create_tmp_table_priv", "CREATE TEMPORARY TABLES" },
            { "Lock_tables_priv", "LOCK TABLES" },
            { "Execute_priv", "EXECUTE" },
            { "Repl_slave_priv", "REPLICATION SLAVE" },
            { "Repl_client_priv", "REPLICATION CLIENT" },
            { "Create_view_priv", "CREATE VIEW" },
            { "Show_view_priv", "SHOW VIEW" },
            { "Create_routine_priv", "CREATE ROUTINE" },
            { "Alter_routine_priv", "ALTER ROUTINE" },
            { "Create_user_priv", "CREATE USER" },
            { "Event_priv", "EVENT" },
            { "Trigger_priv", "TRIGGER" },
            { "Create_tablespace_priv", "CREATE TABLESPACE" }
        };

        public static DataTable LoadUsers(IDatabase db)
        {
            DataTable users = CreateUserTable();
            if (db == null) return users;

            MySqlUserProviderAdapter adapter = MySqlUserProviderAdapter.Detect(db);
            foreach (string sql in BuildUserListSqlCandidates(adapter))
            {
                DataTable source = TrySelect(db, sql);
                AppendUserRows(users, source);
                if (users.Rows.Count > 0) break;
            }

            if (users.Rows.Count == 0)
            {
                AppendUserRows(users, TrySelect(db, BuildCurrentUserFallbackSql()));
            }

            return users;
        }

        public static DataTable CreateUserTable()
        {
            DataTable dt = new DataTable();
            dt.Columns.Add("Name");
            dt.Columns.Add("Type");
            dt.Columns.Add("Host");
            dt.Columns.Add("Status");
            dt.Columns.Add("Source");
            dt.Columns.Add("ProviderFamily");
            dt.Columns.Add("Plugin");
            dt.Columns.Add("PasswordExists");
            dt.Columns.Add("AccountLocked");
            dt.Columns.Add("PasswordExpired");
            dt.Columns.Add("SSLRequired");
            dt.Columns.Add("MaxQuestionsPerHour");
            dt.Columns.Add("MaxUpdatesPerHour");
            dt.Columns.Add("MaxConnectionsPerHour");
            dt.Columns.Add("MaxConnections");
            dt.Columns.Add("CreateTime");
            dt.Columns.Add("Comment");
            dt.Columns.Add("AuthenticationString");
            dt.Columns.Add("PasswordLifetime");
            dt.Columns.Add("PasswordLastChanged");
            dt.Columns.Add("MustChangePassword");
            dt.Columns.Add("Privileges");
            return dt;
        }

        public static IEnumerable<string> BuildUserListSqlCandidates(MySqlUserProviderAdapter adapter)
        {
            if (adapter == null) adapter = new MySqlUserProviderAdapter(string.Empty, null, false);
            if (adapter.IsMariaDb && adapter.HasGlobalPrivTable)
            {
                yield return BuildMariaDbGlobalPrivUserListSql();
                yield return BuildMysqlUserListSql(adapter);
            }
            else
            {
                yield return BuildMysqlUserListSql(adapter);
            }
        }

        public static string BuildMysqlUserListSql(MySqlUserProviderAdapter adapter)
        {
            if (adapter == null) adapter = new MySqlUserProviderAdapter(string.Empty, null, false);
            string providerFamily = adapter.IsMariaDb ? "MariaDB" : "MySQL";
            string plugin = ColumnOrNotSupported(adapter, "plugin");
            string passwordExists = PasswordExistsExpression(adapter);
            string accountLocked = AccountLockedExpression(adapter);
            string passwordExpired = PasswordExpiredExpression(adapter);
            string sslRequired = SslRequiredExpression(adapter);
            string maxQuestions = ColumnAsCharOrNotSupported(adapter, "max_questions");
            string maxUpdates = ColumnAsCharOrNotSupported(adapter, "max_updates");
            string maxConnectionsPerHour = ColumnAsCharOrNotSupported(adapter, "max_connections");
            string maxConnections = ColumnAsCharOrNotSupported(adapter, "max_user_connections");
            string createTime = ColumnAsCharOrNotSupported(adapter, "Create_time");
            string comment = ColumnOrNotSupported(adapter, "User_attributes");
            string authString = AuthenticationStringExpression(adapter);
            string passwordLifetime = ColumnAsCharOrNotSupported(adapter, "password_lifetime");
            string passwordLastChanged = ColumnAsCharOrNotSupported(adapter, "password_last_changed");
            string mustChange = passwordExpired;
            string privileges = PrivilegeSummaryExpression(adapter);
            string status = adapter.HasUserColumn("account_locked")
                ? "CASE WHEN `account_locked` = 'Y' THEN 'Locked' ELSE 'Open' END"
                : "'Active'";

            return "SELECT `User` AS Name, 'User' AS Type, `Host` AS Host, " +
                   status + " AS Status, 'mysql.user' AS Source, '" + providerFamily + "' AS ProviderFamily, " +
                   plugin + " AS Plugin, " +
                   passwordExists + " AS PasswordExists, " +
                   accountLocked + " AS AccountLocked, " +
                   passwordExpired + " AS PasswordExpired, " +
                   sslRequired + " AS SSLRequired, " +
                   maxQuestions + " AS MaxQuestionsPerHour, " +
                   maxUpdates + " AS MaxUpdatesPerHour, " +
                   maxConnectionsPerHour + " AS MaxConnectionsPerHour, " +
                   maxConnections + " AS MaxConnections, " +
                   createTime + " AS CreateTime, " +
                   comment + " AS Comment, " +
                   authString + " AS AuthenticationString, " +
                   passwordLifetime + " AS PasswordLifetime, " +
                   passwordLastChanged + " AS PasswordLastChanged, " +
                   mustChange + " AS MustChangePassword, " +
                   privileges + " AS Privileges " +
                   "FROM mysql.user ORDER BY `User`, `Host`;";
        }

        public static string BuildMariaDbGlobalPrivUserListSql()
        {
            return "SELECT `User` AS Name, 'User' AS Type, `Host` AS Host, " +
                   "'Active' AS Status, 'mysql.global_priv' AS Source, 'MariaDB' AS ProviderFamily, " +
                   "'N/A' AS Plugin, " +
                   "CASE WHEN `Priv` IS NULL OR `Priv` = '' THEN 'N/A' ELSE 'Yes' END AS PasswordExists, " +
                   "'N/A' AS AccountLocked, 'N/A' AS PasswordExpired, 'N/A' AS SSLRequired, " +
                   "'N/A' AS MaxQuestionsPerHour, 'N/A' AS MaxUpdatesPerHour, 'N/A' AS MaxConnectionsPerHour, " +
                   "'N/A' AS MaxConnections, 'N/A' AS CreateTime, 'N/A' AS Comment, " +
                   "CASE WHEN `Priv` IS NULL OR `Priv` = '' THEN 'N/A' ELSE 'Set (hidden)' END AS AuthenticationString, " +
                   "'N/A' AS PasswordLifetime, 'N/A' AS PasswordLastChanged, 'N/A' AS MustChangePassword, " +
                   "CASE WHEN `Priv` IS NULL OR `Priv` = '' THEN 'N/A' ELSE 'Stored in mysql.global_priv' END AS Privileges " +
                   "FROM mysql.global_priv ORDER BY `User`, `Host`;";
        }

        public static string BuildCurrentUserFallbackSql()
        {
            return "SELECT SUBSTRING_INDEX(CURRENT_USER(), '@', 1) AS Name, 'Current User' AS Type, " +
                   "SUBSTRING_INDEX(CURRENT_USER(), '@', -1) AS Host, 'Active' AS Status, " +
                   "'CURRENT_USER()' AS Source, 'MySQL' AS ProviderFamily, " +
                   "'N/A' AS Plugin, 'N/A' AS PasswordExists, 'N/A' AS AccountLocked, 'N/A' AS PasswordExpired, " +
                   "'N/A' AS SSLRequired, 'N/A' AS MaxQuestionsPerHour, 'N/A' AS MaxUpdatesPerHour, " +
                   "'N/A' AS MaxConnectionsPerHour, 'N/A' AS MaxConnections, 'N/A' AS CreateTime, 'N/A' AS Comment, " +
                   "'N/A' AS AuthenticationString, 'N/A' AS PasswordLifetime, 'N/A' AS PasswordLastChanged, 'N/A' AS MustChangePassword, 'N/A' AS Privileges;";
        }

        public static string BuildUserDdlPreview(DataRow userRow)
        {
            if (userRow == null) return string.Empty;
            string user = GetColumnValue(userRow, "Name");
            string host = GetColumnValue(userRow, "Host");
            string account = QuoteAccount(user, host);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("CREATE USER " + account + ";");

            string plugin = GetColumnValue(userRow, "Plugin");
            if (IsMeaningful(plugin)) sb.AppendLine("-- Plugin: " + plugin);

            string passwordExists = GetColumnValue(userRow, "PasswordExists");
            if (IsMeaningful(passwordExists)) sb.AppendLine("-- Password: " + passwordExists + (passwordExists == "Yes" ? " (hash hidden)" : string.Empty));

            string ssl = GetColumnValue(userRow, "SSLRequired");
            if (IsMeaningful(ssl))
            {
                sb.AppendLine("-- SSL: " + ssl);
                if (!string.Equals(ssl, "No", StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine("ALTER USER " + account + " REQUIRE " + NormalizeSslRequirement(ssl) + ";");
                }
            }

            string locked = GetColumnValue(userRow, "AccountLocked");
            if (string.Equals(locked, "Locked", StringComparison.OrdinalIgnoreCase)) sb.AppendLine("ALTER USER " + account + " ACCOUNT LOCK;");
            else if (string.Equals(locked, "Open", StringComparison.OrdinalIgnoreCase)) sb.AppendLine("-- Account: Open");

            string expired = GetColumnValue(userRow, "PasswordExpired");
            if (string.Equals(expired, "Expired", StringComparison.OrdinalIgnoreCase)) sb.AppendLine("ALTER USER " + account + " PASSWORD EXPIRE;");

            string passwordLastChanged = GetColumnValue(userRow, "PasswordLastChanged");
            if (IsMeaningful(passwordLastChanged)) sb.AppendLine("-- Password last changed: " + passwordLastChanged);

            AppendResourceLimitDdl(sb, account, userRow);

            string privileges = GetColumnValue(userRow, "Privileges");
            if (IsMeaningful(privileges))
            {
                sb.AppendLine("-- Privileges: " + privileges);
                string grantSql = BuildGrantSqlFromSummary(privileges, user, host);
                if (grantSql.Length > 0) sb.AppendLine(grantSql);
            }

            string source = GetColumnValue(userRow, "Source");
            if (IsMeaningful(source)) sb.AppendLine("-- Source: " + source);
            return sb.ToString().TrimEnd();
        }

        public static string BuildCreateUserSql(MySqlCreateUserOptions options)
        {
            if (options == null) throw new ArgumentNullException("options");
            string account = QuoteAccount(options.User, options.Host);
            StringBuilder sql = new StringBuilder();
            sql.Append("CREATE USER ").Append(account);
            if (!string.IsNullOrEmpty(options.Plugin))
            {
                sql.Append(" IDENTIFIED WITH ").Append(QuoteIdentifier(options.Plugin));
                if (!string.IsNullOrEmpty(options.Password)) sql.Append(" BY ").Append(QuoteLiteral(options.Password));
            }
            else if (!string.IsNullOrEmpty(options.Password))
            {
                sql.Append(" IDENTIFIED BY ").Append(QuoteLiteral(options.Password));
            }
            if (options.RequireSsl) sql.Append(" REQUIRE SSL");
            if (options.ExpirePassword) sql.Append(" PASSWORD EXPIRE");
            if (options.LockAccount) sql.Append(" ACCOUNT LOCK");
            sql.Append(";");
            return sql.ToString();
        }

        public static List<string> BuildCreateUserSqlStatements(MySqlCreateUserOptions options)
        {
            return new List<string> { BuildCreateUserSql(options) };
        }

        public static List<string> BuildAlterUserSqlStatements(MySqlAlterUserOptions options)
        {
            if (options == null) throw new ArgumentNullException("options");
            List<string> statements = new List<string>();
            string targetUser = options.User;
            string targetHost = options.Host;

            if (options.RenameUser)
            {
                string newUser = string.IsNullOrWhiteSpace(options.NewUser) ? options.User : options.NewUser;
                string newHost = string.IsNullOrWhiteSpace(options.NewHost) ? options.Host : options.NewHost;
                statements.Add(BuildRenameUserSql(options.User, options.Host, newUser, newHost));
                targetUser = newUser;
                targetHost = newHost;
            }

            if (options.ChangePassword || !string.IsNullOrEmpty(options.Plugin))
            {
                statements.Add(BuildAlterAuthenticationSql(targetUser, targetHost, options.Plugin, options.Password, options.ChangePassword));
            }

            if (options.LockAccount.HasValue) statements.Add(BuildAccountLockSql(targetUser, targetHost, options.LockAccount.Value));
            if (options.ExpirePassword) statements.Add(BuildExpirePasswordSql(targetUser, targetHost));
            if (options.RequireSsl) statements.Add(BuildSslRequirementSql(targetUser, targetHost, "SSL"));
            if (options.ClearSslRequirement) statements.Add(BuildSslRequirementSql(targetUser, targetHost, "NONE"));

            string limitSql = BuildResourceLimitSql(targetUser, targetHost, options.MaxQuestionsPerHour, options.MaxUpdatesPerHour, options.MaxConnectionsPerHour, options.MaxUserConnections);
            if (limitSql.Length > 0) statements.Add(limitSql);

            if (statements.Count == 0) throw new InvalidOperationException("No ALTER USER changes were requested.");
            return statements;
        }

        public static List<string> BuildDropUserSqlStatements(string user, string host)
        {
            return new List<string> { BuildDropUserSql(user, host) };
        }

        public static string BuildUserOperationPreview(IEnumerable<string> statements)
        {
            if (statements == null) return string.Empty;
            return string.Join(Environment.NewLine, statements.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray());
        }

        public static int ExecuteUserSqlStatements(IDatabase db, IEnumerable<string> statements)
        {
            if (db == null) throw new ArgumentNullException("db");
            if (statements == null) throw new ArgumentNullException("statements");
            int executed = 0;
            foreach (string statement in statements)
            {
                if (string.IsNullOrWhiteSpace(statement)) continue;
                db.ExecSQL(statement);
                executed++;
            }
            return executed;
        }

        public static string BuildDropUserSql(string user, string host)
        {
            return "DROP USER " + QuoteAccount(user, host) + ";";
        }

        public static string BuildRenameUserSql(string user, string host, string newUser, string newHost)
        {
            return "RENAME USER " + QuoteAccount(user, host) + " TO " + QuoteAccount(newUser, newHost) + ";";
        }

        public static string BuildChangePasswordSql(string user, string host, string password)
        {
            return "ALTER USER " + QuoteAccount(user, host) + " IDENTIFIED BY " + QuoteLiteral(password ?? string.Empty) + ";";
        }

        public static string BuildAccountLockSql(string user, string host, bool locked)
        {
            return "ALTER USER " + QuoteAccount(user, host) + (locked ? " ACCOUNT LOCK;" : " ACCOUNT UNLOCK;");
        }

        public static string BuildExpirePasswordSql(string user, string host)
        {
            return "ALTER USER " + QuoteAccount(user, host) + " PASSWORD EXPIRE;";
        }

        public static string BuildSslRequirementSql(string user, string host, string sslRequirement)
        {
            string value = (sslRequirement ?? string.Empty).Trim().ToUpperInvariant();
            if (value == "NONE" || value == "NO" || value == "DISABLED") return "ALTER USER " + QuoteAccount(user, host) + " REQUIRE NONE;";
            return "ALTER USER " + QuoteAccount(user, host) + " REQUIRE " + NormalizeSslRequirement(sslRequirement) + ";";
        }

        public static string BuildResourceLimitSql(string user, string host, int? maxQuestionsPerHour, int? maxUpdatesPerHour, int? maxConnectionsPerHour, int? maxUserConnections)
        {
            List<string> limits = new List<string>();
            AddResourceLimit(limits, "MAX_QUERIES_PER_HOUR", maxQuestionsPerHour);
            AddResourceLimit(limits, "MAX_UPDATES_PER_HOUR", maxUpdatesPerHour);
            AddResourceLimit(limits, "MAX_CONNECTIONS_PER_HOUR", maxConnectionsPerHour);
            AddResourceLimit(limits, "MAX_USER_CONNECTIONS", maxUserConnections);
            if (limits.Count == 0) return string.Empty;
            return "ALTER USER " + QuoteAccount(user, host) + " WITH " + string.Join(" ", limits.ToArray()) + ";";
        }

        public static string BuildGrantSql(string privilege, string databaseName, string objectName, string user, string host)
        {
            return BuildGrantSql(new[] { privilege }, databaseName, objectName, user, host, false);
        }

        public static string BuildGrantSql(IEnumerable<string> privileges, string databaseName, string objectName, string user, string host, bool withGrantOption)
        {
            return BuildGrantSql(privileges, databaseName, objectName, user, host, withGrantOption, MySqlPrivilegeTargetType.TableOrView);
        }

        public static string BuildGrantSql(IEnumerable<string> privileges, string databaseName, string objectName, string user, string host, bool withGrantOption, MySqlPrivilegeTargetType targetType)
        {
            List<string> normalized = NormalizePrivileges(privileges);
            return "GRANT " + string.Join(", ", normalized.ToArray()) + " ON " + BuildPrivilegeTarget(databaseName, objectName, targetType) + " TO " + QuoteAccount(user, host) + (withGrantOption ? " WITH GRANT OPTION" : string.Empty) + ";";
        }

        public static string BuildRevokeSql(string privilege, string databaseName, string objectName, string user, string host)
        {
            return BuildRevokeSql(new[] { privilege }, databaseName, objectName, user, host);
        }

        public static string BuildRevokeSql(IEnumerable<string> privileges, string databaseName, string objectName, string user, string host)
        {
            return BuildRevokeSql(privileges, databaseName, objectName, user, host, MySqlPrivilegeTargetType.TableOrView);
        }

        public static string BuildRevokeSql(IEnumerable<string> privileges, string databaseName, string objectName, string user, string host, MySqlPrivilegeTargetType targetType)
        {
            List<string> normalized = NormalizePrivileges(privileges);
            return "REVOKE " + string.Join(", ", normalized.ToArray()) + " ON " + BuildPrivilegeTarget(databaseName, objectName, targetType) + " FROM " + QuoteAccount(user, host) + ";";
        }

        public static string BuildPrivilegeTargetPreview(string databaseName, string objectName, MySqlPrivilegeTargetType targetType)
        {
            return BuildPrivilegeTarget(databaseName, objectName, targetType);
        }

        internal static DataTable TrySelect(IDatabase db, string sql)
        {
            try
            {
                return db == null || string.IsNullOrWhiteSpace(sql) ? null : db.SelectSQL(sql);
            }
            catch
            {
                return null;
            }
        }

        internal static string TryReadScalar(IDatabase db, string sql)
        {
            DataTable table = TrySelect(db, sql);
            if (table == null || table.Rows.Count == 0 || table.Columns.Count == 0 || table.Rows[0][0] == DBNull.Value) return string.Empty;
            return Convert.ToString(table.Rows[0][0]);
        }

        internal static string GetColumnValue(DataRow row, string name)
        {
            if (row == null || row.Table == null || string.IsNullOrWhiteSpace(name)) return string.Empty;
            if (row.Table.Columns.Contains(name) && row[name] != DBNull.Value) return Convert.ToString(row[name]);
            foreach (DataColumn column in row.Table.Columns)
            {
                if (string.Equals(column.ColumnName, name, StringComparison.OrdinalIgnoreCase) && row[column] != DBNull.Value)
                    return Convert.ToString(row[column]);
            }
            return string.Empty;
        }

        private static void AppendUserRows(DataTable target, DataTable source)
        {
            if (target == null || source == null) return;
            foreach (DataRow sourceRow in source.Rows)
            {
                DataRow row = target.NewRow();
                foreach (DataColumn column in target.Columns)
                {
                    string value = GetColumnValue(sourceRow, column.ColumnName);
                    row[column.ColumnName] = string.IsNullOrEmpty(value) && !IsIdentityColumn(column.ColumnName) ? NotSupported : value;
                }
                if (GetColumnValue(row, "Name").Length > 0) target.Rows.Add(row);
            }
        }

        private static bool IsIdentityColumn(string columnName)
        {
            return string.Equals(columnName, "Name", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(columnName, "Type", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(columnName, "Host", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(columnName, "Status", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(columnName, "Source", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(columnName, "ProviderFamily", StringComparison.OrdinalIgnoreCase);
        }

        private static string ColumnOrNotSupported(MySqlUserProviderAdapter adapter, string columnName)
        {
            return adapter.HasUserColumn(columnName) ? "COALESCE(CAST(`" + EscapeIdentifierPart(columnName) + "` AS CHAR), 'N/A')" : "'N/A'";
        }

        private static string ColumnAsCharOrNotSupported(MySqlUserProviderAdapter adapter, string columnName)
        {
            return ColumnOrNotSupported(adapter, columnName);
        }

        private static string PasswordExistsExpression(MySqlUserProviderAdapter adapter)
        {
            if (adapter.HasUserColumn("authentication_string"))
                return "CASE WHEN `authentication_string` IS NULL OR `authentication_string` = '' THEN 'No' ELSE 'Yes' END";
            if (adapter.HasUserColumn("Password"))
                return "CASE WHEN `Password` IS NULL OR `Password` = '' THEN 'No' ELSE 'Yes' END";
            return "'N/A'";
        }

        private static string AuthenticationStringExpression(MySqlUserProviderAdapter adapter)
        {
            if (adapter.HasUserColumn("authentication_string"))
                return "CASE WHEN `authentication_string` IS NULL OR `authentication_string` = '' THEN 'N/A' ELSE 'Set (hidden)' END";
            if (adapter.HasUserColumn("Password"))
                return "CASE WHEN `Password` IS NULL OR `Password` = '' THEN 'N/A' ELSE 'Set (hidden)' END";
            return "'N/A'";
        }

        private static string AccountLockedExpression(MySqlUserProviderAdapter adapter)
        {
            return adapter.HasUserColumn("account_locked")
                ? "CASE WHEN `account_locked` = 'Y' THEN 'Locked' ELSE 'Open' END"
                : "'N/A'";
        }

        private static string PasswordExpiredExpression(MySqlUserProviderAdapter adapter)
        {
            return adapter.HasUserColumn("password_expired")
                ? "CASE WHEN `password_expired` = 'Y' THEN 'Expired' ELSE 'Active' END"
                : "'N/A'";
        }

        private static string SslRequiredExpression(MySqlUserProviderAdapter adapter)
        {
            return adapter.HasUserColumn("ssl_type")
                ? "CASE WHEN `ssl_type` IS NULL OR `ssl_type` = '' THEN 'No' ELSE `ssl_type` END"
                : "'N/A'";
        }

        private static string PrivilegeSummaryExpression(MySqlUserProviderAdapter adapter)
        {
            List<string> pieces = new List<string>();
            for (int i = 0; i < PrivilegeColumns.GetLength(0); i++)
            {
                string column = PrivilegeColumns[i, 0];
                string label = PrivilegeColumns[i, 1];
                if (adapter.HasUserColumn(column)) pieces.Add("CASE WHEN `" + EscapeIdentifierPart(column) + "` = 'Y' THEN '" + label + "' END");
            }
            if (pieces.Count == 0) return "'N/A'";
            return "COALESCE(NULLIF(CONCAT_WS(',', " + string.Join(", ", pieces.ToArray()) + "), ''), 'N/A')";
        }

        private static string QuoteAccount(string user, string host)
        {
            string normalizedHost = string.IsNullOrWhiteSpace(host) || string.Equals(host, NotSupported, StringComparison.OrdinalIgnoreCase) ? "%" : host;
            return QuoteLiteral(user ?? string.Empty) + "@" + QuoteLiteral(normalizedHost);
        }

        private static string QuoteLiteral(string value)
        {
            return "'" + (value ?? string.Empty).Replace("\\", "\\\\").Replace("'", "''") + "'";
        }

        private static string QuoteIdentifier(string value)
        {
            return "`" + EscapeIdentifierPart(value) + "`";
        }

        private static string EscapeIdentifierPart(string value)
        {
            return (value ?? string.Empty).Replace("`", "``");
        }

        private static bool IsMeaningful(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && !string.Equals(value, NotSupported, StringComparison.OrdinalIgnoreCase);
        }

        private static void AppendResourceLimitDdl(StringBuilder sb, string account, DataRow userRow)
        {
            List<string> limits = new List<string>();
            AddResourceLimit(limits, "MAX_QUERIES_PER_HOUR", GetColumnValue(userRow, "MaxQuestionsPerHour"));
            AddResourceLimit(limits, "MAX_UPDATES_PER_HOUR", GetColumnValue(userRow, "MaxUpdatesPerHour"));
            AddResourceLimit(limits, "MAX_CONNECTIONS_PER_HOUR", GetColumnValue(userRow, "MaxConnectionsPerHour"));
            AddResourceLimit(limits, "MAX_USER_CONNECTIONS", GetColumnValue(userRow, "MaxConnections"));
            if (limits.Count > 0) sb.AppendLine("ALTER USER " + account + " WITH " + string.Join(" ", limits.ToArray()) + ";");
        }

        private static void AddResourceLimit(List<string> limits, string keyword, string value)
        {
            if (!IsMeaningful(value)) return;
            int parsed;
            if (!int.TryParse(value.Trim(), out parsed) || parsed < 0) return;
            AddResourceLimit(limits, keyword, parsed);
        }

        private static void AddResourceLimit(List<string> limits, string keyword, int? value)
        {
            if (!value.HasValue || value.Value < 0) return;
            limits.Add(keyword + " " + value.Value.ToString());
        }

        private static string BuildAlterAuthenticationSql(string user, string host, string plugin, string password, bool changePassword)
        {
            StringBuilder sql = new StringBuilder();
            sql.Append("ALTER USER ").Append(QuoteAccount(user, host));
            if (!string.IsNullOrEmpty(plugin))
            {
                sql.Append(" IDENTIFIED WITH ").Append(QuoteIdentifier(plugin));
                if (changePassword || password != null) sql.Append(" BY ").Append(QuoteLiteral(password ?? string.Empty));
            }
            else
            {
                sql.Append(" IDENTIFIED BY ").Append(QuoteLiteral(password ?? string.Empty));
            }
            sql.Append(";");
            return sql.ToString();
        }

        private static string BuildGrantSqlFromSummary(string privilegeSummary, string user, string host)
        {
            List<string> privileges = new List<string>();
            bool withGrantOption = false;
            foreach (string part in (privilegeSummary ?? string.Empty).Split(','))
            {
                string trimmed = part.Trim();
                if (!IsMeaningful(trimmed)) continue;
                if (string.Equals(trimmed, "Stored in mysql.global_priv", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(trimmed, "GRANT OPTION", StringComparison.OrdinalIgnoreCase))
                {
                    withGrantOption = true;
                    continue;
                }
                privileges.Add(trimmed);
            }
            if (privileges.Count == 0) return string.Empty;
            return BuildGrantSql(privileges, null, null, user, host, withGrantOption);
        }

        private static string NormalizeSslRequirement(string ssl)
        {
            string value = (ssl ?? string.Empty).Trim().ToUpperInvariant();
            if (value == "SSL" || value == "X509") return value;
            if (value == "ANY") return "SSL";
            if (value == "SPECIFIED") return "X509";
            return "SSL";
        }

        private static List<string> NormalizePrivileges(IEnumerable<string> privileges)
        {
            List<string> normalized = new List<string>();
            if (privileges != null)
            {
                foreach (string privilege in privileges)
                {
                    string value = NormalizePrivilege(privilege);
                    if (!normalized.Contains(value)) normalized.Add(value);
                }
            }
            if (normalized.Count == 0) throw new ArgumentException("At least one privilege is required.", "privileges");
            return normalized;
        }

        private static string NormalizePrivilege(string privilege)
        {
            string value = (privilege ?? string.Empty).Trim().ToUpperInvariant();
            if (value.Length == 0) throw new ArgumentException("Privilege is required.", "privilege");
            foreach (char c in value)
            {
                if (!(char.IsLetter(c) || c == '_' || c == ' ')) throw new ArgumentException("Privilege contains unsupported characters.", "privilege");
            }
            return value.Replace('_', ' ');
        }

        private static string BuildPrivilegeTarget(string databaseName, string objectName)
        {
            return BuildPrivilegeTarget(databaseName, objectName, MySqlPrivilegeTargetType.TableOrView);
        }

        private static string BuildPrivilegeTarget(string databaseName, string objectName, MySqlPrivilegeTargetType targetType)
        {
            if (string.IsNullOrWhiteSpace(databaseName)) return "*.*";
            string db = QuoteIdentifier(databaseName);
            if (string.IsNullOrWhiteSpace(objectName)) return db + ".*";
            if (targetType == MySqlPrivilegeTargetType.Function) return "FUNCTION " + db + "." + QuoteIdentifier(objectName);
            if (targetType == MySqlPrivilegeTargetType.Procedure) return "PROCEDURE " + db + "." + QuoteIdentifier(objectName);
            return db + "." + QuoteIdentifier(objectName);
        }
    }
}
