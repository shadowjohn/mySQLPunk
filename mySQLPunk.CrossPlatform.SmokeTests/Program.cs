using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MySqlPunk.Core.Models;
using MySqlPunk.Core.Providers;
using MySqlPunk.Core.Services;
using MySqlConnector;
using Npgsql;

var tests = new List<(string Name, Func<Task> Run)>
{
    ("連線設定不保存密碼", ProfileStoreDoesNotPersistPasswordsAsync),
    ("查詢結果安全匯出", QueryResultExportFormatsAsync),
    ("Linux Secret Service 安全 round-trip", LinuxSecretServiceRoundTripAsync),
    ("macOS Keychain 安全 round-trip", MacOsKeychainRoundTripAsync),
    ("SQLite 查詢與 DDL/DML", SqliteExecutesQueriesAsync),
    ("SQLite metadata 與預覽 SQL", SqliteLoadsMetadataAsync),
    ("Table 資料安全編輯與衝突防護", TableDataEditingAsync),
    ("跨平台安全更新與下載", CrossPlatformUpdateAssetsAsync),
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

static async Task CrossPlatformUpdateAssetsAsync()
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

    var directory = CreateTemporaryDirectory();
    try
    {
        var packageBytes = Encoding.UTF8.GetBytes("self-contained package 測試內容");
        var expectedHash = Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant();
        var sidecarBytes = Encoding.UTF8.GetBytes($"{expectedHash}  {update.PackageFileName}\n");
        HttpClient CreateClient(byte[] servedPackage) => new(new StubHttpMessageHandler(request =>
        {
            var contents = request.RequestUri == update.Sha256DownloadUri ? sidecarBytes : servedPackage;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(contents)
            };
        }));

        var destinationPath = Path.Combine(directory, update.PackageFileName);
        await File.WriteAllTextAsync(destinationPath, "old-package-must-be-replaced-only-after-verification");
        using (var client = CreateClient(packageBytes))
        {
            var download = await new CrossPlatformUpdateService(client).DownloadPackageAsync(
                update,
                destinationPath);
            Assert(download.Bytes == packageBytes.Length, "安全下載大小不正確");
            Assert(download.Sha256 == expectedHash, "安全下載 SHA-256 不正確");
            Assert((await File.ReadAllBytesAsync(destinationPath)).SequenceEqual(packageBytes), "驗證成功後未原子替換目標檔");
        }

        await File.WriteAllTextAsync(destinationPath, "keep-existing-package");
        using (var client = CreateClient(Encoding.UTF8.GetBytes("corrupted package")))
        {
            await AssertThrowsAsync<InvalidDataException>(() =>
                new CrossPlatformUpdateService(client).DownloadPackageAsync(update, destinationPath));
        }
        Assert(await File.ReadAllTextAsync(destinationPath) == "keep-existing-package", "SHA-256 失敗時覆蓋了既有檔案");
        Assert(!Directory.EnumerateFiles(directory).Any(path => path.EndsWith(".tmp", StringComparison.Ordinal)), "更新失敗留下暫存檔");

        AssertThrows<InvalidDataException>(() => CrossPlatformUpdateService.ParseChecksumSidecar(
            Encoding.UTF8.GetBytes($"{expectedHash}  other-package.tar.gz\n"),
            update.PackageFileName));
        AssertThrows<InvalidDataException>(() => CrossPlatformUpdateService.ParseChecksumSidecar(
            Encoding.UTF8.GetBytes($"{expectedHash}  {update.PackageFileName}\n{expectedHash}  duplicate\n"),
            update.PackageFileName));
        AssertThrows<InvalidDataException>(() => CrossPlatformUpdateService.ParseChecksumSidecar(
            new byte[] { 0xFF, 0xFE, 0xFD },
            update.PackageFileName));
        AssertThrows<InvalidDataException>(() => CrossPlatformUpdateService.ParseChecksumSidecar(
            Encoding.UTF8.GetBytes($"{expectedHash}  **{update.PackageFileName}\n"),
            update.PackageFileName));

        var applyScriptPath = Path.Combine(directory, "apply-update.sh");
        await WriteExecutableScriptAsync(applyScriptPath, "#!/usr/bin/env bash\nexit 0\n");
        var applyStartInfo = new CrossPlatformUpdateService().BuildLinuxApplyStartInfo(
            update,
            new CrossPlatformUpdateDownload(destinationPath, packageBytes.Length, expectedHash),
            applyScriptPath,
            12345,
            "0123456789abcdef0123456789abcdef");
        Assert(applyStartInfo.FileName == applyScriptPath && !applyStartInfo.UseShellExecute, "Linux updater 啟動方式不正確");
        Assert(
            applyStartInfo.ArgumentList.SequenceEqual(new[]
            {
                "--archive", destinationPath,
                "--sha256", expectedHash,
                "--version", "1.0.0.20",
                "--runtime", "linux-x64",
                "--wait-pid", "12345",
                "--lock-token", "0123456789abcdef0123456789abcdef"
            }),
            "Linux updater 參數未使用獨立 ArgumentList 或內容不正確");

        var resultPath = Path.Combine(directory, "last-apply-result");
        await File.WriteAllTextAsync(
            resultPath,
            $"status=rollback\nversion=1.0.0.20\nruntime=linux-x64\nmessage=Startup failed.\nlog={Path.Combine(directory, "apply.log")}\n");
        var applyResult = new CrossPlatformUpdateService().ReadAndClearLinuxApplyResult(resultPath);
        Assert(applyResult is { WasRolledBack: true, Version: "1.0.0.20" }, "Linux rollback 結果解析不正確");
        Assert(!File.Exists(resultPath), "Linux rollback 結果顯示後未清除");
        AssertThrows<InvalidDataException>(() => CrossPlatformUpdateService.ParseLinuxApplyResult(
            $"status=success\nversion=1.0.0.20\nruntime=linux-x64\nmessage=Unexpected.\nlog={Path.Combine(directory, "apply.log")}\n"));

        if (OperatingSystem.IsLinux())
        {
            var previousStateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
            Environment.SetEnvironmentVariable("XDG_STATE_HOME", Path.Combine(directory, "lock-state"));
            try
            {
                var service = new CrossPlatformUpdateService();
                using (var lockOwner = service.StartLinuxApply(
                           update,
                           new CrossPlatformUpdateDownload(destinationPath, packageBytes.Length, expectedHash),
                           applyScriptPath,
                           Environment.ProcessId))
                {
                    await lockOwner.WaitForExitAsync();
                    Assert(lockOwner.ExitCode == 0, "Linux updater lock 測試程序未正常結束");
                }

                AssertThrows<InvalidOperationException>(() =>
                {
                    using var unexpected = service.StartLinuxApply(
                        update,
                        new CrossPlatformUpdateDownload(destinationPath, packageBytes.Length, expectedHash),
                        applyScriptPath,
                        Environment.ProcessId);
                });

                var lockPath = CrossPlatformUpdateService.ResolveLinuxApplyLockPath();
                await File.WriteAllTextAsync(
                    lockPath,
                    "token=0123456789abcdef0123456789abcdef\npid=2147483647\n");
                using (var recovered = service.StartLinuxApply(
                           update,
                           new CrossPlatformUpdateDownload(destinationPath, packageBytes.Length, expectedHash),
                           applyScriptPath,
                           Environment.ProcessId))
                {
                    await recovered.WaitForExitAsync();
                    Assert(recovered.ExitCode == 0, "Linux updater 未從 stale lock 恢復");
                }
                File.Delete(lockPath);
            }
            finally
            {
                Environment.SetEnvironmentVariable("XDG_STATE_HOME", previousStateHome);
            }
        }
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
                note TEXT NULL DEFAULT 'database-default',
                payload BLOB NULL,
                metadata JSON NULL,
                document XML NULL
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
        var binaryColumn = empty.Columns.Single(column => column.Name == "payload");
        Assert(binaryColumn is { ValueKind: TableColumnValueKind.Binary, IsEditable: true }, "SQLite BLOB 應可安全編輯");
        var jsonColumn = empty.Columns.Single(column => column.Name == "metadata");
        Assert(jsonColumn is { ValueKind: TableColumnValueKind.Json, IsEditable: true }, "SQLite JSON 應可驗證後編輯");
        var xmlColumn = empty.Columns.Single(column => column.Name == "document");
        Assert(xmlColumn is { ValueKind: TableColumnValueKind.Xml, IsEditable: true }, "SQLite XML 應可驗證後編輯");

        const string parameterizedName = "Punky '); DROP TABLE editor_sample;--";
        await session.InsertTableRowAsync(
            profile.Database,
            table,
            new[]
            {
                new TableCellInput("name", TableCellInputMode.Value, parameterizedName),
                new TableCellInput("quantity", TableCellInputMode.Value, "7"),
                new TableCellInput("note", TableCellInputMode.Default, string.Empty),
                new TableCellInput("payload", TableCellInputMode.Value, "0x00ff10"),
                new TableCellInput("metadata", TableCellInputMode.Value, "  {\"stage\":\"insert\",\"n\":1}  "),
                new TableCellInput("document", TableCellInputMode.Value, "  <item stage=\"insert\"><n>1</n></item>  ")
            });
        var inserted = await session.LoadTableDataAsync(profile.Database, table);
        Assert(inserted.Rows.Count == 1, "安全新增後應有一列");
        Assert(Convert.ToString(inserted.Rows[0].Values[1]) == parameterizedName, "安全新增參數化字串不正確");
        Assert(Convert.ToInt64(inserted.Rows[0].Values[2]) == 7, "安全新增整數不正確");
        Assert(Convert.ToString(inserted.Rows[0].Values[3]) == "database-default", "資料庫 DEFAULT 未套用");
        Assert(
            inserted.Rows[0].Values[4] is byte[] insertedPayload && insertedPayload.SequenceEqual(new byte[] { 0x00, 0xFF, 0x10 }),
            "SQLite BLOB 安全新增不正確");
        using (var insertedJson = JsonDocument.Parse(Convert.ToString(inserted.Rows[0].Values[5])!))
        {
            Assert(insertedJson.RootElement.GetProperty("stage").GetString() == "insert", "SQLite JSON 安全新增不正確");
            Assert(insertedJson.RootElement.GetProperty("n").GetInt32() == 1, "SQLite JSON 數字型別不正確");
        }
        var insertedXml = System.Xml.Linq.XDocument.Parse(Convert.ToString(inserted.Rows[0].Values[6])!);
        Assert((string?)insertedXml.Root?.Attribute("stage") == "insert", "SQLite XML 安全新增不正確");
        Assert((string?)insertedXml.Root?.Element("n") == "1", "SQLite XML 子元素不正確");
        Assert(
            TableCellValueConverter.MatchesOriginal(
                binaryColumn,
                new TableCellInput("payload", TableCellInputMode.Value, "0X00fF10"),
                inserted.Rows[0].Values[4]),
            "Binary 原值比較應忽略十六進位大小寫");

        var original = inserted.Rows[0];
        await session.UpdateTableRowAsync(
            profile.Database,
            table,
            original,
            new[]
            {
                new TableCellInput("name", TableCellInputMode.Value, "崩琦"),
                new TableCellInput("quantity", TableCellInputMode.Null, string.Empty),
                new TableCellInput("payload", TableCellInputMode.Value, "0xCAFE"),
                new TableCellInput("metadata", TableCellInputMode.Value, "{\"stage\":\"updated\",\"ok\":true}"),
                new TableCellInput("document", TableCellInputMode.Value, "<item stage=\"updated\"><ok>true</ok></item>")
            });
        var updated = await session.LoadTableDataAsync(profile.Database, table);
        Assert(Convert.ToString(updated.Rows[0].Values[1]) == "崩琦", "安全修改字串不正確");
        Assert(updated.Rows[0].Values[2] is null, "安全修改 NULL 不正確");
        Assert(
            updated.Rows[0].Values[4] is byte[] updatedPayload && updatedPayload.SequenceEqual(new byte[] { 0xCA, 0xFE }),
            "SQLite BLOB 安全修改不正確");
        using (var updatedJson = JsonDocument.Parse(Convert.ToString(updated.Rows[0].Values[5])!))
        {
            Assert(updatedJson.RootElement.GetProperty("stage").GetString() == "updated", "SQLite JSON 安全修改不正確");
            Assert(updatedJson.RootElement.GetProperty("ok").GetBoolean(), "SQLite JSON 布林型別不正確");
        }
        var updatedXml = System.Xml.Linq.XDocument.Parse(Convert.ToString(updated.Rows[0].Values[6])!);
        Assert((string?)updatedXml.Root?.Attribute("stage") == "updated", "SQLite XML 安全修改不正確");
        Assert((string?)updatedXml.Root?.Element("ok") == "true", "SQLite XML 子元素修改不正確");

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
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            binaryColumn,
            new TableCellInput("payload", TableCellInputMode.Value, "0xABC")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            binaryColumn,
            new TableCellInput("payload", TableCellInputMode.Value, "0xGG")));
        Assert(
            TableCellValueConverter.Parse(
                binaryColumn,
                new TableCellInput("payload", TableCellInputMode.Value, "0x")) is byte[] { Length: 0 },
            "0x 應代表空 binary，而不是 NULL");
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            binaryColumn,
            new TableCellInput(
                "payload",
                TableCellInputMode.Value,
                "0x" + new string('A', TableCellValueConverter.MaximumEditableBinaryBytes * 2 + 2))));
        Assert(
            TableCellValueConverter.IsBinaryValueTooLargeToEdit(
                binaryColumn,
                new byte[TableCellValueConverter.MaximumEditableBinaryBytes + 1]),
            "超過 1 MiB 的既有 binary 應維持唯讀");
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            jsonColumn,
            new TableCellInput("metadata", TableCellInputMode.Value, "{\"missing\":}")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            jsonColumn,
            new TableCellInput("metadata", TableCellInputMode.Value, "{\"trailing\":true,}")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            jsonColumn,
            new TableCellInput(
                "metadata",
                TableCellInputMode.Value,
                "\"" + new string('x', TableCellValueConverter.MaximumEditableStructuredTextCharacters) + "\"")));
        Assert(
            TableCellValueConverter.IsStructuredTextTooLargeToEdit(
                jsonColumn,
                new string('x', TableCellValueConverter.MaximumEditableStructuredTextCharacters + 1)),
            "超過 1 MiB 字元的既有 JSON 應維持唯讀");
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            xmlColumn,
            new TableCellInput("document", TableCellInputMode.Value, "<item>")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            xmlColumn,
            new TableCellInput("document", TableCellInputMode.Value, "<one/><two/>")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            xmlColumn,
            new TableCellInput(
                "document",
                TableCellInputMode.Value,
                "<!DOCTYPE item [<!ENTITY xxe SYSTEM 'file:///etc/passwd'>]><item>&xxe;</item>")));
        var overlyDeepXml = "<root>" + string.Concat(Enumerable.Repeat("<item>", 65)) +
                           string.Concat(Enumerable.Repeat("</item>", 65)) + "</root>";
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            xmlColumn,
            new TableCellInput("document", TableCellInputMode.Value, overlyDeepXml)));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            xmlColumn,
            new TableCellInput(
                "document",
                TableCellInputMode.Value,
                "<item>" + new string('x', TableCellValueConverter.MaximumEditableStructuredTextCharacters) + "</item>")));
        Assert(
            TableCellValueConverter.IsStructuredTextTooLargeToEdit(
                xmlColumn,
                new string('x', TableCellValueConverter.MaximumEditableStructuredTextCharacters + 1)),
            "超過 1 MiB 字元的既有 XML 應維持唯讀");
        var inetColumn = new TableColumnInfo(0, "address", "inet", true, false, false, false, TableColumnValueKind.NetworkAddress);
        var cidrColumn = inetColumn with { Name = "subnet", DataTypeName = "cidr" };
        var macColumn = inetColumn with { Name = "mac", DataTypeName = "macaddr" };
        var mac8Column = inetColumn with { Name = "mac8", DataTypeName = "macaddr8" };
        Assert(
            Equals(
                TableCellValueConverter.Parse(
                    inetColumn,
                    new TableCellInput("address", TableCellInputMode.Value, "2001:0db8::10/64")),
                "2001:db8::10/64"),
            "IPv6 inet 應正規化但保留 prefix");
        Assert(
            Equals(
                TableCellValueConverter.Parse(
                    cidrColumn,
                    new TableCellInput("subnet", TableCellInputMode.Value, "192.0.2.0/24")),
                "192.0.2.0/24"),
            "IPv4 cidr 應保留網段");
        Assert(
            Equals(
                TableCellValueConverter.Parse(
                    macColumn,
                    new TableCellInput("mac", TableCellInputMode.Value, "08-00-2B-01-02-03")),
                "08:00:2b:01:02:03"),
            "macaddr 應正規化為六段小寫 hex");
        Assert(
            Equals(
                TableCellValueConverter.Parse(
                    mac8Column,
                    new TableCellInput("mac8", TableCellInputMode.Value, "08:00:2b:ff:fe:01:02:03")),
                "08:00:2b:ff:fe:01:02:03"),
            "macaddr8 應接受八段 hex");
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            inetColumn,
            new TableCellInput("address", TableCellInputMode.Value, "192.0.2.1/33")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            inetColumn,
            new TableCellInput("address", TableCellInputMode.Value, "fe80::1%1/64")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            cidrColumn,
            new TableCellInput("subnet", TableCellInputMode.Value, "192.0.2.1/24")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            macColumn,
            new TableCellInput("mac", TableCellInputMode.Value, "08:00:2b:01:02:03:04:05")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            mac8Column,
            new TableCellInput("mac8", TableCellInputMode.Value, "not-a-mac")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            macColumn,
            new TableCellInput("mac", TableCellInputMode.Value, "08:00-2b:01:02:03")));
        var bit8Column = new TableColumnInfo(0, "flags", "bit(8)", true, false, false, false, TableColumnValueKind.UnsignedInteger);
        Assert(
            Equals(
                TableCellValueConverter.Parse(
                    bit8Column,
                    new TableCellInput("flags", TableCellInputMode.Value, "255")),
                255UL),
            "BIT(8) 應接受 0 到 255 的十進位值");
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            bit8Column,
            new TableCellInput("flags", TableCellInputMode.Value, "256")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            bit8Column,
            new TableCellInput("flags", TableCellInputMode.Value, "-1")));
        var bit64Column = bit8Column with { Name = "flags64", DataTypeName = "bit(64)" };
        Assert(
            Equals(
                TableCellValueConverter.Parse(
                    bit64Column,
                    new TableCellInput("flags64", TableCellInputMode.Value, ulong.MaxValue.ToString(CultureInfo.InvariantCulture))),
                ulong.MaxValue),
            "BIT(64) 應接受 UInt64 最大值");
        var fixedBitsColumn = new TableColumnInfo(0, "bits", "bit(8)", true, false, false, false, TableColumnValueKind.BitString);
        var varyingBitsColumn = fixedBitsColumn with { Name = "varbits", DataTypeName = "bit varying(16)" };
        Assert(
            Equals(
                TableCellValueConverter.Parse(
                    fixedBitsColumn,
                    new TableCellInput("bits", TableCellInputMode.Value, "10100101")),
                "10100101"),
            "PostgreSQL BIT(8) 應接受八位 bit string");
        Assert(
            Equals(
                TableCellValueConverter.Parse(
                    varyingBitsColumn,
                    new TableCellInput("varbits", TableCellInputMode.Value, "10101")),
                "10101"),
            "PostgreSQL BIT VARYING 應接受上限內的 bit string");
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            fixedBitsColumn,
            new TableCellInput("bits", TableCellInputMode.Value, "101")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            fixedBitsColumn,
            new TableCellInput("bits", TableCellInputMode.Value, "10102010")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            varyingBitsColumn,
            new TableCellInput("varbits", TableCellInputMode.Value, new string('1', 17))));
        var timeZoneColumn = new TableColumnInfo(
            0,
            "alarm",
            "time with time zone",
            true,
            false,
            false,
            false,
            TableColumnValueKind.TimeWithTimeZone);
        Assert(
            TableCellValueConverter.Parse(
                timeZoneColumn,
                new TableCellInput("alarm", TableCellInputMode.Value, "23:59:59.123456+08:00")) is DateTimeOffset
            {
                Hour: 23,
                Minute: 59,
                Second: 59,
                Offset: { Hours: 8 }
            },
            "PostgreSQL timetz 應保留時間與 offset");
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            timeZoneColumn,
            new TableCellInput("alarm", TableCellInputMode.Value, "12:34:56")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            timeZoneColumn,
            new TableCellInput("alarm", TableCellInputMode.Value, "25:00:00+08:00")));
        var mySqlTimeColumn = new TableColumnInfo(
            0,
            "duration",
            "time(6)",
            true,
            false,
            false,
            false,
            TableColumnValueKind.MySqlTime);
        Assert(
            Equals(
                TableCellValueConverter.Parse(
                    mySqlTimeColumn,
                    new TableCellInput("duration", TableCellInputMode.Value, "838:59:58.123456")),
                TimeSpan.FromTicks(
                    ((838L * 3600 + 59 * 60 + 58) * TimeSpan.TicksPerSecond) + 1_234_560)),
            "MySQL TIME 應支援超過 24 小時與微秒精度");
        Assert(
            Equals(
                TableCellValueConverter.Parse(
                    mySqlTimeColumn,
                    new TableCellInput("duration", TableCellInputMode.Value, "838:59:59")),
                TimeSpan.FromSeconds(838L * 3600 + 59 * 60 + 59)),
            "MySQL TIME 應接受絕對上限 838:59:59");
        Assert(
            Equals(
                TableCellValueConverter.Parse(
                    mySqlTimeColumn,
                    new TableCellInput("duration", TableCellInputMode.Value, "-838:59:58.654321")),
                TimeSpan.FromTicks(
                    -(((838L * 3600 + 59 * 60 + 58) * TimeSpan.TicksPerSecond) + 6_543_210))),
            "MySQL TIME 應支援負值下界與微秒精度");
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            mySqlTimeColumn,
            new TableCellInput("duration", TableCellInputMode.Value, "839:00:00")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            mySqlTimeColumn,
            new TableCellInput("duration", TableCellInputMode.Value, "12:34:56.1234567")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            mySqlTimeColumn,
            new TableCellInput("duration", TableCellInputMode.Value, "838:59:59.000001")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            mySqlTimeColumn with { DataTypeName = "time" },
            new TableCellInput("duration", TableCellInputMode.Value, "12:34:56.1")));
        var mySqlYearColumn = new TableColumnInfo(
            0,
            "release_year",
            "year",
            true,
            false,
            false,
            false,
            TableColumnValueKind.MySqlYear);
        Assert(
            Convert.ToUInt16(TableCellValueConverter.Parse(
                mySqlYearColumn,
                new TableCellInput("release_year", TableCellInputMode.Value, "0"))) == 0,
            "MySQL YEAR 應接受 zero year");
        Assert(
            Convert.ToUInt16(TableCellValueConverter.Parse(
                mySqlYearColumn,
                new TableCellInput("release_year", TableCellInputMode.Value, "2155"))) == 2155,
            "MySQL YEAR 應接受 2155 上界");
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            mySqlYearColumn,
            new TableCellInput("release_year", TableCellInputMode.Value, "1900")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            mySqlYearColumn,
            new TableCellInput("release_year", TableCellInputMode.Value, "2156")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            mySqlYearColumn,
            new TableCellInput("release_year", TableCellInputMode.Value, "70")));
        var exactDecimalColumn = new TableColumnInfo(
            0,
            "amount",
            "decimal(65,30)",
            true,
            false,
            false,
            false,
            TableColumnValueKind.ExactDecimal);
        var exactDecimalText =
            "12345678901234567890123456789012345.123456789012345678901234567890";
        Assert(
            TableCellValueConverter.Parse(
                exactDecimalColumn,
                new TableCellInput("amount", TableCellInputMode.Value, exactDecimalText)) is ExactDecimalValue
            {
                Text: var parsedExactDecimal
            } && parsedExactDecimal == exactDecimalText,
            "DECIMAL(65,30) 應保留超過 .NET decimal 上限的完整文字精度");
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            exactDecimalColumn,
            new TableCellInput("amount", TableCellInputMode.Value, new string('9', 36) + ".0")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            exactDecimalColumn,
            new TableCellInput("amount", TableCellInputMode.Value, "1." + new string('9', 31))));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            exactDecimalColumn,
            new TableCellInput("amount", TableCellInputMode.Value, "1e10")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            exactDecimalColumn,
            new TableCellInput("amount", TableCellInputMode.Value, "1,000.00")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            exactDecimalColumn with
            {
                DataTypeName = "decimal(65,30) unsigned",
                StorageDataTypeName = "decimal(65,30) unsigned"
            },
            new TableCellInput("amount", TableCellInputMode.Value, "-1.0")));
        var unrestrictedNumeric = exactDecimalColumn with
        {
            DataTypeName = "numeric",
            StorageDataTypeName = "numeric"
        };
        Assert(
            TableCellValueConverter.Parse(
                unrestrictedNumeric,
                new TableCellInput("amount", TableCellInputMode.Value, new string('7', 200))) is ExactDecimalValue,
            "PostgreSQL unrestricted numeric 應接受超過 29 位的精確數字");
        var fractionalOnlyNumeric = exactDecimalColumn with
        {
            DataTypeName = "numeric(3,5)",
            StorageDataTypeName = "numeric(3,5)"
        };
        Assert(
            TableCellValueConverter.Parse(
                fractionalOnlyNumeric,
                new TableCellInput("amount", TableCellInputMode.Value, "0.00123")) is ExactDecimalValue,
            "PostgreSQL numeric scale 大於 precision 時應接受足夠的前導小數零");
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            fractionalOnlyNumeric,
            new TableCellInput("amount", TableCellInputMode.Value, "0.01234")));
        var negativeScaleNumeric = exactDecimalColumn with
        {
            DataTypeName = "numeric(2,-3)",
            StorageDataTypeName = "numeric(2,-3)"
        };
        Assert(
            TableCellValueConverter.Parse(
                negativeScaleNumeric,
                new TableCellInput("amount", TableCellInputMode.Value, "12000")) is ExactDecimalValue,
            "PostgreSQL negative numeric scale 應接受不需取整的千位數值");
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            negativeScaleNumeric,
            new TableCellInput("amount", TableCellInputMode.Value, "12345")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            negativeScaleNumeric,
            new TableCellInput("amount", TableCellInputMode.Value, "12000.0")));
        var oversizedExactDecimal = new string(
            '9',
            TableCellValueConverter.MaximumEditableStructuredTextCharacters + 1);
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            unrestrictedNumeric,
            new TableCellInput("amount", TableCellInputMode.Value, oversizedExactDecimal)));
        Assert(
            TableCellValueConverter.IsStructuredTextTooLargeToEdit(
                unrestrictedNumeric,
                oversizedExactDecimal),
            "既有 exact decimal 超過 1 MiB 時應維持唯讀");
        var aliasExactDecimal = exactDecimalColumn with
        {
            DataTypeName = "[dbo].[precise_amount] (decimal(18,6))",
            StorageDataTypeName = "decimal(18,6)"
        };
        Assert(
            TableCellValueConverter.GetExactDecimalDefinition(aliasExactDecimal) ==
            new ExactDecimalDefinition(18, 6, false),
            "SQL Server alias decimal 應依 base type 保留 precision／scale");
        Assert(
            TableCellValueConverter.Parse(
                aliasExactDecimal,
                new TableCellInput("amount", TableCellInputMode.Value, "123456789012.123456")) is ExactDecimalValue,
            "SQL Server alias decimal 應使用 base type 安全解析");
        var intervalColumn = new TableColumnInfo(
            0,
            "duration",
            "interval",
            true,
            false,
            false,
            false,
            TableColumnValueKind.Interval);
        Assert(
            TableCellValueConverter.Parse(
                intervalColumn,
                new TableCellInput(
                    "duration",
                    TableCellInputMode.Value,
                    "months=-14;days=3;microseconds=-14706123456")) is IntervalComponents
            {
                Months: -14,
                Days: 3,
                Microseconds: -14_706_123_456
            },
            "PostgreSQL interval 應保留 months、days 與 microseconds 三個 component");
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            intervalColumn,
            new TableCellInput("duration", TableCellInputMode.Value, "1 month 2 days")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            intervalColumn,
            new TableCellInput("duration", TableCellInputMode.Value, "months=1;days=2;microseconds=3;extra=4")));
        var lsnColumn = new TableColumnInfo(
            0,
            "wal_position",
            "pg_lsn",
            true,
            false,
            false,
            false,
            TableColumnValueKind.LogSequenceNumber);
        Assert(
            Convert.ToString(TableCellValueConverter.Parse(
                lsnColumn,
                new TableCellInput("wal_position", TableCellInputMode.Value, " 16/b374d848 "))) ==
            "16/B374D848",
            "PostgreSQL pg_lsn 應驗證 32-bit hex 分量並正規化為大寫");
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            lsnColumn,
            new TableCellInput("wal_position", TableCellInputMode.Value, "100000000/0")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            lsnColumn,
            new TableCellInput("wal_position", TableCellInputMode.Value, "16//B374D848")));
        var oidColumn = new TableColumnInfo(
            0,
            "object_id",
            "oid",
            true,
            false,
            false,
            false,
            TableColumnValueKind.UnsignedInteger);
        Assert(
            Convert.ToUInt64(TableCellValueConverter.Parse(
                oidColumn,
                new TableCellInput("object_id", TableCellInputMode.Value, uint.MaxValue.ToString(CultureInfo.InvariantCulture)))) ==
            uint.MaxValue,
            "PostgreSQL oid 應接受完整 UInt32 範圍");
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            oidColumn,
            new TableCellInput("object_id", TableCellInputMode.Value, "-1")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            oidColumn,
            new TableCellInput("object_id", TableCellInputMode.Value, "4294967296")));
        var fullTextVectorColumn = new TableColumnInfo(
            0,
            "search_vector",
            "tsvector",
            true,
            false,
            false,
            false,
            TableColumnValueKind.FullTextVector);
        Assert(
            Convert.ToString(TableCellValueConverter.Parse(
                fullTextVectorColumn,
                new TableCellInput("search_vector", TableCellInputMode.Value, " 'dog':2B 'cat':1A,3 "))) ==
            "'dog':2B 'cat':1A,3",
            "PostgreSQL tsvector 應去除外圍空白並交由 PostgreSQL 權威 parser 驗證");
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            fullTextVectorColumn,
            new TableCellInput("search_vector", TableCellInputMode.Value, "'cat'\0:1")));
        var fullTextQueryColumn = new TableColumnInfo(
            0,
            "search_query",
            "tsquery",
            true,
            false,
            false,
            false,
            TableColumnValueKind.FullTextQuery);
        Assert(
            Convert.ToString(TableCellValueConverter.Parse(
                fullTextQueryColumn,
                new TableCellInput("search_query", TableCellInputMode.Value, " 'cat':A & !'dog':* "))) ==
            "'cat':A & !'dog':*",
            "PostgreSQL tsquery 應保留 weight、NOT 與 prefix operator");
        var rangeColumn = new TableColumnInfo(
            0,
            "integer_span",
            "int4range",
            true,
            false,
            false,
            false,
            TableColumnValueKind.PostgreSqlRange);
        Assert(
            Convert.ToString(TableCellValueConverter.Parse(
                rangeColumn,
                new TableCellInput("integer_span", TableCellInputMode.Value, "  [1,10)  "))) ==
            "[1,10)",
            "PostgreSQL range 應去除外圍空白並交由 PostgreSQL 權威 parser 驗證");
        var multirangeColumn = rangeColumn with
        {
            Name = "integer_spans",
            DataTypeName = "int4multirange"
        };
        Assert(
            Convert.ToString(TableCellValueConverter.Parse(
                multirangeColumn,
                new TableCellInput("integer_spans", TableCellInputMode.Value, " {[1,5),[10,15)} "))) ==
            "{[1,5),[10,15)}",
            "PostgreSQL multirange 應保留多段 range 文字");
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            rangeColumn,
            new TableCellInput("integer_span", TableCellInputMode.Value, "[1,\0)")));
        var oversizedRange = new string('x', TableCellValueConverter.MaximumEditableStructuredTextCharacters + 1);
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            rangeColumn,
            new TableCellInput("integer_span", TableCellInputMode.Value, oversizedRange)));
        Assert(
            TableCellValueConverter.IsStructuredTextTooLargeToEdit(rangeColumn, oversizedRange),
            "既有 PostgreSQL range 超過 1 MiB 時應維持唯讀");
        var arrayColumn = new TableColumnInfo(
            0,
            "numbers",
            "int4[]",
            true,
            false,
            false,
            false,
            TableColumnValueKind.PostgreSqlArray);
        Assert(
            Convert.ToString(TableCellValueConverter.Parse(
                arrayColumn,
                new TableCellInput("numbers", TableCellInputMode.Value, "  {{1,2},{3,4}}  "))) ==
            "{{1,2},{3,4}}",
            "PostgreSQL array 應去除外圍空白並保留多維結構");
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            arrayColumn,
            new TableCellInput("numbers", TableCellInputMode.Value, "{1,\0}")));
        var oversizedArray = new string('x', TableCellValueConverter.MaximumEditableStructuredTextCharacters + 1);
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            arrayColumn,
            new TableCellInput("numbers", TableCellInputMode.Value, oversizedArray)));
        Assert(
            TableCellValueConverter.IsStructuredTextTooLargeToEdit(arrayColumn, oversizedArray),
            "既有 PostgreSQL array 超過 1 MiB 時應維持唯讀");
        var geometricColumn = new TableColumnInfo(
            0,
            "location",
            "point",
            true,
            false,
            false,
            false,
            TableColumnValueKind.PostgreSqlGeometric);
        Assert(
            Convert.ToString(TableCellValueConverter.Parse(
                geometricColumn,
                new TableCellInput("location", TableCellInputMode.Value, "  (1.5,2.5)  "))) ==
            "(1.5,2.5)",
            "PostgreSQL geometric 應去除外圍空白並交由 PostgreSQL 權威 parser 驗證");
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            geometricColumn,
            new TableCellInput("location", TableCellInputMode.Value, "(1,\0)")));
        var oversizedGeometric = new string(
            'x',
            TableCellValueConverter.MaximumEditableStructuredTextCharacters + 1);
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            geometricColumn,
            new TableCellInput("location", TableCellInputMode.Value, oversizedGeometric)));
        Assert(
            TableCellValueConverter.IsStructuredTextTooLargeToEdit(geometricColumn, oversizedGeometric),
            "既有 PostgreSQL geometric 超過 1 MiB 時應維持唯讀");
        var serverTextColumn = new TableColumnInfo(
            0,
            "search_path",
            "jsonpath",
            true,
            false,
            false,
            false,
            TableColumnValueKind.PostgreSqlServerValidatedText);
        Assert(
            Convert.ToString(TableCellValueConverter.Parse(
                serverTextColumn,
                new TableCellInput(
                    "search_path",
                    TableCellInputMode.Value,
                    "  $.store.book[*] ? (@.price < 10)  "))) ==
            "$.store.book[*] ? (@.price < 10)",
            "PostgreSQL server-validated text 應去除外圍空白並保留內容");
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            serverTextColumn,
            new TableCellInput("search_path", TableCellInputMode.Value, "$\0")));
        var oversizedServerText = new string(
            'x',
            TableCellValueConverter.MaximumEditableStructuredTextCharacters + 1);
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            serverTextColumn,
            new TableCellInput("search_path", TableCellInputMode.Value, oversizedServerText)));
        Assert(
            TableCellValueConverter.IsStructuredTextTooLargeToEdit(serverTextColumn, oversizedServerText),
            "既有 PostgreSQL server-validated text 超過 1 MiB 時應維持唯讀");
        var hierarchyIdColumn = new TableColumnInfo(
            0,
            "path",
            "hierarchyid",
            true,
            false,
            false,
            false,
            TableColumnValueKind.SqlServerHierarchyId);
        Assert(
            Convert.ToString(TableCellValueConverter.Parse(
                hierarchyIdColumn,
                new TableCellInput("path", TableCellInputMode.Value, "  /1/2.5/  "))) == "/1/2.5/",
            "SQL Server hierarchyid 應去除外圍空白並交由 server 驗證");
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            hierarchyIdColumn,
            new TableCellInput("path", TableCellInputMode.Value, "/1/\0/")));
        var oversizedHierarchyId = new string(
            '1',
            TableCellValueConverter.MaximumEditableStructuredTextCharacters + 1);
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            hierarchyIdColumn,
            new TableCellInput("path", TableCellInputMode.Value, oversizedHierarchyId)));
        Assert(
            TableCellValueConverter.IsStructuredTextTooLargeToEdit(hierarchyIdColumn, oversizedHierarchyId),
            "既有 SQL Server hierarchyid 超過 1 MiB 時應維持唯讀");
        var spatialColumn = new TableColumnInfo(
            0,
            "shape",
            "geometry",
            true,
            false,
            false,
            false,
            TableColumnValueKind.Spatial);
        Assert(
            Convert.ToString(TableCellValueConverter.Parse(
                spatialColumn,
                new TableCellInput("shape", TableCellInputMode.Value, "  srid=4326; POINT (121.5 25)  "))) ==
            "SRID=4326;POINT (121.5 25)",
            "Spatial 應正規化 SRID prefix 並保留 WKT");
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            spatialColumn,
            new TableCellInput("shape", TableCellInputMode.Value, "SRID=-1;POINT (1 2)")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            spatialColumn,
            new TableCellInput("shape", TableCellInputMode.Value, "SRID=0;")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            spatialColumn,
            new TableCellInput("shape", TableCellInputMode.Value, "SRID=0;POINT (1\0 2)")));
        var oversizedSpatial =
            "SRID=0;" + new string('x', TableCellValueConverter.MaximumEditableStructuredTextCharacters);
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            spatialColumn,
            new TableCellInput("shape", TableCellInputMode.Value, oversizedSpatial)));
        Assert(
            TableCellValueConverter.IsStructuredTextTooLargeToEdit(spatialColumn, oversizedSpatial),
            "既有 spatial 超過 1 MiB 時應維持唯讀");
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
        await session.ExecuteAsync(database, "CREATE TABLE sample (id BIGINT UNSIGNED PRIMARY KEY AUTO_INCREMENT, name VARCHAR(40) NOT NULL, quantity INT NULL, note VARCHAR(80) NULL, payload BLOB NULL, metadata JSON NULL, flags8 BIT(8) NULL, flags64 BIT(64) NULL, status ENUM('draft','published','archived') NULL, labels SET('alpha','beta','gamma') NULL, duration TIME(6) NULL, release_year YEAR NULL, high_precision DECIMAL(65,30) NULL, shape GEOMETRY NULL, location POINT NULL, route LINESTRING NULL, area POLYGON NULL, stops MULTIPOINT NULL, paths MULTILINESTRING NULL, regions MULTIPOLYGON NULL, shapes GEOMETRYCOLLECTION NULL);");
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
        await session.ExecuteAsync(database, "CREATE EXTENSION cube;");
        await session.ExecuteAsync(database, "CREATE EXTENSION hstore;");
        await session.ExecuteAsync(database, "CREATE EXTENSION ltree;");
        await session.ExecuteAsync(database, "CREATE TYPE mood AS ENUM ('happy', 'sad', 'comma,value');");
        await session.ExecuteAsync(database, "CREATE TYPE address_type AS (city TEXT, postal_code INTEGER);");
        await session.ExecuteAsync(
            database,
            """
            CREATE TABLE sample (
                id INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                name VARCHAR(40) NOT NULL,
                quantity INTEGER NULL,
                note VARCHAR(80) NULL,
                payload BYTEA NULL,
                metadata JSONB NULL,
                document XML NULL,
                address INET NULL,
                subnet CIDR NULL,
                mac MACADDR NULL,
                mac8 MACADDR8 NULL,
                bits BIT(8) NULL,
                varbits BIT VARYING(16) NULL,
                alarm TIME WITH TIME ZONE NULL,
                duration INTERVAL NULL,
                wal_position PG_LSN NULL,
                object_id OID NULL,
                transaction_id XID NULL,
                command_id CID NULL,
                full_transaction_id XID8 NULL,
                search_vector TSVECTOR NULL,
                search_query TSQUERY NULL,
                integer_span INT4RANGE NULL,
                big_integer_span INT8RANGE NULL,
                numeric_span NUMRANGE NULL,
                timestamp_span TSRANGE NULL,
                timestamp_with_zone_span TSTZRANGE NULL,
                date_span DATERANGE NULL,
                integer_spans INT4MULTIRANGE NULL,
                big_integer_spans INT8MULTIRANGE NULL,
                numeric_spans NUMMULTIRANGE NULL,
                timestamp_spans TSMULTIRANGE NULL,
                timestamp_with_zone_spans TSTZMULTIRANGE NULL,
                date_spans DATEMULTIRANGE NULL,
                numbers INTEGER[] NULL,
                labels TEXT[] NULL,
                matrix INTEGER[][] NULL,
                identifiers UUID[] NULL,
                states mood[] NULL,
                json_items JSONB[] NULL,
                range_items INT4RANGE[] NULL,
                state mood NULL,
                mailing_address address_type NULL,
                measurement cube NULL,
                location POINT NULL,
                infinite_line LINE NULL,
                segment LSEG NULL,
                bounds BOX NULL,
                route PATH NULL,
                area POLYGON NULL,
                radius CIRCLE NULL,
                high_precision NUMERIC(100,50) NULL,
                fractional_only NUMERIC(3,5) NULL,
                rounded_thousands NUMERIC(2,-3) NULL,
                search_path JSONPATH NULL,
                snapshot PG_SNAPSHOT NULL,
                legacy_snapshot TXID_SNAPSHOT NULL,
                attributes HSTORE NULL,
                tree LTREE NULL,
                tree_query LQUERY NULL,
                text_query LTXTQUERY NULL,
                relation REGCLASS NULL,
                role_name REGROLE NULL,
                config_name REGCONFIG NULL,
                collation_name REGCOLLATION NULL,
                dictionary_name REGDICTIONARY NULL,
                namespace_name REGNAMESPACE NULL,
                operator_name REGOPER NULL,
                operator_signature REGOPERATOR NULL,
                function_name REGPROC NULL,
                function_signature REGPROCEDURE NULL,
                type_name REGTYPE NULL
            );
            """);
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
        await session.ExecuteAsync(database, "CREATE TYPE dbo.short_label FROM nvarchar(30) NULL;");
        await session.ExecuteAsync(database, "CREATE TYPE dbo.positive_count FROM int NOT NULL;");
        await session.ExecuteAsync(database, "CREATE TYPE dbo.precise_amount FROM decimal(18,6) NULL;");
        await session.ExecuteAsync(database, "CREATE TABLE dbo.sample (id INT IDENTITY PRIMARY KEY, name NVARCHAR(40) NOT NULL, quantity INT NULL, note NVARCHAR(80) NULL, payload VARBINARY(MAX) NULL, document XML NULL, legacy_text TEXT NULL, legacy_ntext NTEXT NULL, legacy_image IMAGE NULL, high_precision DECIMAL(38,20) NULL, alias_label dbo.short_label NULL, alias_count dbo.positive_count NULL, alias_amount dbo.precise_amount NULL, system_name sysname NULL, node_path hierarchyid NULL, shape geometry NULL, location geography NULL);");
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

    var insertInputs = new List<TableCellInput>
    {
        new("name", TableCellInputMode.Value, "Editor"),
        new("quantity", TableCellInputMode.Value, "7"),
        new("note", TableCellInputMode.Null, string.Empty),
        new("payload", TableCellInputMode.Value, "0x00FF10")
    };
    var exactDecimalColumns = before.Columns
        .Where(column => column.ValueKind == TableColumnValueKind.ExactDecimal)
        .ToList();
    foreach (var exactDecimalColumn in exactDecimalColumns)
    {
        var definition = TableCellValueConverter.GetExactDecimalDefinition(exactDecimalColumn);
        Assert(
            definition is { Precision: not null, Scale: not null },
            $"{session.Profile.ProviderDisplayName} exact decimal metadata 缺少 precision／scale");
        insertInputs.Add(new TableCellInput(
            exactDecimalColumn.Name,
            TableCellInputMode.Value,
            GetExactDecimalTestValue(exactDecimalColumn, updated: false)));
    }

    if (exactDecimalColumns.Count > 0)
    {
        var exactDecimalColumn = exactDecimalColumns[0];
        var definition = TableCellValueConverter.GetExactDecimalDefinition(exactDecimalColumn);
        var invalidValue = new string(
            '9',
            Math.Max(0, definition.Precision!.Value - definition.Scale!.Value) + 1);
        await AssertThrowsAsync<InvalidOperationException>(() => session.InsertTableRowAsync(
            database,
            table,
            new[]
            {
                new TableCellInput("name", TableCellInputMode.Value, "Rejected exact decimal"),
                new TableCellInput(exactDecimalColumn.Name, TableCellInputMode.Value, invalidValue)
            }));
        var rejectedSnapshot = await session.LoadTableDataAsync(database, table);
        Assert(
            rejectedSnapshot.Rows.All(row => Convert.ToString(row.Values[1]) != "Rejected exact decimal"),
            $"{session.Profile.ProviderDisplayName} 不可寫入超過 precision 的 exact decimal");
    }
    var spatialColumns = before.Columns
        .Where(column => column.ValueKind == TableColumnValueKind.Spatial)
        .ToList();
    if (spatialColumns.Count > 0)
    {
        var expectedSpatialColumns = session.Profile.Provider == DatabaseProviderKind.MySql ? 8 : 2;
        Assert(
            spatialColumns.Count == expectedSpatialColumns,
            $"{session.Profile.ProviderDisplayName} spatial metadata 未完整辨識；actual={spatialColumns.Count}");
        foreach (var spatialColumn in spatialColumns)
        {
            Assert(spatialColumn.IsEditable, $"{spatialColumn.DataTypeName} 應可安全編輯");
            Assert(
                before.Rows.All(row => row.Values[spatialColumn.Ordinal] is null or DBNull),
                $"{session.Profile.ProviderDisplayName} NULL {spatialColumn.DataTypeName} 載入後必須維持 NULL");
            insertInputs.Add(new TableCellInput(
                spatialColumn.Name,
                TableCellInputMode.Value,
                GetSpatialTestValue(session.Profile.Provider, spatialColumn, updated: false)));
        }

        var invalidInputs = new[]
        {
            new TableCellInput("name", TableCellInputMode.Value, "Rejected spatial"),
            new TableCellInput(spatialColumns[0].Name, TableCellInputMode.Value, "SRID=0;NOT_A_SHAPE")
        };
        if (session.Profile.Provider == DatabaseProviderKind.MySql)
        {
            await AssertThrowsAsync<MySqlException>(() => session.InsertTableRowAsync(
                database,
                table,
                invalidInputs));
        }
        else
        {
            await AssertThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => session.InsertTableRowAsync(
                database,
                table,
                invalidInputs));
        }

        var rejectedSnapshot = await session.LoadTableDataAsync(database, table);
        Assert(
            rejectedSnapshot.Rows.All(row => Convert.ToString(row.Values[1]) != "Rejected spatial"),
            $"{session.Profile.ProviderDisplayName} 不可寫入 server-side parser 拒絕的畸形 spatial 值");
    }
    var hierarchyIdColumn = before.Columns.SingleOrDefault(column =>
        column.ValueKind == TableColumnValueKind.SqlServerHierarchyId);
    if (hierarchyIdColumn is not null)
    {
        Assert(hierarchyIdColumn.IsEditable, "SQL Server hierarchyid 應可安全編輯");
        Assert(
            before.Rows.All(row => row.Values[hierarchyIdColumn.Ordinal] is null or DBNull),
            "SQL Server NULL hierarchyid 載入後必須維持 NULL");
        insertInputs.Add(new TableCellInput(
            hierarchyIdColumn.Name,
            TableCellInputMode.Value,
            "/1/2.5/"));
        await AssertThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => session.InsertTableRowAsync(
            database,
            table,
            new[]
            {
                new TableCellInput("name", TableCellInputMode.Value, "Rejected hierarchyid"),
                new TableCellInput(hierarchyIdColumn.Name, TableCellInputMode.Value, "/1//")
            }));
        var rejectedSnapshot = await session.LoadTableDataAsync(database, table);
        Assert(
            rejectedSnapshot.Rows.All(row => Convert.ToString(row.Values[1]) != "Rejected hierarchyid"),
            "SQL Server 不可寫入 parser 拒絕的畸形 hierarchyid 值");
    }
    var sqlServerAliasLabelColumn = before.Columns.SingleOrDefault(column => column.Name == "alias_label");
    var sqlServerAliasCountColumn = before.Columns.SingleOrDefault(column => column.Name == "alias_count");
    var sqlServerAliasAmountColumn = before.Columns.SingleOrDefault(column => column.Name == "alias_amount");
    var sqlServerSysnameColumn = before.Columns.SingleOrDefault(column => column.Name == "system_name");
    if (sqlServerAliasLabelColumn is not null)
    {
        Assert(
            sqlServerAliasCountColumn is not null &&
            sqlServerAliasAmountColumn is not null &&
            sqlServerSysnameColumn is not null,
            "SQL Server alias type metadata 不完整");
        Assert(
            sqlServerAliasLabelColumn.ValueKind == TableColumnValueKind.String &&
            sqlServerAliasLabelColumn.DataTypeName == "[dbo].[short_label] (nvarchar(30))" &&
            sqlServerAliasLabelColumn.StorageDataTypeName == "nvarchar(30)",
            $"SQL Server nvarchar alias metadata 不正確；actual={sqlServerAliasLabelColumn.DataTypeName}");
        Assert(
            sqlServerAliasCountColumn!.ValueKind == TableColumnValueKind.Integer &&
            sqlServerAliasCountColumn.DataTypeName == "[dbo].[positive_count] (int)" &&
            sqlServerAliasCountColumn.StorageDataTypeName == "int",
            $"SQL Server int alias metadata 不正確；actual={sqlServerAliasCountColumn.DataTypeName}");
        Assert(
            sqlServerAliasAmountColumn!.ValueKind == TableColumnValueKind.ExactDecimal &&
            sqlServerAliasAmountColumn.DataTypeName == "[dbo].[precise_amount] (decimal(18,6))" &&
            sqlServerAliasAmountColumn.StorageDataTypeName == "decimal(18,6)",
            $"SQL Server decimal alias metadata 不正確；actual={sqlServerAliasAmountColumn.DataTypeName}");
        Assert(
            sqlServerSysnameColumn!.ValueKind == TableColumnValueKind.String &&
            sqlServerSysnameColumn.DataTypeName == "sysname (nvarchar(128))" &&
            sqlServerSysnameColumn.StorageDataTypeName == "nvarchar(128)",
            $"SQL Server sysname metadata 不正確；actual={sqlServerSysnameColumn.DataTypeName}");
        insertInputs.Add(new TableCellInput(
            sqlServerAliasLabelColumn.Name,
            TableCellInputMode.Value,
            "Alias insert"));
        insertInputs.Add(new TableCellInput(
            sqlServerAliasCountColumn.Name,
            TableCellInputMode.Value,
            "42"));
        insertInputs.Add(new TableCellInput(
            sqlServerSysnameColumn.Name,
            TableCellInputMode.Value,
            "alias_object"));

        await AssertThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => session.InsertTableRowAsync(
            database,
            table,
            new[]
            {
                new TableCellInput("name", TableCellInputMode.Value, "Rejected alias overflow"),
                new TableCellInput(sqlServerAliasCountColumn.Name, TableCellInputMode.Value, "2147483648")
            }));
        var rejectedSnapshot = await session.LoadTableDataAsync(database, table);
        Assert(
            rejectedSnapshot.Rows.All(row => Convert.ToString(row.Values[1]) != "Rejected alias overflow"),
            "SQL Server int alias 不可寫入超過 base type 範圍的值");
    }
    var jsonColumn = before.Columns.SingleOrDefault(column => column.ValueKind == TableColumnValueKind.Json);
    if (jsonColumn is not null)
    {
        insertInputs.Add(new TableCellInput(
            jsonColumn.Name,
            TableCellInputMode.Value,
            "{\"stage\":\"insert\",\"n\":1}"));
    }
    var xmlColumn = before.Columns.SingleOrDefault(column => column.ValueKind == TableColumnValueKind.Xml);
    if (xmlColumn is not null)
    {
        insertInputs.Add(new TableCellInput(
            xmlColumn.Name,
            TableCellInputMode.Value,
            "<item stage=\"insert\"><n>1</n></item>"));
    }
    var networkColumns = before.Columns
        .Where(column => column.ValueKind == TableColumnValueKind.NetworkAddress)
        .ToList();
    foreach (var networkColumn in networkColumns)
    {
        insertInputs.Add(new TableCellInput(
            networkColumn.Name,
            TableCellInputMode.Value,
            GetNetworkTestValue(networkColumn, updated: false)));
    }
    var legacyTextColumn = before.Columns.SingleOrDefault(column =>
        string.Equals(column.DataTypeName, "text", StringComparison.OrdinalIgnoreCase));
    var legacyNtextColumn = before.Columns.SingleOrDefault(column =>
        string.Equals(column.DataTypeName, "ntext", StringComparison.OrdinalIgnoreCase));
    var legacyImageColumn = before.Columns.SingleOrDefault(column =>
        string.Equals(column.DataTypeName, "image", StringComparison.OrdinalIgnoreCase));
    if (legacyTextColumn is not null)
    {
        insertInputs.Add(new TableCellInput(legacyTextColumn.Name, TableCellInputMode.Value, "legacy ' text"));
    }
    if (legacyNtextColumn is not null)
    {
        insertInputs.Add(new TableCellInput(legacyNtextColumn.Name, TableCellInputMode.Value, "舊式 Unicode 文字 🐧"));
    }
    if (legacyImageColumn is not null)
    {
        insertInputs.Add(new TableCellInput(legacyImageColumn.Name, TableCellInputMode.Value, "0xDEADBEEF"));
    }
    var bitColumns = before.Columns
        .Where(column =>
            column.ValueKind == TableColumnValueKind.UnsignedInteger &&
            column.DataTypeName.StartsWith("bit(", StringComparison.OrdinalIgnoreCase))
        .ToList();
    foreach (var bitColumn in bitColumns)
    {
        insertInputs.Add(new TableCellInput(
            bitColumn.Name,
            TableCellInputMode.Value,
            GetBitTestValue(bitColumn, updated: false)));
    }
    var enumColumn = before.Columns.SingleOrDefault(column =>
        column.ValueKind == TableColumnValueKind.String &&
        column.DataTypeName.StartsWith("enum(", StringComparison.OrdinalIgnoreCase));
    if (enumColumn is not null)
    {
        insertInputs.Add(new TableCellInput(enumColumn.Name, TableCellInputMode.Value, "draft"));
    }
    var setColumn = before.Columns.SingleOrDefault(column =>
        column.ValueKind == TableColumnValueKind.String &&
        column.DataTypeName.StartsWith("set(", StringComparison.OrdinalIgnoreCase));
    if (setColumn is not null)
    {
        insertInputs.Add(new TableCellInput(setColumn.Name, TableCellInputMode.Value, "beta,alpha"));
    }
    if (enumColumn is not null)
    {
        await AssertThrowsAsync<MySqlException>(() => session.InsertTableRowAsync(
            database,
            table,
            new[]
            {
                new TableCellInput("name", TableCellInputMode.Value, "Rejected enum"),
                new TableCellInput(enumColumn.Name, TableCellInputMode.Value, "not-declared")
            }));
        var rejectedSnapshot = await session.LoadTableDataAsync(database, table);
        Assert(
            rejectedSnapshot.Rows.All(row => Convert.ToString(row.Values[1]) != "Rejected enum"),
            "MySQL／MariaDB 不可在 strict mode 寫入未宣告的 ENUM 值");
    }
    var mySqlTimeColumn = before.Columns.SingleOrDefault(column =>
        column.ValueKind == TableColumnValueKind.MySqlTime);
    if (mySqlTimeColumn is not null)
    {
        insertInputs.Add(new TableCellInput(
            mySqlTimeColumn.Name,
            TableCellInputMode.Value,
            "838:59:58.123456"));
    }
    var mySqlYearColumn = before.Columns.SingleOrDefault(column =>
        column.ValueKind == TableColumnValueKind.MySqlYear);
    if (mySqlYearColumn is not null)
    {
        insertInputs.Add(new TableCellInput(
            mySqlYearColumn.Name,
            TableCellInputMode.Value,
            "1901"));

        await session.InsertTableRowAsync(
            database,
            table,
            new[]
            {
                new TableCellInput("name", TableCellInputMode.Value, "Year zero"),
                new TableCellInput(mySqlYearColumn.Name, TableCellInputMode.Value, "0")
            });
        var zeroYearSnapshot = await session.LoadTableDataAsync(database, table);
        var zeroYearRow = zeroYearSnapshot.Rows.Single(row => Convert.ToString(row.Values[1]) == "Year zero");
        Assert(
            Convert.ToUInt16(zeroYearRow.Values[mySqlYearColumn.Ordinal]) == 0,
            "MySQL／MariaDB YEAR 應無損保存 zero year");
        await session.DeleteTableRowAsync(database, table, zeroYearRow);

        await AssertThrowsAsync<InvalidOperationException>(() => session.InsertTableRowAsync(
            database,
            table,
            new[]
            {
                new TableCellInput("name", TableCellInputMode.Value, "Rejected year"),
                new TableCellInput(mySqlYearColumn.Name, TableCellInputMode.Value, "1900")
            }));
        var rejectedSnapshot = await session.LoadTableDataAsync(database, table);
        Assert(
            rejectedSnapshot.Rows.All(row => Convert.ToString(row.Values[1]) != "Rejected year"),
            "MySQL／MariaDB 不可寫入 1900 或模糊兩位數 YEAR");
    }
    var bitStringColumns = before.Columns
        .Where(column => column.ValueKind == TableColumnValueKind.BitString)
        .ToList();
    foreach (var bitStringColumn in bitStringColumns)
    {
        insertInputs.Add(new TableCellInput(
            bitStringColumn.Name,
            TableCellInputMode.Value,
            GetBitStringTestValue(bitStringColumn, updated: false)));
    }
    var timeZoneColumn = before.Columns.SingleOrDefault(column =>
        column.ValueKind == TableColumnValueKind.TimeWithTimeZone);
    if (timeZoneColumn is not null)
    {
        insertInputs.Add(new TableCellInput(
            timeZoneColumn.Name,
            TableCellInputMode.Value,
            "12:34:56.123456+08:00"));
    }
    var intervalColumn = before.Columns.SingleOrDefault(column =>
        column.ValueKind == TableColumnValueKind.Interval);
    if (intervalColumn is not null)
    {
        insertInputs.Add(new TableCellInput(
            intervalColumn.Name,
            TableCellInputMode.Value,
            "months=14;days=3;microseconds=14706123456"));
    }
    var lsnColumn = before.Columns.SingleOrDefault(column =>
        column.ValueKind == TableColumnValueKind.LogSequenceNumber);
    if (lsnColumn is not null)
    {
        insertInputs.Add(new TableCellInput(
            lsnColumn.Name,
            TableCellInputMode.Value,
            "16/B374D848"));
    }
    var systemIdentifierColumns = before.Columns
        .Where(column =>
            column.ValueKind == TableColumnValueKind.UnsignedInteger &&
            column.DataTypeName is "oid" or "xid" or "cid" or "xid8")
        .ToList();
    foreach (var systemIdentifierColumn in systemIdentifierColumns)
    {
        insertInputs.Add(new TableCellInput(
            systemIdentifierColumn.Name,
            TableCellInputMode.Value,
            GetSystemIdentifierTestValue(systemIdentifierColumn, updated: false)));
    }
    var fullTextVectorColumn = before.Columns.SingleOrDefault(column =>
        column.ValueKind == TableColumnValueKind.FullTextVector);
    if (fullTextVectorColumn is not null)
    {
        insertInputs.Add(new TableCellInput(
            fullTextVectorColumn.Name,
            TableCellInputMode.Value,
            "'cat':1A,3 'dog':2B"));
    }
    var fullTextQueryColumn = before.Columns.SingleOrDefault(column =>
        column.ValueKind == TableColumnValueKind.FullTextQuery);
    if (fullTextQueryColumn is not null)
    {
        insertInputs.Add(new TableCellInput(
            fullTextQueryColumn.Name,
            TableCellInputMode.Value,
            "'cat':A & !'dog':*"));

        await AssertThrowsAsync<PostgresException>(() => session.InsertTableRowAsync(
            database,
            table,
            new[]
            {
                new TableCellInput("name", TableCellInputMode.Value, "Rejected full text"),
                new TableCellInput(fullTextQueryColumn.Name, TableCellInputMode.Value, "'cat' &")
            }));
        var rejectedSnapshot = await session.LoadTableDataAsync(database, table);
        Assert(
            rejectedSnapshot.Rows.All(row => Convert.ToString(row.Values[1]) != "Rejected full text"),
            "PostgreSQL 不可寫入 server-side parser 拒絕的畸形 tsquery");
    }
    var rangeColumns = before.Columns
        .Where(column => column.ValueKind == TableColumnValueKind.PostgreSqlRange)
        .ToList();
    if (rangeColumns.Count > 0)
    {
        Assert(rangeColumns.Count == 12, $"PostgreSQL 12 種內建 range／multirange metadata 未完整辨識；actual={rangeColumns.Count}");
        foreach (var rangeColumn in rangeColumns)
        {
            Assert(rangeColumn.IsEditable, $"PostgreSQL {rangeColumn.DataTypeName} 應可安全編輯");
            insertInputs.Add(new TableCellInput(
                rangeColumn.Name,
                TableCellInputMode.Value,
                GetPostgreSqlRangeTestValue(rangeColumn, updated: false)));
        }

        await AssertThrowsAsync<PostgresException>(() => session.InsertTableRowAsync(
            database,
            table,
            new[]
            {
                new TableCellInput("name", TableCellInputMode.Value, "Rejected range"),
                new TableCellInput(rangeColumns[0].Name, TableCellInputMode.Value, "not-a-range")
            }));
        var rejectedSnapshot = await session.LoadTableDataAsync(database, table);
        Assert(
            rejectedSnapshot.Rows.All(row => Convert.ToString(row.Values[1]) != "Rejected range"),
            "PostgreSQL 不可寫入 server-side parser 拒絕的畸形 range");
    }
    var arrayColumns = before.Columns
        .Where(column => column.ValueKind == TableColumnValueKind.PostgreSqlArray)
        .ToList();
    if (arrayColumns.Count > 0)
    {
        Assert(arrayColumns.Count == 7, $"PostgreSQL array metadata 未完整辨識；actual={arrayColumns.Count}");
        Assert(
            arrayColumns.Single(column => column.Name == "numbers").DataTypeName == "integer[]" &&
            arrayColumns.Single(column => column.Name == "states").DataTypeName == "mood[]",
            "PostgreSQL array metadata 應顯示 element type，而不是籠統 ARRAY");
        foreach (var arrayColumn in arrayColumns)
        {
            Assert(arrayColumn.IsEditable, $"PostgreSQL {arrayColumn.DataTypeName} 應可安全編輯");
            insertInputs.Add(new TableCellInput(
                arrayColumn.Name,
                TableCellInputMode.Value,
                GetPostgreSqlArrayTestValue(arrayColumn, updated: false)));
        }

        await AssertThrowsAsync<PostgresException>(() => session.InsertTableRowAsync(
            database,
            table,
            new[]
            {
                new TableCellInput("name", TableCellInputMode.Value, "Rejected array"),
                new TableCellInput(arrayColumns[0].Name, TableCellInputMode.Value, "{1,2")
            }));
        var rejectedSnapshot = await session.LoadTableDataAsync(database, table);
        Assert(
            rejectedSnapshot.Rows.All(row => Convert.ToString(row.Values[1]) != "Rejected array"),
            "PostgreSQL 不可寫入 server-side parser 拒絕的畸形 array");
    }
    var geometricColumns = before.Columns
        .Where(column => column.ValueKind == TableColumnValueKind.PostgreSqlGeometric)
        .ToList();
    if (geometricColumns.Count > 0)
    {
        Assert(geometricColumns.Count == 7, $"PostgreSQL 7 種 geometric metadata 未完整辨識；actual={geometricColumns.Count}");
        foreach (var geometricColumn in geometricColumns)
        {
            Assert(geometricColumn.IsEditable, $"PostgreSQL {geometricColumn.DataTypeName} 應可安全編輯");
            insertInputs.Add(new TableCellInput(
                geometricColumn.Name,
                TableCellInputMode.Value,
                GetPostgreSqlGeometricTestValue(geometricColumn, updated: false)));
        }

        await AssertThrowsAsync<PostgresException>(() => session.InsertTableRowAsync(
            database,
            table,
            new[]
            {
                new TableCellInput("name", TableCellInputMode.Value, "Rejected geometric"),
                new TableCellInput(geometricColumns[0].Name, TableCellInputMode.Value, "not-a-shape")
            }));
        var rejectedSnapshot = await session.LoadTableDataAsync(database, table);
        Assert(
            rejectedSnapshot.Rows.All(row => Convert.ToString(row.Values[1]) != "Rejected geometric"),
            "PostgreSQL 不可寫入 server-side parser 拒絕的畸形 geometric 值");
    }
    var serverValidatedTextColumns = before.Columns
        .Where(column => column.ValueKind == TableColumnValueKind.PostgreSqlServerValidatedText)
        .ToList();
    if (serverValidatedTextColumns.Count > 0)
    {
        Assert(
            serverValidatedTextColumns.Count == 21,
            $"PostgreSQL server-validated text metadata 未完整辨識；actual={serverValidatedTextColumns.Count}");
        foreach (var serverTextColumn in serverValidatedTextColumns)
        {
            Assert(serverTextColumn.IsEditable, $"PostgreSQL {serverTextColumn.DataTypeName} 應可安全編輯");
            insertInputs.Add(new TableCellInput(
                serverTextColumn.Name,
                TableCellInputMode.Value,
                GetPostgreSqlServerTextTestValue(serverTextColumn, updated: false)));
        }

        var jsonPathColumn = serverValidatedTextColumns.Single(column =>
            column.DataTypeName.Equals("jsonpath", StringComparison.OrdinalIgnoreCase));
        await AssertThrowsAsync<PostgresException>(() => session.InsertTableRowAsync(
            database,
            table,
            new[]
            {
                new TableCellInput("name", TableCellInputMode.Value, "Rejected jsonpath"),
                new TableCellInput(jsonPathColumn.Name, TableCellInputMode.Value, "$.items[*] ? (")
            }));
        var rejectedSnapshot = await session.LoadTableDataAsync(database, table);
        Assert(
            rejectedSnapshot.Rows.All(row => Convert.ToString(row.Values[1]) != "Rejected jsonpath"),
            "PostgreSQL 不可寫入 server-side parser 拒絕的畸形 jsonpath");

        var compositeColumn = serverValidatedTextColumns.Single(column =>
            column.DataTypeName.Equals("address_type", StringComparison.OrdinalIgnoreCase));
        await AssertThrowsAsync<PostgresException>(() => session.InsertTableRowAsync(
            database,
            table,
            new[]
            {
                new TableCellInput("name", TableCellInputMode.Value, "Rejected composite"),
                new TableCellInput(compositeColumn.Name, TableCellInputMode.Value, "(Taipei,not-an-integer)")
            }));
        rejectedSnapshot = await session.LoadTableDataAsync(database, table);
        Assert(
            rejectedSnapshot.Rows.All(row => Convert.ToString(row.Values[1]) != "Rejected composite"),
            "PostgreSQL 不可寫入 server-side parser 拒絕的畸形 composite 值");
    }
    await session.InsertTableRowAsync(database, table, insertInputs);
    var insertedSnapshot = await session.LoadTableDataAsync(database, table);
    var inserted = insertedSnapshot.Rows.Single(row => Convert.ToString(row.Values[1]) == "Editor");
    Assert(Convert.ToInt64(inserted.Values[2]) == 7, $"{session.Profile.ProviderDisplayName} 安全新增整數不正確");
    Assert(
        inserted.Values[4] is byte[] insertedPayload && insertedPayload.SequenceEqual(new byte[] { 0x00, 0xFF, 0x10 }),
        $"{session.Profile.ProviderDisplayName} 安全新增 binary 不正確");
    foreach (var exactDecimalColumn in exactDecimalColumns)
    {
        Assert(
            Convert.ToString(inserted.Values[exactDecimalColumn.Ordinal]) ==
            GetExactDecimalTestValue(exactDecimalColumn, updated: false),
            $"{session.Profile.ProviderDisplayName} 高精度 decimal 安全新增不正確；actual={inserted.Values[exactDecimalColumn.Ordinal]}");
    }
    foreach (var spatialColumn in spatialColumns)
    {
        AssertSpatialValue(
            inserted.Values[spatialColumn.Ordinal],
            GetSpatialTestValue(session.Profile.Provider, spatialColumn, updated: false),
            spatialColumn,
            $"{session.Profile.ProviderDisplayName} {spatialColumn.DataTypeName} 安全新增不正確");
    }
    if (jsonColumn is not null)
    {
        using var insertedJson = JsonDocument.Parse(Convert.ToString(inserted.Values[jsonColumn.Ordinal])!);
        Assert(insertedJson.RootElement.GetProperty("stage").GetString() == "insert", $"{session.Profile.ProviderDisplayName} 安全新增 JSON 不正確");
    }
    if (xmlColumn is not null)
    {
        var insertedXml = System.Xml.Linq.XDocument.Parse(Convert.ToString(inserted.Values[xmlColumn.Ordinal])!);
        Assert((string?)insertedXml.Root?.Attribute("stage") == "insert", $"{session.Profile.ProviderDisplayName} 安全新增 XML 不正確");
    }
    foreach (var networkColumn in networkColumns)
    {
        AssertNetworkValue(
            inserted.Values[networkColumn.Ordinal],
            GetNetworkTestValue(networkColumn, updated: false),
            $"{session.Profile.ProviderDisplayName} 安全新增 {networkColumn.DataTypeName} 不正確");
    }
    if (legacyTextColumn is not null)
    {
        Assert(Convert.ToString(inserted.Values[legacyTextColumn.Ordinal]) == "legacy ' text", "SQL Server text 安全新增不正確");
    }
    if (legacyNtextColumn is not null)
    {
        Assert(Convert.ToString(inserted.Values[legacyNtextColumn.Ordinal]) == "舊式 Unicode 文字 🐧", "SQL Server ntext 安全新增不正確");
    }
    if (legacyImageColumn is not null)
    {
        Assert(
            inserted.Values[legacyImageColumn.Ordinal] is byte[] legacyImage &&
            legacyImage.SequenceEqual(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }),
            "SQL Server image 安全新增不正確");
    }
    foreach (var bitColumn in bitColumns)
    {
        Assert(
            Convert.ToUInt64(inserted.Values[bitColumn.Ordinal]) ==
            ulong.Parse(GetBitTestValue(bitColumn, updated: false), CultureInfo.InvariantCulture),
            $"MySQL {bitColumn.DataTypeName} 安全新增不正確");
    }
    if (enumColumn is not null)
    {
        Assert(Convert.ToString(inserted.Values[enumColumn.Ordinal]) == "draft", "MySQL ENUM 安全新增不正確");
    }
    if (setColumn is not null)
    {
        Assert(
            Convert.ToString(inserted.Values[setColumn.Ordinal]) == "alpha,beta",
            $"MySQL SET 應依宣告順序正規化；actual={inserted.Values[setColumn.Ordinal]}");
    }
    if (mySqlTimeColumn is not null)
    {
        Assert(
            Convert.ToString(inserted.Values[mySqlTimeColumn.Ordinal]) == "838:59:58.123456",
            $"MySQL TIME 正上界安全新增不正確；actual={inserted.Values[mySqlTimeColumn.Ordinal]}");
    }
    if (mySqlYearColumn is not null)
    {
        Assert(
            Convert.ToUInt16(inserted.Values[mySqlYearColumn.Ordinal]) == 1901,
            $"MySQL YEAR 下界安全新增不正確；actual={inserted.Values[mySqlYearColumn.Ordinal]}");
    }
    foreach (var bitStringColumn in bitStringColumns)
    {
        Assert(
            Convert.ToString(inserted.Values[bitStringColumn.Ordinal]) ==
            GetBitStringTestValue(bitStringColumn, updated: false),
            $"PostgreSQL {bitStringColumn.DataTypeName} 安全新增不正確");
    }
    if (timeZoneColumn is not null)
    {
        AssertTimeWithTimeZone(
            inserted.Values[timeZoneColumn.Ordinal],
            new TimeSpan(12, 34, 56) + TimeSpan.FromTicks(1_234_560),
            TimeSpan.FromHours(8),
            "PostgreSQL timetz 安全新增不正確");
    }
    if (intervalColumn is not null)
    {
        Assert(
            Convert.ToString(inserted.Values[intervalColumn.Ordinal]) ==
            "months=14;days=3;microseconds=14706123456",
            $"PostgreSQL interval 安全新增不正確；actual={inserted.Values[intervalColumn.Ordinal]}");
    }
    if (lsnColumn is not null)
    {
        Assert(
            Convert.ToString(inserted.Values[lsnColumn.Ordinal]) == "16/B374D848",
            $"PostgreSQL pg_lsn 安全新增不正確；actual={inserted.Values[lsnColumn.Ordinal]}");
    }
    foreach (var systemIdentifierColumn in systemIdentifierColumns)
    {
        Assert(
            Convert.ToUInt64(inserted.Values[systemIdentifierColumn.Ordinal]) ==
            ulong.Parse(
                GetSystemIdentifierTestValue(systemIdentifierColumn, updated: false),
                CultureInfo.InvariantCulture),
            $"PostgreSQL {systemIdentifierColumn.DataTypeName} 安全新增不正確");
    }
    if (fullTextVectorColumn is not null)
    {
        Assert(
            Convert.ToString(inserted.Values[fullTextVectorColumn.Ordinal]) == "'cat':1A,3 'dog':2B",
            $"PostgreSQL tsvector 安全新增不正確；actual={inserted.Values[fullTextVectorColumn.Ordinal]}");
    }
    if (fullTextQueryColumn is not null)
    {
        Assert(
            Convert.ToString(inserted.Values[fullTextQueryColumn.Ordinal]) == "'cat':A & !'dog':*",
            $"PostgreSQL tsquery 安全新增不正確；actual={inserted.Values[fullTextQueryColumn.Ordinal]}");
    }
    foreach (var rangeColumn in rangeColumns)
    {
        Assert(
            Convert.ToString(inserted.Values[rangeColumn.Ordinal]) ==
            GetPostgreSqlRangeTestValue(rangeColumn, updated: false),
            $"PostgreSQL {rangeColumn.DataTypeName} 安全新增不正確；actual={inserted.Values[rangeColumn.Ordinal]}");
    }
    foreach (var arrayColumn in arrayColumns)
    {
        Assert(
            Convert.ToString(inserted.Values[arrayColumn.Ordinal]) ==
            GetPostgreSqlArrayTestValue(arrayColumn, updated: false),
            $"PostgreSQL {arrayColumn.DataTypeName} 安全新增不正確；actual={inserted.Values[arrayColumn.Ordinal]}");
    }
    foreach (var geometricColumn in geometricColumns)
    {
        Assert(
            Convert.ToString(inserted.Values[geometricColumn.Ordinal]) ==
            GetPostgreSqlGeometricTestValue(geometricColumn, updated: false),
            $"PostgreSQL {geometricColumn.DataTypeName} 安全新增不正確；actual={inserted.Values[geometricColumn.Ordinal]}");
    }
    foreach (var serverTextColumn in serverValidatedTextColumns)
    {
        Assert(
            Convert.ToString(inserted.Values[serverTextColumn.Ordinal]) ==
            GetPostgreSqlServerTextTestValue(serverTextColumn, updated: false),
            $"PostgreSQL {serverTextColumn.DataTypeName} 安全新增不正確；actual={inserted.Values[serverTextColumn.Ordinal]}");
    }
    if (hierarchyIdColumn is not null)
    {
        Assert(
            Convert.ToString(inserted.Values[hierarchyIdColumn.Ordinal]) == "/1/2.5/",
            $"SQL Server hierarchyid 安全新增不正確；actual={inserted.Values[hierarchyIdColumn.Ordinal]}");
    }
    if (sqlServerAliasLabelColumn is not null)
    {
        Assert(
            Convert.ToString(inserted.Values[sqlServerAliasLabelColumn.Ordinal]) == "Alias insert",
            "SQL Server nvarchar alias 安全新增不正確");
        Assert(
            Convert.ToInt64(inserted.Values[sqlServerAliasCountColumn!.Ordinal]) == 42,
            "SQL Server int alias 安全新增不正確");
        Assert(
            Convert.ToString(inserted.Values[sqlServerSysnameColumn!.Ordinal]) == "alias_object",
            "SQL Server sysname 安全新增不正確");
    }

    var firstPage = await session.LoadTableDataAsync(database, table, rowLimit: 1, rowOffset: 0);
    var secondPage = await session.LoadTableDataAsync(database, table, rowLimit: 1, rowOffset: 1);
    Assert(firstPage.HasNextPage && !firstPage.HasPreviousPage, $"{session.Profile.ProviderDisplayName} 第一頁導覽狀態不正確");
    Assert(secondPage.HasPreviousPage, $"{session.Profile.ProviderDisplayName} 第二頁導覽狀態不正確");
    Assert(
        !Equals(firstPage.Rows[0].Values[0], secondPage.Rows[0].Values[0]),
        $"{session.Profile.ProviderDisplayName} 分頁未依 Primary Key 前進");

    var updateInputs = new List<TableCellInput>
    {
        new("quantity", TableCellInputMode.Value, "8"),
        new("payload", TableCellInputMode.Value, "0xCAFE")
    };
    foreach (var exactDecimalColumn in exactDecimalColumns)
    {
        updateInputs.Add(new TableCellInput(
            exactDecimalColumn.Name,
            TableCellInputMode.Value,
            GetExactDecimalTestValue(exactDecimalColumn, updated: true)));
    }
    foreach (var spatialColumn in spatialColumns)
    {
        updateInputs.Add(new TableCellInput(
            spatialColumn.Name,
            TableCellInputMode.Value,
            GetSpatialTestValue(session.Profile.Provider, spatialColumn, updated: true)));
    }
    if (jsonColumn is not null)
    {
        updateInputs.Add(new TableCellInput(
            jsonColumn.Name,
            TableCellInputMode.Value,
            "{\"stage\":\"updated\",\"ok\":true}"));
    }
    if (xmlColumn is not null)
    {
        updateInputs.Add(new TableCellInput(
            xmlColumn.Name,
            TableCellInputMode.Value,
            "<item stage=\"updated\"><ok>true</ok></item>"));
    }
    foreach (var networkColumn in networkColumns)
    {
        updateInputs.Add(new TableCellInput(
            networkColumn.Name,
            TableCellInputMode.Value,
            GetNetworkTestValue(networkColumn, updated: true)));
    }
    if (legacyTextColumn is not null)
    {
        updateInputs.Add(new TableCellInput(legacyTextColumn.Name, TableCellInputMode.Value, "updated legacy text"));
    }
    if (legacyNtextColumn is not null)
    {
        updateInputs.Add(new TableCellInput(legacyNtextColumn.Name, TableCellInputMode.Value, "更新 Unicode 文字 🍎"));
    }
    if (legacyImageColumn is not null)
    {
        updateInputs.Add(new TableCellInput(legacyImageColumn.Name, TableCellInputMode.Value, "0xCAFE"));
    }
    foreach (var bitColumn in bitColumns)
    {
        updateInputs.Add(new TableCellInput(
            bitColumn.Name,
            TableCellInputMode.Value,
            GetBitTestValue(bitColumn, updated: true)));
    }
    if (enumColumn is not null)
    {
        updateInputs.Add(new TableCellInput(enumColumn.Name, TableCellInputMode.Value, "archived"));
    }
    if (setColumn is not null)
    {
        updateInputs.Add(new TableCellInput(setColumn.Name, TableCellInputMode.Value, "gamma,beta"));
    }
    if (mySqlTimeColumn is not null)
    {
        updateInputs.Add(new TableCellInput(
            mySqlTimeColumn.Name,
            TableCellInputMode.Value,
            "-838:59:58.654321"));
    }
    if (mySqlYearColumn is not null)
    {
        updateInputs.Add(new TableCellInput(
            mySqlYearColumn.Name,
            TableCellInputMode.Value,
            "2155"));
    }
    foreach (var bitStringColumn in bitStringColumns)
    {
        updateInputs.Add(new TableCellInput(
            bitStringColumn.Name,
            TableCellInputMode.Value,
            GetBitStringTestValue(bitStringColumn, updated: true)));
    }
    if (timeZoneColumn is not null)
    {
        updateInputs.Add(new TableCellInput(
            timeZoneColumn.Name,
            TableCellInputMode.Value,
            "23:59:58.654321-04:30"));
    }
    if (intervalColumn is not null)
    {
        updateInputs.Add(new TableCellInput(
            intervalColumn.Name,
            TableCellInputMode.Value,
            "months=-1;days=2;microseconds=-11045123456"));
    }
    if (lsnColumn is not null)
    {
        updateInputs.Add(new TableCellInput(
            lsnColumn.Name,
            TableCellInputMode.Value,
            "FFFFFFFF/FFFFFFFF"));
    }
    foreach (var systemIdentifierColumn in systemIdentifierColumns)
    {
        updateInputs.Add(new TableCellInput(
            systemIdentifierColumn.Name,
            TableCellInputMode.Value,
            GetSystemIdentifierTestValue(systemIdentifierColumn, updated: true)));
    }
    if (fullTextVectorColumn is not null)
    {
        updateInputs.Add(new TableCellInput(
            fullTextVectorColumn.Name,
            TableCellInputMode.Value,
            "'bird':4C 'fish':2A"));
    }
    if (fullTextQueryColumn is not null)
    {
        updateInputs.Add(new TableCellInput(
            fullTextQueryColumn.Name,
            TableCellInputMode.Value,
            "'bird' <2> 'fish':B"));
    }
    foreach (var rangeColumn in rangeColumns)
    {
        updateInputs.Add(new TableCellInput(
            rangeColumn.Name,
            TableCellInputMode.Value,
            GetPostgreSqlRangeTestValue(rangeColumn, updated: true)));
    }
    foreach (var arrayColumn in arrayColumns)
    {
        updateInputs.Add(new TableCellInput(
            arrayColumn.Name,
            TableCellInputMode.Value,
            GetPostgreSqlArrayTestValue(arrayColumn, updated: true)));
    }
    foreach (var geometricColumn in geometricColumns)
    {
        updateInputs.Add(new TableCellInput(
            geometricColumn.Name,
            TableCellInputMode.Value,
            GetPostgreSqlGeometricTestValue(geometricColumn, updated: true)));
    }
    foreach (var serverTextColumn in serverValidatedTextColumns)
    {
        updateInputs.Add(new TableCellInput(
            serverTextColumn.Name,
            TableCellInputMode.Value,
            GetPostgreSqlServerTextTestValue(serverTextColumn, updated: true)));
    }
    if (hierarchyIdColumn is not null)
    {
        updateInputs.Add(new TableCellInput(
            hierarchyIdColumn.Name,
            TableCellInputMode.Value,
            "/3/4.5/"));
    }
    if (sqlServerAliasLabelColumn is not null)
    {
        updateInputs.Add(new TableCellInput(
            sqlServerAliasLabelColumn.Name,
            TableCellInputMode.Value,
            "Alias updated"));
        updateInputs.Add(new TableCellInput(
            sqlServerAliasCountColumn!.Name,
            TableCellInputMode.Value,
            "84"));
        updateInputs.Add(new TableCellInput(
            sqlServerSysnameColumn!.Name,
            TableCellInputMode.Value,
            "updated_object"));
    }
    await session.UpdateTableRowAsync(database, table, inserted, updateInputs);
    var updatedSnapshot = await session.LoadTableDataAsync(database, table);
    var updated = updatedSnapshot.Rows.Single(row => Convert.ToString(row.Values[1]) == "Editor");
    Assert(Convert.ToInt64(updated.Values[2]) == 8, $"{session.Profile.ProviderDisplayName} 安全修改不正確");
    Assert(
        updated.Values[4] is byte[] updatedPayload && updatedPayload.SequenceEqual(new byte[] { 0xCA, 0xFE }),
        $"{session.Profile.ProviderDisplayName} 安全修改 binary 不正確");
    foreach (var exactDecimalColumn in exactDecimalColumns)
    {
        Assert(
            Convert.ToString(updated.Values[exactDecimalColumn.Ordinal]) ==
            GetExactDecimalTestValue(exactDecimalColumn, updated: true),
            $"{session.Profile.ProviderDisplayName} 高精度 decimal 安全修改不正確；actual={updated.Values[exactDecimalColumn.Ordinal]}");
    }
    foreach (var spatialColumn in spatialColumns)
    {
        AssertSpatialValue(
            updated.Values[spatialColumn.Ordinal],
            GetSpatialTestValue(session.Profile.Provider, spatialColumn, updated: true),
            spatialColumn,
            $"{session.Profile.ProviderDisplayName} {spatialColumn.DataTypeName} 安全修改不正確");
    }
    if (jsonColumn is not null)
    {
        using var updatedJson = JsonDocument.Parse(Convert.ToString(updated.Values[jsonColumn.Ordinal])!);
        Assert(updatedJson.RootElement.GetProperty("stage").GetString() == "updated", $"{session.Profile.ProviderDisplayName} 安全修改 JSON 不正確");
        Assert(updatedJson.RootElement.GetProperty("ok").GetBoolean(), $"{session.Profile.ProviderDisplayName} JSON 布林型別不正確");
    }
    if (xmlColumn is not null)
    {
        var updatedXml = System.Xml.Linq.XDocument.Parse(Convert.ToString(updated.Values[xmlColumn.Ordinal])!);
        Assert((string?)updatedXml.Root?.Attribute("stage") == "updated", $"{session.Profile.ProviderDisplayName} 安全修改 XML 不正確");
        Assert((string?)updatedXml.Root?.Element("ok") == "true", $"{session.Profile.ProviderDisplayName} XML 子元素不正確");
    }
    foreach (var networkColumn in networkColumns)
    {
        AssertNetworkValue(
            updated.Values[networkColumn.Ordinal],
            GetNetworkTestValue(networkColumn, updated: true),
            $"{session.Profile.ProviderDisplayName} 安全修改 {networkColumn.DataTypeName} 不正確");
    }
    if (legacyTextColumn is not null)
    {
        Assert(Convert.ToString(updated.Values[legacyTextColumn.Ordinal]) == "updated legacy text", "SQL Server text 安全修改不正確");
    }
    if (legacyNtextColumn is not null)
    {
        Assert(Convert.ToString(updated.Values[legacyNtextColumn.Ordinal]) == "更新 Unicode 文字 🍎", "SQL Server ntext 安全修改不正確");
    }
    if (legacyImageColumn is not null)
    {
        Assert(
            updated.Values[legacyImageColumn.Ordinal] is byte[] legacyImage &&
            legacyImage.SequenceEqual(new byte[] { 0xCA, 0xFE }),
            "SQL Server image 安全修改不正確");
    }
    foreach (var bitColumn in bitColumns)
    {
        Assert(
            Convert.ToUInt64(updated.Values[bitColumn.Ordinal]) ==
            ulong.Parse(GetBitTestValue(bitColumn, updated: true), CultureInfo.InvariantCulture),
            $"MySQL {bitColumn.DataTypeName} 安全修改不正確");
    }
    if (enumColumn is not null)
    {
        Assert(Convert.ToString(updated.Values[enumColumn.Ordinal]) == "archived", "MySQL ENUM 安全修改不正確");
    }
    if (setColumn is not null)
    {
        Assert(
            Convert.ToString(updated.Values[setColumn.Ordinal]) == "beta,gamma",
            $"MySQL SET 安全修改應保留宣告順序；actual={updated.Values[setColumn.Ordinal]}");
    }
    if (mySqlTimeColumn is not null)
    {
        Assert(
            Convert.ToString(updated.Values[mySqlTimeColumn.Ordinal]) == "-838:59:58.654321",
            $"MySQL TIME 負下界安全修改不正確；actual={updated.Values[mySqlTimeColumn.Ordinal]}");
    }
    if (mySqlYearColumn is not null)
    {
        Assert(
            Convert.ToUInt16(updated.Values[mySqlYearColumn.Ordinal]) == 2155,
            $"MySQL YEAR 上界安全修改不正確；actual={updated.Values[mySqlYearColumn.Ordinal]}");
    }
    foreach (var bitStringColumn in bitStringColumns)
    {
        Assert(
            Convert.ToString(updated.Values[bitStringColumn.Ordinal]) ==
            GetBitStringTestValue(bitStringColumn, updated: true),
            $"PostgreSQL {bitStringColumn.DataTypeName} 安全修改不正確");
    }
    if (timeZoneColumn is not null)
    {
        AssertTimeWithTimeZone(
            updated.Values[timeZoneColumn.Ordinal],
            new TimeSpan(23, 59, 58) + TimeSpan.FromTicks(6_543_210),
            TimeSpan.FromMinutes(-270),
            "PostgreSQL timetz 安全修改不正確");
    }
    if (intervalColumn is not null)
    {
        Assert(
            Convert.ToString(updated.Values[intervalColumn.Ordinal]) ==
            "months=-1;days=2;microseconds=-11045123456",
            $"PostgreSQL interval 安全修改不正確；actual={updated.Values[intervalColumn.Ordinal]}");
    }
    if (lsnColumn is not null)
    {
        Assert(
            Convert.ToString(updated.Values[lsnColumn.Ordinal]) == "FFFFFFFF/FFFFFFFF",
            $"PostgreSQL pg_lsn 安全修改不正確；actual={updated.Values[lsnColumn.Ordinal]}");
    }
    foreach (var systemIdentifierColumn in systemIdentifierColumns)
    {
        Assert(
            Convert.ToUInt64(updated.Values[systemIdentifierColumn.Ordinal]) ==
            ulong.Parse(
                GetSystemIdentifierTestValue(systemIdentifierColumn, updated: true),
                CultureInfo.InvariantCulture),
            $"PostgreSQL {systemIdentifierColumn.DataTypeName} 安全修改不正確");
    }
    if (fullTextVectorColumn is not null)
    {
        Assert(
            Convert.ToString(updated.Values[fullTextVectorColumn.Ordinal]) == "'bird':4C 'fish':2A",
            $"PostgreSQL tsvector 安全修改不正確；actual={updated.Values[fullTextVectorColumn.Ordinal]}");
    }
    if (fullTextQueryColumn is not null)
    {
        Assert(
            Convert.ToString(updated.Values[fullTextQueryColumn.Ordinal]) == "'bird' <2> 'fish':B",
            $"PostgreSQL tsquery 安全修改不正確；actual={updated.Values[fullTextQueryColumn.Ordinal]}");
    }
    foreach (var rangeColumn in rangeColumns)
    {
        Assert(
            Convert.ToString(updated.Values[rangeColumn.Ordinal]) ==
            GetPostgreSqlRangeTestValue(rangeColumn, updated: true),
            $"PostgreSQL {rangeColumn.DataTypeName} 安全修改不正確；actual={updated.Values[rangeColumn.Ordinal]}");
    }
    foreach (var arrayColumn in arrayColumns)
    {
        Assert(
            Convert.ToString(updated.Values[arrayColumn.Ordinal]) ==
            GetPostgreSqlArrayTestValue(arrayColumn, updated: true),
            $"PostgreSQL {arrayColumn.DataTypeName} 安全修改不正確；actual={updated.Values[arrayColumn.Ordinal]}");
    }
    foreach (var geometricColumn in geometricColumns)
    {
        Assert(
            Convert.ToString(updated.Values[geometricColumn.Ordinal]) ==
            GetPostgreSqlGeometricTestValue(geometricColumn, updated: true),
            $"PostgreSQL {geometricColumn.DataTypeName} 安全修改不正確；actual={updated.Values[geometricColumn.Ordinal]}");
    }
    foreach (var serverTextColumn in serverValidatedTextColumns)
    {
        Assert(
            Convert.ToString(updated.Values[serverTextColumn.Ordinal]) ==
            GetPostgreSqlServerTextTestValue(serverTextColumn, updated: true),
            $"PostgreSQL {serverTextColumn.DataTypeName} 安全修改不正確；actual={updated.Values[serverTextColumn.Ordinal]}");
    }
    if (hierarchyIdColumn is not null)
    {
        Assert(
            Convert.ToString(updated.Values[hierarchyIdColumn.Ordinal]) == "/3/4.5/",
            $"SQL Server hierarchyid 安全修改不正確；actual={updated.Values[hierarchyIdColumn.Ordinal]}");
    }
    if (sqlServerAliasLabelColumn is not null)
    {
        Assert(
            Convert.ToString(updated.Values[sqlServerAliasLabelColumn.Ordinal]) == "Alias updated",
            "SQL Server nvarchar alias 安全修改不正確");
        Assert(
            Convert.ToInt64(updated.Values[sqlServerAliasCountColumn!.Ordinal]) == 84,
            "SQL Server int alias 安全修改不正確");
        Assert(
            Convert.ToString(updated.Values[sqlServerSysnameColumn!.Ordinal]) == "updated_object",
            "SQL Server sysname 安全修改不正確");
    }

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

