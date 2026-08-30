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
    ("資料庫物件搜尋與類型篩選", DatabaseObjectFilteringAsync),
    ("SQL 選取範圍安全執行", SqlExecutionSelectionAsync),
    ("本次執行期間查詢記錄", QueryExecutionHistoryAsync),
    ("Linux Secret Service 安全 round-trip", LinuxSecretServiceRoundTripAsync),
    ("macOS Keychain 安全 round-trip", MacOsKeychainRoundTripAsync),
    ("SQLite 查詢與 DDL/DML", SqliteExecutesQueriesAsync),
    ("SQLite metadata 與預覽 SQL", SqliteLoadsMetadataAsync),
    ("Table 資料安全編輯與衝突防護", TableDataEditingAsync),
    ("跨平台安全更新與下載", CrossPlatformUpdateAssetsAsync),
    ("Provider 驗證與工廠", ProviderFactoryValidatesProfilesAsync)
};

static Task QueryExecutionHistoryAsync()
{
    var history = new QueryExecutionHistory(capacity: 3, byteBudget: 256);
    var start = new DateTimeOffset(2026, 8, 30, 16, 0, 0, TimeSpan.FromHours(8));
    QueryExecutionHistoryEntry Entry(
        string sql,
        string database,
        int seconds,
        bool usedSelection = false) => new(
            start.AddSeconds(seconds),
            DatabaseProviderKind.Sqlite,
            database,
            sql,
            usedSelection,
            TimeSpan.FromMilliseconds(12),
            "完成，共 1 列");

    Assert(history.Add(Entry("SELECT 1;", "first.db", 1)), "有效 SQL 應加入本次記錄");
    Assert(history.Add(Entry("SELECT 2;", "first.db", 2, usedSelection: true)), "第二筆 SQL 應加入記錄");
    Assert(history.Add(Entry("SELECT 3;", "other.db", 3)), "不同資料庫 SQL 應加入記錄");
    Assert(history.Entries.Select(entry => entry.Sql).SequenceEqual(new[] { "SELECT 3;", "SELECT 2;", "SELECT 1;" }),
        "查詢記錄應依最新時間置頂");
    Assert(history.Entries[1].DisplayText.Contains("選取範圍", StringComparison.Ordinal) &&
           history.Entries[1].DisplayText.Contains("first.db", StringComparison.Ordinal),
        "記錄顯示應標示來源資料庫與選取範圍");

    Assert(history.Add(Entry("SELECT 1;", "first.db", 4)), "重複 SQL 應更新到最上方");
    Assert(history.Entries.Count == 3 && history.Entries[0].ExecutedAt == start.AddSeconds(4),
        "同資料庫的相同 SQL 應去重並更新時間");
    Assert(history.Add(Entry("SELECT 4;", "first.db", 5)), "容量邊界前應可新增");
    Assert(history.Entries.Count == 3 && history.Entries.All(entry => entry.Sql != "SELECT 2;"),
        "超過容量時應移除最舊記錄");

    var oversized = string.Concat(Enumerable.Repeat("😀", 65));
    Assert(!history.Add(Entry(oversized, "first.db", 6)) && history.Entries.Count == 3,
        "單筆 SQL 的 UTF-8 bytes 超過總預算時不可加入或破壞既有記錄");
    var budgetHistory = new QueryExecutionHistory(capacity: 10, byteBudget: 10);
    Assert(budgetHistory.Add(Entry("123456", "first.db", 7)) &&
           budgetHistory.Add(Entry("abcde", "first.db", 8)) &&
           budgetHistory.Entries.Count == 1 && budgetHistory.Entries[0].Sql == "abcde",
        "合計 UTF-8 byte 預算超限時應從最舊記錄開始移除");
    Assert(QueryExecutionHistory.BuildPreview("  SELECT\n\t😀  FROM   sample;  ", 22) == "SELECT 😀 FROM sample;",
        "預覽應摺疊空白並保留 Unicode 字元");

    history.Clear();
    Assert(history.Entries.Count == 0, "清除後不應保留本次執行期間的 SQL");
    return Task.CompletedTask;
}

static Task SqlExecutionSelectionAsync()
{
    const string editorSql = "CREATE TABLE should_not_run(id INTEGER);\nSELECT 42 AS chosen;";
    var selectStart = editorSql.IndexOf("SELECT", StringComparison.Ordinal);
    var selected = SqlExecutionSelectionService.Resolve(editorSql, selectStart, editorSql.Length);
    Assert(selected.UsesSelection && selected.Sql == "SELECT 42 AS chosen;",
        "非空白選取範圍應是唯一送出的 SQL");

    var reversed = SqlExecutionSelectionService.Resolve(editorSql, editorSql.Length, selectStart);
    Assert(reversed == selected, "反向選取應解析成相同 SQL");

    var whitespaceStart = editorSql.IndexOf('\n');
    var whitespace = SqlExecutionSelectionService.Resolve(editorSql, whitespaceStart, whitespaceStart + 1);
    Assert(!whitespace.UsesSelection && whitespace.Sql == editorSql,
        "只選到空白時應安全退回完整 SQL");

    var noSelection = SqlExecutionSelectionService.Resolve(editorSql, 0, 0);
    Assert(!noSelection.UsesSelection && noSelection.Sql == editorSql,
        "沒有選取範圍時應維持原本的全文執行");

    var clamped = SqlExecutionSelectionService.Resolve(editorSql, selectStart, int.MaxValue);
    Assert(clamped == selected, "超出文字長度的 UI selection index 應安全限制範圍");
    return Task.CompletedTask;
}

static Task DatabaseObjectFilteringAsync()
{
    IReadOnlyList<DatabaseObjectInfo> objects = new[]
    {
        new DatabaseObjectInfo("sales", "CustomerOrders", DatabaseObjectKind.Table),
        new DatabaseObjectInfo("audit", "OrderHistory", DatabaseObjectKind.View),
        new DatabaseObjectInfo("public", "customers", DatabaseObjectKind.Table),
        new DatabaseObjectInfo(string.Empty, "StatusView", DatabaseObjectKind.View)
    };

    var allObjects = DatabaseObjectFilterService.Filter(objects, "  ");
    Assert(allObjects.SequenceEqual(objects), "空白搜尋應保留全部物件及原始順序");

    var schemaAndName = DatabaseObjectFilterService.Filter(objects, "SALES order");
    Assert(schemaAndName.Count == 1 && schemaAndName[0].Name == "CustomerOrders",
        "搜尋應忽略大小寫並支援 schema 與名稱的多個條件");

    var tablesOnly = DatabaseObjectFilterService.Filter(objects, "customer", DatabaseObjectKind.Table);
    Assert(tablesOnly.Count == 2 && tablesOnly.All(item => item.Kind == DatabaseObjectKind.Table),
        "資料表類型篩選不應混入檢視表");

    var viewsOnly = DatabaseObjectFilterService.Filter(objects, "order", DatabaseObjectKind.View);
    Assert(viewsOnly.Count == 1 && viewsOnly[0].Schema == "audit",
        "檢視表篩選應和搜尋條件同時生效");

    Assert(DatabaseObjectFilterService.Filter(objects, "missing").Count == 0,
        "無符合物件時應回傳空集合");
    return Task.CompletedTask;
}

