using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Threading;
using mySQLPunk.lib;
using mySQLPunk.lib.MssqlExport;

namespace mySQLPunk.lib
{
    public interface IDatabase : IDisposable { }
    internal static class ConnectionOpenService
    {
        internal static MssqlOwnedSession OpenMssqlImportSession(Dictionary<string, object> settings)
        {
            throw new InvalidOperationException("No real profile access");
        }
    }
}

internal sealed class Owner : IDatabase
{
    private readonly SqlConnection connection;
    public Owner(SqlConnection connection) { this.connection = connection; }
    public void Dispose() { connection.Dispose(); }
}

internal static class Program
{
    private static SqlConnection Open(int port, string database)
    {
        var settings = new SqlConnectionStringBuilder
        {
            DataSource = "127.0.0.1," + port, InitialCatalog = database, UserID = "sa",
            Password = Environment.GetEnvironmentVariable("MYSQLPUNK_TEST_PASSWORD"),
            Encrypt = false, TrustServerCertificate = true, ConnectTimeout = 3, Pooling = false
        };
        var connection = new SqlConnection(settings.ConnectionString);
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    private static object Query(SqlConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql; command.CommandTimeout = 60;
        return command.ExecuteScalar();
    }

    private static SqlServerMigrationSession Session(int port, string database)
    {
        return new SqlServerMigrationSession(new Dictionary<string, object>(), database, settings =>
        {
            var connection = Open(port, (string)settings["initial_database"]);
            return new MssqlOwnedSession(new Owner(connection), connection);
        });
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }

    private static void BatchRollback(int port, string database)
    {
        using var connection = Open(port, database);
        Query(connection, "CREATE TABLE dbo.batch_failure(id int PRIMARY KEY, clock time(7), payload varbinary(64), label nvarchar(100), amount decimal(20,6), at datetime2(7), flag bit);");
        var rows = new DataTable();
        rows.Columns.Add("id", typeof(int)); rows.Columns.Add("clock", typeof(TimeSpan));
        rows.Columns.Add("payload", typeof(byte[])); rows.Columns.Add("label", typeof(string));
        rows.Columns.Add("amount", typeof(decimal)); rows.Columns.Add("at", typeof(DateTime)); rows.Columns.Add("flag", typeof(bool));
        for (int index = 0; index < 1000; index++) rows.Rows.Add(index, DBNull.Value, DBNull.Value, "中文😀", DBNull.Value, DBNull.Value, DBNull.Value);
        rows.Rows[999]["id"] = 0;
        bool rejected = false;
        try { SqlServerBatchInsert.Execute(connection, "dbo.batch_failure", rows); }
        catch (SqlException error) when (error.Number == 2627) { rejected = true; }
        Check(rejected && Convert.ToInt32(Query(connection, "SELECT count(*) FROM dbo.batch_failure;")) == 0, "Real SQL second/later batch failure rolls back all rows");
        rows.Rows[999]["id"] = 999;
        SqlServerBatchInsert.Execute(connection, "dbo.batch_failure", rows);
        Check(Convert.ToInt32(Query(connection, "SELECT count(*) FROM dbo.batch_failure;")) == 1000, "Real SQL seven-column 2098 budget, NULL time/binary and safe retry");
        Query(connection, "DROP TABLE dbo.batch_failure;");
    }

    private static void Main(string[] args)
    {
        SqlServerBatchInsertTests.RunParameterTypes(() => new SqlParameter());
        int port2019 = int.Parse(args[0]), port2022 = int.Parse(args[1]);
        if (args.Length > 2 && args[2] == "probe")
        {
            using var oldEngine = Open(port2019, "master");
            using var newEngine = Open(port2022, "master");
            Console.WriteLine(Query(oldEngine, "SELECT SERVERPROPERTY('ProductVersion')"));
            Console.WriteLine(Query(newEngine, "SELECT SERVERPROPERTY('ProductVersion')"));
            return;
        }
        string identity = "mysqlpunk_test_" + Guid.NewGuid().ToString("N");
        var names = new[] { identity + "_old", identity + "_new", identity + "_back" };
        using (var connection = Open(port2019, "master"))
        {
            Check(Convert.ToString(Query(connection, "SELECT SERVERPROPERTY('ProductMajorVersion')")) == "15", "2019 actual engine major15");
            Query(connection, "CREATE DATABASE [" + names[0] + "]; CREATE DATABASE [" + names[2] + "];");
        }
        using (var connection = Open(port2022, "master"))
        {
            Check(Convert.ToString(Query(connection, "SELECT SERVERPROPERTY('ProductMajorVersion')")) == "16", "2022 actual engine major16");
            Query(connection, "CREATE DATABASE [" + names[1] + "];");
        }
        BatchRollback(port2019, names[0]); BatchRollback(port2022, names[1]);
        using (var connection = Open(port2019, names[0]))
        {
            Query(connection, @"CREATE TABLE dbo.fixture (id bigint NOT NULL,label nvarchar(100) NULL,amount decimal(20,6) NULL,at datetime2(7) NULL,clock time(7) NULL,payload varbinary(64) NULL,flag bit NULL);
WITH n AS (SELECT 1 n UNION ALL SELECT n+1 FROM n WHERE n<1005) INSERT dbo.fixture SELECT n,N'繁體中文 😺 ' + CONVERT(nvarchar(12),n),CONVERT(decimal(20,6),n/1000.0),CONVERT(datetime2(7),'2020-02-29T12:34:56.1234567'),CONVERT(time(7),'23:59:59.1234567'),0x00FF0100,1 FROM n OPTION(MAXRECURSION 2000);
INSERT dbo.fixture VALUES(-9223372036854775808,NULL,NULL,NULL,NULL,NULL,NULL),(9223372036854775807,N'',-1234567890.123456,'2000-01-01T00:00:00.0000001','00:00:00.0000001',0x,0);
CREATE TABLE dbo.empty_table (value int NULL);");
        }
        Run(port2019, names[0], port2022, names[1], 16, "2019->2022");
        Run(port2022, names[1], port2019, names[2], 15, "2022->2019");
        using (var origin = Open(port2019, names[0]))
        using (var returned = Open(port2019, names[2]))
        {
            string check = "SELECT CONVERT(nvarchar(30),COUNT_BIG(*))+':'+CONVERT(nvarchar(30),SUM(CONVERT(decimal(30,0),id)))+':'+CONVERT(nvarchar(30),SUM(CASE WHEN label IS NULL THEN 1 ELSE 0 END)) FROM dbo.fixture";
            Check(Convert.ToString(Query(origin, check)) == Convert.ToString(Query(returned, check)), "Round-trip row totals/extreme integer sum/NULL count match");
        }
        Console.WriteLine("PASS actual 2019->2022->2019 migration, 1007 rows plus empty table, UTF16/decimal/datetime2(7)/time(7)/binary/NULL and engine-owned SHA256 content verification; no PK/FK/index/identity migration coverage");
    }

    private static void Run(int sourcePort, string sourceDatabase, int targetPort, string targetDatabase, int major, string label)
    {
        using var source = Session(sourcePort, sourceDatabase);
        using var target = Session(targetPort, targetDatabase);
        var preview = SqlServerCompatibilityMigration.Preview(source.ReadCatalog(), target.ReadCatalog(), major);
        Console.WriteLine(label + " preview source=" + preview.Source.MajorVersion + " target=" + preview.Target.MajorVersion + " blockers=" + string.Join(";", preview.Blockers));
        Check(preview.CanStart, label + " actual catalog preflight");
        var result = SqlServerCompatibilityMigration.Run(preview, targetDatabase, source, target, CancellationToken.None);
        Console.WriteLine(result.Report);
        Check(result.Succeeded && result.Rows == 1007, label + " production session/transaction/content verification");
    }
}
