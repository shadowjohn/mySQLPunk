using Microsoft.Data.Sqlite;
using MySqlPunk.Core.Models;
using MySqlPunk.Core.Services;
using Npgsql;

var oldMajor = int.Parse(Environment.GetEnvironmentVariable("MYSQLPUNK_OLD_PG_MAJOR") ?? "14");
if (oldMajor is not (14 or 16)) throw new ArgumentException("Only the 14/17 and 16/17 fixtures are defined.");
var ports = new[] { int.Parse(args[0]), int.Parse(args[1]) };
var password = Environment.GetEnvironmentVariable("MYSQLPUNK_PG_TEST_PASSWORD")
    ?? throw new InvalidOperationException("Missing isolated fixture password.");
string Connection(int port, string database) => new NpgsqlConnectionStringBuilder
{
    Host = "127.0.0.1", Port = port, Username = "postgres", Password = password,
    Database = database, SslMode = SslMode.Disable, Pooling = false, Timeout = 3
}.ConnectionString;
foreach (var port in ports)
{
    await using var connection = new NpgsqlConnection(Connection(port, "postgres"));
    await connection.OpenAsync();
    Console.WriteLine("Actual PostgreSQL version=" + connection.PostgreSqlVersion);
    var expectedMajor = port == ports[0] ? oldMajor : 17;
    if (connection.PostgreSqlVersion.Major != expectedMajor) throw new Exception("Unexpected engine major.");
}
if (args.Length > 2) return;
var root = Path.Combine(Path.GetTempPath(), "mysqlpunk-version-fixture-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    foreach (var port in ports)
    {
        await using var connection = new NpgsqlConnection(Connection(port, "postgres"));
        await connection.OpenAsync();
        foreach (var database in new[] { "migration_source", "migration_back" })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE DATABASE " + database;
            await command.ExecuteNonQueryAsync();
        }
    }
    ConnectionProfile Pg(int index, string database) => new()
    {
        Name = "isolated PG", Provider = DatabaseProviderKind.PostgreSql, Host = "127.0.0.1",
        Port = ports[index], Username = "postgres", Password = password,
        Database = database, TlsMode = ConnectionTlsMode.Disabled
    };
    var source = new ConnectionProfile
    {
        Name = "isolated SQLite", Provider = DatabaseProviderKind.Sqlite,
        Database = Path.Combine(root, "source.db")
    };
    using (var connection = new SqliteConnection("Data Source=" + source.Database))
    {
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE records (id INTEGER NOT NULL, label TEXT NOT NULL, note TEXT, payload BLOB,
                PRIMARY KEY(id,label)) STRICT, WITHOUT ROWID;
            CREATE INDEX records_label ON records(label);
            INSERT INTO records VALUES
                (1,'abc','繁體中文 😺',X'00FF'),(1,'ABC','',NULL),
                (2,'a','前後空白 ',X''),(2,'a ',NULL,X'010203'),(3,'é','é',NULL);
            CREATE TABLE empty_table (number INTEGER,payload BLOB,note TEXT) STRICT;
            WITH RECURSIVE numbers(number) AS (VALUES(1) UNION ALL SELECT number+1 FROM numbers WHERE number<1000)
            INSERT INTO records SELECT 100+number,'row_'||number,
                CASE WHEN number%3=0 THEN NULL ELSE '測試😀 ' END,
                CASE WHEN number%2=0 THEN NULL ELSE X'00FF' END FROM numbers;
            INSERT INTO records VALUES(-9223372036854775808,'min',NULL,X''),(9223372036854775807,'max','',NULL);
            """;
        command.ExecuteNonQuery();
    }
    async Task Move(ConnectionProfile from, ConnectionProfile to, int major, string label)
    {
        var preview = await LogicalMigrationService.PreviewAsync(from, to, major);
        Console.WriteLine(label + " preview=" + preview.CanStart + " " + string.Join(";", preview.Blockers));
        if (!preview.CanStart) throw new Exception(label + " preflight rejected");
        var result = await LogicalMigrationService.RunAsync(from, to, major, preview, to.Database);
        Console.WriteLine(label + " " + result.Outcome + " rows=" + preview.Tables.Sum(table => table.Rows));
        if (result.Outcome != LogicalMigrationOutcome.Verified) throw new Exception(label + " " + result.Report);
    }
    var old = Pg(0, "migration_source");
    var newer = Pg(1, "migration_source");
    var back = Pg(1, "migration_back");
    await Move(source, newer, 17, "SQLite -> PG17 seed");
    await Move(newer, old, oldMajor, "PG17 -> PG" + oldMajor + " downgrade");
    await Move(old, back, 17, "PG" + oldMajor + " -> PG17 upgrade");
    var final = source.Clone(); final.Database = Path.Combine(root, "back.db");
    using (var connection = new SqliteConnection("Data Source=" + final.Database)) connection.Open();
    await Move(back, final, 3, "PG17 -> SQLite roundtrip");
    using (var connection = new SqliteConnection("Data Source=" + final.Database))
    {
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM records";
        if (Convert.ToInt64(command.ExecuteScalar()) != 1007) throw new Exception("Roundtrip count.");
        command.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type='index' AND name='records_label'";
        if (Convert.ToInt64(command.ExecuteScalar()) != 1) throw new Exception("Named index lost.");
        command.CommandText = "INSERT INTO records VALUES(1,'abc',NULL,NULL)";
        try { command.ExecuteNonQuery(); throw new Exception("Composite PK lost."); }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19) { }
    }
    Console.WriteLine("PASS actual PG17 -> PG" + oldMajor + " -> PG17 plus SQLite roundtrip; 1007 rows + empty table; composite PK, named index, UTF8/emoji, NULL/empty binary/text, signed Int64 extrema; production transaction and SHA256 verification");
}
finally
{
    SqliteConnection.ClearAllPools();
    Directory.Delete(root, true);
}
