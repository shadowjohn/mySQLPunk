using MySqlPunk.Core.Models;
using MySqlPunk.Core.Providers;
using MySqlPunk.Core.Services;

var tests = new List<(string Name, Func<Task> Run)>
{
    ("連線設定不保存密碼", ProfileStoreDoesNotPersistPasswordsAsync),
    ("Linux Secret Service 安全 round-trip", LinuxSecretServiceRoundTripAsync),
    ("macOS Keychain 安全 round-trip", MacOsKeychainRoundTripAsync),
    ("SQLite 查詢與 DDL/DML", SqliteExecutesQueriesAsync),
    ("SQLite metadata 與預覽 SQL", SqliteLoadsMetadataAsync),
    ("Provider 驗證與工廠", ProviderFactoryValidatesProfilesAsync)
};

if (string.Equals(Environment.GetEnvironmentVariable("MYSQLPUNK_LIVE_TESTS"), "1", StringComparison.Ordinal))
{
    tests.Add(("MySQL 實機連線、metadata 與 SQL", MySqlLiveRoundTripAsync));
    tests.Add(("PostgreSQL 實機連線、metadata 與 SQL", PostgreSqlLiveRoundTripAsync));
    tests.Add(("SQL Server 實機連線、metadata 與 SQL", SqlServerLiveRoundTripAsync));
}

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{test.Name}: {exception.Message}");
        Console.Error.WriteLine($"FAIL {test.Name}: {exception}");
    }
}

Console.WriteLine($"跨平台 smoke tests：{tests.Count - failures.Count}/{tests.Count} 通過");
return failures.Count == 0 ? 0 : 1;

static async Task ProfileStoreDoesNotPersistPasswordsAsync()
{
    var directory = CreateTemporaryDirectory();
    try
    {
        var path = Path.Combine(directory, "connections.json");
        var store = new ConnectionProfileStore(path);
        var profile = new ConnectionProfile
        {
            Name = "Secret store profile",
            Provider = DatabaseProviderKind.MySql,
            Host = "localhost",
            Port = 3306,
            Username = "root",
            Password = "this-must-never-be-saved",
            UseSecretStore = true
        };

        await store.SaveAsync(new[] { profile });
        var json = await File.ReadAllTextAsync(path);
        Assert(!json.Contains("this-must-never-be-saved", StringComparison.Ordinal), "設定檔含有密碼內容");
        Assert(!json.Contains("password", StringComparison.OrdinalIgnoreCase), "設定檔含有 password 欄位");

        var loaded = await store.LoadAsync();
        Assert(loaded.Count == 1, "設定檔數量不正確");
        Assert(loaded[0].Password.Length == 0, "載入後不應存在密碼");
        Assert(loaded[0].UseSecretStore, "系統密碼庫 opt-in 旗標應保存");
    }
    finally
    {
        Directory.Delete(directory, true);
    }
}

static async Task LinuxSecretServiceRoundTripAsync()
{
    if (OperatingSystem.IsWindows())
    {
        return;
    }

    var directory = CreateTemporaryDirectory();
    try
    {
        var statePath = Path.Combine(directory, "linux-secret-state");
        var argumentsPath = Path.Combine(directory, "linux-secret-arguments");
        var executablePath = Path.Combine(directory, "secret-tool");
        var script = $$"""
            #!/usr/bin/env bash
            set -euo pipefail
            case "$1" in
              store)
                shift
                printf '%s' "$*" > '{{argumentsPath}}'
                /bin/cat > '{{statePath}}'
                ;;
              lookup)
                [[ -f '{{statePath}}' ]] || exit 1
                /bin/cat '{{statePath}}'
                ;;
              clear)
                /bin/rm -f '{{statePath}}'
                ;;
              *)
                exit 2
                ;;
            esac
            """;
        await WriteExecutableScriptAsync(executablePath, script);

        var profileId = Guid.NewGuid();
        const string secret = "Linux secret with spaces + symbols";
        var store = new LinuxSecretServiceStore(executablePath);
        Assert(store.IsAvailable, "假的 secret-tool 應可使用");
        await store.StoreAsync(profileId, "測試連線", secret);
        Assert(await store.GetAsync(profileId) == secret, "Linux Secret Service round-trip 不正確");

        var arguments = await File.ReadAllTextAsync(argumentsPath);
        Assert(!arguments.Contains(secret, StringComparison.Ordinal), "Linux 密碼不可出現在 process arguments");
        Assert(arguments.Contains(profileId.ToString("N"), StringComparison.Ordinal), "Linux 密碼庫缺少 profile id");

        await store.DeleteAsync(profileId);
        Assert(await store.GetAsync(profileId) is null, "Linux 密碼庫刪除後仍讀到內容");
    }
    finally
    {
        Directory.Delete(directory, true);
    }
}

