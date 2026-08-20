using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace mySQLPunk.lib
{
    public enum MySqlPrivilegeTargetType
    {
        TableOrView,
        Function,
        Procedure
    }

    public enum MySqlUserProviderFamily
    {
        Unknown,
        MySql5,
        MySql8,
        MariaDb
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
        public Version ParsedVersion { get { return ParseServerVersion(Version); } }
        public MySqlUserProviderFamily Family
        {
            get
            {
                if (IsMariaDb) return MySqlUserProviderFamily.MariaDb;
                Version parsed = ParsedVersion;
                if (parsed.Major >= 8) return MySqlUserProviderFamily.MySql8;
                if (parsed.Major == 5) return MySqlUserProviderFamily.MySql5;
                return MySqlUserProviderFamily.Unknown;
            }
        }
        public bool SupportsAlterUser
        {
            get
            {
                Version parsed = ParsedVersion;
                if (IsMariaDb) return parsed.Major > 10 || (parsed.Major == 10 && parsed.Minor >= 2);
                return parsed.Major > 5 || (parsed.Major == 5 && (parsed.Minor > 7 || (parsed.Minor == 7 && parsed.Build >= 6)));
            }
        }
        public bool SupportsAccountLock
        {
            get
            {
                Version parsed = ParsedVersion;
                if (IsMariaDb) return parsed.Major > 10 || (parsed.Major == 10 && parsed.Minor >= 4);
                return parsed.Major > 5 || (parsed.Major == 5 && (parsed.Minor > 7 || (parsed.Minor == 7 && parsed.Build >= 6)));
            }
        }
        public bool SupportsPasswordExpiration
        {
            get
            {
                Version parsed = ParsedVersion;
                if (IsMariaDb) return parsed.Major > 10 || (parsed.Major == 10 && parsed.Minor >= 4);
                return parsed.Major > 5 || (parsed.Major == 5 && parsed.Minor >= 7);
            }
        }
        public bool SupportsShowCreateUser
        {
            get
            {
                Version parsed = ParsedVersion;
                if (IsMariaDb) return parsed.Major > 10 || (parsed.Major == 10 && parsed.Minor >= 2);
                return parsed.Major > 5 || (parsed.Major == 5 && parsed.Minor >= 7);
            }
        }

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

        private static Version ParseServerVersion(string value)
        {
            Match match = Regex.Match(value ?? string.Empty, @"(?<major>\d+)\.(?<minor>\d+)(?:\.(?<build>\d+))?");
            if (!match.Success) return new Version(0, 0, 0);

            int major;
            int minor;
            int build;
            int.TryParse(match.Groups["major"].Value, out major);
            int.TryParse(match.Groups["minor"].Value, out minor);
            int.TryParse(match.Groups["build"].Value, out build);
            return new Version(major, minor, build);
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

            EnrichUsersFromShowStatements(db, users, adapter);

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
            dt.Columns.Add("ProviderVersion");
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
            dt.Columns.Add("CreateStatement");
            dt.Columns.Add("GrantStatements");
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
                   "CASE WHEN COALESCE(JSON_VALUE(`Priv`, '$.account_locked'), 0) IN (1, '1', 'true') THEN 'Locked' ELSE 'Open' END AS Status, " +
                   "'mysql.global_priv' AS Source, 'MariaDB' AS ProviderFamily, " +
                   "COALESCE(NULLIF(JSON_UNQUOTE(JSON_EXTRACT(`Priv`, '$.plugin')), ''), 'N/A') AS Plugin, " +
                   "CASE WHEN COALESCE(JSON_UNQUOTE(JSON_EXTRACT(`Priv`, '$.authentication_string')), '') = '' THEN 'No' ELSE 'Yes' END AS PasswordExists, " +
                   "CASE WHEN COALESCE(JSON_VALUE(`Priv`, '$.account_locked'), 0) IN (1, '1', 'true') THEN 'Locked' ELSE 'Open' END AS AccountLocked, " +
                   "CASE WHEN COALESCE(JSON_VALUE(`Priv`, '$.password_expired'), 0) IN (1, '1', 'true') THEN 'Expired' ELSE 'Active' END AS PasswordExpired, " +
                   "COALESCE(NULLIF(JSON_UNQUOTE(JSON_EXTRACT(`Priv`, '$.ssl_type')), ''), 'No') AS SSLRequired, " +
                   "COALESCE(CAST(JSON_VALUE(`Priv`, '$.max_questions') AS CHAR), '0') AS MaxQuestionsPerHour, " +
                   "COALESCE(CAST(JSON_VALUE(`Priv`, '$.max_updates') AS CHAR), '0') AS MaxUpdatesPerHour, " +
                   "COALESCE(CAST(JSON_VALUE(`Priv`, '$.max_connections') AS CHAR), '0') AS MaxConnectionsPerHour, " +
                   "COALESCE(CAST(JSON_VALUE(`Priv`, '$.max_user_connections') AS CHAR), '0') AS MaxConnections, " +
                   "'N/A' AS CreateTime, COALESCE(NULLIF(JSON_UNQUOTE(JSON_EXTRACT(`Priv`, '$.comment')), ''), 'N/A') AS Comment, " +
                   "CASE WHEN COALESCE(JSON_UNQUOTE(JSON_EXTRACT(`Priv`, '$.authentication_string')), '') = '' THEN 'N/A' ELSE 'Set (hidden)' END AS AuthenticationString, " +
                   "COALESCE(CAST(JSON_VALUE(`Priv`, '$.password_lifetime') AS CHAR), 'N/A') AS PasswordLifetime, " +
                   "CASE WHEN JSON_VALUE(`Priv`, '$.password_last_changed') IS NULL THEN 'N/A' ELSE CAST(FROM_UNIXTIME(JSON_VALUE(`Priv`, '$.password_last_changed')) AS CHAR) END AS PasswordLastChanged, " +
                   "CASE WHEN COALESCE(JSON_VALUE(`Priv`, '$.password_expired'), 0) IN (1, '1', 'true') THEN 'Yes' ELSE 'No' END AS MustChangePassword, " +
                   "'N/A' AS Privileges " +
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

        public static List<string> LoadGrantStatements(IDatabase db, string user, string host)
        {
            List<string> grants = new List<string>();
            DataTable table = TrySelect(db, "SHOW GRANTS FOR " + QuoteAccount(user, host) + ";");
            if (table == null) return grants;

            foreach (DataRow row in table.Rows)
            {
                if (table.Columns.Count == 0 || row[0] == DBNull.Value) continue;
                string statement = SanitizeGrantStatement(Convert.ToString(row[0]));
                if (statement.Length > 0 && !grants.Contains(statement, StringComparer.OrdinalIgnoreCase)) grants.Add(statement.TrimEnd(';') + ";");
            }
            return grants;
        }

        public static string LoadSafeCreateUserStatement(IDatabase db, string user, string host)
        {
            DataTable table = TrySelect(db, "SHOW CREATE USER " + QuoteAccount(user, host) + ";");
            if (table == null || table.Rows.Count == 0 || table.Columns.Count == 0) return string.Empty;

            object value = table.Rows[0][table.Columns.Count - 1];
            return value == null || value == DBNull.Value ? string.Empty : SanitizeCreateUserStatement(Convert.ToString(value));
        }

        public static List<string> GetGrantedPrivilegesForTarget(IEnumerable<string> grantStatements, string databaseName, string objectName, MySqlPrivilegeTargetType targetType)
        {
            string expectedTarget = NormalizeGrantTarget(BuildPrivilegeTarget(databaseName, objectName, targetType));
            List<string> privileges = new List<string>();
            if (grantStatements == null) return privileges;

            foreach (string statement in grantStatements)
            {
                string privilegeText;
                string targetText;
                if (!TryParseGrantStatement(statement, out privilegeText, out targetText)) continue;
                if (!string.Equals(NormalizeGrantTarget(targetText), expectedTarget, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (string privilege in SplitPrivilegeList(privilegeText))
                {
                    if (!privileges.Contains(privilege, StringComparer.OrdinalIgnoreCase)) privileges.Add(privilege);
                }
            }
            return privileges;
        }

        public static bool HasGrantOptionForTarget(IEnumerable<string> grantStatements, string databaseName, string objectName, MySqlPrivilegeTargetType targetType)
        {
            string expectedTarget = NormalizeGrantTarget(BuildPrivilegeTarget(databaseName, objectName, targetType));
            if (grantStatements == null) return false;
            foreach (string statement in grantStatements)
            {
                string privilegeText;
                string targetText;
                if (!TryParseGrantStatement(statement, out privilegeText, out targetText)) continue;
                if (string.Equals(NormalizeGrantTarget(targetText), expectedTarget, StringComparison.OrdinalIgnoreCase) &&
                    statement.IndexOf("WITH GRANT OPTION", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        public static string BuildUserDdlPreview(DataRow userRow)
        {
            if (userRow == null) return string.Empty;
            string user = GetColumnValue(userRow, "Name");
            string host = GetColumnValue(userRow, "Host");
            string account = QuoteAccount(user, host);
            StringBuilder sb = new StringBuilder();
            string createStatement = GetColumnValue(userRow, "CreateStatement");
            if (IsMeaningful(createStatement))
            {
                sb.AppendLine(createStatement.TrimEnd(';') + ";");
                sb.AppendLine("-- Authentication credentials omitted from preview.");
            }
            else
            {
                sb.AppendLine("CREATE USER " + account + ";");
            }

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
            string grantStatements = GetColumnValue(userRow, "GrantStatements");
            if (IsMeaningful(grantStatements))
            {
                sb.AppendLine("-- Privileges: " + (IsMeaningful(privileges) ? privileges : "SHOW GRANTS"));
                foreach (string grant in grantStatements.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    sb.AppendLine(grant.TrimEnd(';') + ";");
                }
            }
            else if (IsMeaningful(privileges))
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
            return BuildCreateUserSql(options, CreateDefaultAdapter());
        }

        public static string BuildCreateUserSql(MySqlCreateUserOptions options, MySqlUserProviderAdapter adapter)
        {
            if (options == null) throw new ArgumentNullException("options");
            if (adapter == null) adapter = CreateDefaultAdapter();
            EnsureSupportedCreateOptions(options, adapter);
            string account = QuoteAccount(options.User, options.Host);
            StringBuilder sql = new StringBuilder();
            sql.Append("CREATE USER ").Append(account);
            if (!string.IsNullOrEmpty(options.Plugin))
            {
                if (adapter.IsMariaDb)
                {
                    sql.Append(" IDENTIFIED VIA ").Append(QuoteIdentifier(options.Plugin));
                    if (!string.IsNullOrEmpty(options.Password)) sql.Append(" USING PASSWORD(").Append(QuoteLiteral(options.Password)).Append(")");
                }
                else
                {
                    sql.Append(" IDENTIFIED WITH ").Append(QuoteIdentifier(options.Plugin));
                    if (!string.IsNullOrEmpty(options.Password)) sql.Append(" BY ").Append(QuoteLiteral(options.Password));
                }
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

        public static List<string> BuildCreateUserSqlStatements(MySqlCreateUserOptions options, MySqlUserProviderAdapter adapter)
        {
            return new List<string> { BuildCreateUserSql(options, adapter) };
        }

        public static List<string> BuildAlterUserSqlStatements(MySqlAlterUserOptions options)
        {
            return BuildAlterUserSqlStatements(options, CreateDefaultAdapter());
        }

        public static List<string> BuildAlterUserSqlStatements(MySqlAlterUserOptions options, MySqlUserProviderAdapter adapter)
        {
            if (options == null) throw new ArgumentNullException("options");
            if (adapter == null) adapter = CreateDefaultAdapter();
            EnsureSupportedAlterOptions(options, adapter);
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
                statements.Add(BuildAlterAuthenticationSql(targetUser, targetHost, options.Plugin, options.Password, options.ChangePassword, adapter));
            }

            if (options.LockAccount.HasValue) statements.Add(BuildAccountLockSql(targetUser, targetHost, options.LockAccount.Value));
            if (options.ExpirePassword) statements.Add(BuildExpirePasswordSql(targetUser, targetHost));
            if (options.RequireSsl) statements.Add(BuildSslRequirementSql(targetUser, targetHost, "SSL", adapter));
            if (options.ClearSslRequirement) statements.Add(BuildSslRequirementSql(targetUser, targetHost, "NONE", adapter));

            string limitSql = BuildResourceLimitSql(targetUser, targetHost, options.MaxQuestionsPerHour, options.MaxUpdatesPerHour, options.MaxConnectionsPerHour, options.MaxUserConnections, adapter);
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
                Dictionary<string, string> result = db.ExecSQL(statement);
                string status = GetResultValue(result, "status");
                if (!string.Equals(status, "OK", StringComparison.OrdinalIgnoreCase))
                {
                    string reason = GetResultValue(result, "reason");
                    if (string.IsNullOrWhiteSpace(reason)) reason = "Database rejected the user operation.";
                    throw new InvalidOperationException("Statement " + (executed + 1) + " failed: " + reason);
                }
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
            return BuildSslRequirementSql(user, host, sslRequirement, CreateDefaultAdapter());
        }

        public static string BuildSslRequirementSql(string user, string host, string sslRequirement, MySqlUserProviderAdapter adapter)
        {
            string value = (sslRequirement ?? string.Empty).Trim().ToUpperInvariant();
            string prefix = adapter != null && !adapter.SupportsAlterUser ? "GRANT USAGE ON *.* TO " : "ALTER USER ";
            if (value == "NONE" || value == "NO" || value == "DISABLED") return prefix + QuoteAccount(user, host) + " REQUIRE NONE;";
            return prefix + QuoteAccount(user, host) + " REQUIRE " + NormalizeSslRequirement(sslRequirement) + ";";
        }

        public static string BuildResourceLimitSql(string user, string host, int? maxQuestionsPerHour, int? maxUpdatesPerHour, int? maxConnectionsPerHour, int? maxUserConnections)
        {
            return BuildResourceLimitSql(user, host, maxQuestionsPerHour, maxUpdatesPerHour, maxConnectionsPerHour, maxUserConnections, CreateDefaultAdapter());
        }

        public static string BuildResourceLimitSql(string user, string host, int? maxQuestionsPerHour, int? maxUpdatesPerHour, int? maxConnectionsPerHour, int? maxUserConnections, MySqlUserProviderAdapter adapter)
        {
            List<string> limits = new List<string>();
            AddResourceLimit(limits, "MAX_QUERIES_PER_HOUR", maxQuestionsPerHour);
            AddResourceLimit(limits, "MAX_UPDATES_PER_HOUR", maxUpdatesPerHour);
            AddResourceLimit(limits, "MAX_CONNECTIONS_PER_HOUR", maxConnectionsPerHour);
            AddResourceLimit(limits, "MAX_USER_CONNECTIONS", maxUserConnections);
            if (limits.Count == 0) return string.Empty;
            string prefix = adapter != null && !adapter.SupportsAlterUser ? "GRANT USAGE ON *.* TO " : "ALTER USER ";
            return prefix + QuoteAccount(user, host) + " WITH " + string.Join(" ", limits.ToArray()) + ";";
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

        private static void EnrichUsersFromShowStatements(IDatabase db, DataTable users, MySqlUserProviderAdapter adapter)
        {
            if (db == null || users == null) return;
            foreach (DataRow row in users.Rows)
            {
                row["ProviderFamily"] = adapter != null && adapter.IsMariaDb ? "MariaDB" : "MySQL";
                row["ProviderVersion"] = string.IsNullOrWhiteSpace(adapter == null ? null : adapter.Version) ? NotSupported : adapter.Version;
                string user = GetColumnValue(row, "Name");
                string host = GetColumnValue(row, "Host");
                if (user.Length == 0) continue;

                List<string> grants = LoadGrantStatements(db, user, host);
                if (grants.Count > 0)
                {
                    row["GrantStatements"] = string.Join(Environment.NewLine, grants.ToArray());
                    List<string> privilegeNames = ExtractPrivilegeSummary(grants);
                    row["Privileges"] = privilegeNames.Count == 0 ? NotSupported : string.Join(",", privilegeNames.ToArray());
                }

                string createStatement = adapter != null && adapter.SupportsShowCreateUser
                    ? LoadSafeCreateUserStatement(db, user, host)
                    : string.Empty;
                if (createStatement.Length > 0) row["CreateStatement"] = createStatement;
            }
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
                   string.Equals(columnName, "ProviderFamily", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(columnName, "ProviderVersion", StringComparison.OrdinalIgnoreCase);
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

        private static string BuildAlterAuthenticationSql(string user, string host, string plugin, string password, bool changePassword, MySqlUserProviderAdapter adapter)
        {
            if (adapter != null && !adapter.SupportsAlterUser)
            {
                if (!string.IsNullOrEmpty(plugin)) throw new NotSupportedException("Authentication plugin changes require ALTER USER support on this server version.");
                return "SET PASSWORD FOR " + QuoteAccount(user, host) + " = PASSWORD(" + QuoteLiteral(password ?? string.Empty) + ");";
            }

            StringBuilder sql = new StringBuilder();
            sql.Append("ALTER USER ").Append(QuoteAccount(user, host));
            if (!string.IsNullOrEmpty(plugin))
            {
                if (adapter != null && adapter.IsMariaDb)
                {
                    sql.Append(" IDENTIFIED VIA ").Append(QuoteIdentifier(plugin));
                    if (changePassword) sql.Append(" USING PASSWORD(").Append(QuoteLiteral(password ?? string.Empty)).Append(")");
                }
                else
                {
                    sql.Append(" IDENTIFIED WITH ").Append(QuoteIdentifier(plugin));
                    if (changePassword) sql.Append(" BY ").Append(QuoteLiteral(password ?? string.Empty));
                }
            }
            else
            {
                sql.Append(" IDENTIFIED BY ").Append(QuoteLiteral(password ?? string.Empty));
            }
            sql.Append(";");
            return sql.ToString();
        }

        private static void EnsureSupportedCreateOptions(MySqlCreateUserOptions options, MySqlUserProviderAdapter adapter)
        {
            if (options.LockAccount && !adapter.SupportsAccountLock)
                throw new NotSupportedException("Account locking is not supported by " + DescribeProvider(adapter) + ".");
            if (options.ExpirePassword && !adapter.SupportsPasswordExpiration)
                throw new NotSupportedException("Password expiration is not supported by " + DescribeProvider(adapter) + ".");
        }

        private static void EnsureSupportedAlterOptions(MySqlAlterUserOptions options, MySqlUserProviderAdapter adapter)
        {
            if (options.LockAccount.HasValue && !adapter.SupportsAccountLock)
                throw new NotSupportedException("Account locking is not supported by " + DescribeProvider(adapter) + ".");
            if (options.ExpirePassword && !adapter.SupportsPasswordExpiration)
                throw new NotSupportedException("Password expiration is not supported by " + DescribeProvider(adapter) + ".");
            if (!adapter.SupportsAlterUser && !string.IsNullOrEmpty(options.Plugin))
                throw new NotSupportedException("Authentication plugin changes are not supported by " + DescribeProvider(adapter) + ".");
        }

        private static string DescribeProvider(MySqlUserProviderAdapter adapter)
        {
            if (adapter == null) return "this provider";
            string family = adapter.IsMariaDb ? "MariaDB" : "MySQL";
            return string.IsNullOrWhiteSpace(adapter.Version) ? family : family + " " + adapter.Version;
        }

        private static MySqlUserProviderAdapter CreateDefaultAdapter()
        {
            return new MySqlUserProviderAdapter("8.0.0", null, false);
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

        private static string GetResultValue(Dictionary<string, string> result, string key)
        {
            if (result == null || string.IsNullOrWhiteSpace(key)) return string.Empty;
            string value;
            if (result.TryGetValue(key, out value)) return value ?? string.Empty;
            foreach (KeyValuePair<string, string> pair in result)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)) return pair.Value ?? string.Empty;
            }
            return string.Empty;
        }

        private static string SanitizeCreateUserStatement(string statement)
        {
            string value = (statement ?? string.Empty).Trim().TrimEnd(';');
            if (value.Length == 0) return string.Empty;

            const string plugin = @"(?<plugin>`(?:``|[^`])+`|'(?:''|[^'])+'|[A-Za-z0-9_]+)";
            const string secret = @"'(?:''|\\.|[^'])*'";
            value = Regex.Replace(value, @"\s+IDENTIFIED\s+WITH\s+" + plugin + @"\s+(?:AS|BY)\s+" + secret,
                " IDENTIFIED WITH ${plugin}", RegexOptions.IgnoreCase);
            value = Regex.Replace(value, @"\s+IDENTIFIED\s+(?:VIA|WITH)\s+" + plugin + @"\s+(?:USING|AS)\s+(?:PASSWORD\s*\(\s*)?" + secret + @"\s*\)?",
                " IDENTIFIED VIA ${plugin}", RegexOptions.IgnoreCase);
            value = Regex.Replace(value, @"\s+IDENTIFIED\s+BY(?:\s+PASSWORD)?\s+" + secret, string.Empty, RegexOptions.IgnoreCase);
            return value.Trim() + ";";
        }

        private static string SanitizeGrantStatement(string statement)
        {
            string value = (statement ?? string.Empty).Trim().TrimEnd(';');
            if (value.Length == 0) return string.Empty;
            const string plugin = @"(?:`(?:``|[^`])+`|'(?:''|[^'])+'|[A-Za-z0-9_]+)";
            const string secret = @"'(?:''|\\.|[^'])*'";
            value = Regex.Replace(value, @"\s+IDENTIFIED\s+BY(?:\s+PASSWORD)?\s+" + secret, string.Empty, RegexOptions.IgnoreCase);
            value = Regex.Replace(value, @"\s+IDENTIFIED\s+WITH\s+" + plugin + @"(?:\s+(?:AS|BY)\s+" + secret + @")?", string.Empty, RegexOptions.IgnoreCase);
            return value.Trim() + ";";
        }

        private static List<string> ExtractPrivilegeSummary(IEnumerable<string> grants)
        {
            List<string> privileges = new List<string>();
            if (grants == null) return privileges;
            foreach (string grant in grants)
            {
                string privilegeText;
                string targetText;
                if (!TryParseGrantStatement(grant, out privilegeText, out targetText)) continue;
                foreach (string privilege in SplitPrivilegeList(privilegeText))
                {
                    if (!privileges.Contains(privilege, StringComparer.OrdinalIgnoreCase)) privileges.Add(privilege);
                }
                if (grant.IndexOf("WITH GRANT OPTION", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    !privileges.Contains("GRANT OPTION", StringComparer.OrdinalIgnoreCase)) privileges.Add("GRANT OPTION");
            }
            return privileges;
        }

        private static bool TryParseGrantStatement(string statement, out string privilegeText, out string targetText)
        {
            privilegeText = string.Empty;
            targetText = string.Empty;
            string value = (statement ?? string.Empty).Trim();
            if (!value.StartsWith("GRANT ", StringComparison.OrdinalIgnoreCase)) return false;
            int onIndex = value.IndexOf(" ON ", StringComparison.OrdinalIgnoreCase);
            if (onIndex < 0) return false;
            int toIndex = value.IndexOf(" TO ", onIndex + 4, StringComparison.OrdinalIgnoreCase);
            if (toIndex < 0) return false;
            privilegeText = value.Substring(6, onIndex - 6).Trim();
            targetText = value.Substring(onIndex + 4, toIndex - onIndex - 4).Trim();
            return privilegeText.Length > 0 && targetText.Length > 0;
        }

        private static IEnumerable<string> SplitPrivilegeList(string privilegeText)
        {
            foreach (string part in (privilegeText ?? string.Empty).Split(','))
            {
                string value = part.Trim().ToUpperInvariant().Replace('_', ' ');
                if (value.Length == 0 || value == "USAGE") continue;
                yield return value;
            }
        }

        private static string NormalizeGrantTarget(string target)
        {
            string value = (target ?? string.Empty).Trim().TrimEnd(';').Replace("`", string.Empty);
            value = Regex.Replace(value, @"\s*\.\s*", ".");
            value = Regex.Replace(value, @"\s+", " ");
            return value.ToUpperInvariant();
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
            if (normalized.Contains("ALL PRIVILEGES")) return new List<string> { "ALL PRIVILEGES" };
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