if (string.Equals(Environment.GetEnvironmentVariable("MYSQLPUNK_LIVE_TESTS"), "1", StringComparison.Ordinal))
{
    tests.Add(("MySQL 實機連線、metadata 與 SQL", MySqlLiveRoundTripAsync));
    tests.Add(("MariaDB 實機連線、metadata 與 SQL", MariaDbLiveRoundTripAsync));
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

        var macBundlePath = Path.Combine(directory, "mySQLPunk.app");
        Directory.CreateDirectory(macBundlePath);
        var macUpdate = update with
        {
            RuntimeIdentifier = "osx-x64",
            PackageFileName = "mySQLPunk-1.0.0.20-osx-x64.app.zip"
        };
        var macApplyStartInfo = new CrossPlatformUpdateService().BuildMacOsApplyStartInfo(
            macUpdate,
            new CrossPlatformUpdateDownload(destinationPath, packageBytes.Length, expectedHash),
            applyScriptPath,
            macBundlePath,
            23456,
            "fedcba9876543210fedcba9876543210");
        Assert(
            macApplyStartInfo.FileName == applyScriptPath && !macApplyStartInfo.UseShellExecute,
            "macOS updater 啟動方式不正確");
        Assert(
            macApplyStartInfo.ArgumentList.SequenceEqual(new[]
            {
                "--archive", destinationPath,
                "--sha256", expectedHash,
                "--version", "1.0.0.20",
                "--runtime", "osx-x64",
                "--wait-pid", "23456",
                "--target-bundle", macBundlePath,
                "--lock-token", "fedcba9876543210fedcba9876543210"
            }),
            "macOS updater 參數未使用獨立 ArgumentList 或內容不正確");

        var resultPath = Path.Combine(directory, "last-apply-result");
        await File.WriteAllTextAsync(
            resultPath,
            $"status=rollback\nversion=1.0.0.20\nruntime=linux-x64\nmessage=Startup failed.\nlog={Path.Combine(directory, "apply.log")}\n");
        var applyResult = new CrossPlatformUpdateService().ReadAndClearLinuxApplyResult(resultPath);
        Assert(applyResult is { WasRolledBack: true, Version: "1.0.0.20" }, "Linux rollback 結果解析不正確");
        Assert(!File.Exists(resultPath), "Linux rollback 結果顯示後未清除");
        AssertThrows<InvalidDataException>(() => CrossPlatformUpdateService.ParseLinuxApplyResult(
            $"status=success\nversion=1.0.0.20\nruntime=linux-x64\nmessage=Unexpected.\nlog={Path.Combine(directory, "apply.log")}\n"));
        await File.WriteAllTextAsync(
            resultPath,
            $"status=rollback\nversion=1.0.0.20\nruntime=osx-x64\nmessage=Startup failed.\nlog={Path.Combine(directory, "mac-apply.log")}\n");
        var macApplyResult = new CrossPlatformUpdateService().ReadAndClearMacOsApplyResult(resultPath);
        Assert(
            macApplyResult is { WasRolledBack: true, RuntimeIdentifier: "osx-x64" },
            "macOS rollback 結果解析不正確");
        Assert(!File.Exists(resultPath), "macOS rollback 結果顯示後未清除");
        AssertThrows<InvalidDataException>(() => CrossPlatformUpdateService.ParseMacOsApplyResult(
            $"status=rollback\nversion=1.0.0.20\nruntime=linux-x64\nmessage=Wrong RID.\nlog={Path.Combine(directory, "mac-apply.log")}\n"));

        if (OperatingSystem.IsLinux())
        {
            var previousStateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
            Environment.SetEnvironmentVariable("XDG_STATE_HOME", Path.Combine(directory, "lock-state"));
            try
            {
                var service = new CrossPlatformUpdateService();
                var currentLinuxRuntime = CrossPlatformUpdateService.ResolveCurrentRuntimeIdentifier();
                var nativeLinuxUpdate = update with
                {
                    RuntimeIdentifier = currentLinuxRuntime,
                    PackageFileName = CrossPlatformUpdateService.BuildPackageFileName(
                        update.LatestVersionText,
                        currentLinuxRuntime)
                };
                var wrongLinuxRuntime = currentLinuxRuntime == "linux-x64"
                    ? "linux-arm64"
                    : "linux-x64";
                var wrongArchitectureUpdate = nativeLinuxUpdate with
                {
                    RuntimeIdentifier = wrongLinuxRuntime,
                    PackageFileName = CrossPlatformUpdateService.BuildPackageFileName(
                        update.LatestVersionText,
                        wrongLinuxRuntime)
                };
                AssertThrows<PlatformNotSupportedException>(() =>
                {
                    using var unexpected = service.StartLinuxApply(
                        wrongArchitectureUpdate,
                        new CrossPlatformUpdateDownload(destinationPath, packageBytes.Length, expectedHash),
                        applyScriptPath,
                        Environment.ProcessId);
                });
                using (var lockOwner = service.StartLinuxApply(
                           nativeLinuxUpdate,
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
                        nativeLinuxUpdate,
                        new CrossPlatformUpdateDownload(destinationPath, packageBytes.Length, expectedHash),
                        applyScriptPath,
                        Environment.ProcessId);
                });

                var lockPath = CrossPlatformUpdateService.ResolveLinuxApplyLockPath();
                await File.WriteAllTextAsync(
                    lockPath,
                    "token=0123456789abcdef0123456789abcdef\npid=2147483647\n");
                using (var recovered = service.StartLinuxApply(
                           nativeLinuxUpdate,
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
            CREATE TABLE numeric_sample (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                amount DECIMAL(30,10) NOT NULL,
                note TEXT NOT NULL,
                approximate_value REAL NULL
            );
            CREATE TABLE temporal_sample (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                event_date DATE NULL,
                clock_time TIME NULL,
                recorded_at DATETIME NULL,
                changed_at TIMESTAMP NULL,
                offset_at DATETIMEOFFSET NULL,
                legacy_time TIME NULL
            );
            INSERT INTO temporal_sample (
                name, event_date, clock_time, recorded_at, changed_at, offset_at, legacy_time)
            VALUES (
                'Temporal before',
                '2026-08-30',
                '12:34:56.1234567',
                '2026-08-30 12:34:56.1234567',
                '2026-08-30 12:34:56',
                '2026-08-30 13:14:15.1234567+08:00',
                45296);
            CREATE TABLE identifier_sample (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                uuid_value UUID NOT NULL,
                guid_value GUID NULL,
                legacy_guid UUID NULL,
                legacy_blob GUID NULL
            );
            INSERT INTO identifier_sample (name, uuid_value, guid_value, legacy_guid, legacy_blob)
            VALUES (
                'Identifier before',
                '123e4567-e89b-12d3-a456-426614174000',
                '6F9619FF-8B86-D011-B42D-00C04FC964FF',
                '{00000000-0000-0000-0000-000000000001}',
                X'00112233445566778899AABBCCDDEEFF');
            CREATE TABLE collation_sample (
                id INTEGER PRIMARY KEY,
                name TEXT COLLATE NOCASE NOT NULL,
                padded TEXT COLLATE RTRIM NOT NULL,
                marker TEXT NOT NULL
            );
            INSERT INTO collation_sample VALUES (1, 'Alpha', 'tail ', 'before');
            INSERT INTO paged_sample (id, name) VALUES
                (1, 'alpha'), (2, 'same'), (3, 'same'), (4, 'beta'), (5, 'zulu');
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

        var nameDescending = new TableDataSort("name", Descending: true);
        var sortedFirstPage = await session.LoadTableDataAsync(
            profile.Database,
            pagedTable,
            rowLimit: 2,
            rowOffset: 0,
            sort: nameDescending);
        var sortedSecondPage = await session.LoadTableDataAsync(
            profile.Database,
            pagedTable,
            rowLimit: 2,
            rowOffset: 2,
            sort: nameDescending);
        var sortedLastPage = await session.LoadTableDataAsync(
            profile.Database,
            pagedTable,
            rowLimit: 2,
            rowOffset: 4,
            sort: nameDescending);
        Assert(
            sortedFirstPage.Rows.Select(row => Convert.ToInt64(row.Values[0])).SequenceEqual(new long[] { 5, 2 }) &&
            sortedSecondPage.Rows.Select(row => Convert.ToInt64(row.Values[0])).SequenceEqual(new long[] { 3, 4 }) &&
            sortedLastPage.Rows.Select(row => Convert.ToInt64(row.Values[0])).SequenceEqual(new long[] { 1 }),
            "欄位遞減排序應在相同值後以 Primary Key 遞增作為跨頁 tie-breaker");
        var sortedExportPage = QueryResultExportService.CreateTablePageResult(sortedFirstPage);
        Assert(
            sortedExportPage.Columns.SequenceEqual(new[] { "id", "name" }) &&
            sortedExportPage.Rows.Select(row => Convert.ToInt64(row[0])).SequenceEqual(new long[] { 5, 2 }) &&
            sortedExportPage.WasTruncated,
            "Table 本頁匯出應保留目前欄位與排序，並標示還有其他頁未包含");
        await using var sortedExportStream = new MemoryStream();
        await QueryResultExportService.WriteAsync(
            sortedExportPage,
            sortedExportStream,
            QueryResultExportFormat.Csv);
        var sortedExportCsv = Encoding.UTF8.GetString(sortedExportStream.ToArray()).TrimStart('\uFEFF');
        Assert(
            sortedExportCsv == "id,name\r\n5,zulu\r\n2,same\r\n",
            $"Table 本頁 CSV 應保留目前排序與安全格式；actual={sortedExportCsv.Replace("\r", "\\r").Replace("\n", "\\n")}");
        var lastExportPage = QueryResultExportService.CreateTablePageResult(sortedLastPage);
        Assert(lastExportPage.WasTruncated, "非第一頁即使已到結尾，匯出仍應標示不是完整 Table");
        await AssertThrowsAsync<ArgumentException>(() => session.LoadTableDataAsync(
            profile.Database,
            pagedTable,
            sort: new TableDataSort("name; DROP TABLE paged_sample", Descending: false)));
        await AssertThrowsAsync<InvalidOperationException>(() => session.LoadTableDataAsync(
            profile.Database,
            table,
            sort: new TableDataSort("metadata", Descending: false)));
        await AssertThrowsAsync<InvalidOperationException>(() => session.LoadTableDataAsync(
            profile.Database,
            new DatabaseObjectInfo(string.Empty, "no_primary_key", DatabaseObjectKind.Table),
            sort: new TableDataSort("name", Descending: false)));

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
        var dateColumn = new TableColumnInfo(
            0,
            "event_date",
            "date",
            true,
            false,
            false,
            false,
            TableColumnValueKind.Date);
        Assert(
            Equals(
                TableCellValueConverter.Parse(
                    dateColumn,
                    new TableCellInput("event_date", TableCellInputMode.Value, " 2026-08-30 ")),
                new DateTime(2026, 8, 30)),
            "Date 欄位應接受純 yyyy-MM-dd 日期");
        Assert(
            TableCellValueConverter.Format(dateColumn, new DateTime(2026, 8, 30)) == "2026-08-30" &&
            TableCellValueConverter.FormatForDisplay(dateColumn, new DateOnly(2026, 8, 30)) == "2026-08-30",
            "Date 欄位在 grid 與編輯器都應顯示純日期");
        Assert(
            TableCellValueConverter.MatchesOriginal(
                dateColumn,
                new TableCellInput("event_date", TableCellInputMode.Value, "2026-08-30"),
                new DateTime(2026, 8, 30)),
            "Date 純日期輸入應與資料庫讀回的午夜 DateTime 視為相同");
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            dateColumn,
            new TableCellInput(
                "event_date",
                TableCellInputMode.Value,
                "2026-08-30T12:34:56.0000000")));

        var temporalTable = new DatabaseObjectInfo(string.Empty, "temporal_sample", DatabaseObjectKind.Table);
        var temporalSnapshot = await session.LoadTableDataAsync(profile.Database, temporalTable);
        var temporalColumns = temporalSnapshot.Columns
            .Where(column => column.ValueKind == TableColumnValueKind.SqliteTemporal)
            .ToList();
        Assert(
            temporalColumns.Select(column => column.Name).SequenceEqual(
                new[]
                {
                    "event_date", "clock_time", "recorded_at", "changed_at", "offset_at", "legacy_time"
                }),
            "SQLite DATE／TIME／DATETIME／TIMESTAMP／DATETIMEOFFSET metadata 應使用無損文字 temporal 編輯器");
        var eventDateColumn = temporalColumns.Single(column => column.Name == "event_date");
        var clockTimeColumn = temporalColumns.Single(column => column.Name == "clock_time");
        var recordedAtColumn = temporalColumns.Single(column => column.Name == "recorded_at");
        var changedAtColumn = temporalColumns.Single(column => column.Name == "changed_at");
        var offsetAtColumn = temporalColumns.Single(column => column.Name == "offset_at");
        Assert(
            TableCellValueConverter.Parse(
                eventDateColumn,
                new TableCellInput("event_date", TableCellInputMode.Value, " 2026-09-01 ")) is
            SqliteTemporalValue { Text: "2026-09-01" } &&
            TableCellValueConverter.Parse(
                clockTimeColumn,
                new TableCellInput("clock_time", TableCellInputMode.Value, "23:59:59.1234567")) is
            SqliteTemporalValue { Text: "23:59:59.1234567" } &&
            TableCellValueConverter.Parse(
                recordedAtColumn,
                new TableCellInput("recorded_at", TableCellInputMode.Value, "2026-09-01T01:02:03.1234567")) is
            SqliteTemporalValue { Text: "2026-09-01T01:02:03.1234567" } &&
            TableCellValueConverter.Parse(
                offsetAtColumn,
                new TableCellInput(
                    "offset_at",
                    TableCellInputMode.Value,
                    "2026-09-01 07:08:09.1234567-04:30")) is
            SqliteTemporalValue { Text: "2026-09-01 07:08:09.1234567-04:30" },
            "SQLite temporal parser 應保留合法 ISO 日期、純時間、日期時間與 offset 日期時間文字");
        foreach (var invalid in new[]
                 {
                     (Column: eventDateColumn, Value: "2026-09-01 00:00:00"),
                     (Column: clockTimeColumn, Value: "2026-09-01 23:59:59"),
                     (Column: clockTimeColumn, Value: "1.00:00:00"),
                     (Column: clockTimeColumn, Value: "24:00:00"),
                     (Column: recordedAtColumn, Value: "2026-09-01T01:02:03+08:00"),
                     (Column: changedAtColumn, Value: "2026-09-01 01:02:03.12345678"),
                     (Column: offsetAtColumn, Value: "2026-09-01 07:08:09.1234567"),
                     (Column: offsetAtColumn, Value: "2026-09-01 07:08:09Z"),
                     (Column: offsetAtColumn, Value: "2026-09-01 07:08:09.12345678+08:00")
                 })
        {
            AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
                invalid.Column,
                new TableCellInput(invalid.Column.Name, TableCellInputMode.Value, invalid.Value)));
        }
        Assert(
            TableCellValueConverter.MatchesOriginal(
                recordedAtColumn,
                new TableCellInput(
                    "recorded_at",
                    TableCellInputMode.Value,
                    "2026-08-30 12:34:56Z"),
                "2026-08-30 12:34:56Z"),
            "SQLite legacy temporal 原值未修改時不可因新格式驗證被連帶重寫");

        var originalTemporalRow = temporalSnapshot.Rows.Single();
        await session.UpdateTableRowAsync(
            profile.Database,
            temporalTable,
            originalTemporalRow,
            new[]
            {
                new TableCellInput("event_date", TableCellInputMode.Value, "2026-09-01"),
                new TableCellInput("clock_time", TableCellInputMode.Value, "23:59:59.1234567"),
                new TableCellInput("recorded_at", TableCellInputMode.Value, "2026-09-01T01:02:03.1234567"),
                new TableCellInput("changed_at", TableCellInputMode.Value, "2026-09-01 04:05:06"),
                new TableCellInput(
                    "offset_at",
                    TableCellInputMode.Value,
                    "2026-09-01T07:08:09.1234567-04:30")
            });
        var storedTemporal = await session.ExecuteAsync(
            profile.Database,
            "SELECT event_date, clock_time, recorded_at, changed_at, offset_at, " +
            "typeof(event_date), typeof(clock_time), typeof(recorded_at), typeof(changed_at), " +
            "typeof(offset_at) " +
            "FROM temporal_sample WHERE id = 1;");
        Assert(
            Convert.ToString(storedTemporal.Rows.Single()[0]) == "2026-09-01" &&
            Convert.ToString(storedTemporal.Rows.Single()[1]) == "23:59:59.1234567" &&
            Convert.ToString(storedTemporal.Rows.Single()[2]) == "2026-09-01T01:02:03.1234567" &&
            Convert.ToString(storedTemporal.Rows.Single()[3]) == "2026-09-01 04:05:06" &&
            Convert.ToString(storedTemporal.Rows.Single()[4]) ==
                "2026-09-01T07:08:09.1234567-04:30" &&
            storedTemporal.Rows.Single().Skip(5).All(value => Convert.ToString(value) == "text"),
            "SQLite temporal 寫入應以 TEXT 精確保留純日期、純時間、無 offset 與含 offset 日期時間");
        var updatedTemporalRow = (await session.LoadTableDataAsync(profile.Database, temporalTable)).Rows.Single();
        await AssertThrowsAsync<InvalidOperationException>(() => session.UpdateTableRowAsync(
            profile.Database,
            temporalTable,
            updatedTemporalRow,
            new[]
            {
                new TableCellInput("clock_time", TableCellInputMode.Value, "2026-09-01 23:59:59")
            }));
        var afterRejectedTemporal = await session.ExecuteAsync(
            profile.Database,
            "SELECT clock_time FROM temporal_sample WHERE id = 1;");
        Assert(
            Convert.ToString(afterRejectedTemporal.Rows.Single()[0]) == "23:59:59.1234567",
            "SQLite 會被注入日期的 TIME 輸入不可落入資料庫");
        await session.ExecuteAsync(
            profile.Database,
            "UPDATE temporal_sample SET recorded_at = '2030-01-02 03:04:05' WHERE id = 1;");
        await AssertThrowsAsync<TableDataConflictException>(() => session.UpdateTableRowAsync(
            profile.Database,
            temporalTable,
            updatedTemporalRow,
            new[] { new TableCellInput("name", TableCellInputMode.Value, "must-not-overwrite") }));
        Assert(
            Convert.ToString((await session.ExecuteAsync(
                profile.Database,
                "SELECT name FROM temporal_sample WHERE id = 1;")).Rows.Single()[0]) == "Temporal before",
            "SQLite temporal optimistic predicate 應攔截外部修改");

        var identifierTable = new DatabaseObjectInfo(string.Empty, "identifier_sample", DatabaseObjectKind.Table);
        var identifierSnapshot = await session.LoadTableDataAsync(profile.Database, identifierTable);
        var identifierColumns = identifierSnapshot.Columns
            .Where(column => column.ValueKind == TableColumnValueKind.SqliteGuid)
            .ToList();
        Assert(
            identifierColumns.Select(column => column.Name).SequenceEqual(
                new[] { "uuid_value", "guid_value", "legacy_guid", "legacy_blob" }),
            "SQLite UUID／GUID metadata 應使用保留文字大小寫的 GUID 編輯器");
        var uuidColumn = identifierColumns.Single(column => column.Name == "uuid_value");
        var guidColumn = identifierColumns.Single(column => column.Name == "guid_value");
        var legacyGuidColumn = identifierColumns.Single(column => column.Name == "legacy_guid");
        var legacyBlobColumn = identifierColumns.Single(column => column.Name == "legacy_blob");
        Assert(
            TableCellValueConverter.Parse(
                uuidColumn,
                new TableCellInput(
                    "uuid_value",
                    TableCellInputMode.Value,
                    " aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee ")) is
            SqliteGuidValue { Text: "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee" } &&
            TableCellValueConverter.Parse(
                guidColumn,
                new TableCellInput(
                    "guid_value",
                    TableCellInputMode.Value,
                    "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE")) is
            SqliteGuidValue { Text: "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE" },
            "SQLite GUID parser 應驗證標準 D 格式並保留輸入大小寫");
        foreach (var invalid in new[]
                 {
                     "aaaaaaaaaaaabbbbccccddddeeeeeeeeeeee",
                     "{aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee}",
                     "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeee",
                     "not-a-guid"
                 })
        {
            AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
                uuidColumn,
                new TableCellInput("uuid_value", TableCellInputMode.Value, invalid)));
        }
        Assert(
            TableCellValueConverter.MatchesOriginal(
                legacyGuidColumn,
                new TableCellInput(
                    "legacy_guid",
                    TableCellInputMode.Value,
                    "{00000000-0000-0000-0000-000000000001}"),
                "{00000000-0000-0000-0000-000000000001}"),
            "SQLite legacy GUID 原值未修改時不可因新格式驗證阻擋其它欄位寫入");
        var legacyBlobValue = identifierSnapshot.Rows.Single().Values[legacyBlobColumn.Ordinal];
        Assert(
            legacyBlobValue is byte[] { Length: 16 } &&
            TableCellValueConverter.MatchesOriginal(
                legacyBlobColumn,
                new TableCellInput(
                    "legacy_blob",
                    TableCellInputMode.Value,
                    TableCellValueConverter.Format(legacyBlobValue)),
                legacyBlobValue),
            "SQLite legacy BLOB GUID 未修改時應保留原始 bytes 與 storage class");

        await session.UpdateTableRowAsync(
            profile.Database,
            identifierTable,
            identifierSnapshot.Rows.Single(),
            new[]
            {
                new TableCellInput("name", TableCellInputMode.Value, "Identifier after"),
                new TableCellInput(
                    "uuid_value",
                    TableCellInputMode.Value,
                    "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                new TableCellInput(
                    "guid_value",
                    TableCellInputMode.Value,
                    "BBBBBBBB-CCCC-DDDD-EEEE-FFFFFFFFFFFF")
            });
        var storedIdentifiers = await session.ExecuteAsync(
            profile.Database,
            "SELECT name, uuid_value, guid_value, legacy_guid, legacy_blob, " +
            "typeof(uuid_value), typeof(guid_value), typeof(legacy_guid), typeof(legacy_blob) " +
            "FROM identifier_sample WHERE id = 1;");
        Assert(
            Convert.ToString(storedIdentifiers.Rows.Single()[0]) == "Identifier after" &&
            Convert.ToString(storedIdentifiers.Rows.Single()[1]) ==
                "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee" &&
            Convert.ToString(storedIdentifiers.Rows.Single()[2]) ==
                "BBBBBBBB-CCCC-DDDD-EEEE-FFFFFFFFFFFF" &&
            Convert.ToString(storedIdentifiers.Rows.Single()[3]) ==
                "{00000000-0000-0000-0000-000000000001}" &&
            Convert.ToString(storedIdentifiers.Rows.Single()[4]) ==
                "0x00112233445566778899AABBCCDDEEFF" &&
            storedIdentifiers.Rows.Single().Skip(5).Take(3)
                .All(value => Convert.ToString(value) == "text") &&
            Convert.ToString(storedIdentifiers.Rows.Single()[8]) == "blob",
            "SQLite UUID／GUID 寫入應以 TEXT 精確保留大小寫且不改寫 legacy TEXT／BLOB 原值");
        var updatedIdentifierRow = (await session.LoadTableDataAsync(
            profile.Database,
            identifierTable)).Rows.Single();
        await AssertThrowsAsync<InvalidOperationException>(() => session.UpdateTableRowAsync(
            profile.Database,
            identifierTable,
            updatedIdentifierRow,
            new[]
            {
                new TableCellInput(
                    "uuid_value",
                    TableCellInputMode.Value,
                    "aaaaaaaaaaaabbbbccccddddeeeeeeeeeeee")
            }));
        Assert(
            Convert.ToString((await session.ExecuteAsync(
                profile.Database,
                "SELECT uuid_value FROM identifier_sample WHERE id = 1;")).Rows.Single()[0]) ==
                "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            "SQLite 非標準 GUID 格式不可落入資料庫");
        await session.ExecuteAsync(
            profile.Database,
            "UPDATE identifier_sample SET guid_value = 'cccccccc-dddd-eeee-ffff-000000000000' WHERE id = 1;");
        await AssertThrowsAsync<TableDataConflictException>(() => session.UpdateTableRowAsync(
            profile.Database,
            identifierTable,
            updatedIdentifierRow,
            new[] { new TableCellInput("name", TableCellInputMode.Value, "must-not-overwrite") }));
        Assert(
            Convert.ToString((await session.ExecuteAsync(
                profile.Database,
                "SELECT name FROM identifier_sample WHERE id = 1;")).Rows.Single()[0]) == "Identifier after",
            "SQLite GUID optimistic predicate 應攔截外部修改");

        var collationTable = new DatabaseObjectInfo(string.Empty, "collation_sample", DatabaseObjectKind.Table);
        var staleCollationRow = (await session.LoadTableDataAsync(
            profile.Database,
            collationTable)).Rows.Single();
        await session.ExecuteAsync(
            profile.Database,
            "UPDATE collation_sample SET name = 'alpha', padded = 'tail' WHERE id = 1;");
        await AssertThrowsAsync<TableDataConflictException>(() => session.UpdateTableRowAsync(
            profile.Database,
            collationTable,
            staleCollationRow,
            new[] { new TableCellInput("marker", TableCellInputMode.Value, "must-not-overwrite") }));
        var afterCollationConflict = await session.ExecuteAsync(
            profile.Database,
            "SELECT name, quote(padded), marker FROM collation_sample WHERE id = 1;");
        Assert(
            Convert.ToString(afterCollationConflict.Rows.Single()[0]) == "alpha" &&
            Convert.ToString(afterCollationConflict.Rows.Single()[1]) == "'tail'" &&
            Convert.ToString(afterCollationConflict.Rows.Single()[2]) == "before",
            "SQLite NOCASE／RTRIM 不可掩蓋外部文字 bytes 變更或提交 stale update");
        var refreshedCollationRow = (await session.LoadTableDataAsync(
            profile.Database,
            collationTable)).Rows.Single();
        await session.UpdateTableRowAsync(
            profile.Database,
            collationTable,
            refreshedCollationRow,
            new[] { new TableCellInput("marker", TableCellInputMode.Value, "after-refresh") });
        Assert(
            Convert.ToString((await session.ExecuteAsync(
                profile.Database,
                "SELECT marker FROM collation_sample WHERE id = 1;")).Rows.Single()[0]) == "after-refresh",
            "SQLite byte-exact optimistic predicate 不可阻擋重新整理後的合法修改");

        var numericTable = new DatabaseObjectInfo(string.Empty, "numeric_sample", DatabaseObjectKind.Table);
        var emptyNumeric = await session.LoadTableDataAsync(profile.Database, numericTable);
        var sqliteNumericColumn = emptyNumeric.Columns.Single(column => column.Name == "amount");
        Assert(
            sqliteNumericColumn is { ValueKind: TableColumnValueKind.SqliteNumeric, IsEditable: true },
            "SQLite NUMERIC affinity 欄位應使用避免 REAL 精度損失的編輯器");
        var sqliteDoubleColumn = emptyNumeric.Columns.Single(column => column.Name == "approximate_value");
        Assert(
            sqliteDoubleColumn is
            {
                ValueKind: TableColumnValueKind.DoublePrecisionFloatingPoint,
                IsEditable: true
            },
            "SQLite REAL 應使用 8-byte 浮點安全編輯器");
        Assert(
            TableCellValueConverter.Parse(
                sqliteDoubleColumn,
                new TableCellInput("approximate_value", TableCellInputMode.Value, "1.23456789012345")) is
            FloatingPointValue { Value: double, Text: "1.23456789012345" },
            "SQLite REAL 應保留可 round-trip 的 canonical double");
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            sqliteDoubleColumn,
            new TableCellInput("approximate_value", TableCellInputMode.Value, "1.23456789012345678")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            sqliteDoubleColumn,
            new TableCellInput("approximate_value", TableCellInputMode.Value, "1e-310")));
        Assert(
            TableCellValueConverter.Parse(
                sqliteNumericColumn,
                new TableCellInput("amount", TableCellInputMode.Value, "+001.2300e+2")) is SqliteNumericValue
            {
                Text: "123"
            },
            "SQLite NUMERIC 應把安全的指數輸入正規化為固定十進位文字");
        Assert(
            TableCellValueConverter.Parse(
                sqliteNumericColumn,
                new TableCellInput("amount", TableCellInputMode.Value, "9223372036854775807")) is SqliteNumericValue,
            "SQLite NUMERIC 應接受可由 INTEGER storage class 無損保存的 Int64 上界");
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            sqliteNumericColumn,
            new TableCellInput("amount", TableCellInputMode.Value, "12345678901234.56")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            sqliteNumericColumn,
            new TableCellInput("amount", TableCellInputMode.Value, "9223372036854775808")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            sqliteNumericColumn,
            new TableCellInput("amount", TableCellInputMode.Value, "1e309")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            sqliteNumericColumn,
            new TableCellInput("amount", TableCellInputMode.Value, "1e-400")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            sqliteNumericColumn,
            new TableCellInput("amount", TableCellInputMode.Value, "1,234.5")));

        await session.InsertTableRowAsync(
            profile.Database,
            numericTable,
            new[]
            {
                new TableCellInput("amount", TableCellInputMode.Value, "1234567890123.45"),
                new TableCellInput("note", TableCellInputMode.Value, "safe-real"),
                new TableCellInput("approximate_value", TableCellInputMode.Value, "1.23456789012345")
            });
        await session.InsertTableRowAsync(
            profile.Database,
            numericTable,
            new[]
            {
                new TableCellInput("amount", TableCellInputMode.Value, "9223372036854775807"),
                new TableCellInput("note", TableCellInputMode.Value, "safe-integer")
            });
        var numericRows = await session.LoadTableDataAsync(profile.Database, numericTable);
        Assert(
            Convert.ToString(numericRows.Rows[0].Values[1]) == "1234567890123.45" &&
            Convert.ToString(numericRows.Rows[1].Values[1]) == "9223372036854775807",
            "SQLite NUMERIC grid 應顯示實際儲存且可 round-trip 的 canonical 數值");
        Assert(
            TableCellValueConverter.Format(sqliteDoubleColumn, numericRows.Rows[0].Values[3]) ==
            "1.23456789012345",
            "SQLite REAL grid 應顯示實際儲存的 canonical double");
        await AssertThrowsAsync<InvalidOperationException>(() => session.InsertTableRowAsync(
            profile.Database,
            numericTable,
            new[]
            {
                new TableCellInput("amount", TableCellInputMode.Value, "1"),
                new TableCellInput("note", TableCellInputMode.Value, "rejected-double"),
                new TableCellInput(
                    "approximate_value",
                    TableCellInputMode.Value,
                    "1.23456789012345678")
            }));
        var numericStorage = await session.ExecuteAsync(
            profile.Database,
            "SELECT CAST(amount AS TEXT), typeof(amount) FROM numeric_sample ORDER BY id;");
        Assert(
            Convert.ToString(numericStorage.Rows[0][0]) == "1234567890123.45" &&
            Convert.ToString(numericStorage.Rows[0][1]) == "real" &&
            Convert.ToString(numericStorage.Rows[1][0]) == "9223372036854775807" &&
            Convert.ToString(numericStorage.Rows[1][1]) == "integer",
            "SQLite NUMERIC 寫入應依 affinity 使用 REAL／INTEGER 且保留 canonical 值");
        await AssertThrowsAsync<InvalidOperationException>(() => session.InsertTableRowAsync(
            profile.Database,
            numericTable,
            new[]
            {
                new TableCellInput("amount", TableCellInputMode.Value, "12345678901234.56"),
                new TableCellInput("note", TableCellInputMode.Value, "must-not-land")
            }));
        Assert(
            (await session.LoadTableDataAsync(profile.Database, numericTable)).Rows.Count == 2,
            "會被 SQLite REAL 無聲取整的 NUMERIC 新增不可落入資料庫");

        await session.UpdateTableRowAsync(
            profile.Database,
            numericTable,
            numericRows.Rows[0],
            new[]
            {
                new TableCellInput("amount", TableCellInputMode.Value, "1.23456789012345e-15")
            });
        var updatedNumericRows = await session.LoadTableDataAsync(profile.Database, numericTable);
        var storedScientificNumeric = Convert.ToString(updatedNumericRows.Rows[0].Values[1]);
        Assert(
            storedScientificNumeric == "1.23456789012345e-15",
            $"SQLite NUMERIC 應顯示實際儲存的科學記號值，實際為 {storedScientificNumeric}");
        Assert(
            TableCellValueConverter.MatchesOriginal(
                sqliteNumericColumn,
                new TableCellInput("amount", TableCellInputMode.Value, "0.00000000000000123456789012345"),
                updatedNumericRows.Rows[0].Values[1]),
            "SQLite NUMERIC 應能以固定或科學記號無損比對同一原值");
        var staleNumericRow = updatedNumericRows.Rows[0];
        await session.ExecuteAsync(
            profile.Database,
            $"UPDATE numeric_sample SET amount = 2.5 WHERE id = {Convert.ToInt64(staleNumericRow.Values[0])};");
        await AssertThrowsAsync<TableDataConflictException>(() => session.UpdateTableRowAsync(
            profile.Database,
            numericTable,
            staleNumericRow,
            new[] { new TableCellInput("note", TableCellInputMode.Value, "must-not-overwrite") }));
        Assert(
            Convert.ToString((await session.LoadTableDataAsync(profile.Database, numericTable)).Rows[0].Values[1]) == "2.5",
            "SQLite NUMERIC optimistic predicate 應攔截外部數值變更");

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
        var fixedBinaryColumn = binaryColumn with
        {
            Name = "fixed_payload",
            DataTypeName = "binary(3)",
            StorageDataTypeName = "binary(3)",
            RequiredBinaryLength = 3
        };
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            fixedBinaryColumn,
            new TableCellInput("fixed_payload", TableCellInputMode.Value, "0xCAFE")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            fixedBinaryColumn,
            new TableCellInput("fixed_payload", TableCellInputMode.Value, "0xCAFEBABE")));
        Assert(
            TableCellValueConverter.Parse(
                fixedBinaryColumn,
                new TableCellInput("fixed_payload", TableCellInputMode.Value, "0xCAFE00")) is byte[] exactBytes &&
            exactBytes.SequenceEqual(new byte[] { 0xCA, 0xFE, 0x00 }),
            "固定長度 binary 應只接受精確 byte 數");
        var nonRoundTrippableCharColumn = inserted.Columns.Single(column => column.Name == "name") with
        {
            DataTypeName = "char(6)",
            StorageDataTypeName = "char(6)",
            MaximumStringLengthInCharacters = 6,
            TrailingSpacesAreNotRoundTrippable = true
        };
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            nonRoundTrippableCharColumn,
            new TableCellInput("name", TableCellInputMode.Value, "AB ")));
        Assert(
            Convert.ToString(TableCellValueConverter.Parse(
                nonRoundTrippableCharColumn,
                new TableCellInput("name", TableCellInputMode.Value, "AB\t"))) == "AB\t" &&
            Convert.ToString(TableCellValueConverter.Parse(
                nonRoundTrippableCharColumn,
                new TableCellInput("name", TableCellInputMode.Value, "AB\u00A0"))) == "AB\u00A0" &&
            Convert.ToString(TableCellValueConverter.Parse(
                nonRoundTrippableCharColumn with { TrailingSpacesAreNotRoundTrippable = false },
                new TableCellInput("name", TableCellInputMode.Value, "AB "))) == "AB ",
            "固定長度 CHAR 只應拒絕 provider 無法 round-trip 的尾端 U+0020 空白");
        var boundedVaryingColumn = nonRoundTrippableCharColumn with
        {
            DataTypeName = "character varying(6)",
            StorageDataTypeName = "character varying(6)",
            TrailingSpacesAreNotRoundTrippable = false
        };
        Assert(
            Convert.ToString(TableCellValueConverter.Parse(
                boundedVaryingColumn,
                new TableCellInput("name", TableCellInputMode.Value, "AB  "))) == "AB  " &&
            Convert.ToString(TableCellValueConverter.Parse(
                boundedVaryingColumn,
                new TableCellInput("name", TableCellInputMode.Value, "🐧台灣ABC"))) == "🐧台灣ABC",
            "PostgreSQL bounded varchar 應保留欄寬內尾端空白，並以 Unicode scalar 計算字元數");
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            boundedVaryingColumn,
            new TableCellInput("name", TableCellInputMode.Value, "ABCDEF ")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            boundedVaryingColumn,
            new TableCellInput("name", TableCellInputMode.Value, "🐧台灣ABCD")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            boundedVaryingColumn,
            new TableCellInput("name", TableCellInputMode.Value, new string('\ud800', 1))));
        var exactEnumColumn = inserted.Columns.Single(column => column.Name == "name") with
        {
            DataTypeName = "enum('draft','Published','café','2','','comma,value','quote''value','back\\\\slash')",
            StorageDataTypeName = "enum('draft','Published','café','2','','comma,value','quote''value','back\\\\slash')",
            AllowedStringValues = new[]
            {
                "draft", "Published", "café", "2", string.Empty, "comma,value", "quote'value", "back\\slash"
            }
        };
        foreach (var allowedValue in exactEnumColumn.AllowedStringValues)
        {
            Assert(
                Convert.ToString(TableCellValueConverter.Parse(
                    exactEnumColumn,
                    new TableCellInput("name", TableCellInputMode.Value, allowedValue))) == allowedValue,
                $"ENUM 宣告成員應可精確保存：{allowedValue}");
        }
        foreach (var lossyValue in new[] { "DRAFT", "draft ", "cafe", "3" })
        {
            AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
                exactEnumColumn,
                new TableCellInput("name", TableCellInputMode.Value, lossyValue)));
        }
        var exactSetColumn = exactEnumColumn with
        {
            DataTypeName = "set('alpha','Beta','café','2','quote''value','back\\\\slash')",
            StorageDataTypeName = "set('alpha','Beta','café','2','quote''value','back\\\\slash')",
            AllowedStringValues = null,
            StringSetMembers = new[] { "alpha", "Beta", "café", "2", "quote'value", "back\\slash" }
        };
        Assert(
            Convert.ToString(TableCellValueConverter.Parse(
                exactSetColumn,
                new TableCellInput("name", TableCellInputMode.Value, "Beta,alpha"))) == "alpha,Beta" &&
            Convert.ToString(TableCellValueConverter.Parse(
                exactSetColumn,
                new TableCellInput("name", TableCellInputMode.Value, "alpha,alpha"))) == "alpha" &&
            Convert.ToString(TableCellValueConverter.Parse(
                exactSetColumn,
                new TableCellInput("name", TableCellInputMode.Value, string.Empty))) == string.Empty,
            "SET 應保留集合語意，並依宣告順序輸出 canonical 值");
        foreach (var lossyValue in new[] { "ALPHA", "alpha ", "cafe", "5" })
        {
            AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
                exactSetColumn,
                new TableCellInput("name", TableCellInputMode.Value, lossyValue)));
        }
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
        var cidrColumn = inetColumn with
        {
            Name = "subnet",
            DataTypeName = "cidr",
            StorageDataTypeName = "cidr"
        };
        var macColumn = inetColumn with
        {
            Name = "mac",
            DataTypeName = "macaddr",
            StorageDataTypeName = "macaddr"
        };
        var mac8Column = inetColumn with
        {
            Name = "mac8",
            DataTypeName = "macaddr8",
            StorageDataTypeName = "macaddr8"
        };
        var mariaDbInet6Column = inetColumn with
        {
            Name = "native_address",
            DataTypeName = "inet6",
            StorageDataTypeName = "inet6"
        };
        var mariaDbInet4Column = inetColumn with
        {
            Name = "native_ipv4",
            DataTypeName = "inet4",
            StorageDataTypeName = "inet4"
        };
        var mariaDbUuidColumn = new TableColumnInfo(
            0,
            "native_uuid",
            "uuid",
            true,
            false,
            false,
            false,
            TableColumnValueKind.Guid);
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
        Assert(
            Equals(
                TableCellValueConverter.Parse(
                    mariaDbInet4Column,
                    new TableCellInput("native_ipv4", TableCellInputMode.Value, " 192.0.2.10 ")),
                "192.0.2.10"),
            "MariaDB INET4 應正規化 IPv4 位址");
        Assert(
            Equals(
                TableCellValueConverter.Parse(
                    mariaDbInet6Column,
                    new TableCellInput("native_address", TableCellInputMode.Value, "192.0.2.10")),
                "::ffff:192.0.2.10"),
            "MariaDB INET6 應把 IPv4 正規化為 mapped IPv6");
        Assert(
            TableCellValueConverter.Format(TableCellValueConverter.Parse(
                mariaDbUuidColumn,
                new TableCellInput(
                    "native_uuid",
                    TableCellInputMode.Value,
                    "550E8400E29B41D4A716446655440000"))) == "550e8400-e29b-41d4-a716-446655440000",
            "MariaDB UUID 應正規化為小寫 D 格式");
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            inetColumn,
            new TableCellInput("address", TableCellInputMode.Value, "192.0.2.1/33")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            inetColumn,
            new TableCellInput("address", TableCellInputMode.Value, "fe80::1%1/64")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            mariaDbInet4Column,
            new TableCellInput("native_ipv4", TableCellInputMode.Value, "2001:db8::1")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            mariaDbInet4Column,
            new TableCellInput("native_ipv4", TableCellInputMode.Value, "192.0.2.10/24")));
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
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            mariaDbInet6Column,
            new TableCellInput("native_address", TableCellInputMode.Value, "2001:db8::1/64")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            mariaDbInet6Column,
            new TableCellInput("native_address", TableCellInputMode.Value, "fe80::1%0")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            mariaDbUuidColumn,
            new TableCellInput("native_uuid", TableCellInputMode.Value, "not-a-uuid")));
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
        var bit64Column = bit8Column with
        {
            Name = "flags64",
            DataTypeName = "bit(64)",
            StorageDataTypeName = "bit(64)"
        };
        Assert(
            Equals(
                TableCellValueConverter.Parse(
                    bit64Column,
                    new TableCellInput("flags64", TableCellInputMode.Value, ulong.MaxValue.ToString(CultureInfo.InvariantCulture))),
                ulong.MaxValue),
            "BIT(64) 應接受 UInt64 最大值");
        var fixedBitsColumn = new TableColumnInfo(0, "bits", "bit(8)", true, false, false, false, TableColumnValueKind.BitString);
        var varyingBitsColumn = fixedBitsColumn with
        {
            Name = "varbits",
            DataTypeName = "bit varying(16)",
            StorageDataTypeName = "bit varying(16)"
        };
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
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            timeZoneColumn,
            new TableCellInput("alarm", TableCellInputMode.Value, "12:34:56.1234567+08:00")));
        var timestampColumn = new TableColumnInfo(
            0,
            "local_timestamp",
            "timestamp(3) without time zone",
            true,
            false,
            false,
            false,
            TableColumnValueKind.PostgreSqlTemporal);
        var timestampWithTimeZoneColumn = timestampColumn with
        {
            Name = "utc_timestamp",
            DataTypeName = "timestamp(4) with time zone",
            StorageDataTypeName = "timestamp(4) with time zone"
        };
        var timeColumn = timestampColumn with
        {
            Name = "local_clock",
            DataTypeName = "time(2) without time zone",
            StorageDataTypeName = "time(2) without time zone"
        };
        Assert(
            Equals(
                TableCellValueConverter.Parse(
                    timestampColumn,
                    new TableCellInput("local_timestamp", TableCellInputMode.Value, "2026-08-30T12:34:56.123")),
                new DateTime(2026, 8, 30, 12, 34, 56).AddMilliseconds(123)),
            "PostgreSQL timestamp(3) 應無損保存毫秒");
        Assert(
            TableCellValueConverter.Parse(
                timestampWithTimeZoneColumn,
                new TableCellInput("utc_timestamp", TableCellInputMode.Value, "2026-08-30T12:34:56.1234+08:00")) is
            DateTimeOffset timestampWithTimeZone &&
            timestampWithTimeZone.Offset == TimeSpan.Zero &&
            timestampWithTimeZone.Hour == 4 &&
            timestampWithTimeZone.Ticks % TimeSpan.TicksPerSecond == 1_234_000,
            "PostgreSQL timestamptz(4) 應轉成 UTC 並保留四位小數秒");
        Assert(
            Equals(
                TableCellValueConverter.Parse(
                    timeColumn,
                    new TableCellInput("local_clock", TableCellInputMode.Value, "24:00:00")),
                TimeSpan.FromDays(1)),
            "PostgreSQL time 應支援 24:00:00 上界");
        Assert(TableCellValueConverter.GetPostgreSqlTemporalScale(timestampColumn) == 3, "timestamp scale 應為 3");
        Assert(
            TableCellValueConverter.GetPostgreSqlTemporalScale(timestampWithTimeZoneColumn) == 4,
            "timestamptz scale 應為 4");
        Assert(TableCellValueConverter.GetPostgreSqlTemporalScale(timeZoneColumn) == 6, "timetz 預設 scale 應為 6");
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            timestampColumn,
            new TableCellInput("local_timestamp", TableCellInputMode.Value, "2026-08-30T12:34:56.1234")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            timestampColumn,
            new TableCellInput("local_timestamp", TableCellInputMode.Value, "2026-08-30T12:34:56.123+08:00")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            timestampWithTimeZoneColumn,
            new TableCellInput("utc_timestamp", TableCellInputMode.Value, "2026-08-30T12:34:56.1234")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            timestampWithTimeZoneColumn,
            new TableCellInput("utc_timestamp", TableCellInputMode.Value, "2026-08-30T12:34:56.12345Z")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            timeColumn,
            new TableCellInput("local_clock", TableCellInputMode.Value, "23:59:59.123")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            timeColumn,
            new TableCellInput("local_clock", TableCellInputMode.Value, "24:00:00.01")));
        var postgreSqlDateColumn = new TableColumnInfo(
            0,
            "event_date",
            "date",
            true,
            false,
            false,
            false,
            TableColumnValueKind.PostgreSqlDate);
        foreach (var expected in new[]
                 {
                     "0001-01-01",
                     "9999-12-31",
                     "4713-01-01 BC",
                     "0001-02-29 BC",
                     "5874897-12-31",
                     "infinity",
                     "-infinity"
                 })
        {
            Assert(
                Equals(
                    TableCellValueConverter.Parse(
                        postgreSqlDateColumn,
                        new TableCellInput("event_date", TableCellInputMode.Value, expected)),
                    expected),
                $"PostgreSQL date 應無損保留 {expected}");
        }
        foreach (var invalid in new[]
                 {
                     "2026-08-30T12:34:56.0000000",
                     "0000-01-01",
                     "4714-01-01 BC",
                     "5874898-01-01",
                     "0004-02-29 BC",
                     "2026-02-29"
                 })
        {
            AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
                postgreSqlDateColumn,
                new TableCellInput("event_date", TableCellInputMode.Value, invalid)));
        }
        var postgreSqlMoneyColumn = new TableColumnInfo(
            0,
            "account_balance",
            "money",
            true,
            false,
            false,
            false,
            TableColumnValueKind.PostgreSqlMoney)
        {
            StorageDataTypeName = "money",
            MonetaryScale = 2
        };
        foreach (var expected in new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["1.2"] = "1.20",
            ["+0001.20"] = "1.20",
            ["-0"] = "0.00",
            ["92233720368547758.07"] = "92233720368547758.07",
            ["-92233720368547758.08"] = "-92233720368547758.08"
        })
        {
            Assert(
                TableCellValueConverter.Parse(
                    postgreSqlMoneyColumn,
                    new TableCellInput("account_balance", TableCellInputMode.Value, expected.Key)) is
                    PostgreSqlMoneyValue money &&
                money.Text == expected.Value &&
                TableCellValueConverter.Format(money) == expected.Value,
                $"PostgreSQL money 應正規化並無損保留 {expected.Key}");
        }
        Assert(
            TableCellValueConverter.MatchesOriginal(
                postgreSqlMoneyColumn,
                new TableCellInput("account_balance", TableCellInputMode.Value, "1.2"),
                "1.20"),
            "PostgreSQL money 應把等值的小數位輸入視為未修改");
        var threeDecimalMoneyColumn = postgreSqlMoneyColumn with { MonetaryScale = 3 };
        Assert(
            TableCellValueConverter.Parse(
                threeDecimalMoneyColumn,
                new TableCellInput("account_balance", TableCellInputMode.Value, "9223372036854775.807")) is
                PostgreSqlMoneyValue threeDecimalMoney &&
            threeDecimalMoney.Text == "9223372036854775.807",
            "PostgreSQL money 應依 lc_monetary 小數位調整 signed 64-bit 正值上界");
        var zeroDecimalMoneyColumn = postgreSqlMoneyColumn with { MonetaryScale = 0 };
        Assert(
            TableCellValueConverter.Parse(
                zeroDecimalMoneyColumn,
                new TableCellInput("account_balance", TableCellInputMode.Value, "-9223372036854775808")) is
                PostgreSqlMoneyValue zeroDecimalMoney &&
            zeroDecimalMoney.Text == "-9223372036854775808",
            "PostgreSQL money 應支援 lc_monetary 為零位小數時的 signed 64-bit 負值下界");
        foreach (var invalid in new[]
                 {
                     "1.234",
                     "92233720368547758.08",
                     "-92233720368547758.09",
                     "$1.23",
                     "1,234.56",
                     "1e2"
                 })
        {
            AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
                postgreSqlMoneyColumn,
                new TableCellInput("account_balance", TableCellInputMode.Value, invalid)));
        }
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.GetPostgreSqlMoneyScale(
            postgreSqlMoneyColumn with { MonetaryScale = null }));
        var mySqlDateColumn = new TableColumnInfo(
            0,
            "event_date",
            "date",
            true,
            false,
            false,
            false,
            TableColumnValueKind.MySqlTemporal);
        var mySqlDateTimeColumn = mySqlDateColumn with
        {
            Name = "recorded_at",
            DataTypeName = "datetime(3)",
            StorageDataTypeName = "datetime(3)"
        };
        var mySqlTimestampColumn = mySqlDateColumn with
        {
            Name = "changed_at",
            DataTypeName = "timestamp(6)",
            StorageDataTypeName = "timestamp(6)"
        };
        Assert(
            Equals(
                TableCellValueConverter.Parse(
                    mySqlDateColumn,
                    new TableCellInput("event_date", TableCellInputMode.Value, "1000-01-01")),
                new DateTime(1000, 1, 1)),
            "MySQL／MariaDB DATE 應接受 1000 年下界");
        Assert(
            Equals(
                TableCellValueConverter.Parse(
                    mySqlDateTimeColumn,
                    new TableCellInput("recorded_at", TableCellInputMode.Value, "9999-12-31T23:59:59.123")),
                new DateTime(9999, 12, 31, 23, 59, 59).AddMilliseconds(123)),
            "MySQL／MariaDB DATETIME(3) 應無損保存毫秒");
        Assert(
            Equals(
                TableCellValueConverter.Parse(
                    mySqlTimestampColumn,
                    new TableCellInput("changed_at", TableCellInputMode.Value, "2030-01-02 03:04:05.123456")),
                new DateTime(2030, 1, 2, 3, 4, 5).AddTicks(1_234_560)),
            "MySQL／MariaDB TIMESTAMP(6) 應無損保存微秒");
        Assert(TableCellValueConverter.GetMySqlTemporalScale(mySqlDateColumn) == 0, "DATE scale 應為 0");
        Assert(TableCellValueConverter.GetMySqlTemporalScale(mySqlDateTimeColumn) == 3, "DATETIME scale 應為 3");
        Assert(TableCellValueConverter.GetMySqlTemporalScale(mySqlTimestampColumn) == 6, "TIMESTAMP scale 應為 6");
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            mySqlDateColumn,
            new TableCellInput("event_date", TableCellInputMode.Value, "0999-12-31")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            mySqlDateColumn,
            new TableCellInput("event_date", TableCellInputMode.Value, "2026-08-30T12:00:00")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            mySqlDateTimeColumn,
            new TableCellInput("recorded_at", TableCellInputMode.Value, "2026-08-30T12:34:56.1234")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            mySqlDateTimeColumn,
            new TableCellInput("recorded_at", TableCellInputMode.Value, "2026-08-30T12:34:56+08:00")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            mySqlTimestampColumn,
            new TableCellInput("changed_at", TableCellInputMode.Value, "2026-08-30T12:34:56.1234567")));
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
        var boundedSignedIntegerColumn = new TableColumnInfo(
            0,
            "tiny_value",
            "tinyint",
            true,
            false,
            false,
            false,
            TableColumnValueKind.Integer)
        {
            StorageDataTypeName = "tinyint",
            IntegerMinimum = sbyte.MinValue,
            IntegerMaximum = (ulong)sbyte.MaxValue
        };
        Assert(
            Convert.ToInt64(TableCellValueConverter.Parse(
                boundedSignedIntegerColumn,
                new TableCellInput("tiny_value", TableCellInputMode.Value, "-128"))) == -128 &&
            Convert.ToInt64(TableCellValueConverter.Parse(
                boundedSignedIntegerColumn,
                new TableCellInput("tiny_value", TableCellInputMode.Value, "127"))) == 127,
            "Signed TINYINT 應接受完整正負邊界");
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            boundedSignedIntegerColumn,
            new TableCellInput("tiny_value", TableCellInputMode.Value, "-129")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            boundedSignedIntegerColumn,
            new TableCellInput("tiny_value", TableCellInputMode.Value, "128")));
        var boundedUnsignedIntegerColumn = boundedSignedIntegerColumn with
        {
            Name = "unsigned_big_value",
            DataTypeName = "bigint unsigned",
            StorageDataTypeName = "bigint unsigned",
            ValueKind = TableColumnValueKind.UnsignedInteger,
            IntegerMinimum = 0,
            IntegerMaximum = ulong.MaxValue
        };
        Assert(
            Convert.ToUInt64(TableCellValueConverter.Parse(
                boundedUnsignedIntegerColumn,
                new TableCellInput(
                    "unsigned_big_value",
                    TableCellInputMode.Value,
                    "18446744073709551615"))) == ulong.MaxValue,
            "BIGINT UNSIGNED 應接受 UInt64 上界");
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            boundedUnsignedIntegerColumn,
            new TableCellInput("unsigned_big_value", TableCellInputMode.Value, "-1")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            boundedUnsignedIntegerColumn,
            new TableCellInput(
                "unsigned_big_value",
                TableCellInputMode.Value,
                "18446744073709551616")));
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
        var singlePrecisionColumn = new TableColumnInfo(
            0,
            "single_value",
            "real",
            true,
            false,
            false,
            false,
            TableColumnValueKind.SinglePrecisionFloatingPoint);
        var parsedSingle = TableCellValueConverter.Parse(
            singlePrecisionColumn,
            new TableCellInput("single_value", TableCellInputMode.Value, "1.2345678"));
        Assert(
            parsedSingle is FloatingPointValue { Value: float, Text: "1.2345678" },
            "4-byte 浮點應保留可 round-trip 的 canonical single");
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            singlePrecisionColumn,
            new TableCellInput("single_value", TableCellInputMode.Value, "1.23456789")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            singlePrecisionColumn,
            new TableCellInput("single_value", TableCellInputMode.Value, "16777217")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            singlePrecisionColumn,
            new TableCellInput("single_value", TableCellInputMode.Value, "1e-40")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            singlePrecisionColumn,
            new TableCellInput("single_value", TableCellInputMode.Value, "NaN")));
        var doublePrecisionColumn = singlePrecisionColumn with
        {
            Name = "double_value",
            DataTypeName = "double precision",
            StorageDataTypeName = "double precision",
            ValueKind = TableColumnValueKind.DoublePrecisionFloatingPoint
        };
        var parsedDouble = TableCellValueConverter.Parse(
            doublePrecisionColumn,
            new TableCellInput("double_value", TableCellInputMode.Value, "1.23456789012345"));
        Assert(
            parsedDouble is FloatingPointValue { Value: double, Text: "1.23456789012345" },
            "8-byte 浮點應保留可 round-trip 的 canonical double");
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            doublePrecisionColumn,
            new TableCellInput("double_value", TableCellInputMode.Value, "1.23456789012345678")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            doublePrecisionColumn,
            new TableCellInput("double_value", TableCellInputMode.Value, "9007199254740993")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            doublePrecisionColumn,
            new TableCellInput("double_value", TableCellInputMode.Value, "1e-310")));
        var scaledUnsignedFloatColumn = singlePrecisionColumn with
        {
            DataTypeName = "float(7,4) unsigned",
            StorageDataTypeName = "float(7,4) unsigned"
        };
        Assert(
            TableCellValueConverter.Parse(
                scaledUnsignedFloatColumn,
                new TableCellInput("single_value", TableCellInputMode.Value, "12.3456")) is
            FloatingPointValue,
            "MySQL／MariaDB FLOAT(M,D) 應接受不需取整的值");
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            scaledUnsignedFloatColumn,
            new TableCellInput("single_value", TableCellInputMode.Value, "12.34567")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            scaledUnsignedFloatColumn,
            new TableCellInput("single_value", TableCellInputMode.Value, "-1")));
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
        var constrainedIntervals = new[]
        {
            (Type: "interval year", Valid: "months=24;days=0;microseconds=0", Invalid: "months=25;days=0;microseconds=0"),
            (Type: "interval year to month", Valid: "months=25;days=0;microseconds=0", Invalid: "months=25;days=1;microseconds=0"),
            (Type: "interval day", Valid: "months=25;days=1;microseconds=0", Invalid: "months=25;days=1;microseconds=3600000000"),
            (Type: "interval day to hour", Valid: "months=25;days=1;microseconds=90000000000", Invalid: "months=25;days=1;microseconds=90060000000"),
            (Type: "interval hour to minute", Valid: "months=25;days=1;microseconds=90060000000", Invalid: "months=25;days=1;microseconds=90060000001"),
            (Type: "interval day to second(3)", Valid: "months=25;days=1;microseconds=90060123000", Invalid: "months=25;days=1;microseconds=90060123456"),
            (Type: "interval(0)", Valid: "months=25;days=1;microseconds=-90061000000", Invalid: "months=25;days=1;microseconds=-90061100000")
        };
        foreach (var constrained in constrainedIntervals)
        {
            var constrainedColumn = intervalColumn with
            {
                DataTypeName = constrained.Type,
                StorageDataTypeName = constrained.Type
            };
            Assert(
                TableCellValueConverter.Parse(
                    constrainedColumn,
                    new TableCellInput("duration", TableCellInputMode.Value, constrained.Valid)) is IntervalComponents,
                $"PostgreSQL {constrained.Type} 應接受可無損保存的 interval 分量");
            AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
                constrainedColumn,
                new TableCellInput("duration", TableCellInputMode.Value, constrained.Invalid)));
        }
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            intervalColumn with { StorageDataTypeName = "interval day to fortnight" },
            new TableCellInput("duration", TableCellInputMode.Value, "months=0;days=1;microseconds=0")));
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
        var sqlDateColumn = new TableColumnInfo(
            0, "event_date", "date", true, false, false, false, TableColumnValueKind.SqlServerTemporal);
        var sqlDateTimeColumn = sqlDateColumn with
        {
            Name = "legacy_time",
            DataTypeName = "datetime",
            StorageDataTypeName = "datetime"
        };
        var sqlSmallDateTimeColumn = sqlDateColumn with
        {
            Name = "minute_time",
            DataTypeName = "smalldatetime",
            StorageDataTypeName = "smalldatetime"
        };
        var sqlDateTime2Column = sqlDateColumn with
        {
            Name = "precise_time",
            DataTypeName = "datetime2(3)",
            StorageDataTypeName = "datetime2(3)"
        };
        var sqlDateTimeOffsetColumn = sqlDateColumn with
        {
            Name = "offset_time",
            DataTypeName = "datetimeoffset(3)",
            StorageDataTypeName = "datetimeoffset(3)"
        };
        var sqlTimeColumn = sqlDateColumn with
        {
            Name = "clock_time",
            DataTypeName = "time(4)",
            StorageDataTypeName = "time(4)"
        };
        Assert(
            TableCellValueConverter.Parse(
                sqlDateColumn,
                new TableCellInput("event_date", TableCellInputMode.Value, "0001-01-01")) is DateTime,
            "SQL Server date 應支援完整 0001–9999 範圍");
        Assert(
            TableCellValueConverter.Parse(
                sqlDateTimeColumn,
                new TableCellInput("legacy_time", TableCellInputMode.Value, "1753-01-01T00:00:00.000")) is DateTime,
            "SQL Server datetime 應接受可無損保存的下界");
        Assert(
            TableCellValueConverter.Parse(
                sqlSmallDateTimeColumn,
                new TableCellInput("minute_time", TableCellInputMode.Value, "2079-06-06T23:59:00")) is DateTime,
            "SQL Server smalldatetime 應接受整分鐘上界");
        Assert(
            TableCellValueConverter.Parse(
                sqlDateTime2Column,
                new TableCellInput("precise_time", TableCellInputMode.Value, "0001-01-01T00:00:00.123")) is DateTime,
            "SQL Server datetime2(3) 應保留毫秒");
        Assert(
            TableCellValueConverter.Parse(
                sqlDateTimeOffsetColumn,
                new TableCellInput("offset_time", TableCellInputMode.Value, "2026-08-30T12:34:56.789+14:00")) is DateTimeOffset,
            "SQL Server datetimeoffset(3) 應保留明確 offset");
        Assert(
            TableCellValueConverter.Parse(
                sqlTimeColumn,
                new TableCellInput("clock_time", TableCellInputMode.Value, "23:59:59.1234")) is TimeSpan,
            "SQL Server time(4) 應保留四位小數秒");
        Assert(TableCellValueConverter.GetSqlServerTemporalScale(sqlDateTime2Column) == 3, "datetime2 scale metadata 應為 3");
        Assert(TableCellValueConverter.GetSqlServerTemporalScale(sqlTimeColumn) == 4, "time scale metadata 應為 4");
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            sqlDateColumn,
            new TableCellInput("event_date", TableCellInputMode.Value, "2026-08-30T12:00:00")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            sqlDateTimeColumn,
            new TableCellInput("legacy_time", TableCellInputMode.Value, "1752-12-31T23:59:59")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            sqlDateTimeColumn,
            new TableCellInput("legacy_time", TableCellInputMode.Value, "2026-08-30T12:34:56.002")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            sqlSmallDateTimeColumn,
            new TableCellInput("minute_time", TableCellInputMode.Value, "2026-08-30T12:34:30")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            sqlDateTime2Column,
            new TableCellInput("precise_time", TableCellInputMode.Value, "2026-08-30T12:34:56.1234")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            sqlDateTimeOffsetColumn,
            new TableCellInput("offset_time", TableCellInputMode.Value, "2026-08-30T12:34:56.789")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            sqlDateTimeOffsetColumn,
            new TableCellInput("offset_time", TableCellInputMode.Value, "2026-08-30T12:34:56.7891+08:00")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            sqlTimeColumn,
            new TableCellInput("clock_time", TableCellInputMode.Value, "23:59:59.12345")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            sqlTimeColumn,
            new TableCellInput("clock_time", TableCellInputMode.Value, "1.00:00:00")));
        var sqlMoneyColumn = new TableColumnInfo(
            0,
            "account_balance",
            "money",
            true,
            false,
            false,
            false,
            TableColumnValueKind.SqlServerMoney)
        {
            StorageDataTypeName = "money"
        };
        var sqlSmallMoneyColumn = sqlMoneyColumn with
        {
            Name = "petty_cash",
            DataTypeName = "smallmoney",
            StorageDataTypeName = "smallmoney"
        };
        foreach (var expected in new[]
                 {
                     (Column: sqlMoneyColumn, Input: "1.2", Canonical: "1.2000"),
                     (Column: sqlMoneyColumn, Input: "+0001.2345", Canonical: "1.2345"),
                     (Column: sqlMoneyColumn, Input: "922337203685477.5807", Canonical: "922337203685477.5807"),
                     (Column: sqlMoneyColumn, Input: "-922337203685477.5808", Canonical: "-922337203685477.5808"),
                     (Column: sqlSmallMoneyColumn, Input: "214748.3647", Canonical: "214748.3647"),
                     (Column: sqlSmallMoneyColumn, Input: "-214748.3648", Canonical: "-214748.3648")
                 })
        {
            Assert(
                TableCellValueConverter.Parse(
                    expected.Column,
                    new TableCellInput(expected.Column.Name, TableCellInputMode.Value, expected.Input)) is
                    SqlServerMoneyValue money &&
                money.Text == expected.Canonical &&
                TableCellValueConverter.Format(money) == expected.Canonical,
                $"SQL Server {expected.Column.StorageDataTypeName} 應正規化並無損保留 {expected.Input}");
        }
        Assert(
            TableCellValueConverter.MatchesOriginal(
                sqlMoneyColumn,
                new TableCellInput("account_balance", TableCellInputMode.Value, "1.2"),
                "1.2000"),
            "SQL Server money 應把等值的小數位輸入視為未修改");
        foreach (var invalid in new[]
                 {
                     (Column: sqlMoneyColumn, Value: "1.23455"),
                     (Column: sqlMoneyColumn, Value: "922337203685477.5808"),
                     (Column: sqlMoneyColumn, Value: "-922337203685477.5809"),
                     (Column: sqlSmallMoneyColumn, Value: "214748.3648"),
                     (Column: sqlSmallMoneyColumn, Value: "-214748.3649"),
                     (Column: sqlSmallMoneyColumn, Value: "$1.23"),
                     (Column: sqlSmallMoneyColumn, Value: "1,234.56"),
                     (Column: sqlSmallMoneyColumn, Value: "1e2")
                 })
        {
            AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
                invalid.Column,
                new TableCellInput(invalid.Column.Name, TableCellInputMode.Value, invalid.Value)));
        }
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
        var sqlVariantColumn = new TableColumnInfo(
            0,
            "variant_value",
            "sql_variant",
            true,
            false,
            false,
            false,
            TableColumnValueKind.SqlServerVariant);
        Assert(
            TableCellValueConverter.Parse(
                sqlVariantColumn,
                new TableCellInput("variant_value", TableCellInputMode.Value, "int:42")) is
                SqlServerVariantValue
            {
                BaseTypeName: "int",
                Value: int intValue,
                CanonicalText: "int:42"
            } && intValue == 42,
            "SQL Server sql_variant 應保留 int 內層型別");
        Assert(
            TableCellValueConverter.Parse(
                sqlVariantColumn,
                new TableCellInput("variant_value", TableCellInputMode.Value, "decimal(18,6):123.450000")) is
                SqlServerVariantValue
            {
                BaseTypeName: "decimal",
                Precision: 18,
                Scale: 6,
                Value: System.Data.SqlTypes.SqlDecimal,
                CanonicalText: "decimal(18,6):123.450000"
            },
            "SQL Server sql_variant 應無損保留 decimal precision／scale");
        const string collatedVariant =
            "nvarchar(30)@Latin1_General_100_BIN2|1033|0:  文字:含冒號與尾端空白  ";
        Assert(
            TableCellValueConverter.Parse(
                sqlVariantColumn,
                new TableCellInput("variant_value", TableCellInputMode.Value, collatedVariant)) is
                SqlServerVariantValue
            {
                BaseTypeName: "nvarchar",
                Size: 30,
                LocaleId: 1033,
                ComparisonStyle: 0,
                CollationName: "Latin1_General_100_BIN2",
                Value: "  文字:含冒號與尾端空白  ",
                CanonicalText: collatedVariant
            },
            "SQL Server sql_variant 字串應保留長度、collation、冒號與外圍空白");
        Assert(
            TableCellValueConverter.Parse(
                sqlVariantColumn,
                new TableCellInput("variant_value", TableCellInputMode.Value, "varbinary(4):0x00ff10")) is
                SqlServerVariantValue
            {
                BaseTypeName: "varbinary",
                Size: 4,
                Value: byte[] { Length: 3 },
                CanonicalText: "varbinary(4):0x00FF10"
            },
            "SQL Server sql_variant binary 應正規化十六進位並保留長度");
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            sqlVariantColumn,
            new TableCellInput("variant_value", TableCellInputMode.Value, "numeric(5,2):1234.56")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            sqlVariantColumn,
            new TableCellInput("variant_value", TableCellInputMode.Value, "nvarchar(2):三個字")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            sqlVariantColumn,
            new TableCellInput("variant_value", TableCellInputMode.Value, "xml:<item />")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            sqlVariantColumn,
            new TableCellInput(
                "variant_value",
                TableCellInputMode.Value,
                "datetime2(3):2026-08-30T12:34:56.7891")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            sqlVariantColumn,
            new TableCellInput(
                "variant_value",
                TableCellInputMode.Value,
                "datetimeoffset(3):2026-08-30T12:34:56.789")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            sqlVariantColumn,
            new TableCellInput(
                "variant_value",
                TableCellInputMode.Value,
                "smalldatetime:2026-08-30T12:34:01")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            sqlVariantColumn,
            new TableCellInput("variant_value", TableCellInputMode.Value, "money:1.23456")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            sqlVariantColumn,
            new TableCellInput("variant_value", TableCellInputMode.Value, "int:1\0")));
        var oversizedVariant =
            "nvarchar(4000):" +
            new string('x', TableCellValueConverter.MaximumEditableStructuredTextCharacters);
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            sqlVariantColumn,
            new TableCellInput("variant_value", TableCellInputMode.Value, oversizedVariant)));
        Assert(
            TableCellValueConverter.IsStructuredTextTooLargeToEdit(sqlVariantColumn, oversizedVariant),
            "既有 SQL Server sql_variant canonical 文字超過 1 MiB 時應維持唯讀");
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

