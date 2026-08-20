using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using mySQLPunk.lib;

internal static class MySqlUserIntegrationTests
{
    private const string OriginalUser = "mysqlpunk_it";
    private const string RenamedUser = "mysqlpunk_it2";
    private const string Host = "%";
    private const string DatabaseName = "mysqlpunk_it_db";

    private static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: MySqlUserIntegrationTests <port> <expected-family> <password>");
            return 2;
        }

        try
        {
            int port = int.Parse(args[0]);
            Run(port, args[1], args[2]);
            Console.WriteLine("[PASS] " + args[1] + " live user manager integration");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[FAIL] " + ex);
            return 1;
        }
    }

    private static void Run(int port, string expectedFamily, string rootPassword)
    {
        using (my_mysql db = ConnectWithRetry(port, rootPassword))
        {
            Cleanup(db);
            try
            {
                MySqlUserProviderAdapter adapter = MySqlUserProviderAdapter.Detect(db);
                string actualFamily = adapter.IsMariaDb ? "MariaDB" : (adapter.Family == MySqlUserProviderFamily.MySql8 ? "MySQL8" : "MySQL5");
                AssertEquals(expectedFamily, actualFamily, "Provider family detection mismatch for " + adapter.Version);

                DataTable initialUsers = MySqlUserManagerService.LoadUsers(db);
                Assert(initialUsers.Rows.Cast<DataRow>().Any(row => Convert.ToString(row["Name"]) == "root"), "User list did not load the root account.");
                DataTable projection = db.SelectSQL("SELECT 1 AS value, 2 AS value, CAST('2026-08-20 12:34:56' AS DATETIME) AS occurred_at, NULL AS optional_value;");
                Assert(projection.Rows.Count == 1 && projection.Columns.Count == 4, "Constraint-free result loading should preserve all projected columns.");
                Assert(projection.Columns[0].ColumnName == "value" && projection.Columns[1].ColumnName == "value_2", "Duplicate provider column names should be made unique without dropping values.");
                Assert(Convert.ToInt32(projection.Rows[0][0]) == 1 && Convert.ToInt32(projection.Rows[0][1]) == 2, "Constraint-free result loading should preserve typed values.");
                Assert(projection.Rows[0][3] == DBNull.Value, "Constraint-free result loading should preserve database nulls.");

                List<string> create = MySqlUserManagerService.BuildCreateUserSqlStatements(new MySqlCreateUserOptions
                {
                    User = OriginalUser,
                    Host = Host,
                    Password = "Start!234",
                    RequireSsl = false,
                    ExpirePassword = adapter.SupportsPasswordExpiration,
                    LockAccount = adapter.SupportsAccountLock
                }, adapter);
                MySqlUserManagerService.ExecuteUserSqlStatements(db, create);

                List<string> alter = MySqlUserManagerService.BuildAlterUserSqlStatements(new MySqlAlterUserOptions
                {
                    User = OriginalUser,
                    Host = Host,
                    RenameUser = true,
                    NewUser = RenamedUser,
                    NewHost = Host,
                    ChangePassword = true,
                    Password = "Next!234",
                    LockAccount = adapter.SupportsAccountLock ? (bool?)false : null,
                    RequireSsl = true,
                    MaxQuestionsPerHour = 100,
                    MaxUpdatesPerHour = 50,
                    MaxConnectionsPerHour = 20,
                    MaxUserConnections = 5
                }, adapter);
                MySqlUserManagerService.ExecuteUserSqlStatements(db, alter);

                EnsureExec(db, "CREATE DATABASE " + QuoteIdentifier(DatabaseName) + ";");
                EnsureExec(db, "CREATE TABLE " + QuoteIdentifier(DatabaseName) + ".`items` (`id` INT PRIMARY KEY, `name` VARCHAR(50));");
                EnsureExec(db, "CREATE PROCEDURE " + QuoteIdentifier(DatabaseName) + ".`sp_ping`() SELECT 1;");

                MySqlUserManagerService.ExecuteUserSqlStatements(db, new[]
                {
                    MySqlUserManagerService.BuildGrantSql(new[] { "SELECT", "UPDATE" }, DatabaseName, "items", RenamedUser, Host, true),
                    MySqlUserManagerService.BuildGrantSql(new[] { "EXECUTE" }, DatabaseName, "sp_ping", RenamedUser, Host, false, MySqlPrivilegeTargetType.Procedure)
                });

                List<string> grants = MySqlUserManagerService.LoadGrantStatements(db, RenamedUser, Host);
                Assert(grants.All(grant => grant.IndexOf("IDENTIFIED BY PASSWORD", StringComparison.OrdinalIgnoreCase) < 0), "SHOW GRANTS exposed a legacy authentication hash.");
                Assert(MySqlUserManagerService.GetGrantedPrivilegesForTarget(grants, DatabaseName, "items", MySqlPrivilegeTargetType.TableOrView).Contains("SELECT"), "Table privilege was not returned by SHOW GRANTS.");
                Assert(MySqlUserManagerService.GetGrantedPrivilegesForTarget(grants, DatabaseName, "sp_ping", MySqlPrivilegeTargetType.Procedure).Contains("EXECUTE"), "Procedure privilege was not returned by SHOW GRANTS.");
                Assert(MySqlUserManagerService.HasGrantOptionForTarget(grants, DatabaseName, "items", MySqlPrivilegeTargetType.TableOrView), "WITH GRANT OPTION was not detected.");

                string safeCreate = MySqlUserManagerService.LoadSafeCreateUserStatement(db, RenamedUser, Host);
                if (adapter.SupportsShowCreateUser)
                {
                    Assert(safeCreate.StartsWith("CREATE USER", StringComparison.OrdinalIgnoreCase), "SHOW CREATE USER was not loaded.");
                    Assert(safeCreate.IndexOf("Next!234", StringComparison.Ordinal) < 0, "DDL preview exposed the clear-text password.");
                }
                else
                {
                    Assert(safeCreate.Length == 0, "A provider without SHOW CREATE USER support should use the generated DDL fallback.");
                }

                DataTable users = MySqlUserManagerService.LoadUsers(db);
                DataRow userRow = users.Rows.Cast<DataRow>().FirstOrDefault(row =>
                    string.Equals(Convert.ToString(row["Name"]), RenamedUser, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Convert.ToString(row["Host"]), Host, StringComparison.OrdinalIgnoreCase));
                Assert(userRow != null, "Renamed account was not present in the user list.");
                if (adapter.IsMariaDb) AssertEquals("mysql.global_priv", Convert.ToString(userRow["Source"]), "MariaDB should read account metadata from mysql.global_priv.");
                string ddl = MySqlUserManagerService.BuildUserDdlPreview(userRow);
                Assert(ddl.IndexOf("GRANT SELECT, UPDATE", StringComparison.OrdinalIgnoreCase) >= 0, "DDL preview did not include object grants.");

                MySqlUserManagerService.ExecuteUserSqlStatements(db, new[]
                {
                    MySqlUserManagerService.BuildRevokeSql(new[] { "SELECT", "UPDATE" }, DatabaseName, "items", RenamedUser, Host),
                    MySqlUserManagerService.BuildRevokeSql(new[] { "EXECUTE" }, DatabaseName, "sp_ping", RenamedUser, Host, MySqlPrivilegeTargetType.Procedure)
                });
                MySqlUserManagerService.ExecuteUserSqlStatements(db, MySqlUserManagerService.BuildDropUserSqlStatements(RenamedUser, Host));
                Assert(!MySqlUserManagerService.LoadUsers(db).Rows.Cast<DataRow>().Any(row => Convert.ToString(row["Name"]) == RenamedUser), "Dropped account remained in the user list.");
            }
            finally
            {
                Cleanup(db);
            }
        }
    }

    private static my_mysql ConnectWithRetry(int port, string password)
    {
        Exception last = null;
        for (int attempt = 0; attempt < 300; attempt++)
        {
            my_mysql db = new my_mysql();
            try
            {
                db.SetConn("Server=127.0.0.1;Port=" + port + ";User ID=root;Password=" + password + ";SslMode=None;AllowPublicKeyRetrieval=True;");
                db.Open();
                return db;
            }
            catch (Exception ex)
            {
                last = ex;
                db.Dispose();
                Thread.Sleep(1000);
            }
        }
        throw new InvalidOperationException("Database container did not become ready.", last);
    }

    private static void Cleanup(IDatabase db)
    {
        EnsureExec(db, "DROP USER IF EXISTS '" + OriginalUser + "'@'" + Host + "';", true);
        EnsureExec(db, "DROP USER IF EXISTS '" + RenamedUser + "'@'" + Host + "';", true);
        EnsureExec(db, "DROP DATABASE IF EXISTS " + QuoteIdentifier(DatabaseName) + ";", true);
    }

    private static void EnsureExec(IDatabase db, string sql, bool ignoreFailure = false)
    {
        Dictionary<string, string> result = db.ExecSQL(sql);
        string status = GetValue(result, "status");
        if (!ignoreFailure && !string.Equals(status, "OK", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(GetValue(result, "reason"));
    }

    private static string GetValue(Dictionary<string, string> values, string key)
    {
        if (values == null) return string.Empty;
        foreach (KeyValuePair<string, string> pair in values)
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)) return pair.Value ?? string.Empty;
        return string.Empty;
    }

    private static string QuoteIdentifier(string value)
    {
        return "`" + (value ?? string.Empty).Replace("`", "``") + "`";
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void AssertEquals(string expected, string actual, string message)
    {
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(message + " Expected: " + expected + " Actual: " + actual);
    }
}