static async Task MacOsKeychainRoundTripAsync()
{
    if (OperatingSystem.IsWindows())
    {
        return;
    }

    var directory = CreateTemporaryDirectory();
    try
    {
        var statePath = Path.Combine(directory, "macos-keychain-state");
        var commandPath = Path.Combine(directory, "macos-keychain-command");
        var executablePath = Path.Combine(directory, "security");
        var script = $$"""
            #!/usr/bin/env bash
            set -euo pipefail
            case "$1" in
              -i)
                IFS= read -r command
                printf '%s' "$command" > '{{commandPath}}'
                printf '%s' "${command##* }" > '{{statePath}}'
                ;;
              find-generic-password)
                [[ -f '{{statePath}}' ]] || exit 44
                /bin/cat '{{statePath}}'
                ;;
              delete-generic-password)
                /bin/rm -f '{{statePath}}'
                ;;
              *)
                exit 2
                ;;
            esac
            """;
        await WriteExecutableScriptAsync(executablePath, script);

        var profileId = Guid.NewGuid();
        const string secret = "macOS 崩琦 secret with spaces";
        var store = new MacOsKeychainSecretStore(executablePath);
        Assert(store.IsAvailable, "假的 security 工具應可使用");
        await store.StoreAsync(profileId, "測試連線", secret);
        Assert(await store.GetAsync(profileId) == secret, "macOS Keychain round-trip 不正確");

        var command = await File.ReadAllTextAsync(commandPath);
        Assert(!command.Contains(secret, StringComparison.Ordinal), "macOS 密碼不可明文出現在互動命令");
        Assert(command.Contains(profileId.ToString("N"), StringComparison.Ordinal), "macOS Keychain 缺少 profile id");

        await store.DeleteAsync(profileId);
        Assert(await store.GetAsync(profileId) is null, "macOS Keychain 刪除後仍讀到內容");
    }
    finally
    {
        Directory.Delete(directory, true);
    }
}

static async Task SqliteExecutesQueriesAsync()
{
    var directory = CreateTemporaryDirectory();
    try
    {
        var profile = CreateSqliteProfile(Path.Combine(directory, "query.db"));
        var session = DatabaseProviderFactory.Create(profile);
        await session.TestConnectionAsync();

        await session.ExecuteAsync(profile.Database, "CREATE TABLE sample (id INTEGER PRIMARY KEY, name TEXT NOT NULL);");
        var insert = await session.ExecuteAsync(profile.Database, "INSERT INTO sample (name) VALUES ('Punky'), ('崩琦');");
        Assert(insert.RowsAffected == 2, "INSERT 影響列數應為 2");

        var query = await session.ExecuteAsync(profile.Database, "SELECT id, name FROM sample ORDER BY id;");
        Assert(query.Columns.SequenceEqual(new[] { "id", "name" }), "查詢欄位不正確");
        Assert(query.Rows.Count == 2, "查詢結果應有 2 列");
        Assert(Convert.ToString(query.Rows[1][1]) == "崩琦", "UTF-8 內容不正確");

        var truncated = await session.ExecuteAsync(
            profile.Database,
            "WITH RECURSIVE numbers(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM numbers WHERE n < 10001) SELECT n FROM numbers;");
        Assert(truncated.Rows.Count == 10_000, "大型結果應限制為 10,000 列");
        Assert(truncated.WasTruncated, "大型結果應標示已截斷");
    }
    finally
    {
        Directory.Delete(directory, true);
    }
}

static async Task SqliteLoadsMetadataAsync()
{
    var directory = CreateTemporaryDirectory();
    try
    {
        var profile = CreateSqliteProfile(Path.Combine(directory, "metadata.db"));
        var session = DatabaseProviderFactory.Create(profile);
        await session.ExecuteAsync(profile.Database, "CREATE TABLE people (id INTEGER); CREATE VIEW people_view AS SELECT * FROM people;");

        var databases = await session.GetDatabasesAsync();
        Assert(databases.Count == 1 && Path.IsPathFullyQualified(databases[0]), "SQLite database 應回傳完整路徑");

        var objects = await session.GetObjectsAsync(databases[0]);
        Assert(objects.Any(item => item.Name == "people" && item.Kind == DatabaseObjectKind.Table), "找不到 people table");
        Assert(objects.Any(item => item.Name == "people_view" && item.Kind == DatabaseObjectKind.View), "找不到 people_view view");

        var preview = session.BuildSelectPreview(objects.Single(item => item.Name == "people"));
        Assert(preview == "SELECT * FROM \"people\" LIMIT 200;", "SQLite 預覽 SQL 不正確");
    }
    finally
    {
        Directory.Delete(directory, true);
    }
}