static string GetNetworkTestValue(TableColumnInfo column, bool updated) =>
    (column.DataTypeName.ToLowerInvariant(), updated) switch
    {
        ("inet", false) => "192.0.2.10/24",
        ("inet", true) => "2001:db8::10/64",
        ("cidr", false) => "192.0.2.0/24",
        ("cidr", true) => "2001:db8::/48",
        ("macaddr", false) => "08:00:2b:01:02:03",
        ("macaddr", true) => "08:00:2b:01:02:04",
        ("macaddr8", false) => "08:00:2b:ff:fe:01:02:03",
        ("macaddr8", true) => "08:00:2b:ff:fe:01:02:04",
        _ => throw new InvalidOperationException($"缺少 {column.DataTypeName} 測試值。")
    };

static string GetSpatialTestValue(
    DatabaseProviderKind provider,
    TableColumnInfo column,
    bool updated) =>
    (provider, column.DataTypeName.ToLowerInvariant(), updated) switch
    {
        (DatabaseProviderKind.MySql, "geometry", false) => "SRID=0;POINT(1 2)",
        (DatabaseProviderKind.MySql, "geometry", true) =>
            "SRID=4326;GEOMETRYCOLLECTION(POINT(3 4),LINESTRING(0 0,1 1))",
        (DatabaseProviderKind.MySql, "point", false) => "SRID=4326;POINT(3 4)",
        (DatabaseProviderKind.MySql, "point", true) => "SRID=0;POINT(5 6)",
        (DatabaseProviderKind.MySql, "linestring", false) => "SRID=0;LINESTRING(0 0,1 1)",
        (DatabaseProviderKind.MySql, "linestring", true) => "SRID=4326;LINESTRING(2 2,3 3)",
        (DatabaseProviderKind.MySql, "polygon", false) => "SRID=0;POLYGON((0 0,0 1,1 1,0 0))",
        (DatabaseProviderKind.MySql, "polygon", true) => "SRID=4326;POLYGON((0 0,0 2,2 2,0 0))",
        (DatabaseProviderKind.MySql, "multipoint", false) => "SRID=0;MULTIPOINT(0 0,1 1)",
        (DatabaseProviderKind.MySql, "multipoint", true) => "SRID=4326;MULTIPOINT(2 2,3 3)",
        (DatabaseProviderKind.MySql, "multilinestring", false) =>
            "SRID=0;MULTILINESTRING((0 0,1 1),(2 2,3 3))",
        (DatabaseProviderKind.MySql, "multilinestring", true) =>
            "SRID=4326;MULTILINESTRING((1 0,2 1),(3 2,4 3))",
        (DatabaseProviderKind.MySql, "multipolygon", false) =>
            "SRID=0;MULTIPOLYGON(((0 0,0 1,1 1,0 0)))",
        (DatabaseProviderKind.MySql, "multipolygon", true) =>
            "SRID=4326;MULTIPOLYGON(((0 0,0 2,2 2,0 0)))",
        (DatabaseProviderKind.MySql, "geomcollection" or "geometrycollection", false) =>
            "SRID=0;GEOMETRYCOLLECTION(POINT(1 2),LINESTRING(0 0,1 1))",
        (DatabaseProviderKind.MySql, "geomcollection" or "geometrycollection", true) =>
            "SRID=4326;GEOMETRYCOLLECTION(POINT(3 4),LINESTRING(2 2,3 3))",
        (DatabaseProviderKind.SqlServer, "geometry", false) => "SRID=4326;POINT (1 2)",
        (DatabaseProviderKind.SqlServer, "geometry", true) => "SRID=3857;LINESTRING (0 0, 1 1)",
        (DatabaseProviderKind.SqlServer, "geography", false) => "SRID=4326;POINT (-122.3 47.6)",
        (DatabaseProviderKind.SqlServer, "geography", true) =>
            "SRID=4326;LINESTRING (-122.3 47.6, -122.4 47.7)",
        _ => throw new InvalidOperationException(
            $"缺少 {provider} {column.Name} spatial 測試值。")
    };

