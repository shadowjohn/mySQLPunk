using System.Text;
using System.Text.Json;
using MySqlPunk.Core.Models;
using MySqlPunk.Core.Providers;
using MySqlPunk.Core.Services;

var tests = new List<(string Name, Func<Task> Run)>
{
    ("連線設定不保存密碼", ProfileStoreDoesNotPersistPasswordsAsync),
    ("查詢結果安全匯出", QueryResultExportFormatsAsync),
    ("Linux Secret Service 安全 round-trip", LinuxSecretServiceRoundTripAsync),
    ("macOS Keychain 安全 round-trip", MacOsKeychainRoundTripAsync),
    ("SQLite 查詢與 DDL/DML", SqliteExecutesQueriesAsync),
    ("SQLite metadata 與預覽 SQL", SqliteLoadsMetadataAsync),
    ("Table 資料安全編輯與衝突防護", TableDataEditingAsync),
    ("跨平台更新資產安全選擇", CrossPlatformUpdateAssetsAsync),
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

static async Task QueryResultExportFormatsAsync()
{
    var directory = CreateTemporaryDirectory();
    try
    {
        var instant = new DateTime(2026, 8, 29, 14, 5, 6, DateTimeKind.Utc);
        var result = new QueryResult
        {
            Columns = new[] { "name", "name", "amount", "created", "payload", "empty", "nullable", "formula" },
            Rows = new IReadOnlyList<object?>[]
            {
                new object?[]
                {
                    "崩琦",
                    "引號 \"、逗號, 與\r\n換行",
                    -12.5m,
                    instant,
                    new byte[] { 0, 255 },
                    string.Empty,
                    null,
                    "=2+3"
                },
                new object?[]
                {
                    "Punky",
                    "tab\tvalue",
                    42,
                    new DateTimeOffset(instant),
                    Array.Empty<byte>(),
                    "text",
                    DBNull.Value,
                    "-not-a-number"
                }
            }
        };

        var csvPath = Path.Combine(directory, "result.csv");
        var csvSummary = await QueryResultExportService.WriteFileAsync(
            result,
            csvPath,
            QueryResultExportFormat.Csv);
        var csvBytes = await File.ReadAllBytesAsync(csvPath);
        var csv = await File.ReadAllTextAsync(csvPath);
        Assert(csvBytes.AsSpan(0, 3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }), "CSV 應使用 UTF-8 BOM");
        Assert(csv.Contains("\r\n", StringComparison.Ordinal), "CSV 應使用 RFC 4180 CRLF");
        Assert(csv.Contains("\"引號 \"\"、逗號, 與\r\n換行\"", StringComparison.Ordinal), "CSV 引號或換行 escaping 錯誤");
        Assert(csv.Contains("'=2+3", StringComparison.Ordinal), "CSV 公式注入未中和");
        Assert(csv.Contains("-12.5", StringComparison.Ordinal), "CSV 負數不可被公式保護改寫");
        Assert(csvSummary.Rows == 2 && csvSummary.Bytes == csvBytes.Length, "CSV 匯出摘要不正確");

        var tsvPath = Path.Combine(directory, "result.tsv");
        await QueryResultExportService.WriteFileAsync(result, tsvPath, QueryResultExportFormat.Tsv);
        var tsv = await File.ReadAllTextAsync(tsvPath);
        Assert(tsv.Contains("\"tab\tvalue\"", StringComparison.Ordinal), "TSV tab escaping 錯誤");
        Assert(tsv.Contains("'-not-a-number", StringComparison.Ordinal), "TSV 公式注入未中和");

        var jsonPath = Path.Combine(directory, "result.json");
        await QueryResultExportService.WriteFileAsync(result, jsonPath, QueryResultExportFormat.Json);
        var jsonBytes = await File.ReadAllBytesAsync(jsonPath);
        Assert(!jsonBytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }), "JSON 不應包含 BOM");
        using var json = JsonDocument.Parse(jsonBytes);
        var first = json.RootElement[0];
        Assert(first.GetProperty("name").GetString() == "崩琦", "JSON Unicode 不正確");
        Assert(first.GetProperty("name_2").GetString()!.Contains("換行", StringComparison.Ordinal), "JSON 重複欄名未穩定改名");
        Assert(first.GetProperty("amount").GetDecimal() == -12.5m, "JSON 數字型別不正確");
        Assert(first.GetProperty("created").GetString() == instant.ToString("O"), "JSON 日期格式不正確");
        Assert(first.GetProperty("payload").GetString() == "0x00FF", "JSON binary 格式不正確");
        Assert(first.GetProperty("empty").GetString() == string.Empty, "JSON 空字串不正確");
        Assert(first.GetProperty("nullable").ValueKind == JsonValueKind.Null, "JSON NULL 不正確");
        Assert(first.GetProperty("formula").GetString() == "=2+3", "JSON 不應改寫一般字串");

        Assert(
            QueryResultExportService.ResolveFormat("result.tab", QueryResultExportFormat.Csv) == QueryResultExportFormat.Tsv,
            "副檔名格式判斷不正確");

        var existingPath = Path.Combine(directory, "existing.csv");
        await File.WriteAllTextAsync(existingPath, "keep-me");
        var invalid = new QueryResult
        {
            Columns = new[] { "first", "second" },
            Rows = new IReadOnlyList<object?>[] { new object?[] { 1 } }
        };
        await AssertThrowsAsync<InvalidDataException>(() =>
            QueryResultExportService.WriteFileAsync(invalid, existingPath, QueryResultExportFormat.Csv));
        Assert(await File.ReadAllTextAsync(existingPath) == "keep-me", "匯出失敗不可覆寫既有檔案");

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(() =>
            QueryResultExportService.WriteFileAsync(
                result,
                existingPath,
                QueryResultExportFormat.Csv,
                cancellation.Token));
        Assert(await File.ReadAllTextAsync(existingPath) == "keep-me", "取消匯出不可覆寫既有檔案");
        Assert(!Directory.EnumerateFiles(directory).Any(path => path.EndsWith(".tmp", StringComparison.Ordinal)), "匯出失敗留下暫存檔");
    }
    finally
    {
        Directory.Delete(directory, true);
    }
}

