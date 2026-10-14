using System.Data.Common;
using Microsoft.Data.Sqlite;
using MySqlPunk.Core.Models;
using MySqlPunk.Core.Providers;
using MySqlPunk.Core.Services;

internal static class LogicalMigrationTests
{
    public static async Task RunAsync()
    {
        foreach (var version in new[] { "8.0.46", "8.4.11", "10.11.19-MariaDB-ubu2204", "11.4.13-MariaDB-ubu2404" })
            Check(LogicalMigrationService.SupportedMySqlVersion(version), "Explicit supported MySQL family version " + version);
        foreach (var version in new[] { "8.1.0", "8.0.46-Percona", "8.4.11-other", "10.6.0-MariaDB", "11.5.0-MariaDB", "11.4.13-MariaDB-other fork", "unknown" })
            Check(!LogicalMigrationService.SupportedMySqlVersion(version), "Unknown version/fork fails closed " + version);
        Check(LogicalMigrationService.SupportedMySqlIdentity("8.0.46", "MySQL Community Server - GPL") &&
            LogicalMigrationService.SupportedMySqlIdentity("10.11.19-MariaDB", "mariadb.org binary distribution"), "Official family identity matches");
        Check(!LogicalMigrationService.SupportedMySqlIdentity("8.0.46", "Percona Server (GPL)") &&
            !LogicalMigrationService.SupportedMySqlIdentity("10.11.19-MariaDB", "unknown fork"), "Bare version with unknown vendor fails closed");
        Check(LogicalMigrationService.PostgreSqlIndexKeyFits(8192, new object?[] { new string('a', 1996), 1L }), "PG conservative tuple boundary fits");
        Check(!LogicalMigrationService.PostgreSqlIndexKeyFits(8192, new object?[] { new string('a', 1997), 1L }), "PG tuple alignment boundary rejects");
        Check(!LogicalMigrationService.PostgreSqlIndexKeyFits(8192, new object?[] { new string('中', 700) }), "PG UTF8 byte budget not character count");
        Check(!LogicalMigrationService.PostgreSqlIndexKeyFits(8192, new object?[] { new string('a', 1000), new string('a', 1000) }), "PG composite budget aggregates columns");
        Check(!LogicalMigrationService.PostgreSqlIndexKeyFits(4096, new object?[] { 1L }) && !LogicalMigrationService.PostgreSqlIndexKeyFits(0, new object?[] { 1L }), "PG unknown/custom page fails closed");
        Check(LogicalMigrationService.PostgreSqlIndexKeyFits(8192, new object?[] { null, Array.Empty<byte>(), "😀", 1L }), "PG NULL/empty/multibyte key costs");
        var root = Path.Combine(Path.GetTempPath(), "mysqlpunk-logical-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Profile(Path.Combine(root, "source.db"));
            var target = Profile(Path.Combine(root, "target.db"));
            CreateEmpty(target.Database);
            using var session = DatabaseProviderFactory.Create(source);
            await session.ExecuteAsync(source.Database, "CREATE TABLE sample (number INTEGER, payload BLOB NOT NULL) STRICT;");
            await session.ExecuteAsync(source.Database, "WITH RECURSIVE numbers(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM numbers WHERE n<1001) INSERT INTO sample SELECT n, X'00FF01' FROM numbers;");
            await session.ExecuteAsync(source.Database, "INSERT INTO sample VALUES (NULL,X''),(-9223372036854775808,X'FF'),(9223372036854775807,X'00'),(1,X'00FF01');");
            var preview = await LogicalMigrationService.PreviewAsync(source, target, 3);
            Check(preview.CanStart && preview.Tables.Single().Rows == 1005 && preview.Statements.Single().EndsWith("STRICT;"), "Real SQLite schema/data preview " + preview.SourceVersion + ": " + string.Join(";", preview.Blockers));
            var denied = await LogicalMigrationService.RunAsync(source, target, 3, preview, "wrong");
            Check(denied.Outcome == LogicalMigrationOutcome.Refused && await ObjectCountAsync(target) == 0, "Confirmation before writing");
            foreach (var fault in new[] { "write", "content", "schema", "cancel", "commit_ack_lost" })
            {
                using var cancellation = new CancellationTokenSource();
                var result = await LogicalMigrationService.RunWithObserverAsync(source, target, 3, preview, target.Database,
                    cancellation.Token, (stage, connection, transaction) =>
                    {
                        if (fault == "write" && stage == "batch") throw new InvalidOperationException("injected write failure");
                        if (fault == "cancel" && stage == "batch") cancellation.Cancel();
                        if (fault == "commit_ack_lost" && stage == "committed") throw new InvalidOperationException("injected lost acknowledgement");
                        if (stage != "verify" || fault is not ("content" or "schema")) return;
                        using var command = connection.CreateCommand(); command.Transaction = transaction;
                        command.CommandText = fault == "content" ? "UPDATE sample SET number=42;" : "CREATE TABLE unexpected (id INTEGER) STRICT;";
                        command.ExecuteNonQuery();
                    });
                Check(result.Outcome == (fault == "commit_ack_lost" ? LogicalMigrationOutcome.Uncertain : LogicalMigrationOutcome.RolledBack), "Real transaction fault " + fault);
                if (fault == "commit_ack_lost")
                {
                    Check(result.RetryBlocked && await ObjectCountAsync(target) == 1, "Lost acknowledgement after actual commit forbids retry");
                    Check(!(await LogicalMigrationService.PreviewAsync(source, target, 3)).CanStart, "Committed target cannot resume or duplicate rows");
                    target = Profile(Path.Combine(root, "fresh.db")); CreateEmpty(target.Database);
                    preview = await LogicalMigrationService.PreviewAsync(source, target, 3);
                }
                else Check(!result.RetryBlocked && await ObjectCountAsync(target) == 0, "Partial writes and DDL actually rolled back " + fault);
            }
            await session.ExecuteAsync(source.Database, "UPDATE sample SET number=44 WHERE number=1;");
            var changed = await LogicalMigrationService.RunAsync(source, target, 3, preview, target.Database);
            Check(changed.Outcome == LogicalMigrationOutcome.RolledBack && await ObjectCountAsync(target) == 0, "Same row count but changed content invalidates approval");
            preview = await LogicalMigrationService.PreviewAsync(source, target, 3);
            var success = await LogicalMigrationService.RunAsync(source, target, 3, preview, target.Database);
            Check(success.Outcome == LogicalMigrationOutcome.Verified && success.Rows == 1005 && success.RetryBlocked, "Verified actual SQLite commit");
            var reverse = Profile(Path.Combine(root, "reverse.db")); CreateEmpty(reverse.Database);
            var reversePreview = await LogicalMigrationService.PreviewAsync(target, reverse, 3);
            var reverseResult = await LogicalMigrationService.RunAsync(target, reverse, 3, reversePreview, reverse.Database);
            Check(reverseResult.Outcome == LogicalMigrationOutcome.Verified && reversePreview.Tables.Single().ContentHash == preview.Tables.Single().ContentHash, "Reverse direction preserves Int64 limits, bytes, NULL and duplicates");
            Check(!(await LogicalMigrationService.PreviewAsync(source, reverse, 17)).CanStart, "Selected version mismatch denied");
            var missing = Profile(Path.Combine(root, "missing.db"));
            try { await LogicalMigrationService.PreviewAsync(source, missing, 3); throw new Exception("Missing target must be denied"); }
            catch (SqliteException) { Check(!File.Exists(missing.Database), "No automatic creation of missing target"); }
            var unsupported = source.Clone(); unsupported.Provider = DatabaseProviderKind.MySql;
            Check(!(await LogicalMigrationService.PreviewAsync(unsupported, reverse, 3)).CanStart, "Unsupported engine refused without opening a network connection");
            var large = Profile(Path.Combine(root, "large.db"));
            using (var owner = DatabaseProviderFactory.Create(large))
            {
                await owner.ExecuteAsync(large.Database, "CREATE TABLE large (payload BLOB) STRICT; INSERT INTO large VALUES (zeroblob(33554432));");
                var empty = Profile(Path.Combine(root, "large-target.db")); CreateEmpty(empty.Database);
                var allocatedBefore = GC.GetTotalAllocatedBytes(true);
                try { await LogicalMigrationService.PreviewAsync(large, empty, 3); throw new Exception("Oversize BLOB must be denied"); }
                catch (InvalidOperationException error) { Check(error.Message.Contains("1 MiB"), "Length refusal before materialization"); }
                Check(GC.GetTotalAllocatedBytes(true) - allocatedBefore < 8 * 1024 * 1024, "32 MiB BLOB is not materialized in managed memory");
                Check(await ObjectCountAsync(empty) == 0, "Oversize read leaves target empty");
                await owner.ExecuteAsync(large.Database, "UPDATE large SET payload=zeroblob(1048576);");
                var boundary = await LogicalMigrationService.PreviewAsync(large, empty, 3);
                Check(boundary.CanStart && (await LogicalMigrationService.RunAsync(large, empty, 3, boundary, empty.Database)).Outcome == LogicalMigrationOutcome.Verified, "1 MiB inclusive boundary transfers in bounded chunks");
            }
            foreach (var definition in new[]
            {
                "CREATE TABLE bad (id INTEGER PRIMARY KEY, payload BLOB) STRICT;",
                "CREATE TABLE bad (id INTEGER DEFAULT 1, payload BLOB) STRICT;",
                "CREATE TABLE bad (id INTEGER CHECK(id>0), payload BLOB) STRICT;",
                "CREATE TABLE bad (id REAL, payload BLOB) STRICT;",
                "CREATE TABLE bad (id TEXT COLLATE NOCASE, payload BLOB) STRICT;",
                "CREATE TABLE bad (id INTEGER, payload BLOB);",
                "CREATE TABLE bad (id INTEGER REFERENCES sample(number), payload BLOB) STRICT;",
                "CREATE TABLE bad (id INTEGER, payload BLOB) STRICT; CREATE INDEX bad_index ON bad(id DESC);",
                "CREATE TABLE bad (id INTEGER NOT NULL, PRIMARY KEY(id)) STRICT;",
                "CREATE TABLE bad (id TEXT NOT NULL, PRIMARY KEY(id)) STRICT, WITHOUT ROWID; CREATE UNIQUE INDEX bad_index ON bad(id);",
                "CREATE TABLE bad (id TEXT NOT NULL, PRIMARY KEY(id)) STRICT, WITHOUT ROWID; CREATE INDEX bad_index ON bad(lower(id));",
                "CREATE TABLE bad (id TEXT NOT NULL, PRIMARY KEY(id)) STRICT, WITHOUT ROWID; CREATE INDEX bad_index ON bad(id) WHERE id<>'';",
                "CREATE TABLE bad (id INTEGER, payload BLOB) STRICT; CREATE TRIGGER bad_trigger AFTER INSERT ON bad BEGIN SELECT 1; END;",
                "CREATE VIEW bad AS SELECT 1;"
            })
            {
                var blocked = Profile(Path.Combine(root, "blocked-" + Guid.NewGuid().ToString("N") + ".db"));
                using var owner = DatabaseProviderFactory.Create(blocked); await owner.ExecuteAsync(blocked.Database, definition);
                var empty = Profile(Path.Combine(root, "empty-" + Guid.NewGuid().ToString("N") + ".db")); CreateEmpty(empty.Database);
                Check(!(await LogicalMigrationService.PreviewAsync(blocked, empty, 3)).CanStart && await ObjectCountAsync(empty) == 0, "Catalog blocks unsupported SQLite semantics");
            }
            await TextKeysAsync(root);
        }
        finally { Directory.Delete(root, true); }
    }

