using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using MySqlPunk.Core.Models;
using MySqlPunk.Core.Providers;
using MySqlPunk.Core.Services;

namespace MySqlPunk.Desktop;

/// <summary>
/// Compares the current connection's database with a database on any saved profile. Target sessions opened
/// here are owned by this window and disposed when it closes; the main window's session is never disposed.
/// </summary>
public sealed partial class SchemaCompareWindow : Window
{
    private readonly IDatabaseSession _sourceSession;
    private readonly string _sourceDatabase;
    private readonly Func<ConnectionProfile, Window, Task<ConnectionProfile?>> _prepareProfile;
    private readonly string _generatorVersion;
    private readonly CancellationTokenSource _closing = new();

    private readonly ComboBox _targetProfileCombo;
    private readonly ComboBox _targetDatabaseCombo;
    private readonly ComboBox _filterCombo;
    private readonly Button _loadDatabasesButton;
    private readonly Button _compareButton;
    private readonly Button _exportButton;
    private readonly Button _syncScriptButton;
    private readonly DataGrid _differenceGrid;
    private readonly TextBlock _summaryText;
    private readonly TextBlock _statusText;

    private IDatabaseSession? _targetSession;
    private Guid? _targetProfileId;
    private SchemaComparisonResult? _result;
    private bool _busy;

    public SchemaCompareWindow()
        : this(null!, string.Empty, Array.Empty<ConnectionProfile>(), (_, _) => Task.FromResult<ConnectionProfile?>(null), string.Empty)
    {
    }

    public SchemaCompareWindow(
        IDatabaseSession sourceSession,
        string sourceDatabase,
        IReadOnlyList<ConnectionProfile> profiles,
        Func<ConnectionProfile, Window, Task<ConnectionProfile?>> prepareProfile,
        string generatorVersion)
    {
        AvaloniaXamlLoader.Load(this);
        _sourceSession = sourceSession;
        _sourceDatabase = sourceDatabase;
        _prepareProfile = prepareProfile;
        _generatorVersion = generatorVersion;

        _targetProfileCombo = this.FindControl<ComboBox>("TargetProfileCombo")!;
        _targetDatabaseCombo = this.FindControl<ComboBox>("TargetDatabaseCombo")!;
        _filterCombo = this.FindControl<ComboBox>("FilterCombo")!;
        _loadDatabasesButton = this.FindControl<Button>("LoadDatabasesButton")!;
        _compareButton = this.FindControl<Button>("CompareButton")!;
        _exportButton = this.FindControl<Button>("ExportButton")!;
        _syncScriptButton = this.FindControl<Button>("SyncScriptButton")!;
        _differenceGrid = this.FindControl<DataGrid>("DifferenceGrid")!;
        _summaryText = this.FindControl<TextBlock>("SummaryText")!;
        _statusText = this.FindControl<TextBlock>("StatusText")!;

        if (sourceSession is not null)
        {
            this.FindControl<TextBlock>("SourceText")!.Text =
                $"來源：{sourceSession.Profile.Name}（{sourceSession.Profile.ProviderDisplayName}）／{sourceDatabase}";
            _targetProfileCombo.ItemsSource = profiles.Select(profile => new ProfileOption(profile)).ToList();
            var current = profiles.FirstOrDefault(profile => profile.Id == sourceSession.Profile.Id);
            _targetProfileCombo.SelectedItem = ((IEnumerable<ProfileOption>)_targetProfileCombo.ItemsSource)
                .FirstOrDefault(option => option.Profile.Id == current?.Id);
        }

        Closing += (_, _) =>
        {
            _closing.Cancel();
            DisposeTargetSession();
        };
    }

