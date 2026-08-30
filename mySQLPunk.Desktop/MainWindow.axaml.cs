using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using MySqlPunk.Core.Models;
using MySqlPunk.Core.Providers;
using MySqlPunk.Core.Services;

namespace MySqlPunk.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly ObservableCollection<ConnectionProfile> _profiles = new();
    private readonly Dictionary<Guid, string> _runtimePasswords = new();
    private readonly ConnectionProfileStore _profileStore = new();
    private readonly ISecretStore _secretStore = SecretStoreFactory.CreateDefault();
    private readonly CrossPlatformUpdateService _updateService = new();
    private readonly QueryExecutionHistory _queryHistory = new();
    private readonly TableColumnPreferenceStore _tableColumnPreferenceStore = new();
    private IDatabaseSession? _session;
    private IReadOnlyList<DatabaseObjectInfo> _databaseObjects = Array.Empty<DatabaseObjectInfo>();
    private QueryResult? _lastResult;
    private CancellationTokenSource? _operationCancellation;
    private bool _loadingDatabases;

    private readonly ListBox _profilesList;
    private readonly ComboBox _databaseCombo;
    private readonly TreeView _objectsTree;
    private readonly TextBox _objectSearchBox;
    private readonly ComboBox _objectTypeCombo;
    private readonly TextBlock _objectCountText;
    private readonly TextBox _sqlEditor;
    private readonly ComboBox _queryHistoryCombo;
    private readonly DataGrid _resultsGrid;
    private readonly ComboBox _exportFormatCombo;
    private readonly TextBlock _statusText;
    private readonly TextBlock _connectionBadge;
    private readonly Button _connectButton;
    private readonly Button _refreshButton;
    private readonly Button _executeButton;
    private readonly Button _exportButton;
    private readonly Button _cancelButton;
    private readonly Button _updateButton;
    private readonly Button _clearQueryHistoryButton;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _profilesList = this.FindControl<ListBox>("ProfilesList")!;
        _databaseCombo = this.FindControl<ComboBox>("DatabaseCombo")!;
        _objectsTree = this.FindControl<TreeView>("ObjectsTree")!;
        _objectSearchBox = this.FindControl<TextBox>("ObjectSearchBox")!;
        _objectTypeCombo = this.FindControl<ComboBox>("ObjectTypeCombo")!;
        _objectCountText = this.FindControl<TextBlock>("ObjectCountText")!;
        _sqlEditor = this.FindControl<TextBox>("SqlEditor")!;
        _queryHistoryCombo = this.FindControl<ComboBox>("QueryHistoryCombo")!;
        _resultsGrid = this.FindControl<DataGrid>("ResultsGrid")!;
        _exportFormatCombo = this.FindControl<ComboBox>("ExportFormatCombo")!;
        _statusText = this.FindControl<TextBlock>("StatusText")!;
        _connectionBadge = this.FindControl<TextBlock>("ConnectionBadge")!;
        _connectButton = this.FindControl<Button>("ConnectButton")!;
        _refreshButton = this.FindControl<Button>("RefreshButton")!;
        _executeButton = this.FindControl<Button>("ExecuteButton")!;
        _exportButton = this.FindControl<Button>("ExportButton")!;
        _cancelButton = this.FindControl<Button>("CancelButton")!;
        _updateButton = this.FindControl<Button>("UpdateButton")!;
        _clearQueryHistoryButton = this.FindControl<Button>("ClearQueryHistoryButton")!;

        _profilesList.ItemsSource = _profiles;
        _objectTypeCombo.SelectedIndex = 0;
        _sqlEditor.AddHandler(KeyDownEvent, SqlEditor_KeyDown, RoutingStrategies.Tunnel);
        Opened += MainWindow_Opened;
    }

    private async void MainWindow_Opened(object? sender, EventArgs e)
    {
        Opened -= MainWindow_Opened;
        try
        {
            var profiles = await _profileStore.LoadAsync();
            foreach (var profile in profiles)
            {
                _profiles.Add(profile);
            }

            if (_profiles.Count > 0)
            {
                _profilesList.SelectedIndex = 0;
                var secretStatus = _profiles.Any(profile => profile.UseSecretStore)
                    ? (_secretStore.IsAvailable
                        ? $"已設定的密碼會在連線時由 {_secretStore.DisplayName} 讀取。"
                        : _secretStore.UnavailableReason)
                    : "未保存的密碼會在連線時要求輸入。";
                SetStatus($"已載入 {_profiles.Count} 組連線設定；{secretStatus}");
            }
        }
        catch (Exception exception)
        {
            await ShowErrorAsync("無法載入連線設定", exception);
        }

        await ShowPreviousUpdateFailureAsync();

        UpdateActionState();
    }

    private async void AddProfile_Click(object? sender, RoutedEventArgs e)
    {
        var editor = new ConnectionEditorWindow(new ConnectionProfile(), secretStore: _secretStore);
        var result = await editor.ShowDialog<ConnectionProfile?>(this);
        if (result is null)
        {
            return;
        }

        var passwordStatus = await ApplyPasswordPreferenceAsync(original: null, result);
        result.Password = string.Empty;
        _profiles.Add(result);
        _profilesList.SelectedItem = result;
        await SaveProfilesAsync();
        SetStatus($"已新增「{result.Name}」；{passwordStatus} 可以按連線開始使用。");
    }

    private async void EditProfile_Click(object? sender, RoutedEventArgs e)
    {
        if (_profilesList.SelectedItem is not ConnectionProfile selected)
        {
            return;
        }

        var editable = selected.Clone();
        var passwordResolution = await ResolvePasswordAsync(selected);
        if (passwordResolution.Found)
        {
            editable.Password = passwordResolution.Password;
        }

        var editor = new ConnectionEditorWindow(editable, passwordResolution.Warning, _secretStore);
        var result = await editor.ShowDialog<ConnectionProfile?>(this);
        if (result is null)
        {
            return;
        }

        var index = _profiles.IndexOf(selected);
        var passwordStatus = await ApplyPasswordPreferenceAsync(selected, result);
        result.Password = string.Empty;
        _profiles[index] = result;
        _profilesList.SelectedIndex = index;
        await SaveProfilesAsync();
        Disconnect($"已更新「{result.Name}」；{passwordStatus} 請重新連線。");
    }

    private async void DeleteProfile_Click(object? sender, RoutedEventArgs e)
    {
        if (_profilesList.SelectedItem is not ConnectionProfile selected)
        {
            return;
        }

        var confirmed = await MessageDialog.ShowAsync(
            this,
            "刪除連線設定",
            $"確定要刪除「{selected.Name}」嗎？資料庫本身不會被刪除。",
            showCancel: true);
        if (!confirmed)
        {
            return;
        }

        var wasConnected = _session?.Profile.Id == selected.Id;
        _runtimePasswords.Remove(selected.Id);
        var secretWarning = selected.UseSecretStore
            ? await DeleteStoredPasswordAsync(selected.Id)
            : string.Empty;
        var preferenceWarning = await DeleteTableColumnPreferencesAsync(selected.Id);
        _profiles.Remove(selected);
        await SaveProfilesAsync();
        if (wasConnected)
        {
            Disconnect($"連線設定已刪除。{secretWarning}{preferenceWarning}");
        }
        else
        {
            SetStatus($"連線設定已刪除。{secretWarning}{preferenceWarning}");
        }

        UpdateActionState();
    }

    private async void ConnectProfile_Click(object? sender, RoutedEventArgs e)
    {
        if (_profilesList.SelectedItem is not ConnectionProfile selected)
        {
            return;
        }

        var connectionProfile = selected.Clone();
        var passwordResolution = await ResolvePasswordAsync(selected);
        if (selected.Provider != DatabaseProviderKind.Sqlite &&
            !passwordResolution.Found)
        {
            var hint = string.IsNullOrWhiteSpace(passwordResolution.Warning)
                ? "請輸入密碼後連線；可選擇交由系統密碼庫安全保存。"
                : $"{passwordResolution.Warning} 請重新輸入密碼後連線。";
            var editor = new ConnectionEditorWindow(connectionProfile, hint, _secretStore);
            var edited = await editor.ShowDialog<ConnectionProfile?>(this);
            if (edited is null)
            {
                return;
            }

            connectionProfile = edited.Clone();
            await ApplyPasswordPreferenceAsync(selected, edited);
            await UpdateStoredProfileAsync(selected, edited);
        }
        else
        {
            connectionProfile.Password = passwordResolution.Password;
        }

        Disconnect($"準備連線至 {connectionProfile.Name}…");
        await RunOperationAsync($"正在連線至 {connectionProfile.Name}…", async cancellationToken =>
        {
            var session = DatabaseProviderFactory.Create(connectionProfile);
            await session.TestConnectionAsync(cancellationToken);
            var databases = await session.GetDatabasesAsync(cancellationToken);

            _session = session;
            _loadingDatabases = true;
            try
            {
                _databaseCombo.ItemsSource = databases;
                var preferredIndex = databases
                    .Select((name, index) => (name, index))
                    .FirstOrDefault(item => string.Equals(item.name, connectionProfile.Database, StringComparison.OrdinalIgnoreCase))
                    .index;
                _databaseCombo.SelectedIndex = databases.Count == 0 ? -1 : preferredIndex;
            }
            finally
            {
                _loadingDatabases = false;
            }

            _connectionBadge.Text = $"已連線 · {connectionProfile.Name}";
            if (_databaseCombo.SelectedItem is string database)
            {
                await LoadObjectsAsync(database, cancellationToken);
            }

            SetStatus($"已連線至 {connectionProfile.ProviderDisplayName}；共 {databases.Count} 個可用資料庫。");
        });
    }

    private void ProfilesList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateActionState();
    }

    private void ProfilesList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_profilesList.SelectedItem is not null && _connectButton.IsEnabled)
        {
            ConnectProfile_Click(sender, new RoutedEventArgs());
        }
    }

    private async void DatabaseCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingDatabases || _session is null || _databaseCombo.SelectedItem is not string database)
        {
            UpdateActionState();
            return;
        }

        await RunOperationAsync($"正在讀取 {database} 的物件…", cancellationToken =>
            LoadObjectsAsync(database, cancellationToken));
    }

    private async void RefreshObjects_Click(object? sender, RoutedEventArgs e)
    {
        if (_session is null || _databaseCombo.SelectedItem is not string database)
        {
            return;
        }

        await RunOperationAsync($"正在重新整理 {database}…", cancellationToken =>
            LoadObjectsAsync(database, cancellationToken));
    }

    private async void ObjectsTree_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_session is null || _objectsTree.SelectedItem is not ObjectTreeItem { DatabaseObject: { } databaseObject })
        {
            return;
        }

        if (databaseObject.Kind == DatabaseObjectKind.Table &&
            _databaseCombo.SelectedItem is string database)
        {
            var editor = new TableDataEditorWindow(
                _session,
                database,
                databaseObject,
                _tableColumnPreferenceStore);
            await editor.ShowDialog(this);
            SetStatus($"已關閉 {databaseObject.DisplayName} 資料編輯器。");
            return;
        }

        _sqlEditor.Text = _session.BuildSelectPreview(databaseObject);
        _sqlEditor.Focus();
        _sqlEditor.CaretIndex = _sqlEditor.Text?.Length ?? 0;
        SetStatus($"已產生 {databaseObject.DisplayName} 的前 200 列預覽 SQL；按 Ctrl+Enter 執行。");
    }

    private void ObjectSearch_Changed(object? sender, TextChangedEventArgs e)
    {
        ApplyObjectFilter();
    }

    private void ObjectType_Changed(object? sender, SelectionChangedEventArgs e)
    {
        ApplyObjectFilter();
    }

    private async void ExecuteSql_Click(object? sender, RoutedEventArgs e)
    {
        await ExecuteCurrentSqlAsync();
    }

    private void QueryHistory_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_queryHistoryCombo.SelectedItem is not QueryExecutionHistoryEntry entry)
        {
            return;
        }

        _sqlEditor.Text = entry.Sql;
        _sqlEditor.SelectionStart = entry.Sql.Length;
        _sqlEditor.SelectionEnd = entry.Sql.Length;
        _queryHistoryCombo.SelectedIndex = -1;
        _sqlEditor.Focus();
        SetStatus($"已從本次記錄載入 {entry.SourceDisplay} 的 SQL；尚未執行。");
    }

    private void ClearQueryHistory_Click(object? sender, RoutedEventArgs e)
    {
        _queryHistory.Clear();
        RefreshQueryHistory();
        SetStatus("本次查詢記錄已清除；沒有修改資料庫或磁碟檔案。");
    }

    private async void SqlEditor_KeyDown(object? sender, KeyEventArgs e)
    {
        var executeModifier = e.KeyModifiers.HasFlag(KeyModifiers.Control) ||
                              e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (e.Key is Key.Enter or Key.Return && executeModifier)
        {
            e.Handled = true;
            await ExecuteCurrentSqlAsync();
        }
    }

    private void CancelOperation_Click(object? sender, RoutedEventArgs e)
    {
        _operationCancellation?.Cancel();
    }

    private async void CheckUpdates_Click(object? sender, RoutedEventArgs e)
    {
        await RunOperationAsync("正在檢查 GitHub Release…", async cancellationToken =>
        {
            var currentVersion = typeof(MainWindow).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);
            var update = await _updateService.CheckLatestAsync(
                currentVersion.ToString(),
                cancellationToken: cancellationToken);
            if (!update.UpdateAvailable)
            {
                SetStatus($"目前已是最新版 {currentVersion}。");
                await MessageDialog.ShowAsync(
                    this,
                    "沒有可用更新",
                    $"目前版本 {currentVersion} 已是最新公開版本。",
                    showCancel: false);
                return;
            }

            if (!update.HasPackageAndChecksum)
            {
                var openReleasePage = await MessageDialog.ShowAsync(
                    this,
                    $"有新版本 {update.LatestVersionText}",
                    $"{update.ReleaseName}\n\n目前版本：{currentVersion}\n最新版本：{update.LatestVersionText}\n這個 Release 尚未同時提供 {update.RuntimeIdentifier} 安裝包與 SHA-256。\n\n是否開啟 GitHub Release 頁確認？",
                    showCancel: true,
                    confirmText: "開啟 Release 頁");
                if (openReleasePage)
                {
                    OpenReleasePage(update);
                }
                else
                {
                    SetStatus($"已找到 {update.LatestVersionText}，稍後可再檢查更新。");
                }
                return;
            }

            var shouldDownload = await MessageDialog.ShowAsync(
                this,
                $"有新版本 {update.LatestVersionText}",
                $"{update.ReleaseName}\n\n目前版本：{currentVersion}\n最新版本：{update.LatestVersionText}\n已找到 {update.RuntimeIdentifier} 的 {update.PackageFileName} 與同名 SHA-256。\n\n是否下載並驗證安裝包？",
                showCancel: true,
                confirmText: "下載並驗證");
            if (!shouldDownload)
            {
                SetStatus($"已找到 {update.LatestVersionText}，稍後可再檢查更新。");
                return;
            }

            if (!StorageProvider.CanSave)
            {
                await MessageDialog.ShowAsync(
                    this,
                    "無法選擇下載位置",
                    "目前桌面環境未提供儲存檔案對話框，將改為開啟 GitHub Release 頁。",
                    showCancel: false);
                OpenReleasePage(update);
                return;
            }

            var isLinuxPackage = update.RuntimeIdentifier.StartsWith("linux-", StringComparison.Ordinal);
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = $"下載 mySQLPunk {update.LatestVersionText}",
                SuggestedFileName = update.PackageFileName,
                FileTypeChoices = new[]
                {
                    new FilePickerFileType(isLinuxPackage ? "Linux 安裝壓縮檔" : "macOS app 壓縮檔")
                    {
                        Patterns = isLinuxPackage ? new[] { "*.tar.gz" } : new[] { "*.app.zip", "*.zip" },
                        MimeTypes = isLinuxPackage ? new[] { "application/gzip" } : new[] { "application/zip" }
                    }
                }
            });
            if (file is null)
            {
                SetStatus($"已取消下載 {update.LatestVersionText}。");
                return;
            }
            if (file.TryGetLocalPath() is not { } destinationPath)
            {
                throw new InvalidOperationException("目前只能把更新安裝包下載到本機檔案。");
            }

            SetStatus($"正在下載並驗證 {update.PackageFileName}…");
            var download = await _updateService.DownloadPackageAsync(
                update,
                destinationPath,
                cancellationToken);
            var installHint = isLinuxPackage
                ? "可由程式安全套用並重新啟動；既有連線設定會保留。"
                : "可由程式安全取代目前 app bundle 並重新啟動；既有連線設定會保留。";
            SetStatus($"已下載並驗證 {download.FormattedBytes}：{download.Path}");
            var canApplyLinuxUpdate = isLinuxPackage && OperatingSystem.IsLinux();
            var applyScriptPath = Path.Combine(AppContext.BaseDirectory, "apply-update.sh");
            var macOsBundlePath = ResolveCurrentMacOsAppBundlePath();
            var canApplyMacOsUpdate = !isLinuxPackage &&
                                      OperatingSystem.IsMacOS() &&
                                      macOsBundlePath is not null;
            if ((canApplyLinuxUpdate || canApplyMacOsUpdate) && File.Exists(applyScriptPath))
            {
                var shouldApply = await MessageDialog.ShowAsync(
                    this,
                    "更新安裝包已驗證",
                    $"檔案：{download.Path}\n大小：{download.FormattedBytes}\nSHA-256：{download.Sha256}\n\n套用後會關閉目前程式；新版若無法正常啟動，會自動回復並重新開啟舊版。是否現在套用？",
                    showCancel: true,
                    confirmText: "套用並重新啟動");
                if (shouldApply)
                {
                    using var updaterProcess = canApplyLinuxUpdate
                        ? _updateService.StartLinuxApply(
                            update,
                            download,
                            applyScriptPath,
                            Environment.ProcessId)
                        : _updateService.StartMacOsApply(
                            update,
                            download,
                            applyScriptPath,
                            macOsBundlePath!,
                            Environment.ProcessId);

                    SetStatus($"正在關閉程式並套用 {update.LatestVersionText}…");
                    if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                    {
                        desktop.Shutdown();
                    }
                    else
                    {
                        Close();
                    }
                    return;
                }

                SetStatus($"已下載並驗證 {update.LatestVersionText}，尚未套用：{download.Path}");
                return;
            }

            await MessageDialog.ShowAsync(
                this,
                "更新安裝包已驗證",
                $"檔案：{download.Path}\n大小：{download.FormattedBytes}\nSHA-256：{download.Sha256}\n\n{installHint}",
                showCancel: false);
        });
    }

    private async Task ShowPreviousUpdateFailureAsync()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            var platformName = OperatingSystem.IsMacOS() ? "macOS" : "Linux";
            var result = OperatingSystem.IsMacOS()
                ? _updateService.ReadAndClearMacOsApplyResult()
                : _updateService.ReadAndClearLinuxApplyResult();
            if (result is null)
            {
                return;
            }

            var title = result.WasRolledBack
                ? $"{platformName} 更新已回復"
                : $"{platformName} 更新未完成";
            var recovery = result.WasRolledBack
                ? "已重新啟動前一個可用版本。"
                : "目前安裝內容沒有被替換。";
            SetStatus($"{title}：{result.Message}");
            await MessageDialog.ShowAsync(
                this,
                title,
                $"版本：{result.Version} ({result.RuntimeIdentifier})\n{recovery}\n\n原因：{result.Message}\n記錄：{result.LogPath}",
                showCancel: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            SetStatus($"無法讀取前次平台更新結果：{exception.Message}");
        }
    }

    private static string? ResolveCurrentMacOsAppBundlePath()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return null;
        }

        var bundlePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));
        return string.Equals(Path.GetFileName(bundlePath), "mySQLPunk.app", StringComparison.Ordinal) &&
               File.Exists(Path.Combine(bundlePath, "Contents", "Info.plist"))
            ? bundlePath
            : null;
    }

    private void OpenReleasePage(CrossPlatformUpdateInfo update)
    {
        Process.Start(new ProcessStartInfo(update.ReleasePageUri.AbsoluteUri)
        {
            UseShellExecute = true
        });
        SetStatus($"已開啟 {update.LatestVersionText} GitHub Release 頁。");
    }

    private async Task ExecuteCurrentSqlAsync()
    {
        if (_session is null || _databaseCombo.SelectedItem is not string database)
        {
            await MessageDialog.ShowAsync(this, "尚未連線", "請先選擇連線設定並連線。", showCancel: false);
            return;
        }

        var execution = SqlExecutionSelectionService.Resolve(
            _sqlEditor.Text,
            _sqlEditor.SelectionStart,
            _sqlEditor.SelectionEnd);
        var operationStatus = execution.UsesSelection
            ? "正在執行選取的 SQL…"
            : "正在執行 SQL…";
        var executedAt = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();
        await RunOperationAsync(operationStatus, async cancellationToken =>
        {
            var result = await _session.ExecuteAsync(database, execution.Sql, cancellationToken);
            stopwatch.Stop();
            DisplayResult(result);
            var historyRecorded = _queryHistory.Add(new QueryExecutionHistoryEntry(
                executedAt,
                _session.Profile.Provider,
                database,
                execution.Sql,
                execution.UsesSelection,
                stopwatch.Elapsed,
                result.Summary));
            RefreshQueryHistory();
            if (!result.HasResultSet)
            {
                await LoadObjectsAsync(database, cancellationToken);
            }

            var executionSummary = execution.UsesSelection
                ? $"{result.Summary}（已執行選取範圍）"
                : result.Summary;
            SetStatus(historyRecorded
                ? executionSummary
                : $"{executionSummary}（SQL 超過本次記錄的 2 MiB 安全上限，未保留）");
        });
    }

    private void RefreshQueryHistory()
    {
        _queryHistoryCombo.ItemsSource = _queryHistory.Entries.ToArray();
        _queryHistoryCombo.PlaceholderText = $"本次查詢記錄（{_queryHistory.Entries.Count}）";
        _queryHistoryCombo.SelectedIndex = -1;
        var canUseHistory = _operationCancellation is null && _queryHistory.Entries.Count > 0;
        _queryHistoryCombo.IsEnabled = canUseHistory;
        _clearQueryHistoryButton.IsEnabled = canUseHistory;
    }

    private async Task LoadObjectsAsync(string database, CancellationToken cancellationToken)
    {
        if (_session is null)
        {
            return;
        }

        var objects = await _session.GetObjectsAsync(database, cancellationToken);
        _databaseObjects = objects;
        var filteredCount = ApplyObjectFilter();
        SetStatus($"{database}：已載入 {objects.Count} 個物件，目前顯示 {filteredCount} 個。");
    }

    private int ApplyObjectFilter()
    {
        var kind = _objectTypeCombo.SelectedIndex switch
        {
            1 => DatabaseObjectKind.Table,
            2 => DatabaseObjectKind.View,
            _ => (DatabaseObjectKind?)null
        };
        var filtered = DatabaseObjectFilterService.Filter(_databaseObjects, _objectSearchBox.Text, kind);
        var tableItems = filtered
            .Where(item => item.Kind == DatabaseObjectKind.Table)
            .Select(item => new ObjectTreeItem(item.DisplayName, item))
            .ToList();
        var viewItems = filtered
            .Where(item => item.Kind == DatabaseObjectKind.View)
            .Select(item => new ObjectTreeItem(item.DisplayName, item))
            .ToList();

        var roots = new List<ObjectTreeItem>();
        if (kind is null or DatabaseObjectKind.Table)
        {
            roots.Add(new ObjectTreeItem($"資料表 ({tableItems.Count})", children: tableItems));
        }

        if (kind is null or DatabaseObjectKind.View)
        {
            roots.Add(new ObjectTreeItem($"檢視表 ({viewItems.Count})", children: viewItems));
        }

        _objectsTree.ItemsSource = roots;
        _objectCountText.Text = filtered.Count == _databaseObjects.Count
            ? $"{filtered.Count} 個"
            : $"{filtered.Count} / {_databaseObjects.Count} 個";
        return filtered.Count;
    }

    private void DisplayResult(QueryResult result)
    {
        _lastResult = result.HasResultSet ? result : null;
        _resultsGrid.Columns.Clear();
        for (var index = 0; index < result.Columns.Count; index++)
        {
            _resultsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = result.Columns[index],
                Binding = new Binding($"Values[{index}]"),
                Width = new DataGridLength(1, DataGridLengthUnitType.Auto)
            });
        }

        _resultsGrid.ItemsSource = result.Rows
            .Select(row => new ResultRow(row.Select(value => value ?? "(NULL)").ToArray()))
            .ToList();
    }

    private async void ExportResult_Click(object? sender, RoutedEventArgs e)
    {
        var result = _lastResult;
        if (result is null)
        {
            return;
        }

        if (result.WasTruncated)
        {
            var confirmed = await MessageDialog.ShowAsync(
                this,
                "匯出截斷結果",
                $"這次查詢只載入前 {result.Rows.Count:N0} 列；匯出檔也只會包含目前載入的資料。是否繼續？",
                showCancel: true);
            if (!confirmed)
            {
                return;
            }
        }

        var selectedFormat = _exportFormatCombo.SelectedIndex switch
        {
            1 => QueryResultExportFormat.Tsv,
            2 => QueryResultExportFormat.Json,
            _ => QueryResultExportFormat.Csv
        };
        var extension = QueryResultExportService.GetDefaultExtension(selectedFormat);
        if (!StorageProvider.CanSave)
        {
            await MessageDialog.ShowAsync(
                this,
                "無法匯出查詢結果",
                "目前桌面環境未提供儲存檔案對話框。",
                showCancel: false);
            return;
        }

        var fileType = new FilePickerFileType(
            $"{QueryResultExportService.GetFormatDisplayName(selectedFormat)} 查詢結果")
        {
            Patterns = new[] { $"*.{extension}" },
            MimeTypes = selectedFormat switch
            {
                QueryResultExportFormat.Csv => new[] { "text/csv" },
                QueryResultExportFormat.Tsv => new[] { "text/tab-separated-values" },
                QueryResultExportFormat.Json => new[] { "application/json" },
                _ => Array.Empty<string>()
            }
        };
        IStorageFile? file;
        try
        {
            file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "匯出查詢結果",
                SuggestedFileName = $"mysqlpunk-result-{DateTime.Now:yyyyMMdd-HHmmss}.{extension}",
                DefaultExtension = extension,
                FileTypeChoices = new[] { fileType }
            });
        }
        catch (Exception exception)
        {
            await ShowErrorAsync("無法開啟儲存檔案對話框", exception);
            return;
        }

        if (file is null)
        {
            return;
        }

        if (file.TryGetLocalPath() is not { } path)
        {
            await MessageDialog.ShowAsync(
                this,
                "無法匯出查詢結果",
                "目前只能匯出到本機檔案。",
                showCancel: false);
            return;
        }

        var format = QueryResultExportService.ResolveFormat(path, selectedFormat);
        await RunOperationAsync("正在匯出查詢結果…", async cancellationToken =>
        {
            var summary = await QueryResultExportService.WriteFileAsync(result, path, format, cancellationToken);
            SetStatus(
                $"已匯出 {summary.Rows:N0} 列 {summary.FormatDisplayName}（{summary.FormattedBytes}）：{summary.Path}");
        });
    }

    private async Task RunOperationAsync(string status, Func<CancellationToken, Task> operation)
    {
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _operationCancellation = cancellation;
        SetBusy(true, status);

        try
        {
            await operation(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            SetStatus("操作已取消。");
        }
        catch (Exception exception)
        {
            await ShowErrorAsync("操作失敗", exception);
        }
        finally
        {
            if (ReferenceEquals(_operationCancellation, cancellation))
            {
                _operationCancellation = null;
                cancellation.Dispose();
                SetBusy(false);
            }
        }
    }

    private async Task UpdateStoredProfileAsync(ConnectionProfile original, ConnectionProfile edited)
    {
        var index = _profiles.IndexOf(original);
        edited.Password = string.Empty;
        _profiles[index] = edited;
        _profilesList.SelectedIndex = index;
        await SaveProfilesAsync();
    }

    private async Task<PasswordResolution> ResolvePasswordAsync(ConnectionProfile profile)
    {
        if (_runtimePasswords.TryGetValue(profile.Id, out var runtimePassword))
        {
            return new PasswordResolution(true, runtimePassword, null);
        }

        if (!profile.UseSecretStore)
        {
            return new PasswordResolution(false, string.Empty, null);
        }

        if (!_secretStore.IsAvailable)
        {
            return new PasswordResolution(false, string.Empty, _secretStore.UnavailableReason);
        }

        try
        {
            var storedPassword = await _secretStore.GetAsync(profile.Id);
            if (storedPassword is null)
            {
                return new PasswordResolution(
                    false,
                    string.Empty,
                    $"{_secretStore.DisplayName} 找不到這組連線的密碼。");
            }

            _runtimePasswords[profile.Id] = storedPassword;
            return new PasswordResolution(true, storedPassword, null);
        }
        catch (Exception exception) when (exception is SecretStoreException or IOException)
        {
            return new PasswordResolution(false, string.Empty, exception.Message);
        }
    }

    private async Task<string> ApplyPasswordPreferenceAsync(
        ConnectionProfile? original,
        ConnectionProfile edited)
    {
        if (edited.Provider == DatabaseProviderKind.Sqlite)
        {
            _runtimePasswords.Remove(edited.Id);
            edited.UseSecretStore = false;
            var warning = original?.UseSecretStore == true
                ? await DeleteStoredPasswordAsync(edited.Id)
                : string.Empty;
            return $"SQLite 不需要保存連線密碼。{warning}";
        }

        if (edited.PasswordChanged || original is null || !string.IsNullOrEmpty(edited.Password))
        {
            _runtimePasswords[edited.Id] = edited.Password;
        }

        if (!edited.UseSecretStore)
        {
            var warning = original?.UseSecretStore == true
                ? await DeleteStoredPasswordAsync(edited.Id)
                : string.Empty;
            return $"密碼只保留到本次程式關閉。{warning}";
        }

        if (!_secretStore.IsAvailable)
        {
            return $"{_secretStore.UnavailableReason} 密碼只保留到本次程式關閉。";
        }

        if (edited.PasswordChanged && string.IsNullOrEmpty(edited.Password))
        {
            var warning = await DeleteStoredPasswordAsync(edited.Id);
            return string.IsNullOrEmpty(warning)
                ? $"已清除 {_secretStore.DisplayName} 中的密碼。"
                : warning;
        }

        if (string.IsNullOrEmpty(edited.Password))
        {
            return $"未輸入密碼；{_secretStore.DisplayName} 設定維持不變。";
        }

        try
        {
            await _secretStore.StoreAsync(edited.Id, edited.Name, edited.Password);
            return $"密碼已安全儲存於 {_secretStore.DisplayName}。";
        }
        catch (Exception exception) when (exception is SecretStoreException or IOException)
        {
            return $"{exception.Message} 密碼只保留到本次程式關閉。";
        }
    }

    private async Task<string> DeleteStoredPasswordAsync(Guid profileId)
    {
        if (!_secretStore.IsAvailable)
        {
            return $"但 {_secretStore.UnavailableReason} 無法確認或清除既有密碼庫項目。";
        }

        try
        {
            await _secretStore.DeleteAsync(profileId);
            return string.Empty;
        }
        catch (Exception exception) when (exception is SecretStoreException or IOException)
        {
            return $"但無法清除 {_secretStore.DisplayName} 項目：{exception.Message}";
        }
    }

    private async Task SaveProfilesAsync()
    {
        try
        {
            await _profileStore.SaveAsync(_profiles);
        }
        catch (Exception exception)
        {
            await ShowErrorAsync("無法儲存連線設定", exception);
        }
    }

    private async Task<string> DeleteTableColumnPreferencesAsync(Guid profileId)
    {
        try
        {
            await _tableColumnPreferenceStore.DeleteProfileAsync(profileId);
            return string.Empty;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return $"但無法清除 Table 欄位偏好：{exception.Message}";
        }
    }

    private void Disconnect(string status)
    {
        _session = null;
        _databaseObjects = Array.Empty<DatabaseObjectInfo>();
        _databaseCombo.ItemsSource = null;
        _objectsTree.ItemsSource = null;
        _objectCountText.Text = "0 個";
        _resultsGrid.ItemsSource = null;
        _resultsGrid.Columns.Clear();
        _lastResult = null;
        _connectionBadge.Text = "尚未連線";
        SetStatus(status);
        UpdateActionState();
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _connectButton.IsEnabled = !busy && _profilesList.SelectedItem is not null;
        _refreshButton.IsEnabled = !busy && _session is not null && _databaseCombo.SelectedItem is not null;
        _executeButton.IsEnabled = !busy && _session is not null && _databaseCombo.SelectedItem is not null;
        _exportButton.IsEnabled = !busy && _lastResult is not null;
        _databaseCombo.IsEnabled = !busy && _session is not null;
        _objectSearchBox.IsEnabled = !busy && _session is not null && _databaseCombo.SelectedItem is not null;
        _objectTypeCombo.IsEnabled = !busy && _session is not null && _databaseCombo.SelectedItem is not null;
        _queryHistoryCombo.IsEnabled = !busy && _queryHistory.Entries.Count > 0;
        _clearQueryHistoryButton.IsEnabled = !busy && _queryHistory.Entries.Count > 0;
        _cancelButton.IsEnabled = busy;
        _updateButton.IsEnabled = !busy;
        if (!string.IsNullOrWhiteSpace(status))
        {
            SetStatus(status);
        }
    }

    private void UpdateActionState()
    {
        if (_operationCancellation is not null)
        {
            return;
        }

        SetBusy(false);
    }

    private void SetStatus(string status)
    {
        _statusText.Text = status;
    }

    private Task ShowErrorAsync(string title, Exception exception)
    {
        SetStatus($"{title}：{exception.Message}");
        return MessageDialog.ShowAsync(this, title, exception.Message, showCancel: false);
    }

    private sealed record ObjectTreeItem(
        string Text,
        DatabaseObjectInfo? DatabaseObject = null,
        IReadOnlyList<ObjectTreeItem>? Children = null)
    {
        public ObjectTreeItem(string text, IReadOnlyList<ObjectTreeItem> children)
            : this(text, null, children)
        {
        }
    }

    private sealed record ResultRow(IReadOnlyList<object?> Values);

    private sealed record PasswordResolution(bool Found, string Password, string? Warning);
}