    public static async Task PostgreSqlLiveAsync()
    {
        if (Environment.GetEnvironmentVariable("MYSQLPUNK_LIVE_TESTS") != "1") throw new InvalidOperationException("Only existing isolated live fixtures are allowed.");
        var root = Path.Combine(Path.GetTempPath(), "mysqlpunk-logical-pg-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var postgres = new ConnectionProfile
        {
            Provider = DatabaseProviderKind.PostgreSql, Name = "Isolated logical migration", Host = Required("MYSQLPUNK_POSTGRES_HOST"),
            Port = int.Parse(Required("MYSQLPUNK_POSTGRES_PORT")), Username = Environment.GetEnvironmentVariable("MYSQLPUNK_POSTGRES_USER") ?? "postgres",
            Password = Required("MYSQLPUNK_POSTGRES_PASSWORD"), Database = "mysqlpunk_migration_" + Guid.NewGuid().ToString("N"), TlsMode = ConnectionTlsMode.Disabled
        };
        using var admin = DatabaseProviderFactory.Create(postgres);
        bool created = false;
        try
        {
            await admin.ExecuteAsync("postgres", "CREATE DATABASE \"" + postgres.Database + "\";"); created = true;
            await PostgreSqlKeyBudgetAsync(root, postgres, admin);
            await PostgreSqlPrimaryKeyNamesAsync(root, postgres, admin);
            var sqlite = Profile(Path.Combine(root, "source.db"));
            using var origin = DatabaseProviderFactory.Create(sqlite);
            await origin.ExecuteAsync(sqlite.Database, "CREATE TABLE sample (number INTEGER, payload BLOB NOT NULL) STRICT; INSERT INTO sample VALUES (-9223372036854775808,X'00FF'),(9223372036854775807,X''),(NULL,X'01'),(NULL,X'01');" + BusinessSql);
            var forward = await LogicalMigrationService.PreviewAsync(sqlite, postgres, 16);
            Check(forward.CanStart, "Real PostgreSQL catalog allows restricted empty target: " + string.Join(";", forward.Blockers));
            Check((await LogicalMigrationService.RunAsync(sqlite, postgres, 16, forward, postgres.Database)).Outcome == LogicalMigrationOutcome.Verified, "SQLite -> PostgreSQL real typed transfer");
            var pgOrder = await admin.ExecuteAsync(postgres.Database, "SELECT label FROM customers ORDER BY label;");
            Check(pgOrder.Rows.Select(row => Convert.ToString(row[0])).SequenceEqual(new[] { "A", "a", "é", "臺灣😀" }), "Real PostgreSQL C collation retains binary Unicode ordering");
            try { await admin.ExecuteAsync(postgres.Database, "INSERT INTO lines VALUES(1,'A');"); throw new Exception("PG composite PK must reject duplicate"); }
            catch (Npgsql.PostgresException error) { Check(error.SqlState == "23505", "Real PG primary key enforced"); }
            var oversizedTarget = Profile(Path.Combine(root, "pg-oversized-target.db")); CreateEmpty(oversizedTarget.Database);
            await admin.ExecuteAsync(postgres.Database, "CREATE TABLE bounded_payload(payload bytea); INSERT INTO bounded_payload VALUES(decode(repeat('FF',33554432),'hex')); ");
            var allocatedBefore = GC.GetTotalAllocatedBytes(true);
            try { await LogicalMigrationService.PreviewAsync(postgres, oversizedTarget, 3); throw new Exception("Oversize PG BLOB must be refused"); }
            catch (InvalidOperationException error) { Check(error.Message.Contains("1 MiB"), "PG length refusal precedes streaming materialization"); }
            Check(GC.GetTotalAllocatedBytes(true) - allocatedBefore < 8 * 1024 * 1024 && await ObjectCountAsync(oversizedTarget) == 0, "Real PG 32 MiB BLOB is not transported as a full managed field and target stays empty");
            await admin.ExecuteAsync(postgres.Database, "UPDATE bounded_payload SET payload=decode(repeat('FF',1048576),'hex');");
            var boundary = await LogicalMigrationService.PreviewAsync(postgres, oversizedTarget, 3);
            Check(boundary.CanStart && (await LogicalMigrationService.RunAsync(postgres, oversizedTarget, 3, boundary, oversizedTarget.Database)).Outcome == LogicalMigrationOutcome.Verified, "Real PG 1 MiB boundary streams and verifies into SQLite");
            await admin.ExecuteAsync(postgres.Database, "DROP TABLE bounded_payload;");
            var reverse = Profile(Path.Combine(root, "target.db")); CreateEmpty(reverse.Database);
            var backward = await LogicalMigrationService.PreviewAsync(postgres, reverse, 3);
            Check(backward.CanStart && backward.Tables.Select(table => table.ContentHash).SequenceEqual(forward.Tables.Select(table => table.ContentHash)), "PostgreSQL -> SQLite real catalog and content");
            Check((await LogicalMigrationService.RunAsync(postgres, reverse, 3, backward, reverse.Database)).Outcome == LogicalMigrationOutcome.Verified, "PostgreSQL -> SQLite verified commit");
            await CheckBusinessAsync(reverse);
            using var pgOwner = DatabaseProviderFactory.Create(postgres);
            foreach (var mutation in new[]
            {
                ("CREATE INDEX extra ON sample((number%2));", "DROP INDEX extra;"),
                ("CREATE INDEX extra ON sample(number);", "DROP INDEX extra;"),
                ("CREATE UNIQUE INDEX extra ON customers(label);", "DROP INDEX extra;"),
                ("CREATE INDEX extra ON customers(label COLLATE \"POSIX\");", "DROP INDEX extra;"),
                ("ALTER TABLE customers ALTER COLUMN label TYPE text COLLATE \"default\";", "ALTER TABLE customers ALTER COLUMN label TYPE text COLLATE \"C\";"),
                ("ALTER TABLE sample ALTER COLUMN number SET DEFAULT 1;", "ALTER TABLE sample ALTER COLUMN number DROP DEFAULT;"),
                ("CREATE SEQUENCE extra_sequence;", "DROP SEQUENCE extra_sequence;"),
                ("SELECT lo_create(1736281931);", "SELECT lo_unlink(1736281931);"),
                ("CREATE COLLATION extra_collation FROM \"C\";", "DROP COLLATION extra_collation;"),
                ("CREATE VIEW extra_view AS SELECT 1;", "DROP VIEW extra_view;"),
                ("CREATE FUNCTION extra_proc() RETURNS integer LANGUAGE SQL AS 'SELECT 1';", "DROP FUNCTION extra_proc();"),
                ("ALTER TABLE sample ENABLE ROW LEVEL SECURITY;", "ALTER TABLE sample DISABLE ROW LEVEL SECURITY;")
            })
            {
                await pgOwner.ExecuteAsync(postgres.Database, mutation.Item1);
                var empty = Profile(Path.Combine(root, "empty-" + Guid.NewGuid().ToString("N") + ".db")); CreateEmpty(empty.Database);
                var denied = await LogicalMigrationService.PreviewAsync(postgres, empty, 3);
                Check(!denied.CanStart, "Production PostgreSQL catalog rejects feature: " + mutation.Item1);
                await pgOwner.ExecuteAsync(postgres.Database, mutation.Item2);
                Check((await LogicalMigrationService.PreviewAsync(postgres, empty, 3)).CanStart, "Catalog no longer blocks after fixture feature removal");
            }
        }
        finally
        {
            Npgsql.NpgsqlConnection.ClearAllPools();
            if (created) await admin.ExecuteAsync("postgres", "DROP DATABASE \"" + postgres.Database + "\";");
            Directory.Delete(root, true);
        }
    }

    private static async Task PostgreSqlPrimaryKeyNamesAsync(string root, ConnectionProfile postgres, IDatabaseSession admin)
    {
        foreach (var scenario in new[] { "table", "index", "truncation" })
        {
            var source = Profile(Path.Combine(root, "pk-collision-" + scenario + ".db"));
            using var owner = DatabaseProviderFactory.Create(source);
            var longPrefix = new string('a', 58);
            var sql = scenario == "truncation"
                ? "CREATE TABLE " + longPrefix + "a(id INTEGER NOT NULL,PRIMARY KEY(id)) STRICT, WITHOUT ROWID; CREATE TABLE " + longPrefix + "b(id INTEGER NOT NULL,PRIMARY KEY(id)) STRICT, WITHOUT ROWID;"
                : "CREATE TABLE records(id INTEGER NOT NULL,label TEXT NOT NULL,PRIMARY KEY(id)) STRICT, WITHOUT ROWID;" +
                    (scenario == "table" ? "CREATE TABLE records_pkey(id INTEGER) STRICT;" : "CREATE INDEX records_pkey ON records(label);");
            await owner.ExecuteAsync(source.Database, sql);
            var denied = await LogicalMigrationService.PreviewAsync(source, postgres, 16);
            Check(!denied.CanStart && denied.Blockers.Any(reason => reason.Contains("名稱空間衝突")), "Real catalog detects PostgreSQL PK name collision: " + scenario);
            Check((await LogicalMigrationService.RunAsync(source, postgres, 16, denied, postgres.Database)).Outcome == LogicalMigrationOutcome.Refused, "Collision refused before DDL");
            var count = await admin.ExecuteAsync(postgres.Database, "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public';");
            Check(Convert.ToInt64(count.Rows.Single()[0]) == 0, "Collision leaves PostgreSQL target empty");
            Console.WriteLine("PASS real PG primary key namespace collision: " + scenario);
        }
        var target = postgres.Clone(); target.Database = "mysqlpunk_pk_names_" + Guid.NewGuid().ToString("N");
        await admin.ExecuteAsync("postgres", "CREATE DATABASE \"" + target.Database + "\";");
        using var targetOwner = DatabaseProviderFactory.Create(target);
        try
        {
            await admin.ExecuteAsync(postgres.Database, "CREATE TABLE records(id bigint NOT NULL,label text COLLATE \"C\" NOT NULL,PRIMARY KEY(id,label)); INSERT INTO records VALUES(1,'臺灣😀'); CREATE INDEX records_label ON records(label ASC NULLS FIRST);");
            var beforeRename = await LogicalMigrationService.PreviewAsync(postgres, target, 16);
            Check(beforeRename.CanStart, "Default PG PK preview allowed");
            await admin.ExecuteAsync(postgres.Database, "ALTER TABLE records RENAME CONSTRAINT records_pkey TO explicit_records_pk;");
            var renamed = await LogicalMigrationService.PreviewAsync(postgres, target, 16);
            Check(renamed.CanStart && renamed.Tables.Single().PrimaryKeyName == "explicit_records_pk" && renamed.Statements.Any(sql => sql.Contains("CONSTRAINT \"explicit_records_pk\"")), "Actual PG constraint catalog reaches preview and DDL");
            Check(beforeRename.Fingerprint != renamed.Fingerprint, "PK rename changes fingerprint");
            Check((await LogicalMigrationService.RunAsync(postgres, target, 16, beforeRename, target.Database)).Outcome == LogicalMigrationOutcome.RolledBack, "Stale constraint name confirmation refused");
            var sqlite = Profile(Path.Combine(root, "explicit-pk-target.db")); CreateEmpty(sqlite.Database);
            var denied = await LogicalMigrationService.PreviewAsync(postgres, sqlite, 3);
            Check(!denied.CanStart && denied.Blockers.Any(reason => reason.Contains("explicit_records_pk")), "Explicit PG PK name cannot silently become SQLite internal PK");
            Check((await LogicalMigrationService.RunAsync(postgres, sqlite, 3, denied, sqlite.Database)).Outcome == LogicalMigrationOutcome.Refused && await ObjectCountAsync(sqlite) == 0, "Explicit PK SQLite rejection leaves target empty");
            Check((await LogicalMigrationService.RunAsync(postgres, target, 16, renamed, target.Database)).Outcome == LogicalMigrationOutcome.Verified, "PG -> PG preserves explicit PK name");
            var catalog = await targetOwner.ExecuteAsync(target.Database, "SELECT conname,ic.relname FROM pg_constraint pc JOIN pg_class ic ON ic.oid=pc.conindid WHERE pc.conrelid='records'::regclass AND pc.contype='p';");
            Check(catalog.Rows.Single().All(value => Equals(value, "explicit_records_pk")), "Target actual constraint and backing index names match");
            await targetOwner.ExecuteAsync(target.Database, "INSERT INTO records VALUES(1,'臺灣😀') ON CONFLICT ON CONSTRAINT explicit_records_pk DO NOTHING;");
            var rows = await targetOwner.ExecuteAsync(target.Database, "SELECT count(*) FROM records;");
            Check(Convert.ToInt64(rows.Rows.Single()[0]) == 1, "ON CONFLICT ON CONSTRAINT dependency remains functional");
            await targetOwner.ExecuteAsync(target.Database, "DROP TABLE records;");
            var corrupted = await LogicalMigrationService.RunWithObserverAsync(postgres, target, 16, renamed, target.Database, CancellationToken.None,
                (stage, connection, transaction) =>
                {
                    if (stage != "verify") return;
                    using var command = connection.CreateCommand(); command.Transaction = transaction;
                    command.CommandText = "ALTER TABLE records RENAME CONSTRAINT explicit_records_pk TO wrong_records_pk;"; command.ExecuteNonQuery();
                });
            Check(corrupted.Outcome == LogicalMigrationOutcome.RolledBack, "Actual target PK rename fails schema verification");
            var empty = await targetOwner.ExecuteAsync(target.Database, "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public';");
            Check(Convert.ToInt64(empty.Rows.Single()[0]) == 0, "PK verification failure rolls back target objects");
            Console.WriteLine("PASS real PG explicit PK catalog/DDL/fingerprint/ON CONFLICT/schema-fault and SQLite refusal");
        }
        finally
        {
            await admin.ExecuteAsync(postgres.Database, "DROP TABLE IF EXISTS records;");
            Npgsql.NpgsqlConnection.ClearAllPools();
            await admin.ExecuteAsync("postgres", "DROP DATABASE \"" + target.Database + "\";");
        }
    }

    private static async Task PostgreSqlKeyBudgetAsync(string root, ConnectionProfile postgres, IDatabaseSession admin)
    {
        var version = await admin.ExecuteAsync(postgres.Database, "SHOW server_version;");
        var page = await admin.ExecuteAsync(postgres.Database, "SHOW block_size;");
        Console.WriteLine("Isolated PostgreSQL version=" + version.Rows.Single()[0] + "; block_size=" + page.Rows.Single()[0]);
        foreach (var scenario in new[] { "pk", "composite", "index", "utf8", "boundary", "unindexed" })
        {
            var source = Profile(Path.Combine(root, "budget-" + scenario + ".db"));
            using (var connection = new SqliteConnection("Data Source=" + source.Database))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                var constraint = scenario is "pk" or "utf8" ? ", PRIMARY KEY(label)" : scenario is "composite" ? ", PRIMARY KEY(label,other)" : scenario == "boundary" ? ", PRIMARY KEY(id,label)" : "";
                command.CommandText = "CREATE TABLE budget(id INTEGER NOT NULL,label TEXT NOT NULL,other TEXT NOT NULL" + constraint + ") STRICT" + (constraint.Length == 0 ? ";" : ", WITHOUT ROWID;") +
                    (scenario == "index" ? "CREATE INDEX budget_label ON budget(label);" : "");
                command.ExecuteNonQuery();
                var random = new Random(20261014);
                var buffer = new byte[6000]; random.NextBytes(buffer);
                var text = Convert.ToBase64String(buffer);
                command.CommandText = "INSERT INTO budget VALUES(1,@label,@other);";
                command.Parameters.AddWithValue("@label", scenario == "utf8" ? new string('中', 700) : scenario == "composite" ? text[..1000] : scenario == "boundary" ? text[..1996] : text);
                command.Parameters.AddWithValue("@other", scenario == "composite" ? text[1000..2000] : "");
                command.ExecuteNonQuery();
            }
            var preview = await LogicalMigrationService.PreviewAsync(source, postgres, 16);
            var allowed = scenario is "boundary" or "unindexed";
            Check(preview.CanStart == allowed, "Real PG key preflight " + scenario + ": " + string.Join(";", preview.Blockers));
            Console.WriteLine("PASS real PG index key preflight: " + scenario + "; CanStart=" + preview.CanStart + "; " + string.Join(";", preview.Blockers));
            if (scenario == "boundary")
            {
                using var connection = new SqliteConnection("Data Source=" + source.Database); connection.Open();
                using var update = connection.CreateCommand();
                update.CommandText = "UPDATE budget SET label=@label;";
                update.Parameters.AddWithValue("@label", new string('x', 8000)); update.ExecuteNonQuery();
                var stale = await LogicalMigrationService.RunAsync(source, postgres, 16, preview, postgres.Database);
                Check(stale.Outcome == LogicalMigrationOutcome.RolledBack, "Changed indexed key invalidates approved preview before writing");
                var empty = await admin.ExecuteAsync(postgres.Database, "SELECT count(*) FROM pg_tables WHERE schemaname='public';");
                Check(Convert.ToInt64(empty.Rows.Single()[0]) == 0, "Stale oversized key leaves real PG target empty");
                update.Parameters[0].Value = new string('x', 1996); update.ExecuteNonQuery();
                preview = await LogicalMigrationService.PreviewAsync(source, postgres, 16);
                Check(preview.CanStart, "Fresh boundary preview after restoring indexed key");
                Console.WriteLine("PASS real PG stale indexed key refused before DDL; fresh preview required");
            }
            var result = await LogicalMigrationService.RunAsync(source, postgres, 16, preview, postgres.Database);
            Check(result.Outcome == (allowed ? LogicalMigrationOutcome.Verified : LogicalMigrationOutcome.Refused), "Real PG key execution " + scenario);
            if (allowed)
            {
                if (scenario == "boundary")
                {
                    try { await admin.ExecuteAsync(postgres.Database, "INSERT INTO budget SELECT * FROM budget;"); throw new Exception("Boundary key must be enforced"); }
                    catch (Npgsql.PostgresException error) { Check(error.SqlState == "23505", "Real PG 2048-byte conservative tuple boundary PK enforced"); }
                }
                await admin.ExecuteAsync(postgres.Database, "DROP TABLE budget;");
            }
            else Check(preview.Blockers.Any(reason => reason.Contains("2048-byte")), "Real PG rejected key has explicit byte budget reason");
            var count = await admin.ExecuteAsync(postgres.Database, "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public';");
            Check(Convert.ToInt64(count.Rows.Single()[0]) == 0, "Real PG target empty after key scenario " + scenario);
            Console.WriteLine("PASS real PG key outcome: " + scenario + "; " + result.Outcome + "; target public objects=0 after owned fixture cleanup");
        }
    }

