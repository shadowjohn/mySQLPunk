using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
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
    private IDatabaseSession? _session;
    private CancellationTokenSource? _operationCancellation;
    private bool _loadingDatabases;

    private readonly ListBox _profilesList;
    private readonly ComboBox _databaseCombo;
    private readonly TreeView _objectsTree;
    private readonly TextBox _sqlEditor;
    private readonly DataGrid _resultsGrid;
    private readonly TextBlock _statusText;
    private readonly TextBlock _connectionBadge;
    private readonly Button _connectButton;
    private readonly Button _refreshButton;
    private readonly Button _executeButton;
    private readonly Button _cancelButton;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _profilesList = this.FindControl<ListBox>("ProfilesList")!;
        _databaseCombo = this.FindControl<ComboBox>("DatabaseCombo")!;
        _objectsTree = this.FindControl<TreeView>("ObjectsTree")!;
        _sqlEditor = this.FindControl<TextBox>("SqlEditor")!;
        _resultsGrid = this.FindControl<DataGrid>("ResultsGrid")!;
        _statusText = this.FindControl<TextBlock>("StatusText")!;
        _connectionBadge = this.FindControl<TextBlock>("ConnectionBadge")!;
        _connectButton = this.FindControl<Button>("ConnectButton")!;
        _refreshButton = this.FindControl<Button>("RefreshButton")!;
        _executeButton = this.FindControl<Button>("ExecuteButton")!;
        _cancelButton = this.FindControl<Button>("CancelButton")!;

        _profilesList.ItemsSource = _profiles;
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
        _profiles.Remove(selected);
        await SaveProfilesAsync();
        if (wasConnected)
        {
            Disconnect($"連線設定已刪除。{secretWarning}");
        }
        else
        {
            SetStatus($"連線設定已刪除。{secretWarning}");
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

    private void ObjectsTree_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_session is null || _objectsTree.SelectedItem is not ObjectTreeItem { DatabaseObject: { } databaseObject })
        {
            return;
        }

        _sqlEditor.Text = _session.BuildSelectPreview(databaseObject);
        _sqlEditor.Focus();
        _sqlEditor.CaretIndex = _sqlEditor.Text?.Length ?? 0;
        SetStatus($"已產生 {databaseObject.DisplayName} 的前 200 列預覽 SQL；按 Ctrl+Enter 執行。");
    }

    private async void ExecuteSql_Click(object? sender, RoutedEventArgs e)
    {
        await ExecuteCurrentSqlAsync();
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

    private async Task ExecuteCurrentSqlAsync()
    {
        if (_session is null || _databaseCombo.SelectedItem is not string database)
        {
            await MessageDialog.ShowAsync(this, "尚未連線", "請先選擇連線設定並連線。", showCancel: false);
            return;
        }

        var sql = _sqlEditor.Text ?? string.Empty;
        await RunOperationAsync("正在執行 SQL…", async cancellationToken =>
        {
            var result = await _session.ExecuteAsync(database, sql, cancellationToken);
            DisplayResult(result);
            SetStatus(result.Summary);
            if (!result.HasResultSet)
            {
                await LoadObjectsAsync(database, cancellationToken);
            }
        });
    }

    private async Task LoadObjectsAsync(string database, CancellationToken cancellationToken)
    {
        if (_session is null)
        {
            return;
        }

        var objects = await _session.GetObjectsAsync(database, cancellationToken);
        var tableItems = objects
            .Where(item => item.Kind == DatabaseObjectKind.Table)
            .Select(item => new ObjectTreeItem(item.DisplayName, item))
            .ToList();
        var viewItems = objects
            .Where(item => item.Kind == DatabaseObjectKind.View)
            .Select(item => new ObjectTreeItem(item.DisplayName, item))
            .ToList();

        _objectsTree.ItemsSource = new[]
        {
            new ObjectTreeItem($"資料表 ({tableItems.Count})", children: tableItems),
            new ObjectTreeItem($"檢視表 ({viewItems.Count})", children: viewItems)
        };
        SetStatus($"{database}：已載入 {objects.Count} 個物件。");
    }

    private void DisplayResult(QueryResult result)
    {
        _resultsGrid.Columns.Clear();
        for (var index = 0; index < result.Columns.Count; index++)
        {
            _resultsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = result.Columns[index],
                Binding = new Binding($"Values[{index}]")
                {
                    TargetNullValue = "(NULL)"
                },
                Width = new DataGridLength(1, DataGridLengthUnitType.Auto)
            });
        }

        _resultsGrid.ItemsSource = result.Rows.Select(row => new ResultRow(row)).ToList();
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

    private void Disconnect(string status)
    {
        _session = null;
        _databaseCombo.ItemsSource = null;
        _objectsTree.ItemsSource = null;
        _resultsGrid.ItemsSource = null;
        _resultsGrid.Columns.Clear();
        _connectionBadge.Text = "尚未連線";
        SetStatus(status);
        UpdateActionState();
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _connectButton.IsEnabled = !busy && _profilesList.SelectedItem is not null;
        _refreshButton.IsEnabled = !busy && _session is not null && _databaseCombo.SelectedItem is not null;
        _executeButton.IsEnabled = !busy && _session is not null && _databaseCombo.SelectedItem is not null;
        _databaseCombo.IsEnabled = !busy && _session is not null;
        _cancelButton.IsEnabled = busy;
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
