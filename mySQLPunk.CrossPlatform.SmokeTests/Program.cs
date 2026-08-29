using System.Globalization;
using System.Net;
using System.Security.Cryptography;
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
        await session.ExecuteAsync(database, "CREATE TABLE sample (id BIGINT UNSIGNED PRIMARY KEY AUTO_INCREMENT, name VARCHAR(40) NOT NULL, quantity INT NULL, note VARCHAR(80) NULL, payload BLOB NULL, metadata JSON NULL, flags8 BIT(8) NULL, flags64 BIT(64) NULL);");
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
        await session.ExecuteAsync(database, "CREATE TABLE sample (id INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY, name VARCHAR(40) NOT NULL, quantity INTEGER NULL, note VARCHAR(80) NULL, payload BYTEA NULL, metadata JSONB NULL, document XML NULL, address INET NULL, subnet CIDR NULL, mac MACADDR NULL, mac8 MACADDR8 NULL, bits BIT(8) NULL, varbits BIT VARYING(16) NULL, alarm TIME WITH TIME ZONE NULL);");
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
        await session.ExecuteAsync(database, "CREATE TABLE dbo.sample (id INT IDENTITY PRIMARY KEY, name NVARCHAR(40) NOT NULL, quantity INT NULL, note NVARCHAR(80) NULL, payload VARBINARY(MAX) NULL, document XML NULL, legacy_text TEXT NULL, legacy_ntext NTEXT NULL, legacy_image IMAGE NULL);");
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
    await session.InsertTableRowAsync(database, table, insertInputs);
    var insertedSnapshot = await session.LoadTableDataAsync(database, table);
    var inserted = insertedSnapshot.Rows.Single(row => Convert.ToString(row.Values[1]) == "Editor");
    Assert(Convert.ToInt64(inserted.Values[2]) == 7, $"{session.Profile.ProviderDisplayName} 安全新增整數不正確");
    Assert(
        inserted.Values[4] is byte[] insertedPayload && insertedPayload.SequenceEqual(new byte[] { 0x00, 0xFF, 0x10 }),
        $"{session.Profile.ProviderDisplayName} 安全新增 binary 不正確");
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
    await session.UpdateTableRowAsync(database, table, inserted, updateInputs);
    var updatedSnapshot = await session.LoadTableDataAsync(database, table);
    var updated = updatedSnapshot.Rows.Single(row => Convert.ToString(row.Values[1]) == "Editor");
    Assert(Convert.ToInt64(updated.Values[2]) == 8, $"{session.Profile.ProviderDisplayName} 安全修改不正確");
    Assert(
        updated.Values[4] is byte[] updatedPayload && updatedPayload.SequenceEqual(new byte[] { 0xCA, 0xFE }),
        $"{session.Profile.ProviderDisplayName} 安全修改 binary 不正確");
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