static void AssertSpatialValue(
    object? actual,
    string expected,
    TableColumnInfo column,
    string message)
{
    var actualText = Convert.ToString(actual) ?? string.Empty;
    if (column.DataTypeName.Equals("multipoint", StringComparison.OrdinalIgnoreCase))
    {
        var separator = expected.IndexOf(';');
        var prefix = expected[..(separator + 1)];
        var points = expected[(expected.IndexOf('(', separator) + 1)..^1].Split(',');
        var alternate = $"{prefix}MULTIPOINT({string.Join(",", points.Select(point => $"({point})"))})";
        Assert(actualText == expected || actualText == alternate, $"{message}；actual={actualText}");
        return;
    }

    Assert(actualText == expected, $"{message}；actual={actualText}");
}

static string GetSystemIdentifierTestValue(TableColumnInfo column, bool updated) =>
    (column.DataTypeName.ToLowerInvariant(), updated) switch
    {
        ("oid", false) => uint.MaxValue.ToString(CultureInfo.InvariantCulture),
        ("xid", false) => "4000000000",
        ("cid", false) => "3000000000",
        ("xid8", false) => ulong.MaxValue.ToString(CultureInfo.InvariantCulture),
        ("oid", true) => "42",
        ("xid", true) => "43",
        ("cid", true) => "44",
        ("xid8", true) => "18446744073709551614",
        _ => throw new InvalidOperationException($"沒有 {column.DataTypeName} 的實機測試值。")
    };

