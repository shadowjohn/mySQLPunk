using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading;
using mySQLPunk.lib;

internal static class DatabaseRenameProviderIntegrationTests
{
    private const string OldDatabase = "mysqlpunk_rename_old";
    private const string NewDatabase = "mysqlpunk_rename_new";

    private static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: DatabaseRenameProviderIntegrationTests <sqlite|postgresql|mssql> [port] [password]");
            return 2;
        }

        try
        {
            string provider = args[0].ToLowerInvariant();
            if (provider == "sqlite") RunSqlite();
            else if (provider == "postgresql") RunPostgreSql(int.Parse(args[1]), args[2]);
            else if (provider == "mssql") RunSqlServer(int.Parse(args[1]), args[2]);
            else throw new ArgumentException("Unsupported provider: " + provider);
            Console.WriteLine("[PASS] " + provider + " live database rename integration");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[FAIL]");
            PrintExceptionChain(ex);
            return 1;
        }
    }

    private static void RunSqlite()
    {
        string directory = Path.Combine(Path.GetTempPath(), "mysqlpunk-sqlite-rename-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string oldPath = Path.Combine(directory, "old.sqlite");
        string newPath = Path.Combine(directory, "renamed.sqlite");
        try
        {
            using (my_sqlite db = new my_sqlite())
            {
                db.SetConn("Data Source=" + oldPath + ";Version=3;");
                db.Open();
                EnsureExec(db, "CREATE TABLE sample(id INTEGER PRIMARY KEY, name TEXT NOT NULL);");
                EnsureExec(db, "INSERT INTO sample(name) VALUES ('中文資料');");

                DatabaseRenameResult result = DatabaseRenameService.Rename(
                    db,
                    "main",
                    "renamed",
                    new DatabaseRenameOptions { SqliteFilePath = oldPath },
                    null);
                Assert(!File.Exists(oldPath) && File.Exists(newPath), "SQLite database file was not moved.");
                Assert(string.Equals(result.NewSqlitePath, newPath, StringComparison.OrdinalIgnoreCase), "SQLite rename result returned the wrong path.");
                DataTable rows = db.SelectSQL("SELECT name FROM sample;");
                Assert(rows.Rows.Count == 1 && Convert.ToString(rows.Rows[0][0]) == "中文資料", "SQLite data was not readable after reopening the renamed file.");
            }
        }
        finally
        {
            System.Data.SQLite.SQLiteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static void RunPostgreSql(int port, string password)
    {
        using (my_postgresql db = ConnectPostgreSql(port, password))
        {
            CleanupServerDatabase(db);
            try
            {
                EnsureExec(db, "CREATE DATABASE \"" + OldDatabase + "\";");
                DatabaseRenameResult result = DatabaseRenameService.Rename(db, OldDatabase, NewDatabase, null, null);
                Assert(!result.OldDatabaseRetained, "PostgreSQL native rename should not retain the old database name.");
                List<string> databases = db.GetDatabases();
                Assert(databases.Any(name => string.Equals(name, NewDatabase, StringComparison.OrdinalIgnoreCase)), "PostgreSQL target database was not found after rename.");
                Assert(!databases.Any(name => string.Equals(name, OldDatabase, StringComparison.OrdinalIgnoreCase)), "PostgreSQL old database name remained after rename.");
            }
            finally
            {
                CleanupServerDatabase(db);
            }
        }
    }

    private static void RunSqlServer(int port, string password)
    {
        using (my_mssql db = ConnectSqlServer(port, password))
        {
            CleanupServerDatabase(db);
            try
            {
                EnsureExec(db, "CREATE DATABASE [" + OldDatabase + "];");
                DatabaseRenameResult result = DatabaseRenameService.Rename(db, OldDatabase, NewDatabase, null, null);
                Assert(!result.OldDatabaseRetained, "SQL Server native rename should not retain the old database name.");
                List<string> databases = db.GetDatabases();
                Assert(databases.Any(name => string.Equals(name, NewDatabase, StringComparison.OrdinalIgnoreCase)), "SQL Server target database was not found after rename.");
                Assert(!databases.Any(name => string.Equals(name, OldDatabase, StringComparison.OrdinalIgnoreCase)), "SQL Server old database name remained after rename.");
            }
            finally
            {
                CleanupServerDatabase(db);
            }
        }
    }

    private static my_postgresql ConnectPostgreSql(int port, string password)
    {
        Exception last = null;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            my_postgresql db = new my_postgresql();
            try
            {
                db.SetConn("Host=127.0.0.1;Port=" + port + ";Username=postgres;Password=" + password + ";Database=postgres;SSL Mode=Disable;Timeout=3;");
                db.Open();
                return db;
            }
            catch (Exception ex)
            {
                last = ex;
                if (db.MCT != null) db.Dispose();
                if (attempt == 0 || attempt == 19)
                {
                    Console.Error.WriteLine("PostgreSQL connection attempt " + (attempt + 1) + ":");
                    PrintExceptionChain(ex);
                }
                Thread.Sleep(1000);
            }
        }
        throw new InvalidOperationException("PostgreSQL container did not become ready.", last);
    }

    private static my_mssql ConnectSqlServer(int port, string password)
    {
        Exception last = null;
        for (int attempt = 0; attempt < 240; attempt++)
        {
            my_mssql db = new my_mssql();
            try
            {
                db.SetConn("Server=127.0.0.1," + port + ";User ID=sa;Password=" + password + ";Initial Catalog=master;Encrypt=False;TrustServerCertificate=True;Connection Timeout=3;");
                db.Open();
                return db;
            }
            catch (Exception ex)
            {
                last = ex;
                if (db.MCT != null) db.Dispose();
                Thread.Sleep(1000);
            }
        }
        throw new InvalidOperationException("SQL Server container did not become ready.", last);
    }

    private static void CleanupServerDatabase(IDatabase db)
    {
        if (string.Equals(db.ProviderName, "mssql", StringComparison.OrdinalIgnoreCase))
        {
            EnsureExec(db, "IF DB_ID(N'" + NewDatabase + "') IS NOT NULL BEGIN ALTER DATABASE [" + NewDatabase + "] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [" + NewDatabase + "]; END", true);
            EnsureExec(db, "IF DB_ID(N'" + OldDatabase + "') IS NOT NULL BEGIN ALTER DATABASE [" + OldDatabase + "] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [" + OldDatabase + "]; END", true);
            return;
        }
        EnsureExec(db, "DROP DATABASE IF EXISTS \"" + NewDatabase + "\";", true);
        EnsureExec(db, "DROP DATABASE IF EXISTS \"" + OldDatabase + "\";", true);
    }

    private static void EnsureExec(IDatabase db, string sql, bool ignoreFailure = false)
    {
        Dictionary<string, string> result = db.ExecSQL(sql);
        string status = GetValue(result, "status");
        if (!ignoreFailure && !string.Equals(status, "OK", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(GetValue(result, "reason") + "\nSQL: " + sql);
    }

    private static string GetValue(Dictionary<string, string> values, string key)
    {
        if (values == null) return string.Empty;
        foreach (KeyValuePair<string, string> pair in values)
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)) return pair.Value ?? string.Empty;
        return string.Empty;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void PrintExceptionChain(Exception exception)
    {
        int depth = 0;
        for (Exception current = exception; current != null && depth < 12; current = current.InnerException)
        {
            Console.Error.WriteLine(new string(' ', depth * 2) + current.GetType().FullName + ": " + current.Message);
            depth++;
        }
    }
}