static Task CrossPlatformUpdateAssetsAsync()
{
    const string releaseJson = """
        {
          "tag_name": "v1.0.0.20",
          "name": "mySQLPunk v1.0.0.20",
          "html_url": "https://github.com/shadowjohn/mySQLPunk/releases/tag/v1.0.0.20",
          "prerelease": false,
          "assets": [
            {
              "name": "mySQLPunk-1.0.0.20-osx-arm64.app.zip",
              "browser_download_url": "https://github.com/shadowjohn/mySQLPunk/releases/download/v1.0.0.20/mySQLPunk-1.0.0.20-osx-arm64.app.zip"
            },
            {
              "name": "mySQLPunk-1.0.0.20-linux-x64.tar.gz.sha256",
              "browser_download_url": "https://github.com/shadowjohn/mySQLPunk/releases/download/v1.0.0.20/mySQLPunk-1.0.0.20-linux-x64.tar.gz.sha256"
            },
            {
              "name": "mySQLPunk-1.0.0.20-linux-x64.tar.gz",
              "browser_download_url": "https://github.com/shadowjohn/mySQLPunk/releases/download/v1.0.0.20/mySQLPunk-1.0.0.20-linux-x64.tar.gz"
            }
          ]
        }
        """;

    var update = CrossPlatformUpdateService.ParseLatestRelease(
        releaseJson,
        "1.0.0.19",
        "linux-x64");
    Assert(update.UpdateAvailable, "新版版本比較不正確");
    Assert(update.LatestVersionText == "1.0.0.20", "最新版文字不正確");
    Assert(update.RuntimeIdentifier == "linux-x64", "更新 RID 不正確");
    Assert(update.PackageFileName == "mySQLPunk-1.0.0.20-linux-x64.tar.gz", "Linux 資產名稱不正確");
    Assert(update.HasPackageAndChecksum, "應同時找到 Linux 安裝包與 SHA-256");
    Assert(update.PackageDownloadUri?.Scheme == "https", "安裝包 URL 必須是 HTTPS");
    Assert(
        CrossPlatformUpdateService.BuildPackageFileName("v1.2.3", "osx-arm64") ==
        "mySQLPunk-1.2.3-osx-arm64.app.zip",
        "macOS 資產名稱不正確");

    var alreadyLatest = CrossPlatformUpdateService.ParseLatestRelease(
        releaseJson,
        "1.0.0.20",
        "linux-x64");
    Assert(!alreadyLatest.UpdateAvailable, "相同版本不應回報更新");

    var untrustedJson = releaseJson.Replace(
        "https://github.com/shadowjohn/mySQLPunk/releases/download/v1.0.0.20/mySQLPunk-1.0.0.20-linux-x64.tar.gz\"",
        "http://example.com/mySQLPunk-1.0.0.20-linux-x64.tar.gz\"",
        StringComparison.Ordinal);
    AssertThrows<InvalidDataException>(() => CrossPlatformUpdateService.ParseLatestRelease(
        untrustedJson,
        "1.0.0.19",
        "linux-x64"));
    AssertThrows<PlatformNotSupportedException>(() =>
        CrossPlatformUpdateService.BuildPackageFileName("1.0.0.20", "linux-riscv64"));

    return Task.CompletedTask;
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

static async Task TableDataEditingAsync()
{
    var directory = CreateTemporaryDirectory();
    try
    {
        var profile = CreateSqliteProfile(Path.Combine(directory, "editing.db"));
        var session = DatabaseProviderFactory.Create(profile);
        await session.ExecuteAsync(
            profile.Database,
            """
            CREATE TABLE editor_sample (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                quantity INTEGER NULL,
                note TEXT NULL DEFAULT 'database-default'
            );
            CREATE TABLE no_primary_key (name TEXT NOT NULL);
            CREATE TABLE without_rowid (id INTEGER PRIMARY KEY, name TEXT NOT NULL) WITHOUT ROWID;
            CREATE TABLE paged_sample (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
            INSERT INTO paged_sample (id, name) VALUES
                (1, 'page-1'), (2, 'page-2'), (3, 'page-3'), (4, 'page-4'), (5, 'page-5');
            """);
        var table = new DatabaseObjectInfo(string.Empty, "editor_sample", DatabaseObjectKind.Table);

        var pagedTable = new DatabaseObjectInfo(string.Empty, "paged_sample", DatabaseObjectKind.Table);
        var firstPage = await session.LoadTableDataAsync(profile.Database, pagedTable, rowLimit: 2, rowOffset: 0);
        var secondPage = await session.LoadTableDataAsync(profile.Database, pagedTable, rowLimit: 2, rowOffset: 2);
        var lastPage = await session.LoadTableDataAsync(profile.Database, pagedTable, rowLimit: 2, rowOffset: 4);
        Assert(firstPage.Rows.Select(row => Convert.ToInt64(row.Values[0])).SequenceEqual(new long[] { 1, 2 }), "第一頁排序不正確");
        Assert(firstPage.HasNextPage && !firstPage.HasPreviousPage, "第一頁導覽狀態不正確");
        Assert(secondPage.Rows.Select(row => Convert.ToInt64(row.Values[0])).SequenceEqual(new long[] { 3, 4 }), "第二頁 offset 不正確");
        Assert(secondPage.HasNextPage && secondPage.HasPreviousPage && secondPage.RowOffset == 2, "第二頁導覽狀態不正確");
        Assert(lastPage.Rows.Count == 1 && Convert.ToInt64(lastPage.Rows[0].Values[0]) == 5, "最後一頁資料不正確");
        Assert(!lastPage.HasNextPage && lastPage.HasPreviousPage, "最後一頁導覽狀態不正確");
        await AssertThrowsAsync<ArgumentOutOfRangeException>(() =>
            session.LoadTableDataAsync(profile.Database, pagedTable, rowLimit: 2, rowOffset: -1));

        var empty = await session.LoadTableDataAsync(profile.Database, table);
        Assert(empty.Rows.Count == 0, "新建 Table 應為空");
        Assert(empty.HasPrimaryKey, "SQLite Primary Key metadata 未辨識");
        Assert(empty.Columns.Single(column => column.Name == "id").IsGenerated, "SQLite INTEGER PK 應視為 generated");
        Assert(!empty.Columns.Single(column => column.Name == "name").HasDefault, "必填欄位不可誤判為有 DEFAULT");
        Assert(empty.Columns.Single(column => column.Name == "note").HasDefault, "SQLite DEFAULT metadata 未辨識");

        const string parameterizedName = "Punky '); DROP TABLE editor_sample;--";
        await session.InsertTableRowAsync(
            profile.Database,
            table,
            new[]
            {
                new TableCellInput("name", TableCellInputMode.Value, parameterizedName),
                new TableCellInput("quantity", TableCellInputMode.Value, "7"),
                new TableCellInput("note", TableCellInputMode.Default, string.Empty)
            });
        var inserted = await session.LoadTableDataAsync(profile.Database, table);
        Assert(inserted.Rows.Count == 1, "安全新增後應有一列");
        Assert(Convert.ToString(inserted.Rows[0].Values[1]) == parameterizedName, "安全新增參數化字串不正確");
        Assert(Convert.ToInt64(inserted.Rows[0].Values[2]) == 7, "安全新增整數不正確");
        Assert(Convert.ToString(inserted.Rows[0].Values[3]) == "database-default", "資料庫 DEFAULT 未套用");

        var original = inserted.Rows[0];
        await session.UpdateTableRowAsync(
            profile.Database,
            table,
            original,
            new[]
            {
                new TableCellInput("name", TableCellInputMode.Value, "崩琦"),
                new TableCellInput("quantity", TableCellInputMode.Null, string.Empty)
            });
        var updated = await session.LoadTableDataAsync(profile.Database, table);
        Assert(Convert.ToString(updated.Rows[0].Values[1]) == "崩琦", "安全修改字串不正確");
        Assert(updated.Rows[0].Values[2] is null, "安全修改 NULL 不正確");

        var staleRow = updated.Rows[0];
        var id = Convert.ToInt64(staleRow.Values[0]);
        await session.ExecuteAsync(
            profile.Database,
            $"UPDATE editor_sample SET name = 'Concurrent' WHERE id = {id};");
        await AssertThrowsAsync<TableDataConflictException>(() =>
            session.UpdateTableRowAsync(
                profile.Database,
                table,
                staleRow,
                new[] { new TableCellInput("name", TableCellInputMode.Value, "不可覆蓋") }));
        var concurrent = await session.LoadTableDataAsync(profile.Database, table);
        Assert(Convert.ToString(concurrent.Rows[0].Values[1]) == "Concurrent", "衝突檢查不應覆蓋外部變更");

        await session.DeleteTableRowAsync(profile.Database, table, concurrent.Rows[0]);
        Assert((await session.LoadTableDataAsync(profile.Database, table)).Rows.Count == 0, "安全刪除後應為空");
        await AssertThrowsAsync<TableDataConflictException>(() =>
            session.DeleteTableRowAsync(profile.Database, table, concurrent.Rows[0]));

        var noPrimaryKey = new DatabaseObjectInfo(string.Empty, "no_primary_key", DatabaseObjectKind.Table);
        await session.InsertTableRowAsync(
            profile.Database,
            noPrimaryKey,
            new[] { new TableCellInput("name", TableCellInputMode.Value, "允許新增") });
        var noKeyRows = await session.LoadTableDataAsync(profile.Database, noPrimaryKey);
        Assert(!noKeyRows.HasPrimaryKey && noKeyRows.Rows.Count == 1, "無 PK Table 應可新增與瀏覽");
        await AssertThrowsAsync<InvalidOperationException>(() =>
            session.LoadTableDataAsync(profile.Database, noPrimaryKey, rowLimit: 1, rowOffset: 1));
        await AssertThrowsAsync<InvalidOperationException>(() =>
            session.DeleteTableRowAsync(profile.Database, noPrimaryKey, noKeyRows.Rows[0]));
        await AssertThrowsAsync<InvalidOperationException>(() =>
            session.UpdateTableRowAsync(
                profile.Database,
                noPrimaryKey,
                noKeyRows.Rows[0],
                new[] { new TableCellInput("name", TableCellInputMode.Value, "不可修改") }));
        await AssertThrowsAsync<InvalidOperationException>(() =>
            session.InsertTableRowAsync(
                profile.Database,
                noPrimaryKey,
                new[] { new TableCellInput("unknown", TableCellInputMode.Default, string.Empty) }));

        var withoutRowId = new DatabaseObjectInfo(string.Empty, "without_rowid", DatabaseObjectKind.Table);
        var withoutRowIdSnapshot = await session.LoadTableDataAsync(profile.Database, withoutRowId);
        Assert(
            !withoutRowIdSnapshot.Columns.Single(column => column.Name == "id").IsGenerated,
            "SQLite WITHOUT ROWID 的 INTEGER PK 不可誤判為 generated");
        await session.InsertTableRowAsync(
            profile.Database,
            withoutRowId,
            new[]
            {
                new TableCellInput("id", TableCellInputMode.Value, "9"),
                new TableCellInput("name", TableCellInputMode.Value, "需要明確 PK")
            });
        Assert((await session.LoadTableDataAsync(profile.Database, withoutRowId)).Rows.Count == 1, "WITHOUT ROWID 新增失敗");

        var integerColumn = inserted.Columns.Single(column => column.Name == "quantity");
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            integerColumn,
            new TableCellInput("quantity", TableCellInputMode.Value, "not-an-integer")));
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
        await session.ExecuteAsync(database, "CREATE TABLE sample (id BIGINT UNSIGNED PRIMARY KEY AUTO_INCREMENT, name VARCHAR(40) NOT NULL, quantity INT NULL, note VARCHAR(80) NULL);");
        var insert = await session.ExecuteAsync(database, "INSERT INTO sample (name) VALUES ('Punky'), ('Linux');");
        Assert(insert.RowsAffected == 2, "MySQL INSERT 影響列數應為 2");

        var result = await session.ExecuteAsync(database, "SELECT id, name FROM sample ORDER BY id;");
        Assert(result.Rows.Count == 2 && Convert.ToString(result.Rows[1][1]) == "Linux", "MySQL 查詢結果不正確");
        var objects = await session.GetObjectsAsync(database);
        var table = objects.SingleOrDefault(item => item.Name == "sample" && item.Kind == DatabaseObjectKind.Table);
        Assert(table is not null, "MySQL metadata 找不到 sample");
        await VerifySafeTableEditingAsync(
            session,
            database,
            table!,
            id => $"UPDATE sample SET name = 'Concurrent' WHERE id = {id};");
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
        await session.ExecuteAsync(database, "CREATE TABLE sample (id INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY, name VARCHAR(40) NOT NULL, quantity INTEGER NULL, note VARCHAR(80) NULL);");
        var insert = await session.ExecuteAsync(database, "INSERT INTO sample (name) VALUES ('Punky'), ('macOS');");
        Assert(insert.RowsAffected == 2, "PostgreSQL INSERT 影響列數應為 2");

        var result = await session.ExecuteAsync(database, "SELECT id, name FROM sample ORDER BY id;");
        Assert(result.Rows.Count == 2 && Convert.ToString(result.Rows[1][1]) == "macOS", "PostgreSQL 查詢結果不正確");
        var objects = await session.GetObjectsAsync(database);
        var table = objects.SingleOrDefault(item => item.Name == "sample" && item.Schema == "public");
        Assert(table is not null, "PostgreSQL metadata 找不到 public.sample");
        await VerifySafeTableEditingAsync(
            session,
            database,
            table!,
            id => $"UPDATE public.sample SET name = 'Concurrent' WHERE id = {id};");
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
        await session.ExecuteAsync(database, "CREATE TABLE dbo.sample (id INT IDENTITY PRIMARY KEY, name NVARCHAR(40) NOT NULL, quantity INT NULL, note NVARCHAR(80) NULL);");
        var insert = await session.ExecuteAsync(database, "INSERT INTO dbo.sample (name) VALUES (N'Punky'), (N'Linux/macOS');");
        Assert(insert.RowsAffected == 2, "SQL Server INSERT 影響列數應為 2");

        var result = await session.ExecuteAsync(database, "SELECT id, name FROM dbo.sample ORDER BY id;");
        Assert(result.Rows.Count == 2 && Convert.ToString(result.Rows[1][1]) == "Linux/macOS", "SQL Server 查詢結果不正確");
        var objects = await session.GetObjectsAsync(database);
        var table = objects.SingleOrDefault(item => item.Name == "sample" && item.Schema == "dbo");
        Assert(table is not null && table.Kind == DatabaseObjectKind.Table, "SQL Server metadata 找不到 dbo.sample");
        Assert(session.BuildSelectPreview(table!) == "SELECT TOP (200) * FROM [dbo].[sample];", "SQL Server 實機預覽 SQL 不正確");
        await VerifySafeTableEditingAsync(
            session,
            database,
            table!,
            id => $"UPDATE dbo.sample SET name = N'Concurrent' WHERE id = {id};");
    }
    finally
    {
        await session.ExecuteAsync(
            "master",
            $"IF DB_ID(N'{database}') IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END");
    }
}