static string GetBitTestValue(TableColumnInfo column, bool updated) =>
    (column.DataTypeName.ToLowerInvariant(), updated) switch
    {
        ("bit(8)", false) => "165",
        ("bit(8)", true) => "90",
        ("bit(64)", false) => "18446744073709551615",
        ("bit(64)", true) => "9223372036854775808",
        _ => throw new InvalidOperationException($"缺少 {column.DataTypeName} 測試值。")
    };

static string GetBitStringTestValue(TableColumnInfo column, bool updated) =>
    (column.DataTypeName.ToLowerInvariant(), updated) switch
    {
        ("bit(8)", false) => "10100101",
        ("bit(8)", true) => "01011010",
        ("bit varying(16)", false) => "101011",
        ("bit varying(16)", true) => "1111000011110000",
        _ => throw new InvalidOperationException($"缺少 {column.DataTypeName} 測試值。")
    };

static string GetPostgreSqlRangeTestValue(TableColumnInfo column, bool updated) =>
    (column.DataTypeName.ToLowerInvariant(), updated) switch
    {
        ("int4range", false) => "[1,10)",
        ("int8range", false) => "[10000000000,10000000100)",
        ("numrange", false) => "[1.25,9.75]",
        ("tsrange", false) => "[\"2026-01-01 00:00:00\",\"2026-01-02 00:00:00\")",
        ("tstzrange", false) => "[\"2026-01-01 00:00:00+00\",\"2026-01-02 00:00:00+00\")",
        ("daterange", false) => "[2026-01-01,2026-02-01)",
        ("int4multirange", false) => "{[1,5),[10,15)}",
        ("int8multirange", false) => "{[10000000000,10000000005),[10000000010,10000000015)}",
        ("nummultirange", false) => "{[1.25,2.5),[5.75,9.5]}",
        ("tsmultirange", false) =>
            "{[\"2026-01-01 00:00:00\",\"2026-01-02 00:00:00\"),[\"2026-02-01 00:00:00\",\"2026-02-02 00:00:00\")}",
        ("tstzmultirange", false) =>
            "{[\"2026-01-01 00:00:00+00\",\"2026-01-02 00:00:00+00\"),[\"2026-02-01 00:00:00+00\",\"2026-02-02 00:00:00+00\")}",
        ("datemultirange", false) => "{[2026-01-01,2026-02-01),[2026-03-01,2026-04-01)}",
        (var dataType, true) when dataType.EndsWith("multirange", StringComparison.Ordinal) => "{}",
        (_, true) => "empty",
        _ => throw new InvalidOperationException($"缺少 {column.DataTypeName} range 測試值。")
    };

