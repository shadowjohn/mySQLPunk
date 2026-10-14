using System.Data.Common;
using System.Diagnostics;
using System.Reflection;
using MySqlConnector;
using Npgsql;
using MySqlPunk.Core.Models;
using MySqlPunk.Core.Services;

internal sealed class MySqlMigrationRegressions(int pgPort, int myPort)
{
    private readonly string _password = Environment.GetEnvironmentVariable("MYSQLPUNK_TEST_PASSWORD") ?? throw new Exception("Missing fixture password.");
    private ConnectionProfile Pg(string database) => new() { Provider = DatabaseProviderKind.PostgreSql, Host = "127.0.0.1", Port = pgPort,
        Username = "postgres", Password = _password, Database = database, TlsMode = ConnectionTlsMode.Disabled };
    private ConnectionProfile My(string database) => new() { Provider = DatabaseProviderKind.MySql, Host = "127.0.0.1", Port = myPort,
        Username = "root", Password = _password, Database = database, TlsMode = ConnectionTlsMode.Disabled };
    private DbConnection Connection(bool mysql, string database) => mysql
        ? new MySqlConnection(new MySqlConnectionStringBuilder { Server = "127.0.0.1", Port = (uint)myPort, UserID = "root", Password = _password,
            Database = database, SslMode = MySqlSslMode.Disabled, Pooling = false, ConnectionTimeout = 3 }.ConnectionString)
        : new NpgsqlConnection(new NpgsqlConnectionStringBuilder { Host = "127.0.0.1", Port = pgPort, Username = "postgres", Password = _password,
            Database = database, SslMode = SslMode.Disable, Pooling = false, Timeout = 3 }.ConnectionString);
    private async Task<object?> SqlAsync(bool mysql, string database, string sql)
    {
        await using var connection = Connection(mysql, database); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
    private async Task<(string Version, int Major, string Collation)> EngineAsync()
    {
        var version = Convert.ToString(await SqlAsync(true, "", "SELECT VERSION()"))!;
        return (version, int.Parse(version.Split('.')[0]), version.Contains("MariaDB", StringComparison.Ordinal) ? "utf8mb4_nopad_bin" : "utf8mb4_0900_bin");
    }
    private async Task CreateMyAsync(string database, string collation) =>
        await SqlAsync(true, "", "CREATE DATABASE " + database + " CHARACTER SET utf8mb4 COLLATE " + collation);
    private async Task CreateSeedAsync(string database)
    {
        await SqlAsync(false, "postgres", "CREATE DATABASE " + database);
        await SqlAsync(false, database, """
            CREATE TABLE records(id bigint NOT NULL, number bigint NOT NULL, optional bigint, note text COLLATE "C", payload bytea,
                CONSTRAINT records_pkey PRIMARY KEY(id,number));
            CREATE INDEX records_optional ON records(optional ASC NULLS FIRST);
            CREATE TABLE empty_table(id bigint, note text COLLATE "C", payload bytea);
            INSERT INTO records SELECT number,number%2,CASE WHEN number%2=0 THEN NULL ELSE number END,
                CASE WHEN number%3=0 THEN NULL ELSE '繁中😀 '||number END,
                CASE WHEN number%2=0 THEN NULL ELSE decode('00ff','hex') END FROM generate_series(1,1000) number;
            INSERT INTO records VALUES(-9223372036854775808,0,NULL,NULL,''::bytea),(9223372036854775807,0,NULL,'',NULL),
                (-1,0,NULL,'a',''::bytea),(-2,0,NULL,'a ',NULL),(-3,0,NULL,'ABC',NULL),(-4,0,NULL,'abc',NULL),(-5,0,NULL,'é',NULL);
            """);
    }
    private static async Task MigrateAsync(ConnectionProfile source, ConnectionProfile target, int major)
    {
        var preview = await LogicalMigrationService.PreviewAsync(source, target, major);
        if (!preview.CanStart) throw new Exception("Preflight refused: " + string.Join(";", preview.Blockers));
        var result = await LogicalMigrationService.RunAsync(source, target, major, preview, target.Database);
        if (result.Outcome != LogicalMigrationOutcome.Verified) throw new Exception(result.Report);
    }
    private async Task<long> ObjectsAsync(string database) => Convert.ToInt64(await SqlAsync(true, database,
        "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE()"));

    public async Task RunBoundariesAsync(long packetBytes, bool crash)
    {
        var engine = await EngineAsync();
        if (Convert.ToInt64(await SqlAsync(true, "", "SELECT @@max_allowed_packet")) != packetBytes) throw new Exception("Wrong isolated packet configuration.");
        var suffix = myPort.ToString();
        var source = "seed_" + suffix;
        await CreateSeedAsync(source);
        await CreateMyAsync("small", engine.Collation);
        await MigrateAsync(Pg(source), My("small"), engine.Major);
        await SqlAsync(false, "postgres", "CREATE DATABASE small_back_" + suffix);
        await MigrateAsync(My("small"), Pg("small_back_" + suffix), 16);
        if (Convert.ToInt64(await SqlAsync(true, "small", "SELECT COUNT(*) FROM records")) != 1007) throw new Exception("Small packet rows lost.");
        var packetSource = "packet_" + suffix;
        await SqlAsync(false, "postgres", "CREATE DATABASE " + packetSource);
        await SqlAsync(false, packetSource, "CREATE TABLE records(id bigint NOT NULL PRIMARY KEY,payload bytea,note text COLLATE \"C\");" +
            "INSERT INTO records SELECT number,decode(repeat('ab',131072),'hex'),repeat('😀',32768) FROM generate_series(1,100) number;");
        await CreateMyAsync("packet", engine.Collation);
        var preview = await LogicalMigrationService.PreviewAsync(Pg(packetSource), My("packet"), engine.Major);
        if (packetBytes == 65536)
        {
            if (preview.CanStart || !preview.Blockers.Any(blocker => blocker.Contains("max_allowed_packet", StringComparison.Ordinal)) || await ObjectsAsync("packet") != 0)
                throw new Exception("Oversize row was not rejected before DDL.");
            Console.WriteLine("PASS actual " + engine.Version + " 64KiB packet: 1007 small rows roundtrip Verified; 128KiB fields refused before DDL; target objects=0");
        }
        else
        {
            if (!preview.CanStart) throw new Exception("Large multi-row packet preview refused.");
            var batches = 0;
            var result = await LogicalMigrationService.RunWithMySqlObserverAsync(Pg(packetSource), My("packet"), engine.Major, preview, CancellationToken.None,
                (stage, _, _) => { if (stage == "batch") batches++; });
            if (result.Outcome != LogicalMigrationOutcome.Verified || batches < 2) throw new Exception("Byte-aware batching did not split: " + result.Report);
            await SqlAsync(false, "postgres", "CREATE DATABASE packet_back_" + suffix);
            await MigrateAsync(My("packet"), Pg("packet_back_" + suffix), 16);
            Console.WriteLine("PASS actual " + engine.Version + " 8MiB packet: 100 x 128KiB binary plus 128KiB UTF8 text; byte-aware batches=" + batches + "; schema/SHA256 roundtrip Verified");
        }
        if (crash) await RunCrashAsync(source, engine.Major, engine.Collation);
    }

    public static async Task CrashChildAsync(int pgPort, int myPort, string stage, string marker, string source, string target)
    {
        var fixture = new MySqlMigrationRegressions(pgPort, myPort);
        var engine = await fixture.EngineAsync();
        var preview = await LogicalMigrationService.PreviewAsync(fixture.Pg(source), fixture.My(target), engine.Major);
        if (!preview.CanStart) throw new Exception("Child preflight refused.");
        await LogicalMigrationService.RunWithMySqlObserverAsync(fixture.Pg(source), fixture.My(target), engine.Major, preview, CancellationToken.None,
            (observed, _, _) =>
            {
                if (observed != stage) return;
                using var file = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                file.WriteByte(1); file.Flush(true);
                Thread.Sleep(Timeout.Infinite);
            });
        throw new Exception("Crash child unexpectedly completed.");
    }

    private async Task RunCrashAsync(string source, int major, string collation)
    {
        foreach (var stage in new[] { "staging", "batch", "published" })
        {
            var database = "crash_" + stage;
            await CreateMyAsync(database, collation);
            var marker = Path.Combine(Path.GetTempPath(), "mysqlpunk-owned-crash-" + Guid.NewGuid().ToString("N"));
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { Assembly.GetExecutingAssembly().Location, pgPort.ToString(), myPort.ToString(), myPort.ToString(),
                "crash-child", stage, marker, source, database }) start.ArgumentList.Add(argument);
            using var child = Process.Start(start) ?? throw new Exception("Own child did not start.");
            var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
            try
            {
                var watch = Stopwatch.StartNew();
                while (!File.Exists(marker) && !child.HasExited && watch.Elapsed < TimeSpan.FromSeconds(40)) await Task.Delay(50);
                if (!File.Exists(marker) || child.HasExited) throw new Exception("Own child did not reach " + stage);
                var expectedObjects = stage == "staging" ? 1 : 2;
                if (await ObjectsAsync(database) != expectedObjects) throw new Exception("Wrong catalog at kill boundary.");
                child.Kill(); await child.WaitForExitAsync();
                if (child.ExitCode == 0) throw new Exception("Child was not terminated.");
                var preview = await LogicalMigrationService.PreviewAsync(Pg(source), My(database), major);
                var staging = Convert.ToInt64(await SqlAsync(true, database,
                    "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND LEFT(TABLE_NAME,4)='_mp_'"));
                if (preview.CanStart || await ObjectsAsync(database) != expectedObjects || staging != (stage == "published" ? 0 : expectedObjects))
                    throw new Exception("Restart did not refuse unchanged nonempty target.");
                if (stage == "batch")
                {
                    await using var connection = Connection(true, database); await connection.OpenAsync();
                    var names = new List<string>();
                    await using (var command = connection.CreateCommand())
                    {
                        command.CommandText = "SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE()";
                        await using var reader = await command.ExecuteReaderAsync();
                        while (await reader.ReadAsync()) names.Add(reader.GetString(0));
                    }
                    foreach (var name in names)
                        if (Convert.ToInt64(await SqlAsync(true, database, "SELECT COUNT(*) FROM `" + name + "`")) != 0) throw new Exception("Killed uncommitted DML retained rows.");
                }
                if (stage == "published")
                {
                    await SqlAsync(false, "postgres", "CREATE DATABASE crash_back_" + myPort);
                    await MigrateAsync(My(database), Pg("crash_back_" + myPort), 16);
                    if (Convert.ToInt64(await SqlAsync(true, database, "SELECT COUNT(*) FROM records")) != 1007) throw new Exception("Published rows lost.");
                }
                Console.WriteLine("PASS actual owned child Process.Kill at " + stage + "; exit=" + child.ExitCode + "; objects=" + expectedObjects +
                    "; staging=" + staging + "; fresh production preview refuses without writes" + (stage == "published" ? "; 1007 rows schema/SHA256 verified" : ""));
            }
            finally
            {
                if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); }
                await stdout; await stderr;
                if (File.Exists(marker)) File.Delete(marker);
            }
        }
        Console.WriteLine("PASS process kill only; engine crash/power loss untested");
    }
}
