using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using MySqlPunk.Core.Models;
using MySqlPunk.Core.Providers;

namespace MySqlPunk.Desktop;

public sealed partial class ConnectionEditorWindow : Window
{
    private readonly ConnectionProfile _source;
    private DatabaseProviderKind _lastProvider;

    private readonly TextBlock _hintText;
    private readonly TextBox _nameBox;
    private readonly ComboBox _providerCombo;
    private readonly Grid _networkFields;
    private readonly TextBox _hostBox;
    private readonly TextBox _portBox;
    private readonly TextBox _usernameBox;
    private readonly TextBox _passwordBox;
    private readonly TextBlock _databaseLabel;
    private readonly TextBox _databaseBox;
    private readonly Button _browseButton;
    private readonly TextBox _timeoutBox;
    private readonly CheckBox _useSslCheck;
    private readonly TextBlock _testStatus;
    private readonly Button _testButton;
    private readonly Button _saveButton;

    public ConnectionEditorWindow()
        : this(new ConnectionProfile())
    {
    }

    public ConnectionEditorWindow(ConnectionProfile profile, string? hint = null)
    {
        AvaloniaXamlLoader.Load(this);
        _source = profile.Clone();
        _lastProvider = _source.Provider;

        _hintText = this.FindControl<TextBlock>("HintText")!;
        _nameBox = this.FindControl<TextBox>("NameBox")!;
        _providerCombo = this.FindControl<ComboBox>("ProviderCombo")!;
        _networkFields = this.FindControl<Grid>("NetworkFields")!;
        _hostBox = this.FindControl<TextBox>("HostBox")!;
        _portBox = this.FindControl<TextBox>("PortBox")!;
        _usernameBox = this.FindControl<TextBox>("UsernameBox")!;
        _passwordBox = this.FindControl<TextBox>("PasswordBox")!;
        _databaseLabel = this.FindControl<TextBlock>("DatabaseLabel")!;
        _databaseBox = this.FindControl<TextBox>("DatabaseBox")!;
        _browseButton = this.FindControl<Button>("BrowseButton")!;
        _timeoutBox = this.FindControl<TextBox>("TimeoutBox")!;
        _useSslCheck = this.FindControl<CheckBox>("UseSslCheck")!;
        _testStatus = this.FindControl<TextBlock>("TestStatus")!;
        _testButton = this.FindControl<Button>("TestButton")!;
        _saveButton = this.FindControl<Button>("SaveButton")!;

        _hintText.Text = hint ?? "支援 MySQL / MariaDB、PostgreSQL 與 SQLite。密碼只存在記憶體，不會寫入連線設定檔。";
        _providerCombo.ItemsSource = Enum.GetValues<DatabaseProviderKind>();
        _providerCombo.SelectedItem = _source.Provider;
        _nameBox.Text = _source.Name;
        _hostBox.Text = _source.Host;
        _portBox.Text = _source.Port > 0 ? _source.Port.ToString() : string.Empty;
        _usernameBox.Text = _source.Username;
        _passwordBox.Text = _source.Password;
        _databaseBox.Text = _source.Database;
        _timeoutBox.Text = _source.TimeoutSeconds.ToString();
        _useSslCheck.IsChecked = _source.UseSsl;
        ApplyProviderVisibility(_source.Provider, resetPort: false);
    }

    private void ProviderCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_providerCombo?.SelectedItem is not DatabaseProviderKind provider || _networkFields is null)
        {
            return;
        }

        var changed = provider != _lastProvider;
        _lastProvider = provider;
        ApplyProviderVisibility(provider, changed);
    }

    private async void BrowseSqlite_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "選擇 SQLite 資料庫",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("SQLite 資料庫") { Patterns = new[] { "*.db", "*.sqlite", "*.sqlite3" } },
                FilePickerFileTypes.All
            }
        });

        if (files.Count == 1 && files[0].TryGetLocalPath() is { } path)
        {
            _databaseBox.Text = path;
        }
    }

    private async void TestConnection_Click(object? sender, RoutedEventArgs e)
    {
        ConnectionProfile profile;
        try
        {
            profile = BuildProfile();
        }
        catch (Exception exception)
        {
            _testStatus.Text = exception.Message;
            return;
        }

        SetBusy(true);
        _testStatus.Text = "正在測試連線…";
        try
        {
            var session = DatabaseProviderFactory.Create(profile);
            await session.TestConnectionAsync();
            _testStatus.Text = "連線成功。";
        }
        catch (Exception exception)
        {
            _testStatus.Text = $"連線失敗：{exception.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Close(BuildProfile());
        }
        catch (Exception exception)
        {
            _testStatus.Text = exception.Message;
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }

    private ConnectionProfile BuildProfile()
    {
        if (_providerCombo.SelectedItem is not DatabaseProviderKind provider)
        {
            throw new InvalidOperationException("請選擇資料庫類型。");
        }

        if (!int.TryParse(_timeoutBox.Text, out var timeout))
        {
            throw new InvalidOperationException("連線逾時必須是整數秒數。");
        }

        var port = 0;
        if (provider != DatabaseProviderKind.Sqlite && !int.TryParse(_portBox.Text, out port))
        {
            throw new InvalidOperationException("連接埠必須是整數。");
        }

        var profile = new ConnectionProfile
        {
            Id = _source.Id,
            Name = _nameBox.Text ?? string.Empty,
            Provider = provider,
            Host = _hostBox.Text ?? string.Empty,
            Port = port,
            Username = _usernameBox.Text ?? string.Empty,
            Password = _passwordBox.Text ?? string.Empty,
            Database = _databaseBox.Text ?? string.Empty,
            TimeoutSeconds = timeout,
            UseSsl = _useSslCheck.IsChecked == true
        };
        profile.Validate();
        return profile;
    }

    private void ApplyProviderVisibility(DatabaseProviderKind provider, bool resetPort)
    {
        var sqlite = provider == DatabaseProviderKind.Sqlite;
        _networkFields.IsVisible = !sqlite;
        _useSslCheck.IsVisible = !sqlite;
        _browseButton.IsVisible = sqlite;
        _databaseLabel.Text = sqlite ? "資料庫檔案" : "預設資料庫";

        if (resetPort && !sqlite)
        {
            _portBox.Text = provider == DatabaseProviderKind.PostgreSql ? "5432" : "3306";
        }
    }

    private void SetBusy(bool busy)
    {
        _testButton.IsEnabled = !busy;
        _saveButton.IsEnabled = !busy;
    }
}