static string GetPostgreSqlArrayTestValue(TableColumnInfo column, bool updated) =>
    (column.Name, updated) switch
    {
        ("numbers", false) => "{1,2,3}",
        ("labels", false) => "{plain,\"comma,value\",\"quote\\\"value\",\"NULL\",NULL}",
        ("matrix", false) => "{{1,2},{3,4}}",
        ("identifiers", false) =>
            "{11111111-1111-1111-1111-111111111111,22222222-2222-2222-2222-222222222222}",
        ("states", false) => "{happy,sad,\"comma,value\"}",
        ("json_items", false) => "{\"{\\\"a\\\": 1}\",\"[1, 2]\"}",
        ("range_items", false) => "{\"[1,5)\",\"[10,15)\"}",
        ("numbers", true) => "{8,9}",
        ("labels", true) => "{updated,\"two,values\",NULL}",
        ("matrix", true) => "{{5,6},{7,8}}",
        ("identifiers", true) => "{33333333-3333-3333-3333-333333333333}",
        ("states", true) => "{sad,\"comma,value\"}",
        ("json_items", true) => "{\"{\\\"updated\\\": true}\"}",
        ("range_items", true) => "{\"[20,30)\",empty}",
        _ => throw new InvalidOperationException($"缺少 {column.Name} PostgreSQL array 測試值。")
    };