static Task ProviderFactoryValidatesProfilesAsync()
{
    var mysql = new ConnectionProfile
    {
        Name = "MySQL",
        Provider = DatabaseProviderKind.MySql,
        Host = "localhost",
        Port = 3306,
        Username = "root"
    };
    var postgres = new ConnectionProfile
    {
        Name = "PostgreSQL",
        Provider = DatabaseProviderKind.PostgreSql,
        Host = "localhost",
        Port = 5432,
        Username = "postgres",
        Database = "postgres"
    };
    var sqlServer = new ConnectionProfile
    {
        Name = "SQL Server",
        Provider = DatabaseProviderKind.SqlServer,
        Host = "localhost",
        Port = 1433,
        Username = "sa",
        Database = "master"
    };

    Assert(DatabaseProviderFactory.Create(mysql).Profile.Provider == DatabaseProviderKind.MySql, "MySQL factory 建立錯誤");
    Assert(DatabaseProviderFactory.Create(postgres).Profile.Provider == DatabaseProviderKind.PostgreSql, "PostgreSQL factory 建立錯誤");
    var sqlServerSession = DatabaseProviderFactory.Create(sqlServer);
    Assert(sqlServerSession.Profile.Provider == DatabaseProviderKind.SqlServer, "SQL Server factory 建立錯誤");
    var sqlServerPreview = sqlServerSession.BuildSelectPreview(
        new DatabaseObjectInfo("dbo", "people]archive", DatabaseObjectKind.Table));
    Assert(sqlServerPreview == "SELECT TOP (200) * FROM [dbo].[people]]archive];", "SQL Server 預覽 SQL 不正確");

    var invalid = mysql.Clone();
    invalid.Username = string.Empty;
    AssertThrows<InvalidOperationException>(() => DatabaseProviderFactory.Create(invalid));
    return Task.CompletedTask;
}

static async Task MySqlLiveRoundTripAsync()
{
    var database = "mysqlpunk_cross_" + Guid.NewGuid().ToString("N")[..10];
    var profile = new ConnectionProfile
    {
        Name = "MySQL live",
        Provider = DatabaseProviderKind.MySql,
        Host = ReadRequiredEnvironment("MYSQLPUNK_MYSQL_HOST"),
        Port = ReadRequiredIntEnvironment("MYSQLPUNK_MYSQL_PORT"),
        Username = Environment.GetEnvironmentVariable("MYSQLPUNK_MYSQL_USER") ?? "root",
        Password = ReadRequiredEnvironment("MYSQLPUNK_MYSQL_PASSWORD"),
        TimeoutSeconds = 20
    };
    var session = DatabaseProviderFactory.Create(profile);
    await session.TestConnectionAsync();

    try
    {
        await session.ExecuteAsync(string.Empty, $"CREATE DATABASE `{database}` CHARACTER SET utf8mb4;");
        await session.ExecuteAsync(database, "CREATE TABLE sample (id INT PRIMARY KEY AUTO_INCREMENT, name VARCHAR(40) NOT NULL);");
        var insert = await session.ExecuteAsync(database, "INSERT INTO sample (name) VALUES ('Punky'), ('Linux');");
        Assert(insert.RowsAffected == 2, "MySQL INSERT 影響列數應為 2");

        var result = await session.ExecuteAsync(database, "SELECT id, name FROM sample ORDER BY id;");
        Assert(result.Rows.Count == 2 && Convert.ToString(result.Rows[1][1]) == "Linux", "MySQL 查詢結果不正確");
        var objects = await session.GetObjectsAsync(database);
        Assert(objects.Any(item => item.Name == "sample" && item.Kind == DatabaseObjectKind.Table), "MySQL metadata 找不到 sample");
    }
    finally
    {
        await session.ExecuteAsync(string.Empty, $"DROP DATABASE IF EXISTS `{database}`;");
    }
}

