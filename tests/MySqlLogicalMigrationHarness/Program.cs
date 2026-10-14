using MySqlConnector;
using Npgsql;
using MySqlPunk.Core.Models;
using MySqlPunk.Core.Services;

var password = Environment.GetEnvironmentVariable("MYSQLPUNK_TEST_PASSWORD") ?? throw new Exception("Missing fixture password.");
var pgPort = int.Parse(args[0]);
var myPorts = new[] { int.Parse(args[1]), int.Parse(args[2]) };
if (args.Length > 3 && args[3] == "crash-child")
{
    await MySqlMigrationRegressions.CrashChildAsync(pgPort, myPorts[0], args[4], args[5], args[6], args[7]);
    return;
}
string PgConnection(string database) => new NpgsqlConnectionStringBuilder
{
    Host = "127.0.0.1", Port = pgPort, Username = "postgres", Password = password,
    Database = database, SslMode = SslMode.Disable, Pooling = false, Timeout = 3
}.ConnectionString;
string MyConnection(int port, string database = "") => new MySqlConnectionStringBuilder
{
    Server = "127.0.0.1", Port = (uint)port, UserID = "root", Password = password,
    Database = database, SslMode = MySqlSslMode.Disabled, Pooling = false, ConnectionTimeout = 3
}.ConnectionString;
await using (var connection = new NpgsqlConnection(PgConnection("postgres")))
{
    await connection.OpenAsync(); Console.WriteLine("Actual PostgreSQL=" + connection.PostgreSqlVersion);
}
foreach (var port in myPorts)
{
    await using var connection = new MySqlConnection(MyConnection(port)); await connection.OpenAsync();
    Console.WriteLine("Actual MySQL family=" + connection.ServerVersion);
}
if (args.Contains("probe")) return;
if (args.Length > 3 && args[3] == "boundaries")
{
    foreach (var port in myPorts) await new MySqlMigrationRegressions(pgPort, port).RunBoundariesAsync(8388608, true);
    foreach (var port in new[] { int.Parse(args[4]), int.Parse(args[5]) })
        await new MySqlMigrationRegressions(pgPort, port).RunBoundariesAsync(65536, false);
    return;
}
async Task PgSql(string database, string sql)
{
    await using var connection = new NpgsqlConnection(PgConnection(database)); await connection.OpenAsync();
    await using var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync();
}
ConnectionProfile Pg(string database) => new()
{
    Provider = DatabaseProviderKind.PostgreSql, Host = "127.0.0.1", Port = pgPort,
    Username = "postgres", Password = password, Database = database, TlsMode = ConnectionTlsMode.Disabled
};
await PgSql("postgres", "CREATE DATABASE seed;CREATE DATABASE mysql_back;CREATE DATABASE maria_back;");
await PgSql("seed", """
    CREATE TABLE records(id bigint NOT NULL, number bigint NOT NULL, note text COLLATE "C", payload bytea,
        CONSTRAINT records_pkey PRIMARY KEY(id,number));
    CREATE INDEX records_number ON records(number ASC NULLS FIRST);
    CREATE TABLE empty_table(id bigint, note text COLLATE "C", payload bytea);
    INSERT INTO records SELECT number,number%2,CASE WHEN number%3=0 THEN NULL ELSE '繁中😀 '||number END,
        CASE WHEN number%2=0 THEN NULL ELSE decode('00ff','hex') END FROM generate_series(1,1000) number;
    INSERT INTO records VALUES(-9223372036854775808,0,NULL,''::bytea),(9223372036854775807,0,'',NULL),
        (-1,0,'a',''::bytea),(-2,0,'a ',NULL),(-3,0,'ABC',NULL),(-4,0,'abc',NULL),(-5,0,'é',NULL);
    """);