static string GetPostgreSqlGeometricTestValue(TableColumnInfo column, bool updated) =>
    (column.Name, updated) switch
    {
        ("location", false) => "(1.5,2.5)",
        ("infinite_line", false) => "{1,2,-3}",
        ("segment", false) => "[(1,2),(3,4)]",
        ("bounds", false) => "(3,4),(1,2)",
        ("route", false) => "[(1,2),(3,4),(5,6)]",
        ("area", false) => "((1,2),(3,4),(5,6))",
        ("radius", false) => "<(1,2),3.5>",
        ("location", true) => "(-10.25,20.75)",
        ("infinite_line", true) => "{4,-5,6}",
        ("segment", true) => "[(-1,-2),(7,8)]",
        ("bounds", true) => "(7,8),(-1,-2)",
        ("route", true) => "((0,0),(2,0),(2,2),(0,2))",
        ("area", true) => "((0,0),(4,0),(4,3))",
        ("radius", true) => "<(-5,6),7.25>",
        _ => throw new InvalidOperationException($"缺少 {column.Name} PostgreSQL geometric 測試值。")
    };

static string GetPostgreSqlServerTextTestValue(TableColumnInfo column, bool updated) =>
    (column.DataTypeName.ToLowerInvariant(), updated) switch
    {
        ("jsonpath", false) => "$.\"store\".\"book\"[*]?(@.\"price\" < 10)",
        ("jsonpath", true) => "strict $.\"track\".\"segments\"[*]?(@.\"HR\" >= 140)",
        ("pg_snapshot" or "txid_snapshot", false) => "10:20:12,15",
        ("pg_snapshot" or "txid_snapshot", true) => "100:120:105,110",
        ("hstore", false) => "\"theme\"=>\"dark\", \"locale\"=>\"zh-TW\"",
        ("hstore", true) => "\"theme\"=>\"light\", \"locale\"=>\"en-US\"",
        ("ltree", false) => "Top.Science.Astronomy",
        ("ltree", true) => "Top.Technology.Databases",
        ("lquery", false) => "Top.*{1,2}.Astronomy",
        ("lquery", true) => "Top.!Science.*",
        ("ltxtquery", false) => "Science & Astronomy",
        ("ltxtquery", true) => "Technology | Databases",
        ("mood", false) => "happy",
        ("mood", true) => "comma,value",
        ("address_type", false) => "(Taipei,100)",
        ("address_type", true) => "(Taichung,400)",
        ("cube", false) => "(1, 2, 3)",
        ("cube", true) => "(4, 5, 6)",
        ("regclass", false) => "pg_class",
        ("regclass", true) => "pg_type",
        ("regrole", false) => "postgres",
        ("regrole", true) => "pg_database_owner",
        ("regconfig", false) => "english",
        ("regconfig", true) => "simple",
        ("regcollation", false) => "\"C\"",
        ("regcollation", true) => "\"POSIX\"",
        ("regdictionary", false) => "simple",
        ("regdictionary", true) => "english_stem",
        ("regnamespace", false) => "public",
        ("regnamespace", true) => "pg_catalog",
        ("regoper", false) => "!!",
        ("regoper", true) => "#-",
        ("regoperator", false) => "!!(NONE,tsquery)",
        ("regoperator", true) => "#-(jsonb,text[])",
        ("regproc", false) => "current_database",
        ("regproc", true) => "\"current_schema\"",
        ("regprocedure", false) => "lower(text)",
        ("regprocedure", true) => "upper(text)",
        ("regtype", false) => "integer",
        ("regtype", true) => "text",
        _ => throw new InvalidOperationException(
            $"缺少 {column.Name} PostgreSQL server-validated text 測試值。")
    };