static Task MySqlLiveRoundTripAsync() =>
    MySqlFamilyLiveRoundTripAsync("MYSQLPUNK_MYSQL", isMariaDb: false);

static Task MariaDbLiveRoundTripAsync() =>
    MySqlFamilyLiveRoundTripAsync("MYSQLPUNK_MARIADB", isMariaDb: true);

static async Task MySqlFamilyLiveRoundTripAsync(string environmentPrefix, bool isMariaDb)
{
    var database = (isMariaDb ? "mysqlpunk_maria_" : "mysqlpunk_cross_") +
                   Guid.NewGuid().ToString("N")[..10];
    var profile = new ConnectionProfile
    {
        Name = isMariaDb ? "MariaDB live" : "MySQL live",
        Provider = DatabaseProviderKind.MySql,
        Host = ReadRequiredEnvironment($"{environmentPrefix}_HOST"),
        Port = ReadRequiredIntEnvironment($"{environmentPrefix}_PORT"),
        Username = Environment.GetEnvironmentVariable($"{environmentPrefix}_USER") ?? "root",
        Password = ReadRequiredEnvironment($"{environmentPrefix}_PASSWORD"),
        TimeoutSeconds = 20
    };
    var session = DatabaseProviderFactory.Create(profile);
    await session.TestConnectionAsync();

    try
    {
        await session.ExecuteAsync(string.Empty, $"CREATE DATABASE `{database}` CHARACTER SET utf8mb4;");
        var nativeColumns = isMariaDb
            ? ", native_uuid UUID NULL, native_address INET6 NULL, native_ipv4 INET4 NULL"
            : string.Empty;
        await session.ExecuteAsync(database, $"CREATE TABLE sample (id BIGINT UNSIGNED PRIMARY KEY AUTO_INCREMENT, name VARCHAR(40) NOT NULL, quantity INT NULL, note VARCHAR(80) NULL, payload BLOB NULL, fixed_payload BINARY(3) NULL, metadata JSON NULL, flags8 BIT(8) NULL, flags64 BIT(64) NULL, status ENUM('draft','published','archived','café','2','','comma,value','quote''value','back\\\\slash') COLLATE utf8mb4_unicode_ci NULL, labels SET('alpha','Beta','café','2','quote''value','back\\\\slash') COLLATE utf8mb4_unicode_ci NULL, ambiguous_labels SET('') NULL, event_date DATE NULL, recorded_at DATETIME(3) NULL, precise_at DATETIME(6) NULL, changed_at TIMESTAMP(2) NULL, duration TIME(6) NULL, release_year YEAR NULL, high_precision DECIMAL(65,30) NULL, tiny_value TINYINT NULL, unsigned_tiny_value TINYINT UNSIGNED NULL, small_value SMALLINT NULL, unsigned_small_value SMALLINT UNSIGNED NULL, zerofill_small_value SMALLINT ZEROFILL NULL, medium_value MEDIUMINT NULL, unsigned_medium_value MEDIUMINT UNSIGNED NULL, integer_value INT NULL, unsigned_integer_value INT UNSIGNED NULL, big_value BIGINT NULL, unsigned_big_value BIGINT UNSIGNED NULL, single_value FLOAT NULL, compact_float FLOAT(10) NULL, double_value DOUBLE NULL, wide_float FLOAT(53) NULL, scaled_value FLOAT(7,4) UNSIGNED NULL, shape GEOMETRY NULL, location POINT NULL, route LINESTRING NULL, area POLYGON NULL, stops MULTIPOINT NULL, paths MULTILINESTRING NULL, regions MULTIPOLYGON NULL, shapes GEOMETRYCOLLECTION NULL, fixed_text CHAR(6) NULL{nativeColumns});");
        await session.ExecuteAsync(
            database,
            "CREATE TABLE collation_sample (" +
            "id INT PRIMARY KEY, " +
            "case_value VARCHAR(40) COLLATE utf8mb4_general_ci NOT NULL, " +
            "accent_value VARCHAR(40) COLLATE utf8mb4_general_ci NOT NULL, " +
            "marker VARCHAR(40) NOT NULL);");
        await session.ExecuteAsync(
            database,
            "INSERT INTO collation_sample VALUES (1, 'Alpha', 'resume', 'before');");
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
        await VerifyMySqlStringExactConflictAsync(
            session,
            database,
            objects.Single(item => item.Name == "collation_sample" && item.Kind == DatabaseObjectKind.Table));
        await VerifyMySqlEnumExactValuesAsync(session, database, table!);
        await VerifyMySqlSetExactMembersAsync(session, database, table!);
        await VerifyNonRoundTrippableTrailingSpacesAsync(session, database, table!);
        await VerifyFixedLengthBinaryAsync(session, database, table!);
        await VerifyFloatingPointTypesAsync(
            session,
            database,
            table!,
            id => $"UPDATE sample SET single_value = 2.5 WHERE id = {id};");
        await VerifyMySqlTemporalTypesAsync(session, database, table!);
        if (isMariaDb)
        {
            await VerifyMariaDbNativeTypesAsync(session, database, table!);
        }
        await VerifyIntegerTypesAsync(
            session,
            database,
            table!,
            id => $"UPDATE sample SET integer_value = 1 WHERE id = {id};");
        await VerifyMySqlMutationWarningsRollbackAsync(session, database, table!);
    }
    finally
    {
        await session.ExecuteAsync(string.Empty, $"DROP DATABASE IF EXISTS `{database}`;");
    }
}

static async Task VerifyMySqlStringExactConflictAsync(
    IDatabaseSession session,
    string database,
    DatabaseObjectInfo table)
{
    var before = await session.LoadTableDataAsync(database, table);
    Assert(
        before.Columns.Where(column => column.Name is "case_value" or "accent_value")
            .All(column => column.ValueKind == TableColumnValueKind.String && column.IsEditable),
        "MySQL／MariaDB collated VARCHAR metadata 應映射為可編輯字串");
    var stale = before.Rows.Single();

    await session.ExecuteAsync(
        database,
        "UPDATE collation_sample SET case_value = 'alpha', accent_value = 'résumé' WHERE id = 1;");
    await AssertThrowsAsync<TableDataConflictException>(() => session.UpdateTableRowAsync(
        database,
        table,
        stale,
        new[] { new TableCellInput("marker", TableCellInputMode.Value, "stale-overwrite") }));

    var afterConflict = await session.ExecuteAsync(
        database,
        "SELECT HEX(case_value), HEX(accent_value), marker FROM collation_sample WHERE id = 1;");
    Assert(
        Convert.ToString(afterConflict.Rows.Single()[0]) == "616C706861" &&
        Convert.ToString(afterConflict.Rows.Single()[1]) == "72C3A973756DC3A9" &&
        Convert.ToString(afterConflict.Rows.Single()[2]) == "before",
        $"{session.Profile.ProviderDisplayName} 大小寫／重音外部變更不可被過期編輯覆寫");

    var refreshed = (await session.LoadTableDataAsync(database, table)).Rows.Single();
    await session.UpdateTableRowAsync(
        database,
        table,
        refreshed,
        new[] { new TableCellInput("marker", TableCellInputMode.Value, "after-refresh") });
    var afterRefresh = await session.ExecuteAsync(
        database,
        "SELECT marker FROM collation_sample WHERE id = 1;");
    Assert(
        Convert.ToString(afterRefresh.Rows.Single()[0]) == "after-refresh",
        $"{session.Profile.ProviderDisplayName} 重新整理後應可正常修改 collated VARCHAR 資料列");
}

static async Task VerifyMySqlTemporalTypesAsync(
    IDatabaseSession session,
    string database,
    DatabaseObjectInfo table)
{
    var before = await session.LoadTableDataAsync(database, table);
    var temporalColumns = before.Columns
        .Where(column => column.ValueKind == TableColumnValueKind.MySqlTemporal)
        .ToDictionary(column => column.Name, StringComparer.Ordinal);
    var expectedTypes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["event_date"] = "date",
        ["recorded_at"] = "datetime(3)",
        ["precise_at"] = "datetime(6)",
        ["changed_at"] = "timestamp(2)"
    };
    Assert(
        temporalColumns.Count == expectedTypes.Count,
        $"MySQL／MariaDB temporal metadata 未完整辨識；actual={temporalColumns.Count}");
    foreach (var expected in expectedTypes)
    {
        Assert(
            temporalColumns.TryGetValue(expected.Key, out var column) &&
            column.IsEditable &&
            column.StorageDataTypeName == expected.Value,
            $"MySQL／MariaDB {expected.Key} metadata 不正確；actual={column?.StorageDataTypeName}");
    }

    await session.InsertTableRowAsync(
        database,
        table,
        new[]
        {
            new TableCellInput("name", TableCellInputMode.Value, "MySQL temporal"),
            new TableCellInput("event_date", TableCellInputMode.Value, "1000-01-01"),
            new TableCellInput("recorded_at", TableCellInputMode.Value, "1000-01-01T00:00:00.123"),
            new TableCellInput("precise_at", TableCellInputMode.Value, "2026-08-30T12:34:56.123456"),
            new TableCellInput("changed_at", TableCellInputMode.Value, "2030-01-02T03:04:05.12")
        });

    var insertedSnapshot = await session.LoadTableDataAsync(database, table);
    var inserted = insertedSnapshot.Rows.Single(row =>
        Convert.ToString(row.Values[1]) == "MySQL temporal");
    Assert(
        Convert.ToString(inserted.Values[temporalColumns["event_date"].Ordinal]) == "1000-01-01" &&
        Convert.ToString(inserted.Values[temporalColumns["recorded_at"].Ordinal]) == "1000-01-01T00:00:00.123" &&
        Convert.ToString(inserted.Values[temporalColumns["precise_at"].Ordinal]) == "2026-08-30T12:34:56.123456" &&
        Convert.ToString(inserted.Values[temporalColumns["changed_at"].Ordinal]) == "2030-01-02T03:04:05.12",
        "MySQL／MariaDB temporal 新增後未保留宣告精度或 canonical 格式");

    var invalidValues = new[]
    {
        (Column: "event_date", Value: "0999-12-31"),
        (Column: "event_date", Value: "2026-08-30T12:00:00"),
        (Column: "recorded_at", Value: "0999-12-31T23:59:59.999"),
        (Column: "recorded_at", Value: "2026-08-30T12:34:56.1234"),
        (Column: "precise_at", Value: "2026-08-30T12:34:56.1234567"),
        (Column: "changed_at", Value: "2030-01-02T03:04:05.123"),
        (Column: "changed_at", Value: "2030-01-02T03:04:05.12+08:00")
    };
    foreach (var invalid in invalidValues)
    {
        await AssertThrowsAsync<InvalidOperationException>(() => session.InsertTableRowAsync(
            database,
            table,
            new[]
            {
                new TableCellInput("name", TableCellInputMode.Value, "Rejected MySQL temporal"),
                new TableCellInput(invalid.Column, TableCellInputMode.Value, invalid.Value)
            }));
    }
    var rejectedSnapshot = await session.LoadTableDataAsync(database, table);
    Assert(
        rejectedSnapshot.Rows.All(row => Convert.ToString(row.Values[1]) != "Rejected MySQL temporal"),
        "MySQL／MariaDB temporal 錯誤／會取整的輸入不可留下半筆資料");

    await session.UpdateTableRowAsync(
        database,
        table,
        inserted,
        new[]
        {
            new TableCellInput("event_date", TableCellInputMode.Value, "9999-12-31"),
            new TableCellInput("recorded_at", TableCellInputMode.Value, "9999-12-31T23:59:59.999"),
            new TableCellInput("precise_at", TableCellInputMode.Value, "9999-12-31T23:59:59.999999"),
            new TableCellInput("changed_at", TableCellInputMode.Value, "2030-12-31T23:59:59.99")
        });
    var updatedSnapshot = await session.LoadTableDataAsync(database, table);
    var updated = updatedSnapshot.Rows.Single(row =>
        Convert.ToString(row.Values[1]) == "MySQL temporal");
    Assert(
        Convert.ToString(updated.Values[temporalColumns["event_date"].Ordinal]) == "9999-12-31" &&
        Convert.ToString(updated.Values[temporalColumns["recorded_at"].Ordinal]) == "9999-12-31T23:59:59.999" &&
        Convert.ToString(updated.Values[temporalColumns["precise_at"].Ordinal]) == "9999-12-31T23:59:59.999999" &&
        Convert.ToString(updated.Values[temporalColumns["changed_at"].Ordinal]) == "2030-12-31T23:59:59.99",
        "MySQL／MariaDB temporal 修改後未保留範圍邊界與宣告精度");

    var id = Convert.ToUInt64(updated.Values[0], CultureInfo.InvariantCulture);
    await session.ExecuteAsync(
        database,
        $"UPDATE sample SET recorded_at = '2026-08-30 12:34:56.789' WHERE id = {id};");
    await AssertThrowsAsync<TableDataConflictException>(() => session.UpdateTableRowAsync(
        database,
        table,
        updated,
        new[] { new TableCellInput("quantity", TableCellInputMode.Value, "99") }));
    var concurrentSnapshot = await session.LoadTableDataAsync(database, table);
    var concurrent = concurrentSnapshot.Rows.Single(row =>
        Convert.ToUInt64(row.Values[0], CultureInfo.InvariantCulture) == id);
    Assert(
        Convert.ToString(concurrent.Values[temporalColumns["recorded_at"].Ordinal]) == "2026-08-30T12:34:56.789" &&
        Convert.ToInt32(concurrent.Values[2], CultureInfo.InvariantCulture) != 99,
        "MySQL／MariaDB temporal 原值變更時 optimistic concurrency 不可覆寫外部資料");

    await session.DeleteTableRowAsync(database, table, concurrent);
    var afterDelete = await session.LoadTableDataAsync(database, table);
    Assert(
        afterDelete.Rows.All(row => Convert.ToUInt64(row.Values[0], CultureInfo.InvariantCulture) != id),
        "MySQL／MariaDB temporal 安全刪除失敗");
}

