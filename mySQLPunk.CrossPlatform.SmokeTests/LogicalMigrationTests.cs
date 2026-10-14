using System.Data.Common;
using Microsoft.Data.Sqlite;
using MySqlPunk.Core.Models;
using MySqlPunk.Core.Providers;
using MySqlPunk.Core.Services;

internal static class LogicalMigrationTests
{
    public static async Task RunAsync()
    {
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
            foreach (var definition in new[]
            {
                "CREATE TABLE bad (id INTEGER PRIMARY KEY, payload BLOB) STRICT;",
                "CREATE TABLE bad (id INTEGER DEFAULT 1, payload BLOB) STRICT;",
                "CREATE TABLE bad (id INTEGER CHECK(id>0), payload BLOB) STRICT;",
                "CREATE TABLE bad (id REAL, payload BLOB) STRICT;",
                "CREATE TABLE bad (id TEXT COLLATE NOCASE, payload BLOB) STRICT;",
                "CREATE TABLE bad (id INTEGER, payload BLOB);",
                "CREATE TABLE bad (id INTEGER REFERENCES sample(number), payload BLOB) STRICT;",
                "CREATE TABLE bad (id INTEGER, payload BLOB) STRICT; CREATE INDEX bad_index ON bad(id);",
                "CREATE TABLE bad (id INTEGER, payload BLOB) STRICT; CREATE TRIGGER bad_trigger AFTER INSERT ON bad BEGIN SELECT 1; END;",
                "CREATE VIEW bad AS SELECT 1;"
            })
            {
                var blocked = Profile(Path.Combine(root, "blocked-" + Guid.NewGuid().ToString("N") + ".db"));
                using var owner = DatabaseProviderFactory.Create(blocked); await owner.ExecuteAsync(blocked.Database, definition);
                var empty = Profile(Path.Combine(root, "empty-" + Guid.NewGuid().ToString("N") + ".db")); CreateEmpty(empty.Database);
                Check(!(await LogicalMigrationService.PreviewAsync(blocked, empty, 3)).CanStart && await ObjectCountAsync(empty) == 0, "Catalog blocks unsupported SQLite semantics");
            }
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
            var sqlite = Profile(Path.Combine(root, "source.db"));
            using var origin = DatabaseProviderFactory.Create(sqlite);
            await origin.ExecuteAsync(sqlite.Database, "CREATE TABLE sample (number INTEGER, payload BLOB NOT NULL) STRICT; INSERT INTO sample VALUES (-9223372036854775808,X'00FF'),(9223372036854775807,X''),(NULL,X'01'),(NULL,X'01');");
            var forward = await LogicalMigrationService.PreviewAsync(sqlite, postgres, 16);
            Check(forward.CanStart, "Real PostgreSQL catalog allows restricted empty target: " + string.Join(";", forward.Blockers));
            Check((await LogicalMigrationService.RunAsync(sqlite, postgres, 16, forward, postgres.Database)).Outcome == LogicalMigrationOutcome.Verified, "SQLite -> PostgreSQL real typed transfer");
            var reverse = Profile(Path.Combine(root, "target.db")); CreateEmpty(reverse.Database);
            var backward = await LogicalMigrationService.PreviewAsync(postgres, reverse, 3);
            Check(backward.CanStart && backward.Tables.Single().ContentHash == forward.Tables.Single().ContentHash, "PostgreSQL -> SQLite real catalog and content");
            Check((await LogicalMigrationService.RunAsync(postgres, reverse, 3, backward, reverse.Database)).Outcome == LogicalMigrationOutcome.Verified, "PostgreSQL -> SQLite verified commit");
            using var pgOwner = DatabaseProviderFactory.Create(postgres);
            foreach (var mutation in new[]
            {
                ("CREATE INDEX extra ON sample(number);", "DROP INDEX extra;"),
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