static string GetExactDecimalTestValue(TableColumnInfo column, bool updated)
{
    var definition = TableCellValueConverter.GetExactDecimalDefinition(column);
    if (definition is not { Precision: { } precision, Scale: { } scale })
    {
        throw new InvalidOperationException($"{column.DataTypeName} 缺少 precision／scale 測試 metadata。");
    }

    var digit = updated ? '8' : '9';
    var fractionDigit = updated ? '2' : '1';
    string magnitude;
    if (scale < 0)
    {
        magnitude = new string(digit, precision) + new string('0', -scale);
    }
    else if (scale > precision)
    {
        magnitude = "0." + new string('0', scale - precision) + new string(fractionDigit, precision);
    }
    else
    {
        var integerDigits = new string(digit, precision - scale);
        var fractionDigits = scale == 0 ? string.Empty : "." + new string(fractionDigit, scale);
        magnitude = integerDigits + fractionDigits;
    }

    return (updated && !definition.IsUnsigned ? "-" : string.Empty) + magnitude;
}

static void AssertNetworkValue(object? actual, string expected, string message)
{
    var actualText = Convert.ToString(actual) ?? string.Empty;
    if (expected.Contains(':', StringComparison.Ordinal) &&
        !expected.Contains("::", StringComparison.Ordinal))
    {
        actualText = actualText.Replace(":", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
        expected = expected.Replace(":", string.Empty, StringComparison.Ordinal);
    }

    Assert(
        string.Equals(actualText, expected, StringComparison.OrdinalIgnoreCase),
        $"{message}；actual={actual?.GetType().FullName}:{Convert.ToString(actual)}，expected={expected}");
}

static void AssertTimeWithTimeZone(object? actual, TimeSpan expectedTime, TimeSpan expectedOffset, string message)
{
    var text = Convert.ToString(actual) ?? string.Empty;
    var parsed = DateTimeOffset.Parse(
        $"2000-01-01T{text}",
        CultureInfo.InvariantCulture,
        DateTimeStyles.None);
    Assert(
        parsed.TimeOfDay == expectedTime && parsed.Offset == expectedOffset,
        $"{message}；actual={text}");
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

file sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        _responseFactory = responseFactory;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_responseFactory(request));
    }
}