static async Task VerifyMariaDbNativeTypesAsync(
    IDatabaseSession session,
    string database,
    DatabaseObjectInfo table)
{
    var before = await session.LoadTableDataAsync(database, table);
    var uuidColumn = before.Columns.Single(column => column.Name == "native_uuid");
    var addressColumn = before.Columns.Single(column => column.Name == "native_address");
    var ipv4Column = before.Columns.Single(column => column.Name == "native_ipv4");
    Assert(
        uuidColumn.ValueKind == TableColumnValueKind.Guid && uuidColumn.IsEditable,
        "MariaDB UUID metadata 應映射為可編輯 Guid");
    Assert(
        addressColumn.ValueKind == TableColumnValueKind.NetworkAddress && addressColumn.IsEditable,
        "MariaDB INET6 metadata 應映射為可編輯 NetworkAddress");
    Assert(
        ipv4Column.ValueKind == TableColumnValueKind.NetworkAddress &&
        ipv4Column.StorageDataTypeName == "inet4" &&
        ipv4Column.IsEditable,
        "MariaDB INET4 metadata 應映射為可編輯 NetworkAddress");

    await session.InsertTableRowAsync(
        database,
        table,
        new[]
        {
            new TableCellInput("name", TableCellInputMode.Value, "MariaDB native"),
            new TableCellInput("native_uuid", TableCellInputMode.Value, "550E8400-E29B-41D4-A716-446655440000"),
            new TableCellInput("native_address", TableCellInputMode.Value, "192.0.2.10"),
            new TableCellInput("native_ipv4", TableCellInputMode.Value, "0.0.0.0")
        });

    var insertedSnapshot = await session.LoadTableDataAsync(database, table);
    var inserted = insertedSnapshot.Rows.Single(row =>
        Convert.ToString(row.Values[1]) == "MariaDB native");
    Assert(
        Convert.ToString(inserted.Values[uuidColumn.Ordinal]) == "550e8400-e29b-41d4-a716-446655440000",
        $"MariaDB UUID 新增後未正規化；actual={inserted.Values[uuidColumn.Ordinal]}");
    Assert(
        Convert.ToString(inserted.Values[addressColumn.Ordinal]) == "::ffff:192.0.2.10",
        $"MariaDB INET6 應把 IPv4 保存為 mapped IPv6；actual={inserted.Values[addressColumn.Ordinal]}");
    Assert(
        Convert.ToString(inserted.Values[ipv4Column.Ordinal]) == "0.0.0.0",
        $"MariaDB INET4 新增後 canonical 值不正確；actual={inserted.Values[ipv4Column.Ordinal]}");
    var insertedIpv4Native = await session.ExecuteAsync(
        database,
        $"SELECT HEX(native_ipv4) FROM sample WHERE id = {Convert.ToInt64(inserted.Values[0])};");
    Assert(
        Convert.ToString(insertedIpv4Native.Rows.Single()[0]) == "00000000",
        "MariaDB INET4 必須以 4-byte binary 保存 IPv4 位址");

    foreach (var invalidInput in new[]
             {
                 new TableCellInput("native_uuid", TableCellInputMode.Value, "not-a-uuid"),
                 new TableCellInput("native_address", TableCellInputMode.Value, "2001:db8::1/64"),
                 new TableCellInput("native_address", TableCellInputMode.Value, "fe80::1%3"),
                 new TableCellInput("native_ipv4", TableCellInputMode.Value, "2001:db8::1"),
                 new TableCellInput("native_ipv4", TableCellInputMode.Value, "192.0.2.1/24")
             })
    {
        await AssertThrowsAsync<InvalidOperationException>(() => session.InsertTableRowAsync(
            database,
            table,
            new[]
            {
                new TableCellInput("name", TableCellInputMode.Value, "Rejected MariaDB native"),
                invalidInput
            }));
    }
    var rejectedSnapshot = await session.LoadTableDataAsync(database, table);
    Assert(
        rejectedSnapshot.Rows.All(row => Convert.ToString(row.Values[1]) != "Rejected MariaDB native"),
        "MariaDB UUID／INET4／INET6 錯誤輸入不可留下半筆資料");

    await session.UpdateTableRowAsync(
        database,
        table,
        inserted,
        new[]
        {
            new TableCellInput("native_uuid", TableCellInputMode.Value, "6ba7b8109dad11d180b400c04fd430c8"),
            new TableCellInput("native_address", TableCellInputMode.Value, "2001:0db8:0:0:0:0:0:1"),
            new TableCellInput("native_ipv4", TableCellInputMode.Value, "255.255.255.255")
        });
    var updatedSnapshot = await session.LoadTableDataAsync(database, table);
    var updated = updatedSnapshot.Rows.Single(row =>
        Convert.ToString(row.Values[1]) == "MariaDB native");
    Assert(
        Convert.ToString(updated.Values[uuidColumn.Ordinal]) == "6ba7b810-9dad-11d1-80b4-00c04fd430c8",
        $"MariaDB UUID 修改後 canonical 值不正確；actual={updated.Values[uuidColumn.Ordinal]}");
    Assert(
        Convert.ToString(updated.Values[addressColumn.Ordinal]) == "2001:db8::1",
        $"MariaDB INET6 修改後 canonical 值不正確；actual={updated.Values[addressColumn.Ordinal]}");
    Assert(
        Convert.ToString(updated.Values[ipv4Column.Ordinal]) == "255.255.255.255",
        $"MariaDB INET4 修改後 canonical 值不正確；actual={updated.Values[ipv4Column.Ordinal]}");

    var id = Convert.ToInt64(updated.Values[0]);
    await session.ExecuteAsync(
        database,
        $"UPDATE sample SET native_uuid = CAST('6ba7b811-9dad-11d1-80b4-00c04fd430c8' AS UUID), " +
        $"native_ipv4 = CAST('203.0.113.9' AS INET4) WHERE id = {id};");
    await AssertThrowsAsync<TableDataConflictException>(() => session.UpdateTableRowAsync(
        database,
        table,
        updated,
        new[] { new TableCellInput("quantity", TableCellInputMode.Value, "99") }));
    var concurrentSnapshot = await session.LoadTableDataAsync(database, table);
    var concurrent = concurrentSnapshot.Rows.Single(row => Convert.ToInt64(row.Values[0]) == id);
    Assert(
        Convert.ToString(concurrent.Values[uuidColumn.Ordinal]) == "6ba7b811-9dad-11d1-80b4-00c04fd430c8" &&
        Convert.ToString(concurrent.Values[ipv4Column.Ordinal]) == "203.0.113.9" &&
        Convert.ToInt32(concurrent.Values[2]) != 99,
        "MariaDB UUID／INET4 原值變更時 optimistic concurrency 不可覆寫外部資料");

    await session.DeleteTableRowAsync(database, table, concurrent);
    var afterDelete = await session.LoadTableDataAsync(database, table);
    Assert(afterDelete.Rows.All(row => Convert.ToInt64(row.Values[0]) != id), "MariaDB UUID／INET4／INET6 安全刪除失敗");
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
        await session.ExecuteAsync(database, "CREATE EXTENSION citext;");
        await session.ExecuteAsync(database, "CREATE EXTENSION hstore;");
        await session.ExecuteAsync(database, "CREATE EXTENSION ltree;");
        await session.ExecuteAsync(
            database,
            "CREATE COLLATION exact_conflict_ci (" +
            "provider = icu, locale = 'und-u-ks-level1', deterministic = false);");
        await session.ExecuteAsync(database, "CREATE TYPE mood AS ENUM ('happy', 'sad', 'comma,value');");
        await session.ExecuteAsync(database, "CREATE TYPE address_type AS (city TEXT, postal_code INTEGER);");
        await session.ExecuteAsync(database, "CREATE DOMAIN positive_count AS INTEGER CHECK (VALUE BETWEEN 1 AND 100);");
        await session.ExecuteAsync(database, "CREATE DOMAIN short_label AS VARCHAR(30) CHECK (length(VALUE) >= 3);");
        await session.ExecuteAsync(database, "CREATE DOMAIN fixed_label AS CHARACTER(6);");
        await session.ExecuteAsync(database, "CREATE DOMAIN precise_amount AS NUMERIC(18,6) CHECK (VALUE <> 0);");
        await session.ExecuteAsync(database, "CREATE DOMAIN work_state AS mood;");
        await session.ExecuteAsync(database, "CREATE DOMAIN subnet_domain AS CIDR CHECK (masklen(VALUE) >= 24);");
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
                event_date DATE NULL,
                account_balance MONEY NULL,
                local_timestamp TIMESTAMP(3) WITHOUT TIME ZONE NULL,
                precise_timestamp TIMESTAMP(6) WITHOUT TIME ZONE NULL,
                utc_timestamp TIMESTAMP(4) WITH TIME ZONE NULL,
                local_clock TIME(2) WITHOUT TIME ZONE NULL,
                duration INTERVAL NULL,
                duration_ms INTERVAL(3) NULL,
                duration_year INTERVAL YEAR NULL,
                duration_month INTERVAL YEAR TO MONTH NULL,
                duration_day INTERVAL DAY NULL,
                duration_hour INTERVAL DAY TO HOUR NULL,
                duration_minute INTERVAL DAY TO MINUTE NULL,
                duration_second INTERVAL DAY TO SECOND(4) NULL,
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
                domain_count positive_count NULL,
                domain_label short_label NULL,
                domain_amount precise_amount NULL,
                domain_state work_state NULL,
                domain_subnet subnet_domain NULL,
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
                small_value SMALLINT NULL,
                integer_value INTEGER NULL,
                big_value BIGINT NULL,
                single_value REAL NULL,
                double_value DOUBLE PRECISION NULL,
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
                type_name REGTYPE NULL,
                fixed_text CHARACTER(6) NULL,
                domain_fixed fixed_label NULL,
                unbounded_fixed bpchar NULL,
                bounded_varying VARCHAR(6) NULL
            );
            """);
        var insert = await session.ExecuteAsync(database, "INSERT INTO sample (name) VALUES ('Punky'), ('macOS');");
        await session.ExecuteAsync(
            database,
            "CREATE TABLE collation_sample (" +
            "id INTEGER PRIMARY KEY, " +
            "case_value CITEXT NOT NULL, " +
            "collated_value TEXT COLLATE exact_conflict_ci NOT NULL, " +
            "marker TEXT NOT NULL);");
        await session.ExecuteAsync(
            database,
            "INSERT INTO collation_sample VALUES (1, 'Alpha', 'Resume', 'before');");
        await session.ExecuteAsync(
            database,
            "CREATE TABLE json_sample (" +
            "id INTEGER PRIMARY KEY, raw_value JSON NOT NULL, marker TEXT NOT NULL);");
        await session.ExecuteAsync(
            database,
            "INSERT INTO json_sample VALUES (1, '{\"a\":1,\"b\":2}', 'before');");
        await session.ExecuteAsync(
            database,
            "CREATE TABLE json_array_sample (" +
            "id INTEGER PRIMARY KEY, raw_items JSON[] NOT NULL, marker TEXT NOT NULL);");
        await session.ExecuteAsync(
            database,
            "INSERT INTO json_array_sample VALUES (" +
            "1, ARRAY['{\"a\":1}'::json, '[1,2]'::json], 'before');");
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
        await VerifyPostgreSqlStringExactConflictAsync(
            session,
            database,
            objects.Single(item =>
                item.Name == "collation_sample" &&
                item.Schema == "public" &&
                item.Kind == DatabaseObjectKind.Table));
        await VerifyPostgreSqlJsonExactConflictAsync(
            session,
            database,
            objects.Single(item =>
                item.Name == "json_sample" &&
                item.Schema == "public" &&
                item.Kind == DatabaseObjectKind.Table));
        await VerifyPostgreSqlArrayWithoutEqualityAsync(
            session,
            database,
            objects.Single(item =>
                item.Name == "json_array_sample" &&
                item.Schema == "public" &&
                item.Kind == DatabaseObjectKind.Table));
        await VerifyNonRoundTrippableTrailingSpacesAsync(session, database, table!);
        await VerifyPostgreSqlBoundedStringsAsync(session, database, table!);
        await VerifyFloatingPointTypesAsync(
            session,
            database,
            table!,
            id => $"UPDATE public.sample SET single_value = 2.5 WHERE id = {id};");
        await VerifyIntegerTypesAsync(
            session,
            database,
            table!,
            id => $"UPDATE public.sample SET integer_value = 1 WHERE id = {id};");
        await VerifyPostgreSqlTemporalTypesAsync(session, database, table!);
        await VerifyPostgreSqlIntervalRestrictionsAsync(session, database, table!);
        await VerifyPostgreSqlMoneyAsync(session, database, table!);
    }
    finally
    {
        await session.ExecuteAsync(
            "postgres",
            $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{database}' AND pid <> pg_backend_pid();");
        await session.ExecuteAsync("postgres", $"DROP DATABASE IF EXISTS \"{database}\";");
    }
}

static async Task VerifyPostgreSqlStringExactConflictAsync(
    IDatabaseSession session,
    string database,
    DatabaseObjectInfo table)
{
    var before = await session.LoadTableDataAsync(database, table);
    var caseColumn = before.Columns.Single(column => column.Name == "case_value");
    var collatedColumn = before.Columns.Single(column => column.Name == "collated_value");
    Assert(
        caseColumn.ValueKind == TableColumnValueKind.String &&
        caseColumn.StorageDataTypeName == "citext" &&
        caseColumn.IsEditable &&
        collatedColumn.ValueKind == TableColumnValueKind.String &&
        collatedColumn.StorageDataTypeName == "text" &&
        collatedColumn.IsEditable,
        $"PostgreSQL citext／collated text metadata 應映射為可編輯字串；" +
        $"actual={caseColumn}, {collatedColumn}");
    var collationSemantics = await session.ExecuteAsync(
        database,
        "SELECT 'Resume'::text COLLATE exact_conflict_ci = " +
        "'résumé'::text COLLATE exact_conflict_ci;");
    Assert(
        Convert.ToBoolean(collationSemantics.Rows.Single()[0], CultureInfo.InvariantCulture),
        "PostgreSQL 測試 collation 應證實會把大小寫與重音不同的文字視為相等");
    var stale = before.Rows.Single();

    await session.ExecuteAsync(
        database,
        "UPDATE public.collation_sample SET collated_value = 'résumé' WHERE id = 1;");
    await AssertThrowsAsync<TableDataConflictException>(() => session.UpdateTableRowAsync(
        database,
        table,
        stale,
        new[] { new TableCellInput("marker", TableCellInputMode.Value, "stale-overwrite") }));

    var afterConflict = await session.ExecuteAsync(
        database,
        "SELECT encode(convert_to(case_value::text, 'UTF8'), 'hex'), " +
        "encode(convert_to(collated_value, 'UTF8'), 'hex'), marker " +
        "FROM public.collation_sample WHERE id = 1;");
    Assert(
        Convert.ToString(afterConflict.Rows.Single()[0]) == "416c706861" &&
        Convert.ToString(afterConflict.Rows.Single()[1]) == "72c3a973756dc3a9" &&
        Convert.ToString(afterConflict.Rows.Single()[2]) == "before",
        "PostgreSQL nondeterministic collation 外部變更不可被過期編輯覆寫");

    var refreshed = (await session.LoadTableDataAsync(database, table)).Rows.Single();
    await session.UpdateTableRowAsync(
        database,
        table,
        refreshed,
        new[]
        {
            new TableCellInput("case_value", TableCellInputMode.Value, "Beta"),
            new TableCellInput("collated_value", TableCellInputMode.Value, "Ångström"),
            new TableCellInput("marker", TableCellInputMode.Value, "after-refresh")
        });
    var afterRefresh = await session.ExecuteAsync(
        database,
        "SELECT case_value::text, collated_value, marker " +
        "FROM public.collation_sample WHERE id = 1;");
    Assert(
        Convert.ToString(afterRefresh.Rows.Single()[0]) == "Beta" &&
        Convert.ToString(afterRefresh.Rows.Single()[1]) == "Ångström" &&
        Convert.ToString(afterRefresh.Rows.Single()[2]) == "after-refresh",
        "PostgreSQL 重新整理後應可正常修改 citext／collated text 資料列");
}

static async Task VerifyPostgreSqlJsonExactConflictAsync(
    IDatabaseSession session,
    string database,
    DatabaseObjectInfo table)
{
    var before = await session.LoadTableDataAsync(database, table);
    var jsonColumn = before.Columns.Single(column => column.Name == "raw_value");
    Assert(
        jsonColumn.ValueKind == TableColumnValueKind.Json &&
        jsonColumn.StorageDataTypeName == "json" &&
        jsonColumn.IsEditable &&
        Convert.ToString(before.Rows.Single().Values[jsonColumn.Ordinal]) == "{\"a\":1,\"b\":2}",
        $"PostgreSQL json metadata／原始文字載入不正確；actual={jsonColumn}");
    var stale = before.Rows.Single();

    const string externalJson = "{ \"b\": 2, \"a\": 1 }";
    await session.ExecuteAsync(
        database,
        $"UPDATE public.json_sample SET raw_value = '{externalJson}'::json WHERE id = 1;");
    await AssertThrowsAsync<TableDataConflictException>(() => session.UpdateTableRowAsync(
        database,
        table,
        stale,
        new[] { new TableCellInput("marker", TableCellInputMode.Value, "stale-overwrite") }));

    var afterConflict = await session.ExecuteAsync(
        database,
        "SELECT raw_value::text, marker FROM public.json_sample WHERE id = 1;");
    Assert(
        Convert.ToString(afterConflict.Rows.Single()[0]) == externalJson &&
        Convert.ToString(afterConflict.Rows.Single()[1]) == "before",
        "PostgreSQL json 格式／key 順序外部變更不可被過期編輯覆寫");

    var refreshed = (await session.LoadTableDataAsync(database, table)).Rows.Single();
    const string updatedJson = "{\"final\":[1,2,3]}";
    await session.UpdateTableRowAsync(
        database,
        table,
        refreshed,
        new[]
        {
            new TableCellInput("raw_value", TableCellInputMode.Value, updatedJson),
            new TableCellInput("marker", TableCellInputMode.Value, "after-refresh")
        });
    var afterRefresh = await session.ExecuteAsync(
        database,
        "SELECT raw_value::text, marker FROM public.json_sample WHERE id = 1;");
    Assert(
        Convert.ToString(afterRefresh.Rows.Single()[0]) == updatedJson &&
        Convert.ToString(afterRefresh.Rows.Single()[1]) == "after-refresh",
        "PostgreSQL 重新整理後應可無損修改 json 原始文字");
}

static async Task VerifyPostgreSqlArrayWithoutEqualityAsync(
    IDatabaseSession session,
    string database,
    DatabaseObjectInfo table)
{
    var before = await session.LoadTableDataAsync(database, table);
    var arrayColumn = before.Columns.Single(column => column.Name == "raw_items");
    var originalItems = Convert.ToString(before.Rows.Single().Values[arrayColumn.Ordinal]);
    Assert(
        arrayColumn.ValueKind == TableColumnValueKind.PostgreSqlArray &&
        arrayColumn.StorageDataTypeName == "json[]" &&
        arrayColumn.IsEditable &&
        !string.IsNullOrWhiteSpace(originalItems),
        $"PostgreSQL json[] metadata／canonical text 載入不正確；actual={arrayColumn}");

    await session.UpdateTableRowAsync(
        database,
        table,
        before.Rows.Single(),
        new[] { new TableCellInput("marker", TableCellInputMode.Value, "round-trip") });
    var afterRoundTrip = await session.ExecuteAsync(
        database,
        "SELECT raw_items::text, marker FROM public.json_array_sample WHERE id = 1;");
    Assert(
        Convert.ToString(afterRoundTrip.Rows.Single()[0]) == originalItems &&
        Convert.ToString(afterRoundTrip.Rows.Single()[1]) == "round-trip",
        "PostgreSQL json[] 不應因元素型別沒有等號運算子而無法修改同列");

    var stale = (await session.LoadTableDataAsync(database, table)).Rows.Single();
    await session.ExecuteAsync(
        database,
        "UPDATE public.json_array_sample " +
        "SET raw_items = ARRAY['{\"external\":true}'::json] WHERE id = 1;");
    await AssertThrowsAsync<TableDataConflictException>(() => session.UpdateTableRowAsync(
        database,
        table,
        stale,
        new[] { new TableCellInput("marker", TableCellInputMode.Value, "stale-overwrite") }));

    var afterConflict = await session.ExecuteAsync(
        database,
        "SELECT raw_items::text, marker, " +
        "ARRAY['{\"external\":true}'::json]::text " +
        "FROM public.json_array_sample WHERE id = 1;");
    Assert(
        Convert.ToString(afterConflict.Rows.Single()[0]) == Convert.ToString(afterConflict.Rows.Single()[2]) &&
        Convert.ToString(afterConflict.Rows.Single()[1]) == "round-trip",
        "PostgreSQL json[] 外部變更不可被過期編輯覆寫");

    var refreshed = (await session.LoadTableDataAsync(database, table)).Rows.Single();
    const string updatedItems = "{\"{\\\"final\\\":true}\",\"null\"}";
    await session.UpdateTableRowAsync(
        database,
        table,
        refreshed,
        new[]
        {
            new TableCellInput("raw_items", TableCellInputMode.Value, updatedItems),
            new TableCellInput("marker", TableCellInputMode.Value, "after-refresh")
        });
    var afterRefresh = await session.ExecuteAsync(
        database,
        "SELECT raw_items::text, marker FROM public.json_array_sample WHERE id = 1;");
    Assert(
        Convert.ToString(afterRefresh.Rows.Single()[0]) == updatedItems &&
        Convert.ToString(afterRefresh.Rows.Single()[1]) == "after-refresh",
        "PostgreSQL 重新整理後應可正常修改 json[]");
}

static async Task VerifyPostgreSqlBoundedStringsAsync(
    IDatabaseSession session,
    string database,
    DatabaseObjectInfo table)
{
    var before = await session.LoadTableDataAsync(database, table);
    var boundedColumn = before.Columns.Single(column => column.Name == "bounded_varying");
    var fixedColumn = before.Columns.Single(column => column.Name == "fixed_text");
    var domainColumn = before.Columns.Single(column => column.Name == "domain_label");
    var unboundedColumn = before.Columns.Single(column => column.Name == "unbounded_fixed");
    Assert(
        boundedColumn.ValueKind == TableColumnValueKind.String &&
        boundedColumn.StorageDataTypeName == "character varying(6)" &&
        boundedColumn.MaximumStringLengthInCharacters == 6 &&
        !boundedColumn.TrailingSpacesAreNotRoundTrippable,
        "PostgreSQL varchar(n) metadata 應保留 Unicode 字元上限且允許欄寬內尾端空白");
    Assert(
        fixedColumn.MaximumStringLengthInCharacters == 6 &&
        domainColumn.MaximumStringLengthInCharacters == 30 &&
        unboundedColumn.MaximumStringLengthInCharacters is null,
        "PostgreSQL character／varchar domain／unbounded bpchar 的字元上限 metadata 不正確");

    await session.ExecuteAsync(
        database,
        "INSERT INTO public.sample (name, bounded_varying) " +
        "VALUES ('Native bounded spaces', 'ABCDEF  ');");
    var nativeSnapshot = await session.LoadTableDataAsync(database, table);
    var nativeRow = nativeSnapshot.Rows.Single(row =>
        Convert.ToString(row.Values[1]) == "Native bounded spaces");
    Assert(
        Convert.ToString(nativeRow.Values[boundedColumn.Ordinal]) == "ABCDEF",
        "PostgreSQL 原生 varchar(n) 應證實會靜默截掉超出欄寬的純尾端空白");
    await session.DeleteTableRowAsync(database, table, nativeRow);

    var invalidValues = new[]
    {
        (Name: "Rejected bounded spaces", Value: "ABCDEF "),
        (Name: "Rejected bounded text", Value: "ABCDEFG"),
        (Name: "Rejected bounded Unicode", Value: "🐧台灣ABCD")
    };
    foreach (var invalid in invalidValues)
    {
        var exception = await CaptureExceptionAsync<InvalidOperationException>(() =>
            session.InsertTableRowAsync(
                database,
                table,
                new[]
                {
                    new TableCellInput("name", TableCellInputMode.Value, invalid.Name),
                    new TableCellInput("bounded_varying", TableCellInputMode.Value, invalid.Value)
                }));
        Assert(
            exception.Message.Contains("最多只能保存 6 個 Unicode 字元", StringComparison.Ordinal),
            $"PostgreSQL varchar(6) 超長輸入應回報 Unicode 字元上限；actual={exception.Message}");
    }
    var rejectedSnapshot = await session.LoadTableDataAsync(database, table);
    Assert(
        invalidValues.All(invalid => rejectedSnapshot.Rows.All(row =>
            Convert.ToString(row.Values[1]) != invalid.Name)),
        "PostgreSQL bounded varchar 超長輸入不可落地");

    await session.InsertTableRowAsync(
        database,
        table,
        new[]
        {
            new TableCellInput("name", TableCellInputMode.Value, "Bounded varchar editor"),
            new TableCellInput("bounded_varying", TableCellInputMode.Value, "AB  ")
        });
    var insertedSnapshot = await session.LoadTableDataAsync(database, table);
    var inserted = insertedSnapshot.Rows.Single(row =>
        Convert.ToString(row.Values[1]) == "Bounded varchar editor");
    Assert(
        Convert.ToString(inserted.Values[boundedColumn.Ordinal]) == "AB  ",
        "PostgreSQL varchar(n) 應保留欄寬內的尾端空白");

    var id = Convert.ToInt64(inserted.Values[0], CultureInfo.InvariantCulture);
    await session.ExecuteAsync(
        database,
        $"UPDATE public.sample SET bounded_varying = 'AB ' WHERE id = {id};");
    await AssertThrowsAsync<TableDataConflictException>(() => session.UpdateTableRowAsync(
        database,
        table,
        inserted,
        new[] { new TableCellInput("quantity", TableCellInputMode.Value, "77") }));
    var concurrentSnapshot = await session.LoadTableDataAsync(database, table);
    var concurrent = concurrentSnapshot.Rows.Single(row =>
        Convert.ToInt64(row.Values[0], CultureInfo.InvariantCulture) == id);
    Assert(
        Convert.ToString(concurrent.Values[boundedColumn.Ordinal]) == "AB " &&
        Convert.ToInt32(concurrent.Values[2], CultureInfo.InvariantCulture) != 77,
        "PostgreSQL varchar(n) 尾端空白被外部修改時 optimistic concurrency 不可覆寫");

    await session.UpdateTableRowAsync(
        database,
        table,
        concurrent,
        new[] { new TableCellInput("bounded_varying", TableCellInputMode.Value, "🐧台灣ABC") });
    var updatedSnapshot = await session.LoadTableDataAsync(database, table);
    var updated = updatedSnapshot.Rows.Single(row =>
        Convert.ToInt64(row.Values[0], CultureInfo.InvariantCulture) == id);
    Assert(
        Convert.ToString(updated.Values[boundedColumn.Ordinal]) == "🐧台灣ABC",
        "PostgreSQL varchar(6) 應以 Unicode scalar 接受六字元多位元組值");
    await session.DeleteTableRowAsync(database, table, updated);
}

static async Task VerifyPostgreSqlMoneyAsync(
    IDatabaseSession session,
    string database,
    DatabaseObjectInfo table)
{
    var before = await session.LoadTableDataAsync(database, table);
    var moneyColumn = before.Columns.Single(column => column.Name == "account_balance");
    Assert(
        moneyColumn.ValueKind == TableColumnValueKind.PostgreSqlMoney &&
        moneyColumn.IsEditable &&
        moneyColumn.StorageDataTypeName == "money" &&
        moneyColumn.MonetaryScale == 2,
        $"PostgreSQL money metadata 不正確；kind={moneyColumn.ValueKind}, scale={moneyColumn.MonetaryScale}");

    await session.InsertTableRowAsync(
        database,
        table,
        new[]
        {
            new TableCellInput("name", TableCellInputMode.Value, "PostgreSQL money"),
            new TableCellInput("account_balance", TableCellInputMode.Value, "92233720368547758.07")
        });
    var insertedSnapshot = await session.LoadTableDataAsync(database, table);
    var inserted = insertedSnapshot.Rows.Single(row => Convert.ToString(row.Values[1]) == "PostgreSQL money");
    Assert(
        Convert.ToString(inserted.Values[moneyColumn.Ordinal]) == "92233720368547758.07",
        "PostgreSQL money 應無損保留正值上界 canonical 文字");

    await session.UpdateTableRowAsync(
        database,
        table,
        inserted,
        new[] { new TableCellInput("account_balance", TableCellInputMode.Value, "-92233720368547758.08") });
    var updatedSnapshot = await session.LoadTableDataAsync(database, table);
    var updated = updatedSnapshot.Rows.Single(row => Convert.ToString(row.Values[1]) == "PostgreSQL money");
    Assert(
        Convert.ToString(updated.Values[moneyColumn.Ordinal]) == "-92233720368547758.08",
        "PostgreSQL money 應無損保留負值下界 canonical 文字");

    foreach (var invalid in new[]
             {
                 "1.235",
                 "92233720368547758.08",
                 "-92233720368547758.09",
                 "$1.23",
                 "1,234.56"
             })
    {
        await AssertThrowsAsync<InvalidOperationException>(() => session.UpdateTableRowAsync(
            database,
            table,
            updated,
            new[] { new TableCellInput("account_balance", TableCellInputMode.Value, invalid) }));
    }
    var rejectedSnapshot = await session.LoadTableDataAsync(database, table);
    var unchanged = rejectedSnapshot.Rows.Single(row => Convert.ToString(row.Values[1]) == "PostgreSQL money");
    Assert(
        Convert.ToString(unchanged.Values[moneyColumn.Ordinal]) == "-92233720368547758.08",
        "PostgreSQL money 不可把錯誤或需取整的輸入寫入");

    var id = Convert.ToInt64(unchanged.Values[0], CultureInfo.InvariantCulture);
    await session.ExecuteAsync(
        database,
        $"UPDATE public.sample SET account_balance = '1.23'::money WHERE id = {id};");
    await AssertThrowsAsync<TableDataConflictException>(() => session.UpdateTableRowAsync(
        database,
        table,
        unchanged,
        new[] { new TableCellInput("quantity", TableCellInputMode.Value, "77") }));
    var concurrentSnapshot = await session.LoadTableDataAsync(database, table);
    var concurrent = concurrentSnapshot.Rows.Single(row =>
        Convert.ToInt64(row.Values[0], CultureInfo.InvariantCulture) == id);
    Assert(
        Convert.ToString(concurrent.Values[moneyColumn.Ordinal]) == "1.23" &&
        Convert.ToInt32(concurrent.Values[2], CultureInfo.InvariantCulture) != 77,
        "PostgreSQL money 原值變更時 optimistic concurrency 不可覆寫外部資料");

    await session.DeleteTableRowAsync(database, table, concurrent);
    var afterDelete = await session.LoadTableDataAsync(database, table);
    Assert(
        afterDelete.Rows.All(row => Convert.ToInt64(row.Values[0], CultureInfo.InvariantCulture) != id),
        "PostgreSQL money 安全刪除失敗");
}

static async Task VerifyPostgreSqlTemporalTypesAsync(
    IDatabaseSession session,
    string database,
    DatabaseObjectInfo table)
{
    var before = await session.LoadTableDataAsync(database, table);
    var dateColumn = before.Columns.Single(column =>
        column.Name == "event_date" && column.ValueKind == TableColumnValueKind.PostgreSqlDate);
    Assert(
        dateColumn.IsEditable && dateColumn.StorageDataTypeName == "date",
        $"PostgreSQL event_date metadata 不正確；actual={dateColumn.StorageDataTypeName}");

    await session.InsertTableRowAsync(
        database,
        table,
        new[]
        {
            new TableCellInput("name", TableCellInputMode.Value, "PostgreSQL date range"),
            new TableCellInput("event_date", TableCellInputMode.Value, "4713-01-01 BC")
        });
    var dateRangeSnapshot = await session.LoadTableDataAsync(database, table);
    var dateRange = dateRangeSnapshot.Rows.Single(row =>
        Convert.ToString(row.Values[1]) == "PostgreSQL date range");
    Assert(
        Convert.ToString(dateRange.Values[dateColumn.Ordinal]) == "4713-01-01 BC",
        "PostgreSQL date 應保留 BC 下界 canonical 文字");
    await session.UpdateTableRowAsync(
        database,
        table,
        dateRange,
        new[]
        {
            new TableCellInput("event_date", TableCellInputMode.Value, "5874897-12-31")
        });
    var updatedDateRangeSnapshot = await session.LoadTableDataAsync(database, table);
    var updatedDateRange = updatedDateRangeSnapshot.Rows.Single(row =>
        Convert.ToString(row.Values[1]) == "PostgreSQL date range");
    Assert(
        Convert.ToString(updatedDateRange.Values[dateColumn.Ordinal]) == "5874897-12-31",
        "PostgreSQL date 應保留 AD 上界 canonical 文字");
    await session.DeleteTableRowAsync(database, table, updatedDateRange);

    await session.InsertTableRowAsync(
        database,
        table,
        new[]
        {
            new TableCellInput("name", TableCellInputMode.Value, "PostgreSQL date infinity"),
            new TableCellInput("event_date", TableCellInputMode.Value, "infinity")
        });
    var infinitySnapshot = await session.LoadTableDataAsync(database, table);
    var infinity = infinitySnapshot.Rows.Single(row =>
        Convert.ToString(row.Values[1]) == "PostgreSQL date infinity");
    Assert(
        Convert.ToString(infinity.Values[dateColumn.Ordinal]) == "infinity",
        "PostgreSQL date 應保留 infinity");
    await session.UpdateTableRowAsync(
        database,
        table,
        infinity,
        new[] { new TableCellInput("event_date", TableCellInputMode.Value, "-infinity") });
    var negativeInfinitySnapshot = await session.LoadTableDataAsync(database, table);
    var negativeInfinity = negativeInfinitySnapshot.Rows.Single(row =>
        Convert.ToString(row.Values[1]) == "PostgreSQL date infinity");
    Assert(
        Convert.ToString(negativeInfinity.Values[dateColumn.Ordinal]) == "-infinity",
        "PostgreSQL date 應保留 -infinity");
    await session.DeleteTableRowAsync(database, table, negativeInfinity);
    var temporalColumns = before.Columns
        .Where(column => column.ValueKind == TableColumnValueKind.PostgreSqlTemporal)
        .ToDictionary(column => column.Name, StringComparer.Ordinal);
    var expectedTypes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["local_timestamp"] = "timestamp(3) without time zone",
        ["precise_timestamp"] = "timestamp(6) without time zone",
        ["utc_timestamp"] = "timestamp(4) with time zone",
        ["local_clock"] = "time(2) without time zone"
    };
    Assert(
        temporalColumns.Count == expectedTypes.Count,
        $"PostgreSQL temporal metadata 未完整辨識；actual={temporalColumns.Count}");
    foreach (var expected in expectedTypes)
    {
        Assert(
            temporalColumns.TryGetValue(expected.Key, out var column) &&
            column.IsEditable &&
            column.StorageDataTypeName == expected.Value,
            $"PostgreSQL {expected.Key} metadata 不正確；actual={column?.StorageDataTypeName}");
    }

    await session.InsertTableRowAsync(
        database,
        table,
        new[]
        {
            new TableCellInput("name", TableCellInputMode.Value, "PostgreSQL temporal"),
            new TableCellInput("event_date", TableCellInputMode.Value, "0001-01-01"),
            new TableCellInput("local_timestamp", TableCellInputMode.Value, "0001-01-01T00:00:00.123"),
            new TableCellInput("precise_timestamp", TableCellInputMode.Value, "2026-08-30T12:34:56.123456"),
            new TableCellInput("utc_timestamp", TableCellInputMode.Value, "2026-08-30T12:34:56.1234+08:00"),
            new TableCellInput("local_clock", TableCellInputMode.Value, "24:00:00")
        });

    var insertedSnapshot = await session.LoadTableDataAsync(database, table);
    var inserted = insertedSnapshot.Rows.Single(row =>
        Convert.ToString(row.Values[1]) == "PostgreSQL temporal");
    Assert(
        TableCellValueConverter.Format(dateColumn, inserted.Values[dateColumn.Ordinal]) == "0001-01-01" &&
        Convert.ToString(inserted.Values[temporalColumns["local_timestamp"].Ordinal]) ==
            "0001-01-01T00:00:00.123" &&
        Convert.ToString(inserted.Values[temporalColumns["precise_timestamp"].Ordinal]) ==
            "2026-08-30T12:34:56.123456" &&
        Convert.ToString(inserted.Values[temporalColumns["utc_timestamp"].Ordinal]) ==
            "2026-08-30T04:34:56.1234Z" &&
        Convert.ToString(inserted.Values[temporalColumns["local_clock"].Ordinal]) == "24:00:00",
        "PostgreSQL temporal 新增後未保留宣告精度、24:00 或 UTC canonical 格式");

    var invalidValues = new[]
    {
        (Column: "event_date", Value: "2026-08-30T12:34:56.0000000"),
        (Column: "local_timestamp", Value: "2026-08-30T12:34:56.1234"),
        (Column: "local_timestamp", Value: "2026-08-30T12:34:56.123+08:00"),
        (Column: "precise_timestamp", Value: "2026-08-30T12:34:56.1234567"),
        (Column: "utc_timestamp", Value: "2026-08-30T12:34:56.1234"),
        (Column: "utc_timestamp", Value: "2026-08-30T12:34:56.12345Z"),
        (Column: "local_clock", Value: "23:59:59.123"),
        (Column: "local_clock", Value: "24:00:00.01"),
        (Column: "local_clock", Value: "25:00:00"),
        (Column: "alarm", Value: "12:34:56.1234567+08:00")
    };
    foreach (var invalid in invalidValues)
    {
        await AssertThrowsAsync<InvalidOperationException>(() => session.InsertTableRowAsync(
            database,
            table,
            new[]
            {
                new TableCellInput("name", TableCellInputMode.Value, "Rejected PostgreSQL temporal"),
                new TableCellInput(invalid.Column, TableCellInputMode.Value, invalid.Value)
            }));
    }
    var rejectedSnapshot = await session.LoadTableDataAsync(database, table);
    Assert(
        rejectedSnapshot.Rows.All(row => Convert.ToString(row.Values[1]) != "Rejected PostgreSQL temporal"),
        "PostgreSQL temporal 錯誤／會取整的輸入不可留下半筆資料");

    await session.UpdateTableRowAsync(
        database,
        table,
        inserted,
        new[]
        {
            new TableCellInput("event_date", TableCellInputMode.Value, "9999-12-31"),
            new TableCellInput("local_timestamp", TableCellInputMode.Value, "9999-12-31T23:59:59.999"),
            new TableCellInput("precise_timestamp", TableCellInputMode.Value, "9999-12-31T23:59:59.999999"),
            new TableCellInput("utc_timestamp", TableCellInputMode.Value, "2026-08-30T01:02:03.4567Z"),
            new TableCellInput("local_clock", TableCellInputMode.Value, "00:00:00.01")
        });
    var updatedSnapshot = await session.LoadTableDataAsync(database, table);
    var updated = updatedSnapshot.Rows.Single(row =>
        Convert.ToString(row.Values[1]) == "PostgreSQL temporal");
    Assert(
        TableCellValueConverter.Format(dateColumn, updated.Values[dateColumn.Ordinal]) == "9999-12-31" &&
        Convert.ToString(updated.Values[temporalColumns["local_timestamp"].Ordinal]) ==
            "9999-12-31T23:59:59.999" &&
        Convert.ToString(updated.Values[temporalColumns["precise_timestamp"].Ordinal]) ==
            "9999-12-31T23:59:59.999999" &&
        Convert.ToString(updated.Values[temporalColumns["utc_timestamp"].Ordinal]) ==
            "2026-08-30T01:02:03.4567Z" &&
        Convert.ToString(updated.Values[temporalColumns["local_clock"].Ordinal]) == "00:00:00.01",
        "PostgreSQL temporal 修改後未保留宣告精度與 UTC canonical 格式");

    var id = Convert.ToInt64(updated.Values[0], CultureInfo.InvariantCulture);
    await session.ExecuteAsync(
        database,
        $"UPDATE public.sample SET local_timestamp = TIMESTAMP '2026-08-30 12:34:56.789' WHERE id = {id};");
    await AssertThrowsAsync<TableDataConflictException>(() => session.UpdateTableRowAsync(
        database,
        table,
        updated,
        new[] { new TableCellInput("quantity", TableCellInputMode.Value, "99") }));
    var concurrentSnapshot = await session.LoadTableDataAsync(database, table);
    var concurrent = concurrentSnapshot.Rows.Single(row =>
        Convert.ToInt64(row.Values[0], CultureInfo.InvariantCulture) == id);
    Assert(
        Convert.ToString(concurrent.Values[temporalColumns["local_timestamp"].Ordinal]) ==
            "2026-08-30T12:34:56.789" &&
        Convert.ToInt32(concurrent.Values[2], CultureInfo.InvariantCulture) != 99,
        "PostgreSQL temporal 原值變更時 optimistic concurrency 不可覆寫外部資料");

    await session.DeleteTableRowAsync(database, table, concurrent);
    var afterDelete = await session.LoadTableDataAsync(database, table);
    Assert(
        afterDelete.Rows.All(row => Convert.ToInt64(row.Values[0], CultureInfo.InvariantCulture) != id),
        "PostgreSQL temporal 安全刪除失敗");
}

static async Task VerifyPostgreSqlIntervalRestrictionsAsync(
    IDatabaseSession session,
    string database,
    DatabaseObjectInfo table)
{
    var before = await session.LoadTableDataAsync(database, table);
    var expectedTypes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["duration"] = "interval",
        ["duration_ms"] = "interval(3)",
        ["duration_year"] = "interval year",
        ["duration_month"] = "interval year to month",
        ["duration_day"] = "interval day",
        ["duration_hour"] = "interval day to hour",
        ["duration_minute"] = "interval day to minute",
        ["duration_second"] = "interval day to second(4)"
    };
    var intervalColumns = before.Columns
        .Where(column => column.ValueKind == TableColumnValueKind.Interval)
        .ToDictionary(column => column.Name, StringComparer.Ordinal);
    Assert(
        intervalColumns.Count == expectedTypes.Count,
        $"PostgreSQL interval metadata 未完整辨識；actual={intervalColumns.Count}");
    foreach (var expected in expectedTypes)
    {
        Assert(
            intervalColumns.TryGetValue(expected.Key, out var column) &&
            column.IsEditable &&
            column.StorageDataTypeName == expected.Value,
            $"PostgreSQL {expected.Key} metadata 不正確；actual={column?.StorageDataTypeName}");
    }

    await session.InsertTableRowAsync(
        database,
        table,
        new[]
        {
            new TableCellInput("name", TableCellInputMode.Value, "PostgreSQL constrained interval"),
            new TableCellInput("duration_ms", TableCellInputMode.Value, "months=14;days=3;microseconds=14706123000"),
            new TableCellInput("duration_year", TableCellInputMode.Value, "months=24;days=0;microseconds=0"),
            new TableCellInput("duration_month", TableCellInputMode.Value, "months=25;days=0;microseconds=0"),
            new TableCellInput("duration_day", TableCellInputMode.Value, "months=25;days=3;microseconds=0"),
            new TableCellInput("duration_hour", TableCellInputMode.Value, "months=25;days=3;microseconds=90000000000"),
            new TableCellInput("duration_minute", TableCellInputMode.Value, "months=25;days=3;microseconds=90060000000"),
            new TableCellInput("duration_second", TableCellInputMode.Value, "months=25;days=3;microseconds=90060123400")
        });

    var insertedSnapshot = await session.LoadTableDataAsync(database, table);
    var inserted = insertedSnapshot.Rows.Single(row =>
        Convert.ToString(row.Values[1]) == "PostgreSQL constrained interval");
    foreach (var expected in new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["duration_ms"] = "months=14;days=3;microseconds=14706123000",
        ["duration_year"] = "months=24;days=0;microseconds=0",
        ["duration_month"] = "months=25;days=0;microseconds=0",
        ["duration_day"] = "months=25;days=3;microseconds=0",
        ["duration_hour"] = "months=25;days=3;microseconds=90000000000",
        ["duration_minute"] = "months=25;days=3;microseconds=90060000000",
        ["duration_second"] = "months=25;days=3;microseconds=90060123400"
    })
    {
        Assert(
            Convert.ToString(inserted.Values[intervalColumns[expected.Key].Ordinal]) == expected.Value,
            $"PostgreSQL {expected.Key} 新增後未保留 interval 分量");
    }

    var invalidValues = new[]
    {
        (Column: "duration_ms", Value: "months=14;days=3;microseconds=14706123456"),
        (Column: "duration_year", Value: "months=25;days=0;microseconds=0"),
        (Column: "duration_month", Value: "months=25;days=1;microseconds=0"),
        (Column: "duration_day", Value: "months=25;days=3;microseconds=86400000000"),
        (Column: "duration_hour", Value: "months=25;days=3;microseconds=90060000000"),
        (Column: "duration_minute", Value: "months=25;days=3;microseconds=90060100000"),
        (Column: "duration_second", Value: "months=25;days=3;microseconds=90060123456")
    };
    foreach (var invalid in invalidValues)
    {
        await AssertThrowsAsync<InvalidOperationException>(() => session.UpdateTableRowAsync(
            database,
            table,
            inserted,
            new[] { new TableCellInput(invalid.Column, TableCellInputMode.Value, invalid.Value) }));
    }

    var rejectedSnapshot = await session.LoadTableDataAsync(database, table);
    var unchanged = rejectedSnapshot.Rows.Single(row =>
        Convert.ToString(row.Values[1]) == "PostgreSQL constrained interval");
    Assert(
        Convert.ToString(unchanged.Values[intervalColumns["duration_ms"].Ordinal]) ==
            "months=14;days=3;microseconds=14706123000",
        "PostgreSQL 不可把受限 interval 的無效輸入取整後寫入");
    await session.DeleteTableRowAsync(database, table, unchanged);
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
        await session.ExecuteAsync(database, "CREATE TYPE dbo.fixed_token FROM binary(4) NULL;");
        await session.ExecuteAsync(database, "CREATE TYPE dbo.ansi_code FROM varchar(6) NULL;");
        await session.ExecuteAsync(database, "CREATE TABLE dbo.sample (id INT IDENTITY PRIMARY KEY, name NVARCHAR(40) NOT NULL, quantity INT NULL, note NVARCHAR(80) NULL, payload VARBINARY(MAX) NULL, fixed_payload BINARY(3) NULL, alias_fixed_payload dbo.fixed_token NULL, document XML NULL, legacy_text TEXT COLLATE SQL_Latin1_General_CP1_CI_AS NULL, legacy_ntext NTEXT NULL, legacy_image IMAGE NULL, high_precision DECIMAL(38,20) NULL, alias_label dbo.short_label NULL, alias_count dbo.positive_count NULL, alias_amount dbo.precise_amount NULL, system_name sysname NULL, account_balance MONEY NULL, petty_cash SMALLMONEY NULL, tiny_value TINYINT NULL, small_value SMALLINT NULL, integer_value INT NULL, big_value BIGINT NULL, single_value REAL NULL, compact_float FLOAT(10) NULL, double_value FLOAT(53) NULL, event_date DATE NULL, legacy_time DATETIME NULL, minute_time SMALLDATETIME NULL, millisecond_time DATETIME2(3) NULL, precise_time DATETIME2(7) NULL, offset_time DATETIMEOFFSET(3) NULL, clock_time TIME(4) NULL, node_path hierarchyid NULL, variant_value sql_variant NULL, variant_text sql_variant NULL, variant_temporal sql_variant NULL, shape geometry NULL, location geography NULL, ansi_text VARCHAR(8) COLLATE SQL_Latin1_General_CP1_CI_AS NULL, ansi_fixed CHAR(4) COLLATE SQL_Latin1_General_CP1_CI_AS NULL, utf8_text VARCHAR(8) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL, unicode_text NVARCHAR(4) COLLATE Latin1_General_100_CI_AS_SC NULL, alias_ansi dbo.ansi_code NULL);");
        var insert = await session.ExecuteAsync(database, "INSERT INTO dbo.sample (name) VALUES (N'Punky'), (N'Linux/macOS');");
        Assert(insert.RowsAffected == 2, "SQL Server INSERT 影響列數應為 2");
        await session.ExecuteAsync(
            database,
            """
            UPDATE dbo.sample
            SET variant_value = CAST(123.450000 AS decimal(18,6)),
                variant_text = CAST(N'Seed:文字  ' AS nvarchar(30)) COLLATE Latin1_General_100_BIN2,
                variant_temporal = CAST('2026-08-30T12:34:56.789' AS datetime2(3))
            WHERE name = N'Punky';
            UPDATE dbo.sample
            SET variant_value = CAST(987654321.12345678 AS numeric(20,8)),
                variant_text = CAST('ASCII seed' AS varchar(20)),
                variant_temporal = CAST(0x00FF10 AS varbinary(4))
            WHERE name = N'Linux/macOS';
            """);

        var result = await session.ExecuteAsync(database, "SELECT id, name FROM dbo.sample ORDER BY id;");
        Assert(result.Rows.Count == 2 && Convert.ToString(result.Rows[1][1]) == "Linux/macOS", "SQL Server 查詢結果不正確");
        var objects = await session.GetObjectsAsync(database);
        var table = objects.SingleOrDefault(item => item.Name == "sample" && item.Schema == "dbo");
        Assert(table is not null && table.Kind == DatabaseObjectKind.Table, "SQL Server metadata 找不到 dbo.sample");
        Assert(session.BuildSelectPreview(table!) == "SELECT TOP (200) * FROM [dbo].[sample];", "SQL Server 實機預覽 SQL 不正確");
        await VerifySqlServerStringTypesAsync(session, database, table!);
        await VerifySafeTableEditingAsync(
            session,
            database,
            table!,
            id => $"UPDATE dbo.sample SET name = N'Concurrent' WHERE id = {id};");
        await VerifyFixedLengthBinaryAsync(session, database, table!);
        await VerifyFloatingPointTypesAsync(
            session,
            database,
            table!,
            id => $"UPDATE dbo.sample SET single_value = 2.5 WHERE id = {id};");
        await VerifyIntegerTypesAsync(
            session,
            database,
            table!,
            id => $"UPDATE dbo.sample SET integer_value = 1 WHERE id = {id};");
        await VerifySqlServerMoneyAsync(session, database, table!);
        await VerifySqlServerTemporalTypesAsync(session, database, table!);
    }
    finally
    {
        await session.ExecuteAsync(
            "master",
            $"IF DB_ID(N'{database}') IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END");
    }
}

static async Task VerifySqlServerStringTypesAsync(
    IDatabaseSession session,
    string database,
    DatabaseObjectInfo table)
{
    var before = await session.LoadTableDataAsync(database, table);
    var ansiTextColumn = before.Columns.Single(column => column.Name == "ansi_text");
    var ansiFixedColumn = before.Columns.Single(column => column.Name == "ansi_fixed");
    var utf8TextColumn = before.Columns.Single(column => column.Name == "utf8_text");
    var unicodeTextColumn = before.Columns.Single(column => column.Name == "unicode_text");
    var aliasAnsiColumn = before.Columns.Single(column => column.Name == "alias_ansi");
    var legacyTextColumn = before.Columns.Single(column => column.Name == "legacy_text");

    Assert(
        ansiTextColumn.ValueKind == TableColumnValueKind.String &&
        ansiTextColumn.StorageDataTypeName == "varchar(8)" &&
        ansiTextColumn.MaximumStringLengthInBytes == 8 &&
        ansiTextColumn.StorageCollationName == "SQL_Latin1_General_CP1_CI_AS",
        $"SQL Server varchar length／collation metadata 不正確；actual={ansiTextColumn}");
    Assert(
        ansiFixedColumn.StorageDataTypeName == "char(4)" &&
        ansiFixedColumn.MaximumStringLengthInBytes == 4 &&
        ansiFixedColumn.StorageCollationName == "SQL_Latin1_General_CP1_CI_AS",
        $"SQL Server char length／collation metadata 不正確；actual={ansiFixedColumn}");
    Assert(
        utf8TextColumn.StorageDataTypeName == "varchar(8)" &&
        utf8TextColumn.MaximumStringLengthInBytes == 8 &&
        utf8TextColumn.StorageCollationName == "Latin1_General_100_CI_AS_SC_UTF8",
        $"SQL Server UTF-8 varchar metadata 不正確；actual={utf8TextColumn}");
    Assert(
        unicodeTextColumn.StorageDataTypeName == "nvarchar(4)" &&
        unicodeTextColumn.MaximumStringLengthInBytes == 8 &&
        unicodeTextColumn.StorageCollationName == "Latin1_General_100_CI_AS_SC",
        $"SQL Server nvarchar byte length metadata 不正確；actual={unicodeTextColumn}");
    Assert(
        aliasAnsiColumn.DataTypeName == "[dbo].[ansi_code] (varchar(6))" &&
        aliasAnsiColumn.StorageDataTypeName == "varchar(6)" &&
        aliasAnsiColumn.MaximumStringLengthInBytes == 6 &&
        !string.IsNullOrWhiteSpace(aliasAnsiColumn.StorageCollationName),
        $"SQL Server varchar alias metadata 不正確；actual={aliasAnsiColumn}");
    Assert(
        legacyTextColumn.StorageCollationName == "SQL_Latin1_General_CP1_CI_AS",
        $"SQL Server text collation metadata 不正確；actual={legacyTextColumn}");

    await session.ExecuteAsync(
        database,
        "INSERT INTO dbo.sample (name, ansi_text) VALUES (N'Native ANSI loss', N'漢字');");
    var nativeLoss = await session.ExecuteAsync(
        database,
        "SELECT ansi_text FROM dbo.sample WHERE name = N'Native ANSI loss';");
    Assert(
        Convert.ToString(nativeLoss.Rows.Single()[0]) == "??",
        "SQL Server 實機前提改變：legacy code-page varchar 不再把不可表示 Unicode 靜默替換為問號");
    await session.ExecuteAsync(database, "DELETE FROM dbo.sample WHERE name = N'Native ANSI loss';");

    var rejectedCases = new (string Name, string ColumnName, string Value)[]
    {
        ("Rejected ANSI encoding", "ansi_text", "漢字"),
        ("Rejected ANSI bytes", "ansi_text", "123456789"),
        ("Rejected UTF8 bytes", "utf8_text", "台灣人"),
        ("Rejected Unicode bytes", "unicode_text", "🐧台灣A"),
        ("Rejected alias encoding", "alias_ansi", "漢字"),
        ("Rejected legacy text encoding", "legacy_text", "漢字")
    };
    foreach (var rejectedCase in rejectedCases)
    {
        await AssertThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => session.InsertTableRowAsync(
            database,
            table,
            new[]
            {
                new TableCellInput("name", TableCellInputMode.Value, rejectedCase.Name),
                new TableCellInput(rejectedCase.ColumnName, TableCellInputMode.Value, rejectedCase.Value)
            }));
        var rejectedSnapshot = await session.LoadTableDataAsync(database, table);
        Assert(
            rejectedSnapshot.Rows.All(row => Convert.ToString(row.Values[1]) != rejectedCase.Name),
            $"SQL Server {rejectedCase.ColumnName} 無損驗證拒絕時不可留下半筆資料");
    }

    await session.InsertTableRowAsync(
        database,
        table,
        new[]
        {
            new TableCellInput("name", TableCellInputMode.Value, "String safety"),
            new TableCellInput("ansi_text", TableCellInputMode.Value, "ABCDEFGH"),
            new TableCellInput("ansi_fixed", TableCellInputMode.Value, "AB"),
            new TableCellInput("utf8_text", TableCellInputMode.Value, "台灣"),
            new TableCellInput("unicode_text", TableCellInputMode.Value, "🐧台灣"),
            new TableCellInput("alias_ansi", TableCellInputMode.Value, "CODE6"),
            new TableCellInput("legacy_text", TableCellInputMode.Value, "legacy ASCII")
        });
    var insertedNative = await session.ExecuteAsync(
        database,
        "SELECT ansi_text, DATALENGTH(ansi_text), ansi_fixed, DATALENGTH(ansi_fixed), " +
        "utf8_text, DATALENGTH(utf8_text), unicode_text, DATALENGTH(unicode_text), " +
        "alias_ansi, DATALENGTH(alias_ansi), CONVERT(varchar(max), legacy_text) " +
        "FROM dbo.sample WHERE name = N'String safety';");
    var insertedValues = insertedNative.Rows.Single();
    Assert(
        Convert.ToString(insertedValues[0]) == "ABCDEFGH" && Convert.ToInt32(insertedValues[1]) == 8 &&
        Convert.ToString(insertedValues[2]) == "AB  " && Convert.ToInt32(insertedValues[3]) == 4 &&
        Convert.ToString(insertedValues[4]) == "台灣" && Convert.ToInt32(insertedValues[5]) == 6 &&
        Convert.ToString(insertedValues[6]) == "🐧台灣" && Convert.ToInt32(insertedValues[7]) == 8 &&
        Convert.ToString(insertedValues[8]) == "CODE6" && Convert.ToInt32(insertedValues[9]) == 5 &&
        Convert.ToString(insertedValues[10]) == "legacy ASCII",
        "SQL Server 一般 ANSI／Unicode 字串合法邊界新增未完整保存");

    var insertedSnapshot = await session.LoadTableDataAsync(database, table);
    var inserted = insertedSnapshot.Rows.Single(row => Convert.ToString(row.Values[1]) == "String safety");
    await session.UpdateTableRowAsync(
        database,
        table,
        inserted,
        new[]
        {
            new TableCellInput("ansi_text", TableCellInputMode.Value, "87654321"),
            new TableCellInput("ansi_fixed", TableCellInputMode.Value, "WXYZ"),
            new TableCellInput("utf8_text", TableCellInputMode.Value, "海龜"),
            new TableCellInput("unicode_text", TableCellInputMode.Value, "🍎臺北"),
            new TableCellInput("alias_ansi", TableCellInputMode.Value, "A1B2C3"),
            new TableCellInput("legacy_text", TableCellInputMode.Value, "updated ASCII")
        });
    var updatedNative = await session.ExecuteAsync(
        database,
        "SELECT ansi_text, ansi_fixed, utf8_text, DATALENGTH(utf8_text), unicode_text, " +
        "DATALENGTH(unicode_text), alias_ansi, CONVERT(varchar(max), legacy_text) " +
        "FROM dbo.sample WHERE name = N'String safety';");
    var updatedValues = updatedNative.Rows.Single();
    Assert(
        Convert.ToString(updatedValues[0]) == "87654321" &&
        Convert.ToString(updatedValues[1]) == "WXYZ" &&
        Convert.ToString(updatedValues[2]) == "海龜" && Convert.ToInt32(updatedValues[3]) == 6 &&
        Convert.ToString(updatedValues[4]) == "🍎臺北" && Convert.ToInt32(updatedValues[5]) == 8 &&
        Convert.ToString(updatedValues[6]) == "A1B2C3" &&
        Convert.ToString(updatedValues[7]) == "updated ASCII",
        "SQL Server 一般 ANSI／Unicode 字串安全修改未完整保存");

    var updatedSnapshot = await session.LoadTableDataAsync(database, table);
    var updated = updatedSnapshot.Rows.Single(row => Convert.ToString(row.Values[1]) == "String safety");
    await AssertThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => session.UpdateTableRowAsync(
        database,
        table,
        updated,
        new[] { new TableCellInput("ansi_text", TableCellInputMode.Value, "漢字") }));
    var unchanged = await session.ExecuteAsync(
        database,
        "SELECT ansi_text FROM dbo.sample WHERE name = N'String safety';");
    Assert(
        Convert.ToString(unchanged.Rows.Single()[0]) == "87654321",
        "SQL Server ANSI 字串修改被拒後必須回復原值");

    await session.InsertTableRowAsync(
        database,
        table,
        new[]
        {
            new TableCellInput("name", TableCellInputMode.Value, "String trailing conflict"),
            new TableCellInput("ansi_text", TableCellInputMode.Value, "edge  ")
        });
    var conflictSnapshot = await session.LoadTableDataAsync(database, table);
    var staleConflictRow = conflictSnapshot.Rows.Single(row =>
        Convert.ToString(row.Values[1]) == "String trailing conflict");
    await session.ExecuteAsync(
        database,
        "UPDATE dbo.sample SET ansi_text = 'edge   ' WHERE name = N'String trailing conflict';");
    await AssertThrowsAsync<TableDataConflictException>(() => session.UpdateTableRowAsync(
        database,
        table,
        staleConflictRow,
        new[] { new TableCellInput("note", TableCellInputMode.Value, "must not land") }));
    var conflictNative = await session.ExecuteAsync(
        database,
        "SELECT DATALENGTH(ansi_text), note FROM dbo.sample WHERE name = N'String trailing conflict';");
    Assert(
        Convert.ToInt32(conflictNative.Rows.Single()[0]) == 7 &&
        conflictNative.Rows.Single()[1] is null or DBNull,
        "SQL Server varchar 只改變尾端空白時仍必須被 optimistic concurrency 偵測");
    var currentConflictSnapshot = await session.LoadTableDataAsync(database, table);
    var currentConflictRow = currentConflictSnapshot.Rows.Single(row =>
        Convert.ToString(row.Values[1]) == "String trailing conflict");
    await session.DeleteTableRowAsync(database, table, currentConflictRow);
    await session.DeleteTableRowAsync(database, table, updated);
}

static async Task VerifySqlServerMoneyAsync(
    IDatabaseSession session,
    string database,
    DatabaseObjectInfo table)
{
    var before = await session.LoadTableDataAsync(database, table);
    var moneyColumns = before.Columns
        .Where(column => column.ValueKind == TableColumnValueKind.SqlServerMoney)
        .ToDictionary(column => column.Name, StringComparer.Ordinal);
    Assert(
        moneyColumns.Count == 2 &&
        moneyColumns["account_balance"].StorageDataTypeName == "money" &&
        moneyColumns["petty_cash"].StorageDataTypeName == "smallmoney" &&
        moneyColumns.Values.All(column => column.IsEditable),
        "SQL Server money／smallmoney metadata 應映射為可安全編輯的專用型別");

    await session.InsertTableRowAsync(
        database,
        table,
        new[]
        {
            new TableCellInput("name", TableCellInputMode.Value, "SQL money"),
            new TableCellInput("account_balance", TableCellInputMode.Value, "922337203685477.5807"),
            new TableCellInput("petty_cash", TableCellInputMode.Value, "214748.3647")
        });
    var insertedSnapshot = await session.LoadTableDataAsync(database, table);
    var inserted = insertedSnapshot.Rows.Single(row => Convert.ToString(row.Values[1]) == "SQL money");
    Assert(
        Convert.ToString(inserted.Values[moneyColumns["account_balance"].Ordinal]) == "922337203685477.5807" &&
        Convert.ToString(inserted.Values[moneyColumns["petty_cash"].Ordinal]) == "214748.3647",
        "SQL Server money／smallmoney 應無損保留正值上界 canonical 文字");

    await session.UpdateTableRowAsync(
        database,
        table,
        inserted,
        new[]
        {
            new TableCellInput("account_balance", TableCellInputMode.Value, "-922337203685477.5808"),
            new TableCellInput("petty_cash", TableCellInputMode.Value, "-214748.3648")
        });
    var updatedSnapshot = await session.LoadTableDataAsync(database, table);
    var updated = updatedSnapshot.Rows.Single(row => Convert.ToString(row.Values[1]) == "SQL money");
    Assert(
        Convert.ToString(updated.Values[moneyColumns["account_balance"].Ordinal]) == "-922337203685477.5808" &&
        Convert.ToString(updated.Values[moneyColumns["petty_cash"].Ordinal]) == "-214748.3648",
        "SQL Server money／smallmoney 應無損保留負值下界 canonical 文字");

    foreach (var invalid in new[]
             {
                 (Column: "account_balance", Value: "1.23455"),
                 (Column: "account_balance", Value: "922337203685477.5808"),
                 (Column: "petty_cash", Value: "214748.3648"),
                 (Column: "petty_cash", Value: "1,234.56")
             })
    {
        await AssertThrowsAsync<InvalidOperationException>(() => session.UpdateTableRowAsync(
            database,
            table,
            updated,
            new[] { new TableCellInput(invalid.Column, TableCellInputMode.Value, invalid.Value) }));
    }
    var rejectedSnapshot = await session.LoadTableDataAsync(database, table);
    var unchanged = rejectedSnapshot.Rows.Single(row => Convert.ToString(row.Values[1]) == "SQL money");
    Assert(
        Convert.ToString(unchanged.Values[moneyColumns["account_balance"].Ordinal]) == "-922337203685477.5808" &&
        Convert.ToString(unchanged.Values[moneyColumns["petty_cash"].Ordinal]) == "-214748.3648",
        "SQL Server money／smallmoney 不可把錯誤或需取整的輸入寫入");

    var id = Convert.ToInt64(unchanged.Values[0], CultureInfo.InvariantCulture);
    await session.ExecuteAsync(
        database,
        $"UPDATE dbo.sample SET account_balance = CAST(1.2345 AS money) WHERE id = {id};");
    await AssertThrowsAsync<TableDataConflictException>(() => session.UpdateTableRowAsync(
        database,
        table,
        unchanged,
        new[] { new TableCellInput("quantity", TableCellInputMode.Value, "77") }));
    var concurrentSnapshot = await session.LoadTableDataAsync(database, table);
    var concurrent = concurrentSnapshot.Rows.Single(row =>
        Convert.ToInt64(row.Values[0], CultureInfo.InvariantCulture) == id);
    Assert(
        Convert.ToString(concurrent.Values[moneyColumns["account_balance"].Ordinal]) == "1.2345" &&
        Convert.ToInt32(concurrent.Values[2], CultureInfo.InvariantCulture) != 77,
        "SQL Server money 原值變更時 optimistic concurrency 不可覆寫外部資料");

    await session.DeleteTableRowAsync(database, table, concurrent);
    var afterDelete = await session.LoadTableDataAsync(database, table);
    Assert(
        afterDelete.Rows.All(row => Convert.ToInt64(row.Values[0], CultureInfo.InvariantCulture) != id),
        "SQL Server money 安全刪除失敗");
}

static async Task VerifySqlServerTemporalTypesAsync(
    IDatabaseSession session,
    string database,
    DatabaseObjectInfo table)
{
    var before = await session.LoadTableDataAsync(database, table);
    var temporalColumns = before.Columns
        .Where(column => column.ValueKind == TableColumnValueKind.SqlServerTemporal)
        .ToDictionary(column => column.Name, StringComparer.Ordinal);
    var expectedTypes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["event_date"] = "date",
        ["legacy_time"] = "datetime",
        ["minute_time"] = "smalldatetime",
        ["millisecond_time"] = "datetime2(3)",
        ["precise_time"] = "datetime2(7)",
        ["offset_time"] = "datetimeoffset(3)",
        ["clock_time"] = "time(4)"
    };
    Assert(
        temporalColumns.Count == expectedTypes.Count,
        $"SQL Server temporal metadata 未完整辨識；actual={temporalColumns.Count}");
    foreach (var expected in expectedTypes)
    {
        Assert(
            temporalColumns.TryGetValue(expected.Key, out var column) &&
            column.IsEditable &&
            column.StorageDataTypeName == expected.Value,
            $"SQL Server {expected.Key} metadata 不正確；actual={column?.StorageDataTypeName}");
    }

    await session.InsertTableRowAsync(
        database,
        table,
        new[]
        {
            new TableCellInput("name", TableCellInputMode.Value, "SQL temporal"),
            new TableCellInput("event_date", TableCellInputMode.Value, "0001-01-01"),
            new TableCellInput("legacy_time", TableCellInputMode.Value, "1753-01-01T00:00:00.000"),
            new TableCellInput("minute_time", TableCellInputMode.Value, "2079-06-06T23:59:00"),
            new TableCellInput("millisecond_time", TableCellInputMode.Value, "0001-01-01T00:00:00.123"),
            new TableCellInput("precise_time", TableCellInputMode.Value, "2026-08-30T12:34:56.1234567"),
            new TableCellInput("offset_time", TableCellInputMode.Value, "2026-08-30T12:34:56.789+14:00"),
            new TableCellInput("clock_time", TableCellInputMode.Value, "23:59:59.1234")
        });

    var insertedSnapshot = await session.LoadTableDataAsync(database, table);
    var inserted = insertedSnapshot.Rows.Single(row => Convert.ToString(row.Values[1]) == "SQL temporal");
    Assert(
        Convert.ToDateTime(inserted.Values[temporalColumns["event_date"].Ordinal], CultureInfo.InvariantCulture) ==
        new DateTime(1, 1, 1),
        "SQL Server date 應保留 0001 年下界");
    Assert(
        Convert.ToDateTime(inserted.Values[temporalColumns["millisecond_time"].Ordinal], CultureInfo.InvariantCulture) ==
        new DateTime(1, 1, 1).AddMilliseconds(123),
        "SQL Server datetime2(3) 不可被 legacy datetime 的 1753 下界或 3.33ms 精度截斷");
    Assert(
        Convert.ToDateTime(inserted.Values[temporalColumns["precise_time"].Ordinal], CultureInfo.InvariantCulture).Ticks %
        TimeSpan.TicksPerSecond == 1_234_567,
        "SQL Server datetime2(7) 應保留 100ns 精度");
    Assert(
        inserted.Values[temporalColumns["offset_time"].Ordinal] is DateTimeOffset insertedOffset &&
        insertedOffset.Offset == TimeSpan.FromHours(14) &&
        insertedOffset.Ticks % TimeSpan.TicksPerSecond == 7_890_000,
        "SQL Server datetimeoffset(3) 應保留毫秒與 +14:00 offset");
    Assert(
        inserted.Values[temporalColumns["clock_time"].Ordinal] is TimeSpan insertedTime &&
        insertedTime == new TimeSpan(0, 23, 59, 59, 123).Add(TimeSpan.FromTicks(4_000)),
        "SQL Server time(4) 應保留四位小數秒");

    var invalidValues = new[]
    {
        (Column: "event_date", Value: "2026-08-30T12:00:00"),
        (Column: "legacy_time", Value: "1752-12-31T23:59:59"),
        (Column: "legacy_time", Value: "2026-08-30T12:34:56.002"),
        (Column: "minute_time", Value: "2026-08-30T12:34:30"),
        (Column: "minute_time", Value: "2080-01-01T00:00:00"),
        (Column: "millisecond_time", Value: "2026-08-30T12:34:56.1234"),
        (Column: "offset_time", Value: "2026-08-30T12:34:56.789"),
        (Column: "offset_time", Value: "2026-08-30T12:34:56.7891+08:00"),
        (Column: "clock_time", Value: "23:59:59.12345"),
        (Column: "clock_time", Value: "1.00:00:00")
    };
    foreach (var invalid in invalidValues)
    {
        await AssertThrowsAsync<InvalidOperationException>(() => session.InsertTableRowAsync(
            database,
            table,
            new[]
            {
                new TableCellInput("name", TableCellInputMode.Value, "Rejected SQL temporal"),
                new TableCellInput(invalid.Column, TableCellInputMode.Value, invalid.Value)
            }));
    }
    var rejectedSnapshot = await session.LoadTableDataAsync(database, table);
    Assert(
        rejectedSnapshot.Rows.All(row => Convert.ToString(row.Values[1]) != "Rejected SQL temporal"),
        "SQL Server temporal 錯誤／會取整的輸入不可留下半筆資料");

    await session.UpdateTableRowAsync(
        database,
        table,
        inserted,
        new[]
        {
            new TableCellInput("event_date", TableCellInputMode.Value, "9999-12-31"),
            new TableCellInput("legacy_time", TableCellInputMode.Value, "9999-12-31T23:59:59.997"),
            new TableCellInput("minute_time", TableCellInputMode.Value, "1900-01-01T00:00:00"),
            new TableCellInput("millisecond_time", TableCellInputMode.Value, "9999-12-31T23:59:59.999"),
            new TableCellInput("precise_time", TableCellInputMode.Value, "9999-12-31T23:59:59.9999999"),
            new TableCellInput("offset_time", TableCellInputMode.Value, "2026-08-30T01:02:03.456Z"),
            new TableCellInput("clock_time", TableCellInputMode.Value, "00:00:00.0001")
        });
    var updatedSnapshot = await session.LoadTableDataAsync(database, table);
    var updated = updatedSnapshot.Rows.Single(row => Convert.ToString(row.Values[1]) == "SQL temporal");
    Assert(
        Convert.ToDateTime(updated.Values[temporalColumns["event_date"].Ordinal], CultureInfo.InvariantCulture) ==
        new DateTime(9999, 12, 31),
        "SQL Server date 修改後未保留 9999 年上界");
    Assert(
        updated.Values[temporalColumns["offset_time"].Ordinal] is DateTimeOffset updatedOffset &&
        updatedOffset.Offset == TimeSpan.Zero &&
        updatedOffset.Ticks % TimeSpan.TicksPerSecond == 4_560_000,
        "SQL Server datetimeoffset 修改後未保留 Z 與 millisecond");

    var id = Convert.ToInt64(updated.Values[0]);
    await session.ExecuteAsync(
        database,
        $"UPDATE dbo.sample SET millisecond_time = CAST('2026-08-30T12:34:56.789' AS datetime2(3)) WHERE id = {id};");
    await AssertThrowsAsync<TableDataConflictException>(() => session.UpdateTableRowAsync(
        database,
        table,
        updated,
        new[] { new TableCellInput("quantity", TableCellInputMode.Value, "99") }));
    var concurrentSnapshot = await session.LoadTableDataAsync(database, table);
    var concurrent = concurrentSnapshot.Rows.Single(row => Convert.ToInt64(row.Values[0]) == id);
    Assert(
        Convert.ToDateTime(
            concurrent.Values[temporalColumns["millisecond_time"].Ordinal],
            CultureInfo.InvariantCulture).Ticks % TimeSpan.TicksPerSecond == 7_890_000 &&
        Convert.ToInt32(concurrent.Values[2]) != 99,
        "SQL Server temporal 原值變更時 optimistic concurrency 不可覆寫外部資料");

    await session.DeleteTableRowAsync(database, table, concurrent);
    var afterDelete = await session.LoadTableDataAsync(database, table);
    Assert(afterDelete.Rows.All(row => Convert.ToInt64(row.Values[0]) != id), "SQL Server temporal 安全刪除失敗");
}

static async Task VerifyIntegerTypesAsync(
    IDatabaseSession session,
    string database,
    DatabaseObjectInfo table,
    Func<long, string> buildConcurrentUpdateSql)
{
    if (session.Profile.Provider != DatabaseProviderKind.MySql)
    {
        await VerifyIntegerTypesCoreAsync(session, database, table, buildConcurrentUpdateSql);
        return;
    }

    var originalModeResult = await session.ExecuteAsync(database, "SELECT @@GLOBAL.sql_mode;");
    var originalMode = Convert.ToString(originalModeResult.Rows.Single()[0]) ?? string.Empty;
    await session.ExecuteAsync(database, "SET GLOBAL sql_mode = '';");
    try
    {
        var activeMode = await session.ExecuteAsync(database, "SELECT @@SESSION.sql_mode;");
        Assert(
            string.IsNullOrEmpty(Convert.ToString(activeMode.Rows.Single()[0])),
            "MySQL／MariaDB integer 測試必須實際進入 non-strict session");
        await VerifyIntegerTypesCoreAsync(session, database, table, buildConcurrentUpdateSql);
    }
    finally
    {
        var escapedMode = originalMode.Replace("'", "''", StringComparison.Ordinal);
        await session.ExecuteAsync(database, $"SET GLOBAL sql_mode = '{escapedMode}';");
    }
}

static async Task VerifyMySqlMutationWarningsRollbackAsync(
    IDatabaseSession session,
    string database,
    DatabaseObjectInfo table)
{
    await session.ExecuteAsync(
        database,
        """
        ALTER TABLE sample
            ADD bounded_text VARCHAR(5) CHARACTER SET utf8mb4 NULL,
            ADD latin_text VARCHAR(10) CHARACTER SET latin1 NULL,
            ADD bounded_payload VARBINARY(3) NULL,
            ADD tiny_text TINYTEXT CHARACTER SET utf8mb4 NULL,
            ADD tiny_payload TINYBLOB NULL;
        """);

    var originalModeResult = await session.ExecuteAsync(database, "SELECT @@GLOBAL.sql_mode;");
    var originalMode = Convert.ToString(originalModeResult.Rows.Single()[0]) ?? string.Empty;
    var originalMaxErrorResult = await session.ExecuteAsync(database, "SELECT @@GLOBAL.max_error_count;");
    var originalMaxErrorCount = Convert.ToUInt64(originalMaxErrorResult.Rows.Single()[0]);
    await session.ExecuteAsync(database, "SET GLOBAL sql_mode = ''; ");
    await session.ExecuteAsync(database, "SET GLOBAL max_error_count = 0;");
    try
    {
        var activeMode = await session.ExecuteAsync(
            database,
            "SELECT @@SESSION.sql_mode, @@SESSION.max_error_count;");
        Assert(
            string.IsNullOrEmpty(Convert.ToString(activeMode.Rows.Single()[0])) &&
            Convert.ToUInt64(activeMode.Rows.Single()[1]) == 0,
            "MySQL／MariaDB mutation diagnostics 測試必須實際進入 non-strict 且不保存 warning 明細的 session");

        var invalidMutations = new[]
        {
            (Name: "Rejected VARCHAR", Column: "bounded_text", Value: "六個中文字元"),
            (Name: "Rejected charset", Column: "latin_text", Value: "不可表示的中文字"),
            (Name: "Rejected VARBINARY", Column: "bounded_payload", Value: "0x00010203"),
            (Name: "Rejected TINYTEXT", Column: "tiny_text", Value: string.Concat(Enumerable.Repeat("🐧", 64))),
            (Name: "Rejected TINYBLOB", Column: "tiny_payload", Value: "0x" + Convert.ToHexString(new byte[256]))
        };
        foreach (var invalid in invalidMutations)
        {
            var exception = await CaptureExceptionAsync<InvalidOperationException>(() =>
                session.InsertTableRowAsync(
                    database,
                    table,
                    new[]
                    {
                        new TableCellInput("name", TableCellInputMode.Value, invalid.Name),
                        new TableCellInput(invalid.Column, TableCellInputMode.Value, invalid.Value)
                    }));
            Assert(
                exception.Message.Contains("寫入警告", StringComparison.Ordinal) &&
                exception.Message.Contains("本次新增已回復", StringComparison.Ordinal),
                $"MySQL／MariaDB 應回報並回復 {invalid.Column} 的 warning；actual={exception.Message}");
        }

        var rejected = await session.LoadTableDataAsync(database, table);
        Assert(
            invalidMutations.All(invalid =>
                rejected.Rows.All(row => Convert.ToString(row.Values[1]) != invalid.Name)),
            "MySQL／MariaDB non-strict warning 不可留下被截斷或替換的資料列");

        var validTinyText = string.Concat(Enumerable.Repeat("🐧", 63));
        var validTinyPayload = Enumerable.Range(0, byte.MaxValue)
            .Select(value => (byte)value)
            .ToArray();
        await session.InsertTableRowAsync(
            database,
            table,
            new[]
            {
                new TableCellInput("name", TableCellInputMode.Value, "Warning guard"),
                new TableCellInput("bounded_text", TableCellInputMode.Value, "繁體🐧"),
                new TableCellInput("latin_text", TableCellInputMode.Value, "ASCII"),
                new TableCellInput("bounded_payload", TableCellInputMode.Value, "0x00FF10"),
                new TableCellInput("tiny_text", TableCellInputMode.Value, validTinyText),
                new TableCellInput(
                    "tiny_payload",
                    TableCellInputMode.Value,
                    "0x" + Convert.ToHexString(validTinyPayload))
            });

        var insertedSnapshot = await session.LoadTableDataAsync(database, table);
        var inserted = insertedSnapshot.Rows.Single(row =>
            Convert.ToString(row.Values[1]) == "Warning guard");
        var boundedTextColumn = insertedSnapshot.Columns.Single(column => column.Name == "bounded_text");
        var boundedPayloadColumn = insertedSnapshot.Columns.Single(column => column.Name == "bounded_payload");
        var tinyTextColumn = insertedSnapshot.Columns.Single(column => column.Name == "tiny_text");
        var tinyPayloadColumn = insertedSnapshot.Columns.Single(column => column.Name == "tiny_payload");
        Assert(
            Convert.ToString(inserted.Values[boundedTextColumn.Ordinal]) == "繁體🐧" &&
            inserted.Values[boundedPayloadColumn.Ordinal] is byte[] boundedBytes &&
            boundedBytes.SequenceEqual(new byte[] { 0x00, 0xFF, 0x10 }) &&
            Convert.ToString(inserted.Values[tinyTextColumn.Ordinal]) == validTinyText &&
            inserted.Values[tinyPayloadColumn.Ordinal] is byte[] tinyBytes &&
            tinyBytes.SequenceEqual(validTinyPayload),
            "MySQL／MariaDB 無 warning 的邊界值應正常 commit");

        var updateException = await CaptureExceptionAsync<InvalidOperationException>(() =>
            session.UpdateTableRowAsync(
                database,
                table,
                inserted,
                new[]
                {
                    new TableCellInput("bounded_text", TableCellInputMode.Value, "abcdef")
                }));
        Assert(
            updateException.Message.Contains("本次修改已回復", StringComparison.Ordinal),
            $"MySQL／MariaDB UPDATE warning 應在 commit 前回復；actual={updateException.Message}");

        var afterRejectedUpdate = await session.LoadTableDataAsync(database, table);
        var unchanged = afterRejectedUpdate.Rows.Single(row =>
            Convert.ToString(row.Values[1]) == "Warning guard");
        Assert(
            Convert.ToString(unchanged.Values[boundedTextColumn.Ordinal]) == "繁體🐧",
            "MySQL／MariaDB UPDATE warning 不可留下被截斷的值");

        await session.UpdateTableRowAsync(
            database,
            table,
            unchanged,
            new[]
            {
                new TableCellInput("bounded_text", TableCellInputMode.Value, "台灣abc"),
                new TableCellInput("bounded_payload", TableCellInputMode.Value, "0xABCDEF")
            });
        var updatedSnapshot = await session.LoadTableDataAsync(database, table);
        var updated = updatedSnapshot.Rows.Single(row =>
            Convert.ToString(row.Values[1]) == "Warning guard");
        Assert(
            Convert.ToString(updated.Values[boundedTextColumn.Ordinal]) == "台灣abc" &&
            updated.Values[boundedPayloadColumn.Ordinal] is byte[] updatedBytes &&
            updatedBytes.SequenceEqual(new byte[] { 0xAB, 0xCD, 0xEF }),
            "MySQL／MariaDB 無 warning 的 UPDATE 應正常 commit");

        await session.DeleteTableRowAsync(database, table, updated);
        var afterDelete = await session.LoadTableDataAsync(database, table);
        Assert(
            afterDelete.Rows.All(row => Convert.ToString(row.Values[1]) != "Warning guard"),
            "MySQL／MariaDB 無 warning 的 DELETE 應正常 commit");
    }
    finally
    {
        var escapedMode = originalMode.Replace("'", "''", StringComparison.Ordinal);
        await session.ExecuteAsync(database, $"SET GLOBAL sql_mode = '{escapedMode}';");
        await session.ExecuteAsync(database, $"SET GLOBAL max_error_count = {originalMaxErrorCount};");
    }
}

static async Task VerifyMySqlEnumExactValuesAsync(
    IDatabaseSession session,
    string database,
    DatabaseObjectInfo table)
{
    var snapshot = await session.LoadTableDataAsync(database, table);
    var enumColumn = snapshot.Columns.Single(column => column.Name == "status");
    string[] expectedValues =
    {
        "draft", "published", "archived", "café", "2", string.Empty,
        "comma,value", "quote'value", "back\\slash"
    };
    Assert(
        enumColumn.ValueKind == TableColumnValueKind.String &&
        enumColumn.AllowedStringValues is not null &&
        enumColumn.AllowedStringValues.SequenceEqual(expectedValues, StringComparer.Ordinal),
        $"{session.Profile.ProviderDisplayName} ENUM metadata 應保留所有宣告成員與特殊字元");

    string[] lossyValues = { "DRAFT", "draft ", "cafe", "4" };
    foreach (var lossyValue in lossyValues)
    {
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            enumColumn,
            new TableCellInput(enumColumn.Name, TableCellInputMode.Value, lossyValue)));
    }

    await session.ExecuteAsync(
        database,
        "INSERT INTO sample (name, status) VALUES " +
        "('Native ENUM case', 'DRAFT'), " +
        "('Native ENUM spaces', 'draft '), " +
        "('Native ENUM accent', 'cafe'), " +
        "('Native ENUM numeric', '4');");
    var nativeCanonicalization = await session.ExecuteAsync(
        database,
        "SELECT name, status FROM sample WHERE name LIKE 'Native ENUM %' ORDER BY id;");
    Assert(
        nativeCanonicalization.Rows.Count == lossyValues.Length &&
        Convert.ToString(nativeCanonicalization.Rows[0][1]) == "draft" &&
        Convert.ToString(nativeCanonicalization.Rows[1][1]) == "draft" &&
        Convert.ToString(nativeCanonicalization.Rows[2][1]) == "café" &&
        Convert.ToString(nativeCanonicalization.Rows[3][1]) == "café",
        $"{session.Profile.ProviderDisplayName} 原生 ENUM 應重現無 warning 的大小寫、空白、重音與數字索引正規化");
    await session.ExecuteAsync(database, "DELETE FROM sample WHERE name LIKE 'Native ENUM %';");

    for (var index = 0; index < expectedValues.Length; index++)
    {
        await session.InsertTableRowAsync(
            database,
            table,
            new[]
            {
                new TableCellInput("name", TableCellInputMode.Value, $"Exact ENUM {index}"),
                new TableCellInput(enumColumn.Name, TableCellInputMode.Value, expectedValues[index])
            });
    }

    var exactRows = await session.ExecuteAsync(
        database,
        "SELECT status, status + 0 FROM sample WHERE name LIKE 'Exact ENUM %' ORDER BY id;");
    Assert(exactRows.Rows.Count == expectedValues.Length, "ENUM 精確值 round-trip 筆數不正確");
    for (var index = 0; index < expectedValues.Length; index++)
    {
        Assert(
            Convert.ToString(exactRows.Rows[index][0]) == expectedValues[index] &&
            Convert.ToInt32(exactRows.Rows[index][1], CultureInfo.InvariantCulture) == index + 1,
            $"{session.Profile.ProviderDisplayName} ENUM 精確值未無損保存：index={index + 1}");
    }
    await session.ExecuteAsync(database, "DELETE FROM sample WHERE name LIKE 'Exact ENUM %';");

    for (var index = 0; index < lossyValues.Length; index++)
    {
        var input = lossyValues[index];
        await AssertThrowsAsync<InvalidOperationException>(() => session.InsertTableRowAsync(
            database,
            table,
            new[]
            {
                new TableCellInput("name", TableCellInputMode.Value, $"Rejected exact ENUM {index}"),
                new TableCellInput(enumColumn.Name, TableCellInputMode.Value, input)
            }));
    }
    var rejectedCount = await session.ExecuteAsync(
        database,
        "SELECT COUNT(*) FROM sample WHERE name LIKE 'Rejected exact ENUM %';");
    Assert(
        Convert.ToInt32(rejectedCount.Rows.Single()[0], CultureInfo.InvariantCulture) == 0,
        $"{session.Profile.ProviderDisplayName} 不可寫入會被 ENUM 靜默正規化的值");
}

static async Task VerifyMySqlSetExactMembersAsync(
    IDatabaseSession session,
    string database,
    DatabaseObjectInfo table)
{
    var snapshot = await session.LoadTableDataAsync(database, table);
    var setColumn = snapshot.Columns.Single(column => column.Name == "labels");
    string[] expectedMembers = { "alpha", "Beta", "café", "2", "quote'value", "back\\slash" };
    Assert(
        setColumn.ValueKind == TableColumnValueKind.String &&
        setColumn.StringSetMembers is not null &&
        setColumn.StringSetMembers.SequenceEqual(expectedMembers, StringComparer.Ordinal),
        $"{session.Profile.ProviderDisplayName} SET metadata 應保留所有宣告成員與特殊字元");
    var ambiguousSetColumn = snapshot.Columns.Single(column => column.Name == "ambiguous_labels");
    Assert(
        ambiguousSetColumn.ValueKind == TableColumnValueKind.Unsupported &&
        !ambiguousSetColumn.IsEditable &&
        ambiguousSetColumn.StringSetMembers?.SequenceEqual(new[] { string.Empty }, StringComparer.Ordinal) == true,
        $"{session.Profile.ProviderDisplayName} 空字串 SET 成員無法與空集合區分，應保持唯讀");

    string[] lossyValues = { "ALPHA", "alpha ", "cafe", "5" };
    foreach (var lossyValue in lossyValues)
    {
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            setColumn,
            new TableCellInput(setColumn.Name, TableCellInputMode.Value, lossyValue)));
    }
    Assert(
        Convert.ToString(TableCellValueConverter.Parse(
            setColumn,
            new TableCellInput(setColumn.Name, TableCellInputMode.Value, "Beta,alpha"))) == "alpha,Beta" &&
        Convert.ToString(TableCellValueConverter.Parse(
            setColumn,
            new TableCellInput(setColumn.Name, TableCellInputMode.Value, "alpha,alpha"))) == "alpha",
        $"{session.Profile.ProviderDisplayName} SET 應依宣告順序 canonicalize 無序集合與重複成員");

    await session.ExecuteAsync(
        database,
        "INSERT INTO sample (name, labels) VALUES " +
        "('Native SET case', 'ALPHA'), " +
        "('Native SET spaces', 'alpha '), " +
        "('Native SET accent', 'cafe'), " +
        "('Native SET numeric', '5');");
    var nativeCanonicalization = await session.ExecuteAsync(
        database,
        "SELECT name, labels FROM sample WHERE name LIKE 'Native SET %' ORDER BY id;");
    Assert(
        nativeCanonicalization.Rows.Count == lossyValues.Length &&
        Convert.ToString(nativeCanonicalization.Rows[0][1]) == "alpha" &&
        Convert.ToString(nativeCanonicalization.Rows[1][1]) == "alpha" &&
        Convert.ToString(nativeCanonicalization.Rows[2][1]) == "café" &&
        Convert.ToString(nativeCanonicalization.Rows[3][1]) == "alpha,café",
        $"{session.Profile.ProviderDisplayName} 原生 SET 應重現無 warning 的大小寫、空白、重音與數字 bitmap 正規化");
    await session.ExecuteAsync(database, "DELETE FROM sample WHERE name LIKE 'Native SET %';");

    var exactValues = new[]
    {
        string.Empty,
        "alpha",
        "Beta",
        "café",
        "2",
        "quote'value",
        "back\\slash",
        "back\\slash,alpha,quote'value"
    };
    var expectedCanonicalValues = new[]
    {
        string.Empty,
        "alpha",
        "Beta",
        "café",
        "2",
        "quote'value",
        "back\\slash",
        "alpha,quote'value,back\\slash"
    };
    var expectedNumericValues = new[] { 0, 1, 2, 4, 8, 16, 32, 49 };
    for (var index = 0; index < exactValues.Length; index++)
    {
        await session.InsertTableRowAsync(
            database,
            table,
            new[]
            {
                new TableCellInput("name", TableCellInputMode.Value, $"Exact SET {index}"),
                new TableCellInput(setColumn.Name, TableCellInputMode.Value, exactValues[index])
            });
    }

    var exactRows = await session.ExecuteAsync(
        database,
        "SELECT labels, labels + 0 FROM sample WHERE name LIKE 'Exact SET %' ORDER BY id;");
    Assert(exactRows.Rows.Count == exactValues.Length, "SET 精確成員 round-trip 筆數不正確");
    for (var index = 0; index < exactValues.Length; index++)
    {
        Assert(
            Convert.ToString(exactRows.Rows[index][0]) == expectedCanonicalValues[index] &&
            Convert.ToInt32(exactRows.Rows[index][1], CultureInfo.InvariantCulture) == expectedNumericValues[index],
            $"{session.Profile.ProviderDisplayName} SET 精確成員未保存成相同集合：index={index}");
    }
    await session.ExecuteAsync(database, "DELETE FROM sample WHERE name LIKE 'Exact SET %';");

    for (var index = 0; index < lossyValues.Length; index++)
    {
        var input = lossyValues[index];
        await AssertThrowsAsync<InvalidOperationException>(() => session.InsertTableRowAsync(
            database,
            table,
            new[]
            {
                new TableCellInput("name", TableCellInputMode.Value, $"Rejected exact SET {index}"),
                new TableCellInput(setColumn.Name, TableCellInputMode.Value, input)
            }));
    }
    var rejectedCount = await session.ExecuteAsync(
        database,
        "SELECT COUNT(*) FROM sample WHERE name LIKE 'Rejected exact SET %';");
    Assert(
        Convert.ToInt32(rejectedCount.Rows.Single()[0], CultureInfo.InvariantCulture) == 0,
        $"{session.Profile.ProviderDisplayName} 不可寫入會改變所選成員的 SET 值");
}

static async Task VerifyNonRoundTrippableTrailingSpacesAsync(
    IDatabaseSession session,
    string database,
    DatabaseObjectInfo table)
{
    var before = await session.LoadTableDataAsync(database, table);
    var fixedTextColumn = before.Columns.Single(column => column.Name == "fixed_text");
    Assert(
        fixedTextColumn.ValueKind == TableColumnValueKind.String &&
        fixedTextColumn.IsEditable &&
        fixedTextColumn.TrailingSpacesAreNotRoundTrippable,
        $"{session.Profile.ProviderDisplayName} fixed CHAR metadata 應標記尾端空白無法 round-trip");
    Assert(
        !before.Columns.Single(column => column.Name == "name").TrailingSpacesAreNotRoundTrippable,
        $"{session.Profile.ProviderDisplayName} VARCHAR 不可誤標為固定長度 CHAR");
    AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
        fixedTextColumn,
        new TableCellInput("fixed_text", TableCellInputMode.Value, "AB ")));
    Assert(
        Convert.ToString(TableCellValueConverter.Parse(
            fixedTextColumn,
            new TableCellInput("fixed_text", TableCellInputMode.Value, "AB\t"))) == "AB\t" &&
        Convert.ToString(TableCellValueConverter.Parse(
            fixedTextColumn,
            new TableCellInput("fixed_text", TableCellInputMode.Value, "AB\u00A0"))) == "AB\u00A0",
        $"{session.Profile.ProviderDisplayName} fixed CHAR 只應拒絕尾端 U+0020 空白");

    if (session.Profile.Provider == DatabaseProviderKind.PostgreSql)
    {
        var domainColumn = before.Columns.Single(column => column.Name == "domain_fixed");
        var unboundedColumn = before.Columns.Single(column => column.Name == "unbounded_fixed");
        Assert(
            domainColumn.ValueKind == TableColumnValueKind.String &&
            domainColumn.StorageDataTypeName == "character(6)" &&
            domainColumn.TrailingSpacesAreNotRoundTrippable,
            "PostgreSQL character domain 應依 base type 標記尾端空白無法 round-trip");
        Assert(
            unboundedColumn.ValueKind == TableColumnValueKind.String &&
            unboundedColumn.StorageDataTypeName == "bpchar" &&
            unboundedColumn.TrailingSpacesAreNotRoundTrippable,
            "PostgreSQL unbounded bpchar 應標記尾端空白無法 round-trip");
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            domainColumn,
            new TableCellInput("domain_fixed", TableCellInputMode.Value, "CD ")));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            unboundedColumn,
            new TableCellInput("unbounded_fixed", TableCellInputMode.Value, "EF ")));
    }

    const string nativeName = "Native CHAR spaces";
    if (session.Profile.Provider == DatabaseProviderKind.MySql)
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server = session.Profile.Host,
            Port = (uint)session.Profile.Port,
            UserID = session.Profile.Username,
            Password = session.Profile.Password,
            Database = database,
            SslMode = session.Profile.UseSsl ? MySqlSslMode.Preferred : MySqlSslMode.None
        };
        await using var connection = new MySqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SET SESSION sql_mode = 'STRICT_ALL_TABLES';";
        await command.ExecuteNonQueryAsync();
        command.CommandText =
            "INSERT INTO sample (name, fixed_text) VALUES ('Native CHAR spaces', 'AB  ');";
        await command.ExecuteNonQueryAsync();
        command.CommandText = "SHOW COUNT(*) WARNINGS;";
        Assert(
            Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) == 0,
            "MySQL／MariaDB CHAR 尾端空白失真應證實不會產生 warning");
    }
    else
    {
        await session.ExecuteAsync(
            database,
            "INSERT INTO public.sample (name, fixed_text, unbounded_fixed) " +
            "VALUES ('Native CHAR spaces', 'AB  ', 'EF  ');");
    }

    var nativeSnapshot = await session.LoadTableDataAsync(database, table);
    var nativeRow = nativeSnapshot.Rows.Single(row => Convert.ToString(row.Values[1]) == nativeName);
    var nativeValue = Convert.ToString(nativeRow.Values[fixedTextColumn.Ordinal]) ?? string.Empty;
    Assert(
        nativeValue != "AB  " && nativeValue.TrimEnd(' ') == "AB",
        $"{session.Profile.ProviderDisplayName} 原生 CHAR 應證實無法讀回輸入的尾端空白數量；actual=[{nativeValue}]");
    if (session.Profile.Provider == DatabaseProviderKind.PostgreSql)
    {
        var unboundedColumn = nativeSnapshot.Columns.Single(column => column.Name == "unbounded_fixed");
        var unboundedValue = Convert.ToString(nativeRow.Values[unboundedColumn.Ordinal]) ?? string.Empty;
        Assert(
            unboundedValue == "EF",
            $"PostgreSQL 原生 unbounded bpchar 應證實會移除尾端空白；actual=[{unboundedValue}]");
    }
    await session.DeleteTableRowAsync(database, table, nativeRow);

    var rejectedException = await CaptureExceptionAsync<InvalidOperationException>(() =>
        session.InsertTableRowAsync(
            database,
            table,
            new[]
            {
                new TableCellInput("name", TableCellInputMode.Value, "Rejected CHAR spaces"),
                new TableCellInput("fixed_text", TableCellInputMode.Value, "AB ")
            }));
    Assert(
        rejectedException.Message.Contains("U+0020", StringComparison.Ordinal) &&
        rejectedException.Message.Contains("VARCHAR", StringComparison.Ordinal),
        $"{session.Profile.ProviderDisplayName} fixed CHAR 應提供可行的無損修正說明；actual={rejectedException.Message}");
    var rejectedSnapshot = await session.LoadTableDataAsync(database, table);
    Assert(
        rejectedSnapshot.Rows.All(row => Convert.ToString(row.Values[1]) != "Rejected CHAR spaces"),
        $"{session.Profile.ProviderDisplayName} fixed CHAR 尾端空白輸入不可落地");

    await session.InsertTableRowAsync(
        database,
        table,
        new[]
        {
            new TableCellInput("name", TableCellInputMode.Value, "Fixed CHAR editor"),
            new TableCellInput("fixed_text", TableCellInputMode.Value, "ABC")
        });
    var insertedSnapshot = await session.LoadTableDataAsync(database, table);
    var inserted = insertedSnapshot.Rows.Single(row =>
        Convert.ToString(row.Values[1]) == "Fixed CHAR editor");
    Assert(
        (Convert.ToString(inserted.Values[fixedTextColumn.Ordinal]) ?? string.Empty).TrimEnd(' ') == "ABC",
        $"{session.Profile.ProviderDisplayName} fixed CHAR 合法短值新增不正確");

    await AssertThrowsAsync<InvalidOperationException>(() => session.UpdateTableRowAsync(
        database,
        table,
        inserted,
        new[] { new TableCellInput("fixed_text", TableCellInputMode.Value, "XYZ ") }));
    var unchangedSnapshot = await session.LoadTableDataAsync(database, table);
    var unchanged = unchangedSnapshot.Rows.Single(row =>
        Convert.ToString(row.Values[1]) == "Fixed CHAR editor");
    Assert(
        (Convert.ToString(unchanged.Values[fixedTextColumn.Ordinal]) ?? string.Empty).TrimEnd(' ') == "ABC",
        $"{session.Profile.ProviderDisplayName} fixed CHAR 無效修改不可落地");

    await session.UpdateTableRowAsync(
        database,
        table,
        unchanged,
        new[] { new TableCellInput("fixed_text", TableCellInputMode.Value, "UVWXYZ") });
    var updatedSnapshot = await session.LoadTableDataAsync(database, table);
    var updated = updatedSnapshot.Rows.Single(row =>
        Convert.ToString(row.Values[1]) == "Fixed CHAR editor");
    Assert(
        Convert.ToString(updated.Values[fixedTextColumn.Ordinal]) == "UVWXYZ",
        $"{session.Profile.ProviderDisplayName} fixed CHAR 無尾端空白的滿長值應正常保存");
    await session.DeleteTableRowAsync(database, table, updated);
}

static async Task VerifyFixedLengthBinaryAsync(
    IDatabaseSession session,
    string database,
    DatabaseObjectInfo table)
{
    var before = await session.LoadTableDataAsync(database, table);
    var fixedBinaryColumns = before.Columns
        .Where(column => column.RequiredBinaryLength is not null)
        .ToDictionary(column => column.Name, StringComparer.Ordinal);
    var expectedCount = session.Profile.Provider == DatabaseProviderKind.SqlServer ? 2 : 1;
    var fixedPayloadColumn = fixedBinaryColumns["fixed_payload"];
    Assert(
        fixedBinaryColumns.Count == expectedCount &&
        fixedPayloadColumn.ValueKind == TableColumnValueKind.Binary &&
        fixedPayloadColumn.StorageDataTypeName == "binary(3)" &&
        fixedPayloadColumn.RequiredBinaryLength == 3,
        $"{session.Profile.ProviderDisplayName} fixed binary metadata 不正確");

    TableColumnInfo? aliasFixedPayloadColumn = null;
    if (session.Profile.Provider == DatabaseProviderKind.SqlServer)
    {
        aliasFixedPayloadColumn = fixedBinaryColumns["alias_fixed_payload"];
        Assert(
            aliasFixedPayloadColumn.DataTypeName == "[dbo].[fixed_token] (binary(4))" &&
            aliasFixedPayloadColumn.StorageDataTypeName == "binary(4)" &&
            aliasFixedPayloadColumn.RequiredBinaryLength == 4,
            $"SQL Server fixed binary alias metadata 不正確；actual={aliasFixedPayloadColumn.DataTypeName}");
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            aliasFixedPayloadColumn,
            new TableCellInput("alias_fixed_payload", TableCellInputMode.Value, "0x010203")));
    }

    var qualifiedTable = session.Profile.Provider == DatabaseProviderKind.SqlServer
        ? "dbo.sample"
        : "sample";
    await session.ExecuteAsync(
        database,
        $"INSERT INTO {qualifiedTable} (name, fixed_payload) VALUES ('Native binary padding', 0xCAFE);");
    var nativeSnapshot = await session.LoadTableDataAsync(database, table);
    var nativePadded = nativeSnapshot.Rows.Single(row =>
        Convert.ToString(row.Values[1]) == "Native binary padding");
    Assert(
        nativePadded.Values[fixedPayloadColumn.Ordinal] is byte[] nativeBytes &&
        nativeBytes.SequenceEqual(new byte[] { 0xCA, 0xFE, 0x00 }),
        $"{session.Profile.ProviderDisplayName} BINARY(3) 原生短值應證實會無 warning 補 0x00");
    await session.DeleteTableRowAsync(database, table, nativePadded);

    var invalidValues = new[]
    {
        (Name: "Rejected short binary", Value: "0xCAFE"),
        (Name: "Rejected long binary", Value: "0xCAFEBABE")
    };
    foreach (var invalid in invalidValues)
    {
        await AssertThrowsAsync<InvalidOperationException>(() => session.InsertTableRowAsync(
            database,
            table,
            new[]
            {
                new TableCellInput("name", TableCellInputMode.Value, invalid.Name),
                new TableCellInput("fixed_payload", TableCellInputMode.Value, invalid.Value)
            }));
    }
    var rejectedSnapshot = await session.LoadTableDataAsync(database, table);
    Assert(
        invalidValues.All(invalid => rejectedSnapshot.Rows.All(row =>
            Convert.ToString(row.Values[1]) != invalid.Name)),
        $"{session.Profile.ProviderDisplayName} fixed binary 無效新增不可落地");

    var validInputs = new List<TableCellInput>
    {
        new("name", TableCellInputMode.Value, "Fixed binary editor"),
        new("fixed_payload", TableCellInputMode.Value, "0x00FF10")
    };
    if (aliasFixedPayloadColumn is not null)
    {
        validInputs.Add(new TableCellInput(
            "alias_fixed_payload",
            TableCellInputMode.Value,
            "0x01020304"));
    }
    await session.InsertTableRowAsync(database, table, validInputs);

    var insertedSnapshot = await session.LoadTableDataAsync(database, table);
    var inserted = insertedSnapshot.Rows.Single(row =>
        Convert.ToString(row.Values[1]) == "Fixed binary editor");
    Assert(
        inserted.Values[fixedPayloadColumn.Ordinal] is byte[] insertedBytes &&
        insertedBytes.SequenceEqual(new byte[] { 0x00, 0xFF, 0x10 }),
        $"{session.Profile.ProviderDisplayName} fixed binary 精確長度新增不正確");
    if (aliasFixedPayloadColumn is not null)
    {
        Assert(
            inserted.Values[aliasFixedPayloadColumn.Ordinal] is byte[] aliasBytes &&
            aliasBytes.SequenceEqual(new byte[] { 0x01, 0x02, 0x03, 0x04 }),
            "SQL Server fixed binary alias 精確長度新增不正確");
    }

    await AssertThrowsAsync<InvalidOperationException>(() => session.UpdateTableRowAsync(
        database,
        table,
        inserted,
        new[]
        {
            new TableCellInput("fixed_payload", TableCellInputMode.Value, "0xABCD")
        }));
    var afterRejectedUpdate = await session.LoadTableDataAsync(database, table);
    var unchanged = afterRejectedUpdate.Rows.Single(row =>
        Convert.ToString(row.Values[1]) == "Fixed binary editor");
    Assert(
        unchanged.Values[fixedPayloadColumn.Ordinal] is byte[] unchangedBytes &&
        unchangedBytes.SequenceEqual(new byte[] { 0x00, 0xFF, 0x10 }),
        $"{session.Profile.ProviderDisplayName} fixed binary 短值修改不可自動補零後落地");

    var validUpdates = new List<TableCellInput>
    {
        new("fixed_payload", TableCellInputMode.Value, "0xABCDEF")
    };
    if (aliasFixedPayloadColumn is not null)
    {
        validUpdates.Add(new TableCellInput(
            "alias_fixed_payload",
            TableCellInputMode.Value,
            "0x10203040"));
    }
    await session.UpdateTableRowAsync(database, table, unchanged, validUpdates);

    var updatedSnapshot = await session.LoadTableDataAsync(database, table);
    var updated = updatedSnapshot.Rows.Single(row =>
        Convert.ToString(row.Values[1]) == "Fixed binary editor");
    Assert(
        updated.Values[fixedPayloadColumn.Ordinal] is byte[] updatedBytes &&
        updatedBytes.SequenceEqual(new byte[] { 0xAB, 0xCD, 0xEF }),
        $"{session.Profile.ProviderDisplayName} fixed binary 精確長度修改不正確");
    if (aliasFixedPayloadColumn is not null)
    {
        Assert(
            updated.Values[aliasFixedPayloadColumn.Ordinal] is byte[] aliasUpdatedBytes &&
            aliasUpdatedBytes.SequenceEqual(new byte[] { 0x10, 0x20, 0x30, 0x40 }),
            "SQL Server fixed binary alias 精確長度修改不正確");
    }

    await session.DeleteTableRowAsync(database, table, updated);
    var afterDelete = await session.LoadTableDataAsync(database, table);
    Assert(
        afterDelete.Rows.All(row => Convert.ToString(row.Values[1]) != "Fixed binary editor"),
        $"{session.Profile.ProviderDisplayName} fixed binary 安全刪除不正確");
}

static async Task VerifyIntegerTypesCoreAsync(
    IDatabaseSession session,
    string database,
    DatabaseObjectInfo table,
    Func<long, string> buildConcurrentUpdateSql)
{
    var expected = session.Profile.Provider switch
    {
        DatabaseProviderKind.MySql => new Dictionary<string, (long Minimum, ulong Maximum, TableColumnValueKind Kind)>(
            StringComparer.Ordinal)
        {
            ["tiny_value"] = (sbyte.MinValue, (ulong)sbyte.MaxValue, TableColumnValueKind.Integer),
            ["unsigned_tiny_value"] = (0, byte.MaxValue, TableColumnValueKind.UnsignedInteger),
            ["small_value"] = (short.MinValue, (ulong)short.MaxValue, TableColumnValueKind.Integer),
            ["unsigned_small_value"] = (0, ushort.MaxValue, TableColumnValueKind.UnsignedInteger),
            ["zerofill_small_value"] = (0, ushort.MaxValue, TableColumnValueKind.UnsignedInteger),
            ["medium_value"] = (-8_388_608, 8_388_607, TableColumnValueKind.Integer),
            ["unsigned_medium_value"] = (0, 16_777_215, TableColumnValueKind.UnsignedInteger),
            ["integer_value"] = (int.MinValue, int.MaxValue, TableColumnValueKind.Integer),
            ["unsigned_integer_value"] = (0, uint.MaxValue, TableColumnValueKind.UnsignedInteger),
            ["big_value"] = (long.MinValue, (ulong)long.MaxValue, TableColumnValueKind.Integer),
            ["unsigned_big_value"] = (0, ulong.MaxValue, TableColumnValueKind.UnsignedInteger)
        },
        DatabaseProviderKind.PostgreSql => new Dictionary<string, (long Minimum, ulong Maximum, TableColumnValueKind Kind)>(
            StringComparer.Ordinal)
        {
            ["small_value"] = (short.MinValue, (ulong)short.MaxValue, TableColumnValueKind.Integer),
            ["integer_value"] = (int.MinValue, int.MaxValue, TableColumnValueKind.Integer),
            ["big_value"] = (long.MinValue, (ulong)long.MaxValue, TableColumnValueKind.Integer)
        },
        DatabaseProviderKind.SqlServer => new Dictionary<string, (long Minimum, ulong Maximum, TableColumnValueKind Kind)>(
            StringComparer.Ordinal)
        {
            ["tiny_value"] = (0, byte.MaxValue, TableColumnValueKind.Integer),
            ["small_value"] = (short.MinValue, (ulong)short.MaxValue, TableColumnValueKind.Integer),
            ["integer_value"] = (int.MinValue, int.MaxValue, TableColumnValueKind.Integer),
            ["big_value"] = (long.MinValue, (ulong)long.MaxValue, TableColumnValueKind.Integer)
        },
        _ => throw new InvalidOperationException(
            $"{session.Profile.ProviderDisplayName} 不在 integer 實機矩陣範圍內。")
    };

    var before = await session.LoadTableDataAsync(database, table);
    var columns = expected.ToDictionary(
        item => item.Key,
        item => before.Columns.Single(column => column.Name == item.Key),
        StringComparer.Ordinal);
    foreach (var item in expected)
    {
        var column = columns[item.Key];
        Assert(
            column.ValueKind == item.Value.Kind &&
            column.IntegerMinimum == item.Value.Minimum &&
            column.IntegerMaximum == item.Value.Maximum &&
            column.IsEditable,
            $"{session.Profile.ProviderDisplayName} {column.DataTypeName} integer metadata 範圍不正確");

        var minimumText = item.Value.Minimum.ToString(CultureInfo.InvariantCulture);
        var maximumText = item.Value.Maximum.ToString(CultureInfo.InvariantCulture);
        Assert(
            TableCellValueConverter.Parse(
                column,
                new TableCellInput(column.Name, TableCellInputMode.Value, minimumText)) is not null &&
            TableCellValueConverter.Parse(
                column,
                new TableCellInput(column.Name, TableCellInputMode.Value, maximumText)) is not null,
            $"{session.Profile.ProviderDisplayName} {column.DataTypeName} 應接受正負範圍邊界");

        var belowMinimum = item.Value.Minimum == long.MinValue
            ? "-9223372036854775809"
            : (item.Value.Minimum - 1).ToString(CultureInfo.InvariantCulture);
        var aboveMaximum = item.Value.Maximum == ulong.MaxValue
            ? "18446744073709551616"
            : (item.Value.Maximum + 1).ToString(CultureInfo.InvariantCulture);
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            column,
            new TableCellInput(column.Name, TableCellInputMode.Value, belowMinimum)));
        AssertThrows<InvalidOperationException>(() => TableCellValueConverter.Parse(
            column,
            new TableCellInput(column.Name, TableCellInputMode.Value, aboveMaximum)));
    }

    var minimumInputs = new List<TableCellInput>
    {
        new("name", TableCellInputMode.Value, "Integer minimum")
    };
    var maximumInputs = new List<TableCellInput>
    {
        new("name", TableCellInputMode.Value, "Integer maximum")
    };
    foreach (var item in expected)
    {
        minimumInputs.Add(new TableCellInput(
            item.Key,
            TableCellInputMode.Value,
            item.Value.Minimum.ToString(CultureInfo.InvariantCulture)));
        maximumInputs.Add(new TableCellInput(
            item.Key,
            TableCellInputMode.Value,
            item.Value.Maximum.ToString(CultureInfo.InvariantCulture)));
    }

    await session.InsertTableRowAsync(database, table, minimumInputs);
    await session.InsertTableRowAsync(database, table, maximumInputs);
    var boundarySnapshot = await session.LoadTableDataAsync(database, table);
    var minimumRow = boundarySnapshot.Rows.Single(row =>
        Convert.ToString(row.Values[1]) == "Integer minimum");
    var maximumRow = boundarySnapshot.Rows.Single(row =>
        Convert.ToString(row.Values[1]) == "Integer maximum");
    foreach (var item in expected)
    {
        var column = columns[item.Key];
        Assert(
            Convert.ToString(minimumRow.Values[column.Ordinal], CultureInfo.InvariantCulture) ==
            item.Value.Minimum.ToString(CultureInfo.InvariantCulture) &&
            Convert.ToString(maximumRow.Values[column.Ordinal], CultureInfo.InvariantCulture) ==
            item.Value.Maximum.ToString(CultureInfo.InvariantCulture),
            $"{session.Profile.ProviderDisplayName} {column.DataTypeName} 邊界實機 round-trip 不正確");
    }

    var representative = expected.First();
    var representativeColumn = columns[representative.Key];
    var invalidBelow = representative.Value.Minimum == long.MinValue
        ? "-9223372036854775809"
        : (representative.Value.Minimum - 1).ToString(CultureInfo.InvariantCulture);
    var invalidAbove = representative.Value.Maximum == ulong.MaxValue
        ? "18446744073709551616"
        : (representative.Value.Maximum + 1).ToString(CultureInfo.InvariantCulture);
    foreach (var invalid in new[] { invalidBelow, invalidAbove })
    {
        await AssertThrowsAsync<InvalidOperationException>(() => session.InsertTableRowAsync(
            database,
            table,
            new[]
            {
                new TableCellInput("name", TableCellInputMode.Value, "Rejected integer"),
                new TableCellInput(representativeColumn.Name, TableCellInputMode.Value, invalid)
            }));
    }
    Assert(
        (await session.LoadTableDataAsync(database, table)).Rows.All(row =>
            Convert.ToString(row.Values[1]) != "Rejected integer"),
        $"{session.Profile.ProviderDisplayName} 不可寫入會溢位或被截到邊界的 integer");

    await session.UpdateTableRowAsync(
        database,
        table,
        minimumRow,
        new[]
        {
            new TableCellInput("note", TableCellInputMode.Value, "integer updated"),
            new TableCellInput("integer_value", TableCellInputMode.Value, "0")
        });
    var updatedSnapshot = await session.LoadTableDataAsync(database, table);
    var updated = updatedSnapshot.Rows.Single(row =>
        Convert.ToString(row.Values[1]) == "Integer minimum");
    var id = Convert.ToInt64(updated.Values[0], CultureInfo.InvariantCulture);
    await session.ExecuteAsync(database, buildConcurrentUpdateSql(id));
    await AssertThrowsAsync<TableDataConflictException>(() => session.UpdateTableRowAsync(
        database,
        table,
        updated,
        new[] { new TableCellInput("note", TableCellInputMode.Value, "must-not-overwrite") }));

    var concurrentSnapshot = await session.LoadTableDataAsync(database, table);
    var concurrent = concurrentSnapshot.Rows.Single(row => Convert.ToInt64(row.Values[0]) == id);
    var currentMaximum = concurrentSnapshot.Rows.Single(row =>
        Convert.ToString(row.Values[1]) == "Integer maximum");
    Assert(
        Convert.ToInt64(concurrent.Values[columns["integer_value"].Ordinal]) == 1,
        $"{session.Profile.ProviderDisplayName} integer optimistic predicate 未攔截外部更新");
    await session.DeleteTableRowAsync(database, table, concurrent);
    await session.DeleteTableRowAsync(database, table, currentMaximum);
    var afterDelete = await session.LoadTableDataAsync(database, table);
    Assert(
        afterDelete.Rows.All(row =>
            Convert.ToString(row.Values[1]) is not ("Integer minimum" or "Integer maximum")),
        $"{session.Profile.ProviderDisplayName} integer 邊界測試列安全刪除失敗");
}

static async Task VerifyFloatingPointTypesAsync(
    IDatabaseSession session,
    string database,
    DatabaseObjectInfo table,
    Func<long, string> buildConcurrentUpdateSql)
{
    var before = await session.LoadTableDataAsync(database, table);
    var singleColumn = before.Columns.Single(column => column.Name == "single_value");
    var doubleColumn = before.Columns.Single(column => column.Name == "double_value");
    Assert(
        singleColumn is
        {
            ValueKind: TableColumnValueKind.SinglePrecisionFloatingPoint,
            IsEditable: true
        },
        $"{session.Profile.ProviderDisplayName} 4-byte 浮點 metadata 應可安全編輯");
    Assert(
        doubleColumn is
        {
            ValueKind: TableColumnValueKind.DoublePrecisionFloatingPoint,
            IsEditable: true
        },
        $"{session.Profile.ProviderDisplayName} 8-byte 浮點 metadata 應可安全編輯");

    var insertInputs = new List<TableCellInput>
    {
        new("name", TableCellInputMode.Value, "Floating point"),
        new("single_value", TableCellInputMode.Value, "1.2345678"),
        new("double_value", TableCellInputMode.Value, "1.23456789012345")
    };
    var compactColumn = before.Columns.SingleOrDefault(column => column.Name == "compact_float");
    if (compactColumn is not null)
    {
        Assert(
            compactColumn is
            {
                ValueKind: TableColumnValueKind.SinglePrecisionFloatingPoint,
                IsEditable: true
            },
            $"{session.Profile.ProviderDisplayName} compact FLOAT precision 應映射為 4-byte 浮點");
        insertInputs.Add(new TableCellInput(
            compactColumn.Name,
            TableCellInputMode.Value,
            "1.2345678"));
    }

    var wideColumn = before.Columns.SingleOrDefault(column => column.Name == "wide_float");
    if (wideColumn is not null)
    {
        Assert(
            wideColumn is
            {
                ValueKind: TableColumnValueKind.DoublePrecisionFloatingPoint,
                IsEditable: true
            },
            $"{session.Profile.ProviderDisplayName} FLOAT(53) 應映射為 8-byte 浮點");
        insertInputs.Add(new TableCellInput(
            wideColumn.Name,
            TableCellInputMode.Value,
            "1.23456789012345"));
    }

    var scaledColumn = before.Columns.SingleOrDefault(column => column.Name == "scaled_value");
    if (scaledColumn is not null)
    {
        Assert(
            scaledColumn is
            {
                ValueKind: TableColumnValueKind.SinglePrecisionFloatingPoint,
                IsEditable: true
            },
            $"{session.Profile.ProviderDisplayName} FLOAT(M,D) 應映射為 4-byte 浮點");
        insertInputs.Add(new TableCellInput(
            scaledColumn.Name,
            TableCellInputMode.Value,
            "12.3456"));
    }

    await session.InsertTableRowAsync(database, table, insertInputs);
    var insertedSnapshot = await session.LoadTableDataAsync(database, table);
    var inserted = insertedSnapshot.Rows.Single(row =>
        Convert.ToString(row.Values[1]) == "Floating point");
    Assert(
        TableCellValueConverter.Format(singleColumn, inserted.Values[singleColumn.Ordinal]) == "1.2345678",
        $"{session.Profile.ProviderDisplayName} 4-byte 浮點實機 round-trip 不正確");
    Assert(
        TableCellValueConverter.Format(doubleColumn, inserted.Values[doubleColumn.Ordinal]) ==
        "1.23456789012345",
        $"{session.Profile.ProviderDisplayName} 8-byte 浮點實機 round-trip 不正確");
    Assert(
        TableCellValueConverter.MatchesOriginal(
            singleColumn,
            new TableCellInput("single_value", TableCellInputMode.Value, "+01.2345678e+0"),
            inserted.Values[singleColumn.Ordinal]),
        $"{session.Profile.ProviderDisplayName} 4-byte 浮點 optimistic predicate 應接受等值科學記號");

    await AssertThrowsAsync<InvalidOperationException>(() => session.InsertTableRowAsync(
        database,
        table,
        new[]
        {
            new TableCellInput("name", TableCellInputMode.Value, "Rejected single"),
            new TableCellInput("single_value", TableCellInputMode.Value, "1.23456789")
        }));
    await AssertThrowsAsync<InvalidOperationException>(() => session.InsertTableRowAsync(
        database,
        table,
        new[]
        {
            new TableCellInput("name", TableCellInputMode.Value, "Rejected double"),
            new TableCellInput("double_value", TableCellInputMode.Value, "1.23456789012345678")
        }));
    if (scaledColumn is not null)
    {
        await AssertThrowsAsync<InvalidOperationException>(() => session.InsertTableRowAsync(
            database,
            table,
            new[]
            {
                new TableCellInput("name", TableCellInputMode.Value, "Rejected scaled float"),
                new TableCellInput(scaledColumn.Name, TableCellInputMode.Value, "12.34567")
            }));
    }

    var rejectedSnapshot = await session.LoadTableDataAsync(database, table);
    Assert(
        rejectedSnapshot.Rows.All(row =>
            !Convert.ToString(row.Values[1])!.StartsWith("Rejected ", StringComparison.Ordinal)),
        $"{session.Profile.ProviderDisplayName} 不可寫入會無聲降精度的浮點值");

    var updateInputs = new List<TableCellInput>
    {
        new("single_value", TableCellInputMode.Value, "9.876543"),
        new("double_value", TableCellInputMode.Value, "9.87654321012345")
    };
    if (compactColumn is not null)
    {
        updateInputs.Add(new TableCellInput(
            compactColumn.Name,
            TableCellInputMode.Value,
            "9.876543"));
    }
    if (wideColumn is not null)
    {
        updateInputs.Add(new TableCellInput(
            wideColumn.Name,
            TableCellInputMode.Value,
            "9.87654321012345"));
    }
    if (scaledColumn is not null)
    {
        updateInputs.Add(new TableCellInput(
            scaledColumn.Name,
            TableCellInputMode.Value,
            "98.7654"));
    }

    await session.UpdateTableRowAsync(database, table, inserted, updateInputs);
    var updatedSnapshot = await session.LoadTableDataAsync(database, table);
    var updated = updatedSnapshot.Rows.Single(row =>
        Convert.ToString(row.Values[1]) == "Floating point");
    Assert(
        TableCellValueConverter.Format(singleColumn, updated.Values[singleColumn.Ordinal]) == "9.876543" &&
        TableCellValueConverter.Format(doubleColumn, updated.Values[doubleColumn.Ordinal]) ==
        "9.87654321012345",
        $"{session.Profile.ProviderDisplayName} 浮點更新後實機 round-trip 不正確");

    var id = Convert.ToInt64(updated.Values[0], CultureInfo.InvariantCulture);
    await session.ExecuteAsync(database, buildConcurrentUpdateSql(id));
    await AssertThrowsAsync<TableDataConflictException>(() => session.UpdateTableRowAsync(
        database,
        table,
        updated,
        new[] { new TableCellInput("note", TableCellInputMode.Value, "must-not-overwrite") }));
    var concurrentSnapshot = await session.LoadTableDataAsync(database, table);
    var concurrent = concurrentSnapshot.Rows.Single(row => Convert.ToInt64(row.Values[0]) == id);
    Assert(
        TableCellValueConverter.Format(singleColumn, concurrent.Values[singleColumn.Ordinal]) == "2.5",
        $"{session.Profile.ProviderDisplayName} 浮點 optimistic predicate 未攔截外部更新");
    await session.DeleteTableRowAsync(database, table, concurrent);
    Assert(
        (await session.LoadTableDataAsync(database, table)).Rows.All(row =>
            Convert.ToInt64(row.Values[0]) != id),
        $"{session.Profile.ProviderDisplayName} 浮點測試列安全刪除失敗");
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
    var sqlServerVariantColumns = before.Columns
        .Where(column => column.ValueKind == TableColumnValueKind.SqlServerVariant)
        .ToList();
    var sqlServerVariantValueColumn = sqlServerVariantColumns.SingleOrDefault(column =>
        column.Name == "variant_value");
    var sqlServerVariantTextColumn = sqlServerVariantColumns.SingleOrDefault(column =>
        column.Name == "variant_text");
    var sqlServerVariantTemporalColumn = sqlServerVariantColumns.SingleOrDefault(column =>
        column.Name == "variant_temporal");
    if (sqlServerVariantColumns.Count > 0)
    {
        Assert(
            sqlServerVariantColumns.Count == 3 &&
            sqlServerVariantValueColumn is not null &&
            sqlServerVariantTextColumn is not null &&
            sqlServerVariantTemporalColumn is not null &&
            sqlServerVariantColumns.All(column =>
                column.DataTypeName == "sql_variant" &&
                column.StorageDataTypeName == "sql_variant" &&
                column.IsEditable),
            "SQL Server sql_variant metadata 不完整或未標示為可編輯");
        var seededDecimal = before.Rows.Single(row => Convert.ToString(row.Values[1]) == "Punky");
        Assert(
            Convert.ToString(seededDecimal.Values[sqlServerVariantValueColumn!.Ordinal]) ==
            "decimal(18,6):123.450000",
            $"SQL Server sql_variant decimal 載入未保留 base type／precision／scale；actual={seededDecimal.Values[sqlServerVariantValueColumn.Ordinal]}");
        var seededText = Convert.ToString(seededDecimal.Values[sqlServerVariantTextColumn!.Ordinal]);
        Assert(
            seededText?.StartsWith(
                "nvarchar(30)@Latin1_General_100_BIN2|1033|",
                StringComparison.Ordinal) == true &&
            seededText.EndsWith(":Seed:文字  ", StringComparison.Ordinal),
            $"SQL Server sql_variant nvarchar 載入未保留長度、collation 或文字；actual={seededText}");
        Assert(
            Convert.ToString(seededDecimal.Values[sqlServerVariantTemporalColumn!.Ordinal]) ==
            "datetime2(3):2026-08-30T12:34:56.7890000",
            $"SQL Server sql_variant datetime2 載入未保留 scale；actual={seededDecimal.Values[sqlServerVariantTemporalColumn.Ordinal]}");

        var seededNumeric = before.Rows.Single(row => Convert.ToString(row.Values[1]) == "Linux/macOS");
        Assert(
            Convert.ToString(seededNumeric.Values[sqlServerVariantValueColumn.Ordinal]) ==
            "numeric(20,8):987654321.12345678",
            $"SQL Server sql_variant numeric 載入未保留 synonym 與精度；actual={seededNumeric.Values[sqlServerVariantValueColumn.Ordinal]}");
        Assert(
            Convert.ToString(seededNumeric.Values[sqlServerVariantTextColumn.Ordinal])?.StartsWith(
                "varchar(20)@",
                StringComparison.Ordinal) == true,
            $"SQL Server sql_variant varchar 載入未保留長度與 collation；actual={seededNumeric.Values[sqlServerVariantTextColumn.Ordinal]}");
        Assert(
            Convert.ToString(seededNumeric.Values[sqlServerVariantTemporalColumn.Ordinal]) ==
            "varbinary(4):0x00FF10",
            $"SQL Server sql_variant varbinary 載入未保留長度與 bytes；actual={seededNumeric.Values[sqlServerVariantTemporalColumn.Ordinal]}");

        await session.UpdateTableRowAsync(
            database,
            table,
            seededDecimal,
            new[]
            {
                new TableCellInput("note", TableCellInputMode.Value, "variant predicate"),
                new TableCellInput(
                    sqlServerVariantTextColumn.Name,
                    TableCellInputMode.Value,
                    seededText!.Replace(":Seed:文字  ", ":Updated:文字  ", StringComparison.Ordinal))
            });
        before = await session.LoadTableDataAsync(database, table);
        var updatedSeededDecimal = before.Rows.Single(row => Convert.ToString(row.Values[1]) == "Punky");
        Assert(
            Convert.ToString(updatedSeededDecimal.Values[3]) ==
            "variant predicate",
            "SQL Server sql_variant custom collation 原值 predicate 無法安全 round-trip");
        var updatedCollatedText = Convert.ToString(
            updatedSeededDecimal.Values[sqlServerVariantTextColumn.Ordinal]);
        Assert(
            updatedCollatedText?.StartsWith(
                "nvarchar(30)@Latin1_General_100_BIN2|1033|",
                StringComparison.Ordinal) == true &&
            updatedCollatedText.EndsWith(":Updated:文字  ", StringComparison.Ordinal),
            $"SQL Server sql_variant custom collation 寫入未保留完整 metadata；actual={updatedCollatedText}");

        var variantTypeMatrix = new (string Tag, string ExpectedBaseType)[]
        {
            ("tinyint:255", "tinyint"),
            ("smallint:-32768", "smallint"),
            ("int:-2147483648", "int"),
            ("bigint:9223372036854775807", "bigint"),
            ("bit:true", "bit"),
            ("decimal(18,6):123.450000", "decimal"),
            ("numeric(20,8):987654321.12345678", "numeric"),
            ("money:123.4567", "money"),
            ("smallmoney:-123.4567", "smallmoney"),
            ("float:1.23456789012345", "float"),
            ("real:1.2345", "real"),
            ("date:2026-08-30", "date"),
            ("datetime:2026-08-30T12:34:56", "datetime"),
            ("smalldatetime:2026-08-30T12:34:00", "smalldatetime"),
            ("datetime2(7):2026-08-30T12:34:56.1234567", "datetime2"),
            ("datetimeoffset(3):2026-08-30T12:34:56.789+08:00", "datetimeoffset"),
            ("time(4):12:34:56.7890", "time"),
            ("uniqueidentifier:12345678-1234-5678-9abc-def012345678", "uniqueidentifier"),
            ("char(12):ASCII", "char"),
            ("varchar(12):ASCII", "varchar"),
            ("nchar(12):繁體", "nchar"),
            ("nvarchar(12):繁體", "nvarchar"),
            ("binary(4):0xCAFE", "binary"),
            ("varbinary(4):0xCAFE", "varbinary")
        };
        for (var matrixIndex = 0; matrixIndex < variantTypeMatrix.Length; matrixIndex++)
        {
            var matrixCase = variantTypeMatrix[matrixIndex];
            var matrixName = $"Variant matrix {matrixIndex}";
            await session.InsertTableRowAsync(
                database,
                table,
                new[]
                {
                    new TableCellInput("name", TableCellInputMode.Value, matrixName),
                    new TableCellInput(
                        sqlServerVariantValueColumn.Name,
                        TableCellInputMode.Value,
                        matrixCase.Tag)
                });
            var matrixSnapshot = await session.LoadTableDataAsync(database, table);
            var matrixRow = matrixSnapshot.Rows.Single(row => Convert.ToString(row.Values[1]) == matrixName);
            var matrixId = Convert.ToInt64(matrixRow.Values[0]);
            var matrixProperty = await session.ExecuteAsync(
                database,
                $"SELECT CONVERT(varchar(30), SQL_VARIANT_PROPERTY(variant_value, 'BaseType')) " +
                $"FROM dbo.sample WHERE id = {matrixId};");
            Assert(
                Convert.ToString(matrixProperty.Rows.Single()[0]) == matrixCase.ExpectedBaseType,
                $"SQL Server sql_variant {matrixCase.Tag} 寫入後的 BaseType 不正確；" +
                $"actual={matrixProperty.Rows.Single()[0]}");
            await session.DeleteTableRowAsync(database, table, matrixRow);
        }
        before = await session.LoadTableDataAsync(database, table);

        insertInputs.Add(new TableCellInput(
            sqlServerVariantValueColumn.Name,
            TableCellInputMode.Value,
            "int:42"));
        insertInputs.Add(new TableCellInput(
            sqlServerVariantTextColumn.Name,
            TableCellInputMode.Value,
            "nvarchar(30):Variant insert:文字  "));
        insertInputs.Add(new TableCellInput(
            sqlServerVariantTemporalColumn.Name,
            TableCellInputMode.Value,
            "datetimeoffset(3):2026-08-30T12:34:56.789+08:00"));

        await AssertThrowsAsync<InvalidOperationException>(() => session.InsertTableRowAsync(
            database,
            table,
            new[]
            {
                new TableCellInput("name", TableCellInputMode.Value, "Rejected sql_variant"),
                new TableCellInput(sqlServerVariantValueColumn.Name, TableCellInputMode.Value, "decimal(5,2):1234.56")
            }));
        var rejectedSnapshot = await session.LoadTableDataAsync(database, table);
        Assert(
            rejectedSnapshot.Rows.All(row => Convert.ToString(row.Values[1]) != "Rejected sql_variant"),
            "SQL Server sql_variant 本機驗證拒絕時不可留下半筆資料");

        await AssertThrowsAsync<InvalidOperationException>(() => session.InsertTableRowAsync(
            database,
            table,
            new[]
            {
                new TableCellInput("name", TableCellInputMode.Value, "Rejected variant range"),
                new TableCellInput(sqlServerVariantTemporalColumn.Name, TableCellInputMode.Value, "smalldatetime:1000-01-01T00:00:00")
            }));
        rejectedSnapshot = await session.LoadTableDataAsync(database, table);
        Assert(
            rejectedSnapshot.Rows.All(row => Convert.ToString(row.Values[1]) != "Rejected variant range"),
            "SQL Server sql_variant server range 拒絕時不可留下半筆資料");

        await AssertThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => session.InsertTableRowAsync(
            database,
            table,
            new[]
            {
                new TableCellInput("name", TableCellInputMode.Value, "Rejected variant encoding"),
                new TableCellInput(
                    sqlServerVariantTextColumn.Name,
                    TableCellInputMode.Value,
                    "varchar(2)@SQL_Latin1_General_CP1_CI_AS|1033|196609:漢字")
            }));
        rejectedSnapshot = await session.LoadTableDataAsync(database, table);
        Assert(
            rejectedSnapshot.Rows.All(row => Convert.ToString(row.Values[1]) != "Rejected variant encoding"),
            "SQL Server sql_variant varchar 不可靜默替換無法由 collation 表示的 Unicode 字元");
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
            sqlServerAliasCountColumn.StorageDataTypeName == "int" &&
            sqlServerAliasCountColumn.IntegerMinimum == int.MinValue &&
            sqlServerAliasCountColumn.IntegerMaximum == int.MaxValue,
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

        await AssertThrowsAsync<InvalidOperationException>(() => session.InsertTableRowAsync(
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
    var postgreSqlDomainCountColumn = before.Columns.SingleOrDefault(column => column.Name == "domain_count");
    var postgreSqlDomainLabelColumn = before.Columns.SingleOrDefault(column => column.Name == "domain_label");
    var postgreSqlDomainAmountColumn = before.Columns.SingleOrDefault(column => column.Name == "domain_amount");
    var postgreSqlDomainStateColumn = before.Columns.SingleOrDefault(column => column.Name == "domain_state");
    var postgreSqlDomainSubnetColumn = before.Columns.SingleOrDefault(column => column.Name == "domain_subnet");
    if (postgreSqlDomainCountColumn is not null)
    {
        Assert(
            postgreSqlDomainLabelColumn is not null &&
            postgreSqlDomainAmountColumn is not null &&
            postgreSqlDomainStateColumn is not null &&
            postgreSqlDomainSubnetColumn is not null,
            "PostgreSQL domain metadata 不完整");
        Assert(
            postgreSqlDomainCountColumn.ValueKind == TableColumnValueKind.Integer &&
            postgreSqlDomainCountColumn.DataTypeName == "\"public\".\"positive_count\" (integer)" &&
            postgreSqlDomainCountColumn.StorageDataTypeName == "integer" &&
            postgreSqlDomainCountColumn.IntegerMinimum == int.MinValue &&
            postgreSqlDomainCountColumn.IntegerMaximum == int.MaxValue,
            $"PostgreSQL integer domain metadata 不正確；actual={postgreSqlDomainCountColumn.DataTypeName}");
        Assert(
            postgreSqlDomainLabelColumn!.ValueKind == TableColumnValueKind.String &&
            postgreSqlDomainLabelColumn.DataTypeName == "\"public\".\"short_label\" (character varying(30))" &&
            postgreSqlDomainLabelColumn.StorageDataTypeName == "character varying(30)",
            $"PostgreSQL varchar domain metadata 不正確；actual={postgreSqlDomainLabelColumn.DataTypeName}");
        Assert(
            postgreSqlDomainAmountColumn!.ValueKind == TableColumnValueKind.ExactDecimal &&
            postgreSqlDomainAmountColumn.DataTypeName == "\"public\".\"precise_amount\" (numeric(18,6))" &&
            postgreSqlDomainAmountColumn.StorageDataTypeName == "numeric(18,6)",
            $"PostgreSQL numeric domain metadata 不正確；actual={postgreSqlDomainAmountColumn.DataTypeName}");
        Assert(
            postgreSqlDomainStateColumn!.ValueKind == TableColumnValueKind.PostgreSqlServerValidatedText &&
            postgreSqlDomainStateColumn.DataTypeName == "\"public\".\"work_state\" (mood)" &&
            postgreSqlDomainStateColumn.StorageDataTypeName == "mood",
            $"PostgreSQL enum domain metadata 不正確；actual={postgreSqlDomainStateColumn.DataTypeName}");
        Assert(
            postgreSqlDomainSubnetColumn!.ValueKind == TableColumnValueKind.NetworkAddress &&
            postgreSqlDomainSubnetColumn.DataTypeName == "\"public\".\"subnet_domain\" (cidr)" &&
            postgreSqlDomainSubnetColumn.StorageDataTypeName == "cidr",
            $"PostgreSQL CIDR domain metadata 不正確；actual={postgreSqlDomainSubnetColumn.DataTypeName}");
        insertInputs.Add(new TableCellInput(
            postgreSqlDomainCountColumn.Name,
            TableCellInputMode.Value,
            "42"));
        insertInputs.Add(new TableCellInput(
            postgreSqlDomainLabelColumn.Name,
            TableCellInputMode.Value,
            "Domain insert"));

        await AssertThrowsAsync<PostgresException>(() => session.InsertTableRowAsync(
            database,
            table,
            new[]
            {
                new TableCellInput("name", TableCellInputMode.Value, "Rejected domain constraint"),
                new TableCellInput(postgreSqlDomainCountColumn.Name, TableCellInputMode.Value, "0")
            }));
        var rejectedSnapshot = await session.LoadTableDataAsync(database, table);
        Assert(
            rejectedSnapshot.Rows.All(row => Convert.ToString(row.Values[1]) != "Rejected domain constraint"),
            "PostgreSQL domain constraint 拒絕時不可留下半筆資料");
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
        insertInputs.Add(new TableCellInput(setColumn.Name, TableCellInputMode.Value, "Beta,alpha"));
    }
    if (enumColumn is not null)
    {
        await AssertThrowsAsync<InvalidOperationException>(() => session.InsertTableRowAsync(
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
            "MySQL／MariaDB 應在送出 SQL 前拒絕未宣告的 ENUM 值");
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
        column.Name == "duration" && column.ValueKind == TableColumnValueKind.Interval);
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
            serverValidatedTextColumns.Count == 22,
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
            Convert.ToString(inserted.Values[setColumn.Ordinal]) == "alpha,Beta",
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
    if (sqlServerVariantValueColumn is not null)
    {
        Assert(
            Convert.ToString(inserted.Values[sqlServerVariantValueColumn.Ordinal]) == "int:42",
            $"SQL Server sql_variant int 安全新增不正確；actual={inserted.Values[sqlServerVariantValueColumn.Ordinal]}");
        var insertedVariantText = Convert.ToString(inserted.Values[sqlServerVariantTextColumn!.Ordinal]);
        Assert(
            insertedVariantText?.StartsWith("nvarchar(30)@", StringComparison.Ordinal) == true &&
            insertedVariantText.EndsWith(":Variant insert:文字  ", StringComparison.Ordinal),
            $"SQL Server sql_variant nvarchar 安全新增不正確；actual={insertedVariantText}");
        Assert(
            Convert.ToString(inserted.Values[sqlServerVariantTemporalColumn!.Ordinal]) ==
            "datetimeoffset(3):2026-08-30T12:34:56.7890000+08:00",
            $"SQL Server sql_variant datetimeoffset 安全新增不正確；actual={inserted.Values[sqlServerVariantTemporalColumn.Ordinal]}");
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
    if (postgreSqlDomainCountColumn is not null)
    {
        Assert(
            Convert.ToInt64(inserted.Values[postgreSqlDomainCountColumn.Ordinal]) == 42,
            "PostgreSQL integer domain 安全新增不正確");
        Assert(
            Convert.ToString(inserted.Values[postgreSqlDomainLabelColumn!.Ordinal]) == "Domain insert",
            "PostgreSQL varchar domain 安全新增不正確");
    }

    var firstPage = await session.LoadTableDataAsync(database, table, rowLimit: 1, rowOffset: 0);
    var secondPage = await session.LoadTableDataAsync(database, table, rowLimit: 1, rowOffset: 1);
    Assert(firstPage.HasNextPage && !firstPage.HasPreviousPage, $"{session.Profile.ProviderDisplayName} 第一頁導覽狀態不正確");
    Assert(secondPage.HasPreviousPage, $"{session.Profile.ProviderDisplayName} 第二頁導覽狀態不正確");
    Assert(
        !Equals(firstPage.Rows[0].Values[0], secondPage.Rows[0].Values[0]),
        $"{session.Profile.ProviderDisplayName} 分頁未依 Primary Key 前進");

    var sortedFirstPage = await session.LoadTableDataAsync(
        database,
        table,
        rowLimit: 1,
        rowOffset: 0,
        sort: new TableDataSort("name", Descending: true));
    var sortedSecondPage = await session.LoadTableDataAsync(
        database,
        table,
        rowLimit: 1,
        rowOffset: 1,
        sort: new TableDataSort("name", Descending: true));
    var expectedSortedIds = await session.ExecuteAsync(
        database,
        BuildStableSortProbeSql(session.Profile.Provider, table));
    Assert(
        Convert.ToString(sortedFirstPage.Rows.Single().Values[0], CultureInfo.InvariantCulture) ==
            Convert.ToString(expectedSortedIds.Rows[0][0], CultureInfo.InvariantCulture) &&
        Convert.ToString(sortedSecondPage.Rows.Single().Values[0], CultureInfo.InvariantCulture) ==
            Convert.ToString(expectedSortedIds.Rows[1][0], CultureInfo.InvariantCulture),
        $"{session.Profile.ProviderDisplayName} 欄位排序未使用 provider 原生順序或 Primary Key tie-breaker");
    await AssertThrowsAsync<ArgumentException>(() => session.LoadTableDataAsync(
        database,
        table,
        sort: new TableDataSort("missing_sort_column", Descending: false)));
    await AssertThrowsAsync<InvalidOperationException>(() => session.LoadTableDataAsync(
        database,
        table,
        sort: new TableDataSort("payload", Descending: false)));

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
        updateInputs.Add(new TableCellInput(setColumn.Name, TableCellInputMode.Value, "café,Beta"));
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
    if (sqlServerVariantValueColumn is not null)
    {
        updateInputs.Add(new TableCellInput(
            sqlServerVariantValueColumn.Name,
            TableCellInputMode.Value,
            "numeric(20,8):123456789.87654321"));
        updateInputs.Add(new TableCellInput(
            sqlServerVariantTextColumn!.Name,
            TableCellInputMode.Value,
            "varbinary(4):0xCAFE"));
        updateInputs.Add(new TableCellInput(
            sqlServerVariantTemporalColumn!.Name,
            TableCellInputMode.Value,
            "time(4):12:34:56.7890"));
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
    if (postgreSqlDomainCountColumn is not null)
    {
        updateInputs.Add(new TableCellInput(
            postgreSqlDomainCountColumn.Name,
            TableCellInputMode.Value,
            "84"));
        updateInputs.Add(new TableCellInput(
            postgreSqlDomainLabelColumn!.Name,
            TableCellInputMode.Value,
            "Domain updated"));
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
            Convert.ToString(updated.Values[setColumn.Ordinal]) == "Beta,café",
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
    if (sqlServerVariantValueColumn is not null)
    {
        Assert(
            Convert.ToString(updated.Values[sqlServerVariantValueColumn.Ordinal]) ==
            "numeric(20,8):123456789.87654321",
            $"SQL Server sql_variant numeric 安全修改不正確；actual={updated.Values[sqlServerVariantValueColumn.Ordinal]}");
        Assert(
            Convert.ToString(updated.Values[sqlServerVariantTextColumn!.Ordinal]) ==
            "varbinary(4):0xCAFE",
            $"SQL Server sql_variant varbinary 安全修改不正確；actual={updated.Values[sqlServerVariantTextColumn.Ordinal]}");
        Assert(
            Convert.ToString(updated.Values[sqlServerVariantTemporalColumn!.Ordinal]) ==
            "time(4):12:34:56.7890000",
            $"SQL Server sql_variant time 安全修改不正確；actual={updated.Values[sqlServerVariantTemporalColumn.Ordinal]}");
        var properties = await session.ExecuteAsync(
            database,
            $"SELECT CONVERT(varchar(30), SQL_VARIANT_PROPERTY(variant_value, 'BaseType')), " +
            $"CONVERT(int, SQL_VARIANT_PROPERTY(variant_value, 'Precision')), " +
            $"CONVERT(int, SQL_VARIANT_PROPERTY(variant_value, 'Scale')), " +
            $"CONVERT(varchar(30), SQL_VARIANT_PROPERTY(variant_text, 'BaseType')), " +
            $"CONVERT(int, SQL_VARIANT_PROPERTY(variant_text, 'MaxLength')), " +
            $"CONVERT(varchar(30), SQL_VARIANT_PROPERTY(variant_temporal, 'BaseType')), " +
            $"CONVERT(int, SQL_VARIANT_PROPERTY(variant_temporal, 'Scale')) " +
            $"FROM dbo.sample WHERE id = {Convert.ToInt64(updated.Values[0])};");
        var propertyRow = properties.Rows.Single();
        Assert(
            Convert.ToString(propertyRow[0]) == "numeric" &&
            Convert.ToInt32(propertyRow[1]) == 20 &&
            Convert.ToInt32(propertyRow[2]) == 8 &&
            Convert.ToString(propertyRow[3]) == "varbinary" &&
            Convert.ToInt32(propertyRow[4]) == 4 &&
            Convert.ToString(propertyRow[5]) == "time" &&
            Convert.ToInt32(propertyRow[6]) == 4,
            "SQL Server SQL_VARIANT_PROPERTY 直接查庫未保留修改後的 base type metadata");
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
    if (postgreSqlDomainCountColumn is not null)
    {
        Assert(
            Convert.ToInt64(updated.Values[postgreSqlDomainCountColumn.Ordinal]) == 84,
            "PostgreSQL integer domain 安全修改不正確");
        Assert(
            Convert.ToString(updated.Values[postgreSqlDomainLabelColumn!.Ordinal]) == "Domain updated",
            "PostgreSQL varchar domain 安全修改不正確");
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

static string BuildStableSortProbeSql(DatabaseProviderKind provider, DatabaseObjectInfo table)
{
    string Quote(string identifier) => provider switch
    {
        DatabaseProviderKind.MySql => $"`{identifier.Replace("`", "``", StringComparison.Ordinal)}`",
        DatabaseProviderKind.SqlServer => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]",
        _ => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
    };

    var qualifiedName = string.IsNullOrWhiteSpace(table.Schema)
        ? Quote(table.Name)
        : $"{Quote(table.Schema)}.{Quote(table.Name)}";
    return $"SELECT {Quote("id")} FROM {qualifiedName} ORDER BY {Quote("name")} DESC, {Quote("id")} ASC;";
}

static string GetNetworkTestValue(TableColumnInfo column, bool updated) =>
    (column.StorageDataTypeName.ToLowerInvariant(), updated) switch
    {
        ("inet", false) => "192.0.2.10/24",
        ("inet", true) => "2001:db8::10/64",
        ("cidr", false) => "192.0.2.0/24",
        ("cidr", true) => "2001:db8::/48",
        ("macaddr", false) => "08:00:2b:01:02:03",
        ("macaddr", true) => "08:00:2b:01:02:04",
        ("macaddr8", false) => "08:00:2b:ff:fe:01:02:03",
        ("macaddr8", true) => "08:00:2b:ff:fe:01:02:04",
        ("inet4", false) => "192.0.2.20",
        ("inet4", true) => "203.0.113.20",
        ("inet6", false) => "2001:db8::20",
        ("inet6", true) => "::ffff:192.0.2.20",
        _ => throw new InvalidOperationException($"缺少 {column.DataTypeName} 測試值。")
    };

static string GetSpatialTestValue(
    DatabaseProviderKind provider,
    TableColumnInfo column,
    bool updated) =>
    (provider, column.StorageDataTypeName.ToLowerInvariant(), updated) switch
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
    if (column.StorageDataTypeName.Equals("multipoint", StringComparison.OrdinalIgnoreCase))
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
    (column.StorageDataTypeName.ToLowerInvariant(), updated) switch
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
    (column.StorageDataTypeName.ToLowerInvariant(), updated) switch
    {
        ("bit(8)", false) => "165",
        ("bit(8)", true) => "90",
        ("bit(64)", false) => "18446744073709551615",
        ("bit(64)", true) => "9223372036854775808",
        _ => throw new InvalidOperationException($"缺少 {column.DataTypeName} 測試值。")
    };

static string GetBitStringTestValue(TableColumnInfo column, bool updated) =>
    (column.StorageDataTypeName.ToLowerInvariant(), updated) switch
    {
        ("bit(8)", false) => "10100101",
        ("bit(8)", true) => "01011010",
        ("bit varying(16)", false) => "101011",
        ("bit varying(16)", true) => "1111000011110000",
        _ => throw new InvalidOperationException($"缺少 {column.DataTypeName} 測試值。")
    };

static string GetPostgreSqlRangeTestValue(TableColumnInfo column, bool updated) =>
    (column.StorageDataTypeName.ToLowerInvariant(), updated) switch
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
    (column.StorageDataTypeName.ToLowerInvariant(), updated) switch
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

static async Task<TException> CaptureExceptionAsync<TException>(Func<Task> action)
    where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException exception)
    {
        return exception;
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