for (var index = 0; index < myPorts.Length; index++)
{
    var port = myPorts[index];
    var major = index == 0 ? 8 : 11;
    var collation = index == 0 ? "utf8mb4_0900_bin" : "utf8mb4_nopad_bin";
    await using (var connection = new MySqlConnection(MyConnection(port)))
    {
        await connection.OpenAsync(); await using var command = connection.CreateCommand();
        command.CommandText = "CREATE DATABASE target CHARACTER SET utf8mb4 COLLATE " + collation;
        await command.ExecuteNonQueryAsync();
    }
    var target = new ConnectionProfile
    {
        Provider = DatabaseProviderKind.MySql, Host = "127.0.0.1", Port = port,
        Username = "root", Password = password, Database = "target", TlsMode = ConnectionTlsMode.Disabled
    };
    var preview = await LogicalMigrationService.PreviewAsync(Pg("seed"), target, major);
    Console.WriteLine("PG -> " + major + " preview=" + preview.CanStart + " " + string.Join(";", preview.Blockers));
    if (!preview.CanStart) throw new Exception("Preflight refused.");
    var result = await LogicalMigrationService.RunAsync(Pg("seed"), target, major, preview, "target");
    Console.WriteLine(result.Outcome + " " + result.Report);
    if (result.Outcome != LogicalMigrationOutcome.Verified) throw new Exception("Target migration failed.");
    var back = Pg(index == 0 ? "mysql_back" : "maria_back");
    preview = await LogicalMigrationService.PreviewAsync(target, back, 16);
    Console.WriteLine(major + " -> PG preview=" + preview.CanStart + " " + string.Join(";", preview.Blockers));
    if (!preview.CanStart) throw new Exception("Reverse preflight refused.");
    result = await LogicalMigrationService.RunAsync(target, back, 16, preview, back.Database);
    if (result.Outcome != LogicalMigrationOutcome.Verified) throw new Exception(result.Report);
    await using (var connection = new MySqlConnection(MyConnection(port, "target")))
    {
        await connection.OpenAsync(); await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO records(id,number) VALUES(1,1)";
        try { await command.ExecuteNonQueryAsync(); throw new Exception("Duplicate primary key accepted."); }
        catch (MySqlException exception) when (exception.Number == 1062) { }
    }
    Console.WriteLine("PASS actual PG16 <-> " + (index == 0 ? "MySQL8.4" : "MariaDB11.4") + "; 1007 rows + empty table, integer composite PK/index, UTF8/emoji/NULL/empty bytes/text/Int64 extrema, production schema/SHA256 verification");
    async Task MySql(string database, string sql)
    {
        await using var connection = new MySqlConnection(MyConnection(port, database)); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync();
    }
    async Task<long> Count(string database, string sql)
    {
        await using var connection = new MySqlConnection(MyConnection(port, database)); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
    async Task<ConnectionProfile> Empty(string database)
    {
        await MySql("", "CREATE DATABASE " + database + " CHARACTER SET utf8mb4 COLLATE " + collation);
        var profile = target.Clone(); profile.Database = database; return profile;
    }
    var nonempty = await LogicalMigrationService.PreviewAsync(Pg("seed"), target, major);
    if (nonempty.CanStart) throw new Exception("Existing target accepted.");
    foreach (var scenario in new[] { "cancelled", "batch_cancel", "batch_fault", "verify_fault", "collision", "ack_lost", "disconnect" })
    {
        var isolated = await Empty(scenario);
        var approved = await LogicalMigrationService.PreviewAsync(Pg("seed"), isolated, major);
        if (!approved.CanStart) throw new Exception("Fault fixture preflight failed.");
        using var cancellation = new CancellationTokenSource();
        var fired = false;
        result = await LogicalMigrationService.RunWithMySqlObserverAsync(Pg("seed"), isolated, major, approved, cancellation.Token,
            (stage, connection, transaction) =>
            {
                if (fired) return;
                if (scenario == "cancelled" && stage == "staging") { fired = true; cancellation.Cancel(); }
                if (scenario == "batch_cancel" && stage == "batch") { fired = true; cancellation.Cancel(); }
                if (scenario == "batch_fault" && stage == "batch") { fired = true; throw new IOException("Synthetic batch failure."); }
                if (scenario == "verify_fault" && stage == "verify")
                {
                    fired = true;
                    using var lookup = connection.CreateCommand(); lookup.Transaction = transaction;
                    lookup.CommandText = "SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND LEFT(TABLE_NAME,4)='_mp_' ORDER BY TABLE_NAME DESC LIMIT 1";
                    var table = Convert.ToString(lookup.ExecuteScalar());
                    using var damage = connection.CreateCommand(); damage.Transaction = transaction;
                    damage.CommandText = "DELETE FROM `" + table + "`"; damage.ExecuteNonQuery();
                }
                if (scenario == "collision" && stage == "publish")
                {
                    fired = true; using var command = connection.CreateCommand();
                    command.CommandText = "CREATE TABLE records(id bigint) ENGINE=InnoDB"; command.ExecuteNonQuery();
                }
                if (scenario == "ack_lost" && stage == "published") { fired = true; throw new IOException("Synthetic lost acknowledgement after real RENAME."); }
                if (scenario == "disconnect" && stage == "published") { fired = true; connection.Close(); }
            });
        var expected = scenario is "collision" or "ack_lost" or "disconnect" ? LogicalMigrationOutcome.Uncertain : LogicalMigrationOutcome.StagingRemains;
        if (!fired || result.Outcome != expected || !result.RetryBlocked) throw new Exception("Fault outcome mismatch: " + scenario + " " + result.Report);
        if (scenario != "ack_lost" && !result.Report.Contains("_mp_", StringComparison.Ordinal)) throw new Exception("Missing staging residue report.");
        if (scenario is "ack_lost" or "disconnect")
        {
            if (await Count(scenario, "SELECT count(*) FROM records") != 1007 ||
                await Count(scenario, "SELECT count(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND LEFT(TABLE_NAME,4)='_mp_'") != 0)
                throw new Exception("Real publish did not precede acknowledgement fault.");
        }
        else
        {
            var stagingCount = await Count(scenario, "SELECT count(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND LEFT(TABLE_NAME,4)='_mp_'");
            if (stagingCount != (scenario == "cancelled" ? 1 : 2)) throw new Exception("Staging residue count.");
            if (await Count(scenario, "SELECT count(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='empty_table'") != 0)
                throw new Exception("Partial publish leaked first target table.");
            if (scenario != "collision")
            {
                var stageNames = new List<string>();
                await using (var connection = new MySqlConnection(MyConnection(port, scenario)))
                {
                    await connection.OpenAsync(); await using var command = connection.CreateCommand();
                    command.CommandText = "SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND LEFT(TABLE_NAME,4)='_mp_'";
                    await using var reader = await command.ExecuteReaderAsync();
                    while (await reader.ReadAsync()) stageNames.Add(reader.GetString(0));
                }
                foreach (var name in stageNames)
                    if (await Count(scenario, "SELECT count(*) FROM `" + name + "`") != 0) throw new Exception("Failed staging DML did not roll back.");
            }
        }
        var retry = await LogicalMigrationService.PreviewAsync(Pg("seed"), isolated, major);
        if (retry.CanStart) throw new Exception("Blind retry with residue/published tables accepted.");
        Console.WriteLine("PASS real " + major + " " + scenario + " => " + result.Outcome + "; retry blocked; owned residue/publish state checked");
    }
    var stale = await Empty("stale");
    var oldPreview = await LogicalMigrationService.PreviewAsync(Pg("seed"), stale, major);
    await PgSql("seed", "UPDATE records SET note='changed_" + index + "' WHERE id=1;");
    result = await LogicalMigrationService.RunAsync(Pg("seed"), stale, major, oldPreview, "stale");
    if (result.Outcome != LogicalMigrationOutcome.Refused || await Count("stale", "SELECT count(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE()") != 0)
        throw new Exception("Stale snapshot wrote target.");
    foreach (var (database, definition) in new[]
    {
        ("unsigned_source", "CREATE TABLE records(id bigint unsigned) ENGINE=InnoDB"),
        ("default_source", "CREATE TABLE records(id bigint DEFAULT 1) ENGINE=InnoDB"),
        ("prefix_source", "CREATE TABLE records(note longtext, KEY records_note(note(12))) ENGINE=InnoDB"),
        ("trigger_source", "CREATE TABLE records(id bigint) ENGINE=InnoDB;CREATE TRIGGER audit_insert BEFORE INSERT ON records FOR EACH ROW SET NEW.id=1"),
        ("procedure_source", "CREATE TABLE records(id bigint) ENGINE=InnoDB;CREATE PROCEDURE hidden_logic() SELECT 1"),
        ("view_source", "CREATE TABLE records(id bigint) ENGINE=InnoDB;CREATE VIEW hidden_view AS SELECT id FROM records"),
        ("pad_source", "CREATE TABLE records(note longtext COLLATE utf8mb4_bin) ENGINE=InnoDB")
    })
    {
        var bad = await Empty(database); await MySql(database, definition);
        var rejected = await LogicalMigrationService.PreviewAsync(bad, Pg("mysql_back"), 16);
        if (rejected.CanStart || !rejected.Blockers.Any(reason => reason.StartsWith("來源：", StringComparison.Ordinal)))
            throw new Exception("Catalog feature silently accepted: " + database);
        Console.WriteLine("PASS actual catalog refuses " + major + " " + database);
    }
    Console.WriteLine("PASS stale same-count source refused before DDL; existing targets refused; real fault/residue and unsupported catalog checks for " + major);
    var faultDatabase = "source_fault_" + index;
    await PgSql("postgres", "CREATE DATABASE " + faultDatabase);
    var pgFault = Pg(faultDatabase);
    preview = await LogicalMigrationService.PreviewAsync(target, pgFault, 16);
    await MySql("target", "UPDATE records SET note='mysql_source_changed' WHERE id=1");
    result = await LogicalMigrationService.RunAsync(target, pgFault, 16, preview, faultDatabase);
    if (result.Outcome != LogicalMigrationOutcome.RolledBack) throw new Exception("MySQL source stale snapshot not refused transactionally.");
    preview = await LogicalMigrationService.PreviewAsync(target, pgFault, 16);
    result = await LogicalMigrationService.RunWithObserverAsync(target, pgFault, 16, preview, faultDatabase, CancellationToken.None,
        (stage, connection, transaction) => { if (stage == "batch") throw new IOException("Synthetic PostgreSQL target batch failure."); });
    if (result.Outcome != LogicalMigrationOutcome.RolledBack || result.RetryBlocked) throw new Exception("PG target rollback semantics changed.");
    await using (var connection = new NpgsqlConnection(PgConnection(faultDatabase)))
    {
        await connection.OpenAsync(); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public'";
        if (Convert.ToInt64(await command.ExecuteScalarAsync()) != 0) throw new Exception("PG target schema residue after rollback.");
    }
    var oversize = await Empty("oversize_source");
    await MySql("oversize_source", "CREATE TABLE records(payload longblob) ENGINE=InnoDB; INSERT INTO records VALUES(REPEAT('x',16777216))");
    try
    {
        await LogicalMigrationService.PreviewAsync(oversize, pgFault, 16);
        throw new Exception("Oversize BLOB accepted.");
    }
    catch (InvalidOperationException exception) when (exception.Message.Contains("1 MiB", StringComparison.Ordinal)) { }
    var preCancel = await Empty("pre_cancel");
    preview = await LogicalMigrationService.PreviewAsync(Pg("seed"), preCancel, major);
    using (var cancellation = new CancellationTokenSource())
    {
        cancellation.Cancel();
        try { await LogicalMigrationService.RunAsync(Pg("seed"), preCancel, major, preview, "pre_cancel", cancellation.Token); throw new Exception("Pre-cancel ignored."); }
        catch (OperationCanceledException) { }
    }
    if (await Count("pre_cancel", "SELECT count(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE()") != 0)
        throw new Exception("Pre-cancel wrote objects.");
    Console.WriteLine("PASS actual " + major + " source snapshot invalidation, PG partial write rollback with public objects=0, 16 MiB bounded BLOB refusal, pre-cancel empty target");
}
await PgSql("postgres", "CREATE DATABASE custom_pk;CREATE DATABASE text_pk;CREATE DATABASE primary_named_source;CREATE DATABASE primary_named_target;");
await PgSql("primary_named_source", "CREATE TABLE records(id bigint NOT NULL, CONSTRAINT \"PRIMARY\" PRIMARY KEY(id));INSERT INTO records VALUES(1);");
var namedPreview = await LogicalMigrationService.PreviewAsync(Pg("primary_named_source"), Pg("primary_named_target"), 16);
if (!namedPreview.CanStart || !namedPreview.Statements.Any(sql => sql.Contains("CONSTRAINT \"PRIMARY\"", StringComparison.Ordinal)))
    throw new Exception("PostgreSQL user-defined PRIMARY name misidentified as MySQL intrinsic name.");
var namedResult = await LogicalMigrationService.RunAsync(Pg("primary_named_source"), Pg("primary_named_target"), 16, namedPreview, "primary_named_target");
if (namedResult.Outcome != LogicalMigrationOutcome.Verified) throw new Exception(namedResult.Report);
await PgSql("primary_named_target", "INSERT INTO records VALUES(1) ON CONFLICT ON CONSTRAINT \"PRIMARY\" DO NOTHING;");
Console.WriteLine("PASS actual PG catalog preserves user-defined PRIMARY constraint, DDL/schema/SHA256 and ON CONFLICT; only MySQL intrinsic PRIMARY maps to PG default");
await PgSql("custom_pk", "CREATE TABLE records(id bigint NOT NULL, CONSTRAINT explicit_records_pk PRIMARY KEY(id));");
await PgSql("text_pk", "CREATE TABLE records(label text COLLATE \"C\" NOT NULL, CONSTRAINT records_pkey PRIMARY KEY(label));");
foreach (var source in new[] { "custom_pk", "text_pk" })
{
    await using var connection = new MySqlConnection(MyConnection(myPorts[0])); await connection.OpenAsync();
    await using var command = connection.CreateCommand(); command.CommandText = "CREATE DATABASE " + source + " CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_bin";
    await command.ExecuteNonQueryAsync();
    var target = new ConnectionProfile { Provider = DatabaseProviderKind.MySql, Host = "127.0.0.1", Port = myPorts[0], Username = "root", Password = password, Database = source, TlsMode = ConnectionTlsMode.Disabled };
    var denied = await LogicalMigrationService.PreviewAsync(Pg(source), target, 8);
    if (denied.CanStart) throw new Exception("PG named/text key semantics lost silently.");
    Console.WriteLine("PASS PG -> MySQL refuses " + source + " before DDL");
}