    private void TargetProfile_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_targetDatabaseCombo is null)
        {
            return;
        }

        _targetDatabaseCombo.ItemsSource = null;
        _targetDatabaseCombo.IsEnabled = false;
        UpdateButtons();
        if (_targetProfileCombo.SelectedItem is ProfileOption option &&
            option.Profile.Id == _sourceSession?.Profile.Id)
        {
            // Same connection: reuse the already-open source session, no password prompt needed.
            _ = LoadDatabasesAsync();
        }
    }

    private void TargetDatabase_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_busy)
        {
            UpdateButtons();
        }
    }

    private async void LoadDatabases_Click(object? sender, RoutedEventArgs e) => await LoadDatabasesAsync();

    private async Task LoadDatabasesAsync()
    {
        if (_busy || _targetProfileCombo.SelectedItem is not ProfileOption option)
        {
            return;
        }

        await RunAsync($"正在連線至 {option.Profile.Name}…", async cancellationToken =>
        {
            var session = await GetTargetSessionAsync(option.Profile, cancellationToken);
            if (session is null)
            {
                SetStatus("已取消目標連線。");
                return;
            }

            var databases = await session.GetDatabasesAsync(cancellationToken);
            _targetDatabaseCombo.ItemsSource = databases;
            _targetDatabaseCombo.IsEnabled = databases.Count > 0;
            var preferred = ReferenceEquals(session, _sourceSession)
                ? databases.FirstOrDefault(name => !string.Equals(name, _sourceDatabase, StringComparison.Ordinal))
                : databases.FirstOrDefault(name => string.Equals(name, option.Profile.Database, StringComparison.OrdinalIgnoreCase));
            _targetDatabaseCombo.SelectedItem = preferred ?? databases.FirstOrDefault();
            SetStatus($"{option.Profile.Name}：{databases.Count} 個資料庫。");
        });
    }

    private async Task<IDatabaseSession?> GetTargetSessionAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        if (profile.Id == _sourceSession.Profile.Id)
        {
            DisposeTargetSession();
            return _sourceSession;
        }

        if (_targetSession is not null && _targetProfileId == profile.Id)
        {
            return _targetSession;
        }

        DisposeTargetSession();
        var prepared = await _prepareProfile(profile, this);
        if (prepared is null)
        {
            return null;
        }

        var session = DatabaseProviderFactory.Create(prepared);
        try
        {
            await session.TestConnectionAsync(cancellationToken);
        }
        catch
        {
            session.Dispose();
            throw;
        }

        _targetSession = session;
        _targetProfileId = profile.Id;
        return session;
    }

    private async void Compare_Click(object? sender, RoutedEventArgs e)
    {
        if (_targetProfileCombo.SelectedItem is not ProfileOption option ||
            _targetDatabaseCombo.SelectedItem is not string targetDatabase)
        {
            return;
        }

        await RunAsync("正在讀取結構…", async cancellationToken =>
        {
            var targetSession = await GetTargetSessionAsync(option.Profile, cancellationToken);
            if (targetSession is null)
            {
                return;
            }

            if (ReferenceEquals(targetSession, _sourceSession) &&
                string.Equals(targetDatabase, _sourceDatabase, StringComparison.Ordinal))
            {
                SetStatus("來源與目標是同一個資料庫，請選擇其他資料庫。");
                return;
            }

            // Progress<T> posts to the UI thread asynchronously; stop honouring reports once the comparison is
            // done so a late "3/3" message cannot overwrite the final summary.
            var finished = false;
            void Report(string text)
            {
                if (!finished)
                {
                    SetStatus(text);
                }
            }

            var sourceObjects = await _sourceSession.GetObjectsAsync(_sourceDatabase, cancellationToken);
            var source = await DataDictionaryService.CollectAsync(
                _sourceSession,
                _sourceDatabase,
                sourceObjects,
                new Progress<DataDictionaryProgress>(report =>
                    Report($"讀取來源 {report.Completed + 1}/{report.Total}：{report.Current.DisplayName}")),
                cancellationToken);
            var targetObjects = await targetSession.GetObjectsAsync(targetDatabase, cancellationToken);
            var target = await DataDictionaryService.CollectAsync(
                targetSession,
                targetDatabase,
                targetObjects,
                new Progress<DataDictionaryProgress>(report =>
                    Report($"讀取目標 {report.Completed + 1}/{report.Total}：{report.Current.DisplayName}")),
                cancellationToken);

            _result = SchemaComparisonService.Compare(
                new SchemaComparisonSide(_sourceSession.Profile.Name, _sourceSession.Profile.ProviderDisplayName, _sourceDatabase)
                {
                    Provider = _sourceSession.Profile.Provider
                },
                source,
                new SchemaComparisonSide(targetSession.Profile.Name, targetSession.Profile.ProviderDisplayName, targetDatabase)
                {
                    Provider = targetSession.Profile.Provider
                },
                target);
            _summaryText.Text = _result.Warnings.Count == 0
                ? _result.Summary
                : $"{_result.Summary}；{_result.Warnings.Count} 個物件無法讀取（報告內註明）";
            ApplyFilter();
            finished = true;
            SetStatus($"比較完成：來源 {source.Count} 個物件、目標 {target.Count} 個物件。");
        });
    }

    private void Filter_SelectionChanged(object? sender, SelectionChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (_differenceGrid is null || _result is null)
        {
            return;
        }

        SchemaDifferenceKind? kind = _filterCombo.SelectedIndex switch
        {
            1 => SchemaDifferenceKind.OnlyInSource,
            2 => SchemaDifferenceKind.OnlyInTarget,
            3 => SchemaDifferenceKind.Changed,
            _ => null
        };
        _differenceGrid.ItemsSource = _result.Differences.Where(difference => kind is null || difference.Kind == kind).ToList();
    }

    private async void Export_Click(object? sender, RoutedEventArgs e)
    {
        var result = _result;
        if (result is null || !StorageProvider.CanSave)
        {
            return;
        }

        IStorageFile? file;
        try
        {
            file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "匯出結構比較報告",
                SuggestedFileName = $"mysqlpunk-schema-compare-{DateTime.Now:yyyyMMdd-HHmmss}.html",
                DefaultExtension = "html",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("HTML 報告") { Patterns = new[] { "*.html", "*.htm" }, MimeTypes = new[] { "text/html" } }
                }
            });
        }
        catch (Exception exception)
        {
            SetStatus($"無法開啟儲存檔案對話框：{exception.Message}");
            return;
        }

        if (file?.TryGetLocalPath() is not { } path)
        {
            return;
        }

        await RunAsync("正在匯出報告…", async cancellationToken =>
        {
            var html = SchemaComparisonService.BuildHtml(result, _generatorVersion);
            var (bytes, written) = await HtmlReportFile.WriteAsync(html, path, cancellationToken);
            SetStatus($"已匯出結構比較報告（{bytes / 1024d:N1} KB）：{written}");
        });
    }

    private async void SyncScript_Click(object? sender, RoutedEventArgs e)
    {
        var result = _result;
        if (result is null)
        {
            return;
        }

        if (!SchemaSyncScriptService.CanGenerate(result, out var reason))
        {
            SetStatus(reason);
            return;
        }

        try
        {
            var script = SchemaSyncScriptService.Generate(result);
            var target = $"{result.Target.ConnectionName}（{result.Target.ProviderName}）／{result.Target.Database}";
            SetStatus($"已產生同步 SQL：{script.Summary}");
            await new SyncScriptWindow(script, target).ShowDialog(this);
        }
        catch (Exception exception)
        {
            SetStatus($"無法產生同步 SQL：{exception.Message}");
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private async Task RunAsync(string status, Func<CancellationToken, Task> operation)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        UpdateButtons();
        SetStatus(status);
        try
        {
            await operation(_closing.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetStatus($"操作失敗：{exception.Message}");
        }
        finally
        {
            _busy = false;
            UpdateButtons();
        }
    }

    private void UpdateButtons()
    {
        if (_compareButton is null)
        {
            return;
        }

        _targetProfileCombo.IsEnabled = !_busy;
        _loadDatabasesButton.IsEnabled = !_busy && _targetProfileCombo.SelectedItem is not null;
        _targetDatabaseCombo.IsEnabled = !_busy && _targetDatabaseCombo.ItemsSource is not null;
        _compareButton.IsEnabled = !_busy && _targetDatabaseCombo.SelectedItem is not null;
        _exportButton.IsEnabled = !_busy && _result is not null;
        _syncScriptButton.IsEnabled = !_busy && _result is not null;
    }

    private void SetStatus(string text) => _statusText.Text = text;

    private void DisposeTargetSession()
    {
        _targetSession?.Dispose();
        _targetSession = null;
        _targetProfileId = null;
    }

    private sealed record ProfileOption(ConnectionProfile Profile)
    {
        public override string ToString() => $"{Profile.Name}（{Profile.ProviderDisplayName}）";
    }
}