    private const string BusinessSql = "CREATE TABLE customers (id INTEGER NOT NULL, label TEXT COLLATE BINARY NOT NULL, note TEXT, payload BLOB, PRIMARY KEY(id)) STRICT, WITHOUT ROWID; " +
        "CREATE INDEX customer_label ON customers(label); CREATE INDEX customer_note ON customers(note,id); " +
        "CREATE TABLE lines (customer INTEGER NOT NULL, code TEXT NOT NULL, PRIMARY KEY(customer,code)) STRICT, WITHOUT ROWID; " +
        "INSERT INTO customers VALUES (1,'臺灣😀','',X'00FF'),(2,'A',NULL,NULL),(3,'a','é',X''),(4,'é','中文',NULL); " +
        "INSERT INTO lines VALUES (1,'A'),(1,'a'),(2,'😀');";

    private static async Task TextKeysAsync(string root)
    {
        var source = Profile(Path.Combine(root, "business.db"));
        var target = Profile(Path.Combine(root, "business-target.db")); CreateEmpty(target.Database);
        using var owner = DatabaseProviderFactory.Create(source);
        await owner.ExecuteAsync(source.Database, BusinessSql);
        var preview = await LogicalMigrationService.PreviewAsync(source, target, 3);
        Check(preview.CanStart && preview.Tables.Single(table => table.Name == "customers").PrimaryKey.SequenceEqual(new[] { "id" }) && preview.Statements.Count == 4, "Real text, manual integer/composite PK and ordinary indexes preview: " + string.Join(";", preview.Blockers));
        using (var cancel = new CancellationTokenSource())
        {
            var canceled = await LogicalMigrationService.RunWithObserverAsync(source, target, 3, preview, target.Database, cancel.Token,
                (stage, connection, transaction) => { if (stage == "index") cancel.Cancel(); });
            Check(canceled.Outcome == LogicalMigrationOutcome.RolledBack && await ObjectCountAsync(target) == 0, "Cancel after first real index rolls back schema, indexes and rows");
        }
        await owner.ExecuteAsync(source.Database, "DROP INDEX customer_note; CREATE INDEX customer_note ON customers(id,note);");
        var changed = await LogicalMigrationService.RunAsync(source, target, 3, preview, target.Database);
        Check(changed.Outcome == LogicalMigrationOutcome.RolledBack && await ObjectCountAsync(target) == 0, "Index column-order change invalidates approved fingerprint");
        await owner.ExecuteAsync(source.Database, "DROP INDEX customer_note; CREATE INDEX customer_note ON customers(note,id);");
        var failed = await LogicalMigrationService.RunWithObserverAsync(source, target, 3, preview, target.Database, CancellationToken.None,
            (stage, connection, transaction) =>
            {
                if (stage != "verify") return;
                using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "DROP INDEX customer_note;"; command.ExecuteNonQuery();
            });
        Check(failed.Outcome == LogicalMigrationOutcome.RolledBack && await ObjectCountAsync(target) == 0, "Missing index in verification rolls back all schema/data");
        Check((await LogicalMigrationService.RunAsync(source, target, 3, preview, target.Database)).Outcome == LogicalMigrationOutcome.Verified, "Real business schema/content verified transfer");
        await CheckBusinessAsync(target);
        var empty = Profile(Path.Combine(root, "text-invalid-target.db")); CreateEmpty(empty.Database);
        foreach (var value in new[] { "CAST(X'FF' AS TEXT)", "CAST(X'410042' AS TEXT)", "printf('%.*c',1048577,'a')" })
        {
            await owner.ExecuteAsync(source.Database, "UPDATE customers SET note=" + value + " WHERE id=1;");
            try { await LogicalMigrationService.PreviewAsync(source, empty, 3); throw new Exception("Invalid or oversized text must be refused"); }
            catch (InvalidOperationException) { Check(await ObjectCountAsync(empty) == 0, "Invalid UTF8/NUL/oversized text leaves empty target"); }
        }
        Console.WriteLine("PASS 真實 SQLite 文字、主鍵、索引、限額與回滾子案例");
    }