static async Task PostgreSqlLiveRoundTripAsync()
{
    var database = "mysqlpunk_cross_" + Guid.NewGuid().ToString("N")[..10];
    var profile = new ConnectionProfile
    {
        Name = "PostgreSQL live",
        Provider = DatabaseProviderKind.PostgreSql,
        Host = ReadRequiredEnvironment("MYSQLPUNK_POSTGRES_HOST"),
        Port = ReadRequiredIntEnvironment("MYSQLPUNK_POSTGRES_PORT"),
        Username = Environment.GetEnvironmentVariable("MYSQLPUNK_POSTGRES_USER") ?? "postgres",
        Password = ReadRequiredEnvironment("MYSQLPUNK_POSTGRES_PASSWORD"),
        Database = "postgres",
        TimeoutSeconds = 20
    };
    var session = DatabaseProviderFactory.Create(profile);
    await session.TestConnectionAsync();

    try
    {
        await session.ExecuteAsync("postgres", $"CREATE DATABASE \"{database}\";");
        await session.ExecuteAsync(database, "CREATE TABLE sample (id INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY, name VARCHAR(40) NOT NULL);");
        var insert = await session.ExecuteAsync(database, "INSERT INTO sample (name) VALUES ('Punky'), ('macOS');");
        Assert(insert.RowsAffected == 2, "PostgreSQL INSERT 影響列數應為 2");

        var result = await session.ExecuteAsync(database, "SELECT id, name FROM sample ORDER BY id;");
        Assert(result.Rows.Count == 2 && Convert.ToString(result.Rows[1][1]) == "macOS", "PostgreSQL 查詢結果不正確");
        var objects = await session.GetObjectsAsync(database);
        Assert(objects.Any(item => item.Name == "sample" && item.Schema == "public"), "PostgreSQL metadata 找不到 public.sample");
    }
    finally
    {
        await session.ExecuteAsync(
            "postgres",
            $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{database}' AND pid <> pg_backend_pid();");
        await session.ExecuteAsync("postgres", $"DROP DATABASE IF EXISTS \"{database}\";");
    }
}

static async Task SqlServerLiveRoundTripAsync()
{
    var database = "mysqlpunk_cross_" + Guid.NewGuid().ToString("N")[..10];
    var profile = new ConnectionProfile
    {
        Name = "SQL Server live",
        Provider = DatabaseProviderKind.SqlServer,
        Host = ReadRequiredEnvironment("MYSQLPUNK_SQLSERVER_HOST"),
        Port = ReadRequiredIntEnvironment("MYSQLPUNK_SQLSERVER_PORT"),
        Username = Environment.GetEnvironmentVariable("MYSQLPUNK_SQLSERVER_USER") ?? "sa",
        Password = ReadRequiredEnvironment("MYSQLPUNK_SQLSERVER_PASSWORD"),
        Database = "master",
        TimeoutSeconds = 30
    };
    var session = DatabaseProviderFactory.Create(profile);
    await session.TestConnectionAsync();

    try
    {
        await session.ExecuteAsync("master", $"CREATE DATABASE [{database}];");
        await session.ExecuteAsync(database, "CREATE TABLE dbo.sample (id INT IDENTITY PRIMARY KEY, name NVARCHAR(40) NOT NULL);");
        var insert = await session.ExecuteAsync(database, "INSERT INTO dbo.sample (name) VALUES (N'Punky'), (N'Linux/macOS');");
        Assert(insert.RowsAffected == 2, "SQL Server INSERT 影響列數應為 2");

        var result = await session.ExecuteAsync(database, "SELECT id, name FROM dbo.sample ORDER BY id;");
        Assert(result.Rows.Count == 2 && Convert.ToString(result.Rows[1][1]) == "Linux/macOS", "SQL Server 查詢結果不正確");
        var objects = await session.GetObjectsAsync(database);
        var table = objects.SingleOrDefault(item => item.Name == "sample" && item.Schema == "dbo");
        Assert(table is not null && table.Kind == DatabaseObjectKind.Table, "SQL Server metadata 找不到 dbo.sample");
        Assert(session.BuildSelectPreview(table!) == "SELECT TOP (200) * FROM [dbo].[sample];", "SQL Server 實機預覽 SQL 不正確");
    }
    finally
    {
        await session.ExecuteAsync(
            "master",
            $"IF DB_ID(N'{database}') IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END");
    }
}

static ConnectionProfile CreateSqliteProfile(string path) => new()
{
    Name = "SQLite smoke",
    Provider = DatabaseProviderKind.Sqlite,
    Database = path
};

static string CreateTemporaryDirectory()
{
    var path = Path.Combine(Path.GetTempPath(), "mysqlpunk-cross-platform-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    return path;
}

static async Task WriteExecutableScriptAsync(string path, string contents)
{
    if (OperatingSystem.IsWindows())
    {
        throw new PlatformNotSupportedException("此測試腳本只用於 Unix-like 平台。");
    }

    await File.WriteAllTextAsync(path, contents);
    File.SetUnixFileMode(
        path,
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
}

static string ReadRequiredEnvironment(string name)
{
    var value = Environment.GetEnvironmentVariable(name);
    return string.IsNullOrWhiteSpace(value)
        ? throw new InvalidOperationException($"缺少環境變數 {name}")
        : value;
}

static int ReadRequiredIntEnvironment(string name)
{
    return int.TryParse(ReadRequiredEnvironment(name), out var value)
        ? value
        : throw new InvalidOperationException($"環境變數 {name} 必須是整數");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertThrows<TException>(Action action)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"預期丟出 {typeof(TException).Name}");
}