static async Task VerifySafeTableEditingAsync(
    IDatabaseSession session,
    string database,
    DatabaseObjectInfo table,
    Func<long, string> buildConcurrentUpdateSql)
{
    var before = await session.LoadTableDataAsync(database, table);
    Assert(before.HasPrimaryKey, $"{session.Profile.ProviderDisplayName} 未辨識 Primary Key");
    Assert(before.Columns.Single(column => column.Name == "id").IsGenerated, $"{session.Profile.ProviderDisplayName} 未辨識 generated id");

    await session.InsertTableRowAsync(
        database,
        table,
        new[]
        {
            new TableCellInput("name", TableCellInputMode.Value, "Editor"),
            new TableCellInput("quantity", TableCellInputMode.Value, "7"),
            new TableCellInput("note", TableCellInputMode.Null, string.Empty)
        });
    var insertedSnapshot = await session.LoadTableDataAsync(database, table);
    var inserted = insertedSnapshot.Rows.Single(row => Convert.ToString(row.Values[1]) == "Editor");
    Assert(Convert.ToInt64(inserted.Values[2]) == 7, $"{session.Profile.ProviderDisplayName} 安全新增整數不正確");

    var firstPage = await session.LoadTableDataAsync(database, table, rowLimit: 1, rowOffset: 0);
    var secondPage = await session.LoadTableDataAsync(database, table, rowLimit: 1, rowOffset: 1);
    Assert(firstPage.HasNextPage && !firstPage.HasPreviousPage, $"{session.Profile.ProviderDisplayName} 第一頁導覽狀態不正確");
    Assert(secondPage.HasPreviousPage, $"{session.Profile.ProviderDisplayName} 第二頁導覽狀態不正確");
    Assert(
        !Equals(firstPage.Rows[0].Values[0], secondPage.Rows[0].Values[0]),
        $"{session.Profile.ProviderDisplayName} 分頁未依 Primary Key 前進");

    await session.UpdateTableRowAsync(
        database,
        table,
        inserted,
        new[] { new TableCellInput("quantity", TableCellInputMode.Value, "8") });
    var updatedSnapshot = await session.LoadTableDataAsync(database, table);
    var updated = updatedSnapshot.Rows.Single(row => Convert.ToString(row.Values[1]) == "Editor");
    Assert(Convert.ToInt64(updated.Values[2]) == 8, $"{session.Profile.ProviderDisplayName} 安全修改不正確");

    var id = Convert.ToInt64(updated.Values[0]);
    await session.ExecuteAsync(database, buildConcurrentUpdateSql(id));
    await AssertThrowsAsync<TableDataConflictException>(() =>
        session.UpdateTableRowAsync(
            database,
            table,
            updated,
            new[] { new TableCellInput("quantity", TableCellInputMode.Value, "9") }));
    var concurrentSnapshot = await session.LoadTableDataAsync(database, table);
    var concurrent = concurrentSnapshot.Rows.Single(row => Convert.ToInt64(row.Values[0]) == id);
    Assert(Convert.ToString(concurrent.Values[1]) == "Concurrent", $"{session.Profile.ProviderDisplayName} 衝突時覆蓋了外部變更");

    await session.DeleteTableRowAsync(database, table, concurrent);
    var afterDelete = await session.LoadTableDataAsync(database, table);
    Assert(afterDelete.Rows.All(row => Convert.ToInt64(row.Values[0]) != id), $"{session.Profile.ProviderDisplayName} 安全刪除失敗");
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

static async Task AssertThrowsAsync<TException>(Func<Task> action)
    where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"預期丟出 {typeof(TException).Name}");
}