    private static async Task CheckBusinessAsync(ConnectionProfile profile)
    {
        using var owner = DatabaseProviderFactory.Create(profile);
        var ordered = await owner.ExecuteAsync(profile.Database, "SELECT label FROM customers ORDER BY label;");
        Check(ordered.Rows.Select(row => Convert.ToString(row[0])).SequenceEqual(new[] { "A", "a", "é", "臺灣😀" }), "Binary UTF8 collation order preserved");
        var indexes = await owner.ExecuteAsync(profile.Database, "PRAGMA index_list(customers);");
        Check(indexes.Rows.Any(row => Equals(row[1], "customer_label")) && indexes.Rows.Any(row => Equals(row[1], "customer_note")), "Actual index names preserved");
        foreach (var sql in new[] { "INSERT INTO customers(id,label) VALUES (1,'duplicate');", "INSERT INTO customers(label) VALUES ('no automatic identity');", "INSERT INTO lines VALUES(1,'A');" })
        {
            try { await owner.ExecuteAsync(profile.Database, sql); throw new Exception("PK constraint must reject write"); }
            catch (SqliteException) { }
        }
    }

    private static string Required(string key) => Environment.GetEnvironmentVariable(key) ?? throw new InvalidOperationException("Missing isolated fixture variable " + key);
    private static ConnectionProfile Profile(string path) => new() { Provider = DatabaseProviderKind.Sqlite, Name = "Isolated logical migration", Database = path };
    private static void CreateEmpty(string path) { using var connection = new SqliteConnection("Data Source=" + path); connection.Open(); }
    private static async Task<long> ObjectCountAsync(ConnectionProfile profile)
    {
        using var session = DatabaseProviderFactory.Create(profile);
        var result = await session.ExecuteAsync(profile.Database, "SELECT count(*) FROM sqlite_schema;");
        return Convert.ToInt64(result.Rows.Single().Single());
    }
    private static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
}
