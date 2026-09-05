using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using MySqlPunk.Core.Models;
using MySqlPunk.Core.Providers;
using MySqlPunk.Core.Services;

namespace MySqlPunk.Desktop;

public sealed partial class ConnectionEditorWindow : Window
{
    private readonly ConnectionProfile _source;
    private readonly ISecretStore _secretStore;
    private DatabaseProviderKind _lastProvider;
    private bool _passwordChanged;

    private readonly TextBlock _hintText;
    private readonly TextBox _connectionUriBox;
    private readonly Button _importUriButton;
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
    private readonly TextBlock _tlsModeLabel;
    private readonly ComboBox _tlsModeCombo;
    private readonly TextBlock _tlsModeDescription;
    private readonly Grid _tlsCertificateFields;
    private readonly TextBlock _tlsCaLabel;
    private readonly TextBox _tlsCaPathBox;
    private readonly Button _browseTlsCaButton;
    private readonly TextBlock _tlsClientCertificateLabel;
    private readonly TextBox _tlsClientCertificateBox;
    private readonly Button _browseTlsClientCertificateButton;
    private readonly TextBlock _tlsClientKeyLabel;
    private readonly TextBox _tlsClientKeyBox;
    private readonly Button _browseTlsClientKeyButton;
    private readonly TextBlock _tlsCertificateHint;
    private readonly Grid _sshSection;
    private readonly CheckBox _sshEnabledCheck;
    private readonly Grid _sshFields;
    private readonly TextBox _sshHostBox;
    private readonly TextBox _sshPortBox;
    private readonly TextBox _sshUsernameBox;
    private readonly TextBox _sshPasswordBox;
    private readonly TextBox _sshKeyPathBox;
    private readonly TextBox _sshPassphraseBox;
    private readonly TextBox _sshFingerprintBox;
    private readonly CheckBox _rememberPasswordCheck;
    private readonly TextBlock _testStatus;
    private readonly Button _testButton;
    private readonly Button _saveButton;

    public ConnectionEditorWindow()
        : this(new ConnectionProfile())
    {
    }

    public ConnectionEditorWindow(
        ConnectionProfile profile,
        string? hint = null,
        ISecretStore? secretStore = null)
    {
        AvaloniaXamlLoader.Load(this);
        _source = profile.Clone();
        _secretStore = secretStore ?? SecretStoreFactory.CreateDefault();
        _lastProvider = _source.Provider;

        _hintText = this.FindControl<TextBlock>("HintText")!;
        _connectionUriBox = this.FindControl<TextBox>("ConnectionUriBox")!;
        _importUriButton = this.FindControl<Button>("ImportUriButton")!;
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
        _tlsModeLabel = this.FindControl<TextBlock>("TlsModeLabel")!;
        _tlsModeCombo = this.FindControl<ComboBox>("TlsModeCombo")!;
        _tlsModeDescription = this.FindControl<TextBlock>("TlsModeDescription")!;
        _tlsCertificateFields = this.FindControl<Grid>("TlsCertificateFields")!;
        _tlsCaLabel = this.FindControl<TextBlock>("TlsCaLabel")!;
        _tlsCaPathBox = this.FindControl<TextBox>("TlsCaPathBox")!;
        _browseTlsCaButton = this.FindControl<Button>("BrowseTlsCaButton")!;
        _tlsClientCertificateLabel = this.FindControl<TextBlock>("TlsClientCertificateLabel")!;
        _tlsClientCertificateBox = this.FindControl<TextBox>("TlsClientCertificateBox")!;
        _browseTlsClientCertificateButton = this.FindControl<Button>("BrowseTlsClientCertificateButton")!;
        _tlsClientKeyLabel = this.FindControl<TextBlock>("TlsClientKeyLabel")!;
        _tlsClientKeyBox = this.FindControl<TextBox>("TlsClientKeyBox")!;
        _browseTlsClientKeyButton = this.FindControl<Button>("BrowseTlsClientKeyButton")!;
        _tlsCertificateHint = this.FindControl<TextBlock>("TlsCertificateHint")!;
        _sshSection = this.FindControl<Grid>("SshSection")!;
        _sshEnabledCheck = this.FindControl<CheckBox>("SshEnabledCheck")!;
        _sshFields = this.FindControl<Grid>("SshFields")!;
        _sshHostBox = this.FindControl<TextBox>("SshHostBox")!;
        _sshPortBox = this.FindControl<TextBox>("SshPortBox")!;
        _sshUsernameBox = this.FindControl<TextBox>("SshUsernameBox")!;
        _sshPasswordBox = this.FindControl<TextBox>("SshPasswordBox")!;
        _sshKeyPathBox = this.FindControl<TextBox>("SshKeyPathBox")!;
        _sshPassphraseBox = this.FindControl<TextBox>("SshPassphraseBox")!;
        _sshFingerprintBox = this.FindControl<TextBox>("SshFingerprintBox")!;
        _rememberPasswordCheck = this.FindControl<CheckBox>("RememberPasswordCheck")!;
        _testStatus = this.FindControl<TextBlock>("TestStatus")!;
        _testButton = this.FindControl<Button>("TestButton")!;
        _saveButton = this.FindControl<Button>("SaveButton")!;

        _hintText.Text = hint ?? "支援 MySQL / MariaDB、PostgreSQL、SQL Server 與 SQLite。密碼不會寫入連線設定檔，可選擇交由系統密碼庫保存。";
        _providerCombo.ItemsSource = Enum.GetValues<DatabaseProviderKind>();
        _providerCombo.SelectedItem = _source.Provider;
        _nameBox.Text = _source.Name;
        _hostBox.Text = _source.Host;
        _portBox.Text = _source.Port > 0 ? _source.Port.ToString() : string.Empty;
        _usernameBox.Text = _source.Username;
        _passwordBox.Text = _source.Password;
        _databaseBox.Text = _source.Database;
        _timeoutBox.Text = _source.TimeoutSeconds.ToString();
        _tlsCaPathBox.Text = _source.TlsCaCertificatePath;
        _tlsClientCertificateBox.Text = _source.TlsClientCertificatePath;
        _tlsClientKeyBox.Text = _source.TlsClientKeyPath;
        _sshEnabledCheck.IsChecked = _source.SshEnabled;
        _sshHostBox.Text = _source.SshHost;
        _sshPortBox.Text = _source.SshPort > 0 ? _source.SshPort.ToString() : "22";
        _sshUsernameBox.Text = _source.SshUsername;
        _sshPasswordBox.Text = _source.SshPassword;
        _sshKeyPathBox.Text = _source.SshPrivateKeyPath;
        _sshPassphraseBox.Text = _source.SshKeyPassphrase;
        _sshFingerprintBox.Text = _source.SshHostKeyFingerprint;
        _sshFields.IsVisible = _source.SshEnabled;
        _rememberPasswordCheck.IsChecked = _source.UseSecretStore;
        _rememberPasswordCheck.IsEnabled = _secretStore.IsAvailable || _source.UseSecretStore;
        _rememberPasswordCheck.Content = _secretStore.IsAvailable
            ? $"將資料庫密碼與 SSH 密碼／私鑰密語安全儲存在 {_secretStore.DisplayName}"
            : _secretStore.UnavailableReason;
        _passwordBox.TextChanged += (_, _) => _passwordChanged = true;
        ApplyProviderVisibility(_source.Provider, resetPort: false);
    }

    private void ImportUri_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var imported = ConnectionUriImportService.Parse(_connectionUriBox.Text);
            _providerCombo.SelectedItem = imported.Provider;
            _nameBox.Text = imported.Name;
            _hostBox.Text = imported.Host;
            _portBox.Text = imported.Port > 0 ? imported.Port.ToString() : string.Empty;
            _usernameBox.Text = imported.Username;
            _passwordBox.Text = imported.Password;
            _databaseBox.Text = imported.Database;
            _timeoutBox.Text = imported.TimeoutSeconds.ToString();
            SelectTlsMode(imported.Provider, imported.TlsMode);
            _tlsCaPathBox.Text = imported.TlsCaCertificatePath;
            _tlsClientCertificateBox.Text = imported.TlsClientCertificatePath;
            _tlsClientKeyBox.Text = imported.TlsClientKeyPath;
            _rememberPasswordCheck.IsChecked = false;
            _connectionUriBox.Text = string.Empty;
            var tlsLabel = (_tlsModeCombo.SelectedItem as TlsModeOption)?.Label ?? imported.TlsMode.ToString();
            _testStatus.Text = $"URI 已安全套用並從輸入框清除；TLS：{tlsLabel}。密碼仍不會寫入連線設定檔。";
        }
        catch (InvalidDataException exception)
        {
            _testStatus.Text = exception.Message;
        }
    }

    private void ProviderCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_providerCombo?.SelectedItem is not DatabaseProviderKind provider || _networkFields is null)
        {
            return;
        }

        var previousProvider = _lastProvider;
        var changed = provider != previousProvider;
        ApplyProviderVisibility(provider, changed, previousProvider);
        _lastProvider = provider;
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

    private void SshEnabledCheck_Changed(object? sender, RoutedEventArgs e)
    {
        if (_sshFields is null)
        {
            return;
        }

        _sshFields.IsVisible = _sshEnabledCheck.IsChecked == true;
    }

    private void BrowseSshKey_Click(object? sender, RoutedEventArgs e) =>
        _ = BrowseCertificateAsync(_sshKeyPathBox, "SSH 私鑰", includeKeyPatterns: true);

    private void BrowseTlsCa_Click(object? sender, RoutedEventArgs e) =>
        _ = BrowseCertificateAsync(_tlsCaPathBox, _tlsCaLabel.Text ?? "CA 憑證", includeKeyPatterns: false);

    private void BrowseTlsClientCertificate_Click(object? sender, RoutedEventArgs e) =>
        _ = BrowseCertificateAsync(_tlsClientCertificateBox, "客戶端憑證", includeKeyPatterns: false);

    private void BrowseTlsClientKey_Click(object? sender, RoutedEventArgs e) =>
        _ = BrowseCertificateAsync(_tlsClientKeyBox, "客戶端私鑰", includeKeyPatterns: true);

    private async Task BrowseCertificateAsync(TextBox target, string title, bool includeKeyPatterns)
    {
        try
        {
            var patterns = includeKeyPatterns
                ? new[] { "*.key", "*.pem" }
                : new[] { "*.pem", "*.crt", "*.cer", "*.der" };
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = $"選擇{title}",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType(includeKeyPatterns ? "PEM 私鑰" : "憑證檔") { Patterns = patterns },
                    FilePickerFileTypes.All
                }
            });

            if (files.Count == 1 && files[0].TryGetLocalPath() is { } path)
            {
                target.Text = path;
            }
        }
        catch (Exception exception)
        {
            _testStatus.Text = $"無法開啟檔案選擇器：{exception.Message}";
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
            using var session = DatabaseProviderFactory.Create(profile);
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

        var sshPort = 22;
        if (provider != DatabaseProviderKind.Sqlite &&
            _sshEnabledCheck.IsChecked == true &&
            !string.IsNullOrWhiteSpace(_sshPortBox.Text) &&
            !int.TryParse(_sshPortBox.Text, out sshPort))
        {
            throw new InvalidOperationException("SSH 連接埠必須是整數。");
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
            UseSecretStore = provider != DatabaseProviderKind.Sqlite &&
                             _rememberPasswordCheck.IsChecked == true,
            PasswordChanged = _passwordChanged,
            Database = _databaseBox.Text ?? string.Empty,
            TimeoutSeconds = timeout,
            TlsMode = (_tlsModeCombo.SelectedItem as TlsModeOption)?.Mode ??
                      throw new InvalidOperationException("請選擇 TLS 模式。"),
            TlsCaCertificatePath = _tlsCaPathBox.Text ?? string.Empty,
            TlsClientCertificatePath = _tlsClientCertificateBox.Text ?? string.Empty,
            TlsClientKeyPath = _tlsClientKeyBox.Text ?? string.Empty,
            SshEnabled = provider != DatabaseProviderKind.Sqlite && _sshEnabledCheck.IsChecked == true,
            SshHost = _sshHostBox.Text ?? string.Empty,
            SshPort = sshPort,
            SshUsername = _sshUsernameBox.Text ?? string.Empty,
            SshPassword = _sshPasswordBox.Text ?? string.Empty,
            SshPrivateKeyPath = _sshKeyPathBox.Text ?? string.Empty,
            SshKeyPassphrase = _sshPassphraseBox.Text ?? string.Empty,
            SshHostKeyFingerprint = _sshFingerprintBox.Text ?? string.Empty
        };
        profile.Validate();
        // Fail closed before saving or testing: a missing or over-shared certificate file must not be
        // persisted as if it were usable.
        ConnectionTlsCertificateFiles.EnsureReadable(profile);
        SshTunnelRules.EnsureReadable(profile);
        if (profile.SshEnabled && profile.SshPassword.Length == 0 && profile.SshPrivateKeyPath.Length == 0)
        {
            throw new InvalidOperationException("SSH Tunnel 至少需要 SSH 密碼或 SSH 私鑰其中一種驗證方式。");
        }

        return profile;
    }

    private void ApplyProviderVisibility(
        DatabaseProviderKind provider,
        bool resetPort,
        DatabaseProviderKind? previousProvider = null)
    {
        var previousMode = (_tlsModeCombo.SelectedItem as TlsModeOption)?.Mode ?? _source.TlsMode;
        var sqlite = provider == DatabaseProviderKind.Sqlite;
        _networkFields.IsVisible = !sqlite;
        _tlsModeLabel.IsVisible = !sqlite;
        _tlsModeCombo.IsVisible = !sqlite;
        _rememberPasswordCheck.IsVisible = !sqlite;
        _tlsCertificateFields.IsVisible = !sqlite;
        _sshSection.IsVisible = !sqlite;
        var clientCertificateSupported = ConnectionTlsCertificateFiles.SupportsClientCertificate(provider);
        _tlsClientCertificateLabel.IsVisible = clientCertificateSupported;
        _tlsClientCertificateBox.IsVisible = clientCertificateSupported;
        _browseTlsClientCertificateButton.IsVisible = clientCertificateSupported;
        _tlsClientKeyLabel.IsVisible = clientCertificateSupported;
        _tlsClientKeyBox.IsVisible = clientCertificateSupported;
        _browseTlsClientKeyButton.IsVisible = clientCertificateSupported;
        if (resetPort && !clientCertificateSupported)
        {
            // The new provider cannot send a client certificate; keep the fields empty instead of silently
            // carrying a PEM pair into a profile that would then fail validation.
            _tlsClientCertificateBox.Text = string.Empty;
            _tlsClientKeyBox.Text = string.Empty;
        }

        _tlsCaLabel.Text = provider == DatabaseProviderKind.SqlServer ? "伺服器憑證" : "CA 憑證";
        _tlsCaPathBox.PlaceholderText = provider == DatabaseProviderKind.SqlServer
            ? "選填：與 SQL Server 憑證精確比對的 PEM／DER／CER 檔絕對路徑"
            : "選填：PEM／DER CA 憑證檔絕對路徑";
        _tlsCertificateHint.Text = provider switch
        {
            DatabaseProviderKind.SqlServer =>
                "伺服器憑證檔會與 SQL Server 出示的憑證精確比對，需搭配 Mandatory 或 Strict；SQL Server 跨平台版不支援客戶端憑證。",
            DatabaseProviderKind.Sqlite => string.Empty,
            _ =>
                "CA 憑證需搭配 VerifyCA／VerifyFull 才會生效；客戶端憑證與私鑰須同時指定 PEM 檔，模式至少 Required，私鑰檔權限須為只有自己可讀（600）。"
        };
        _browseButton.IsVisible = sqlite;
        _databaseLabel.Text = sqlite ? "資料庫檔案" : "預設資料庫";

        if (resetPort && !sqlite)
        {
            _portBox.Text = provider switch
            {
                DatabaseProviderKind.PostgreSql => "5432",
                DatabaseProviderKind.SqlServer => "1433",
                _ => "3306"
            };
        }

        var requestedMode = resetPort
            ? previousProvider == DatabaseProviderKind.Sqlite
                ? ConnectionTlsMode.Default
                : ResolveProviderChangedTlsMode(provider, previousMode)
            : previousMode;
        SelectTlsMode(provider, sqlite ? ConnectionTlsMode.Disabled : requestedMode);
        if (resetPort)
        {
            var selectedLabel = (_tlsModeCombo.SelectedItem as TlsModeOption)?.Label ?? requestedMode.ToString();
            _testStatus.Text = $"資料庫類型已變更；TLS 已安全調整為「{selectedLabel}」，請確認後再儲存。";
        }
    }

    private void SelectTlsMode(DatabaseProviderKind provider, ConnectionTlsMode requestedMode)
    {
        var options = GetTlsModeOptions(provider);
        _tlsModeCombo.ItemsSource = options;
        _tlsModeCombo.SelectedItem = options.FirstOrDefault(option => option.Mode == requestedMode) ?? options[0];
        UpdateTlsModeDescription();
    }

    private void TlsModeCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateTlsModeDescription();
    }

    private void UpdateTlsModeDescription()
    {
        if (_tlsModeDescription is null ||
            _providerCombo?.SelectedItem is not DatabaseProviderKind provider ||
            _tlsModeCombo?.SelectedItem is not TlsModeOption option)
        {
            return;
        }

        _tlsModeDescription.Text = GetTlsModeDescription(provider, option.Mode);
    }

    private static ConnectionTlsMode ResolveProviderChangedTlsMode(
        DatabaseProviderKind provider,
        ConnectionTlsMode previousMode)
    {
        if (provider == DatabaseProviderKind.Sqlite)
        {
            return ConnectionTlsMode.Disabled;
        }

        if (ConnectionTlsModeRules.IsSupported(provider, previousMode))
        {
            return previousMode;
        }

        if (provider == DatabaseProviderKind.SqlServer)
        {
            return previousMode == ConnectionTlsMode.Optional
                ? ConnectionTlsMode.Optional
                : ConnectionTlsMode.Mandatory;
        }

        return previousMode switch
        {
            ConnectionTlsMode.Strict or ConnectionTlsMode.Mandatory or ConnectionTlsMode.VerifyFull =>
                ConnectionTlsMode.VerifyFull,
            ConnectionTlsMode.VerifyCertificateAuthority => ConnectionTlsMode.VerifyCertificateAuthority,
            ConnectionTlsMode.Required => ConnectionTlsMode.Required,
            ConnectionTlsMode.Optional or ConnectionTlsMode.Allow => provider == DatabaseProviderKind.PostgreSql
                ? ConnectionTlsMode.Allow
                : ConnectionTlsMode.Preferred,
            _ => ConnectionTlsMode.Default
        };
    }

    private static string GetTlsModeDescription(DatabaseProviderKind provider, ConnectionTlsMode mode) => mode switch
    {
        ConnectionTlsMode.Default when provider == DatabaseProviderKind.SqlServer =>
            "SqlClient 7 預設為 Mandatory：要求 TLS，並驗證憑證鏈與主機名稱。",
        ConnectionTlsMode.Default => "沿用驅動程式的 Prefer／Preferred：優先 TLS，但 server 不支援時可退回未加密。",
        ConnectionTlsMode.Disabled => "明確停用 TLS；只適合受控的本機或隔離測試環境。",
        ConnectionTlsMode.Optional => "允許未加密；SQL Server 要求加密時仍會協商 TLS。",
        ConnectionTlsMode.Allow => "先嘗試未加密；PostgreSQL server 要求時才改用 TLS。",
        ConnectionTlsMode.Preferred => "優先使用 TLS，但 server 不支援時可退回未加密。",
        ConnectionTlsMode.Required => "強制使用 TLS，但不驗證 server 憑證身分。",
        ConnectionTlsMode.Mandatory => "強制 TLS，並驗證 SQL Server 憑證鏈與主機名稱。",
        ConnectionTlsMode.VerifyCertificateAuthority => "強制 TLS 並驗證憑證授權單位，但不核對主機名稱。",
        ConnectionTlsMode.VerifyFull => "強制 TLS，同時驗證憑證授權單位與主機名稱，建議正式環境使用。",
        ConnectionTlsMode.Strict => "要求 TDS 8.0 嚴格加密與憑證驗證；不支援時直接失敗，不會降級。",
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private static IReadOnlyList<TlsModeOption> GetTlsModeOptions(DatabaseProviderKind provider) => provider switch
    {
        DatabaseProviderKind.MySql => new[]
        {
            new TlsModeOption(ConnectionTlsMode.Default, "驅動程式預設（Preferred，可退回未加密）"),
            new TlsModeOption(ConnectionTlsMode.Disabled, "Disabled（停用 TLS）"),
            new TlsModeOption(ConnectionTlsMode.Preferred, "Preferred（可退回未加密）"),
            new TlsModeOption(ConnectionTlsMode.Required, "Required（強制 TLS，不驗證憑證）"),
            new TlsModeOption(ConnectionTlsMode.VerifyCertificateAuthority, "VerifyCA（驗證憑證授權單位）"),
            new TlsModeOption(ConnectionTlsMode.VerifyFull, "VerifyFull（驗證憑證與主機名稱）")
        },
        DatabaseProviderKind.PostgreSql => new[]
        {
            new TlsModeOption(ConnectionTlsMode.Default, "驅動程式預設（Prefer，可退回未加密）"),
            new TlsModeOption(ConnectionTlsMode.Disabled, "Disable（停用 TLS）"),
            new TlsModeOption(ConnectionTlsMode.Allow, "Allow（server 要求時使用 TLS）"),
            new TlsModeOption(ConnectionTlsMode.Preferred, "Prefer（可退回未加密）"),
            new TlsModeOption(ConnectionTlsMode.Required, "Require（強制 TLS，不驗證憑證）"),
            new TlsModeOption(ConnectionTlsMode.VerifyCertificateAuthority, "VerifyCA（驗證憑證授權單位）"),
            new TlsModeOption(ConnectionTlsMode.VerifyFull, "VerifyFull（驗證憑證與主機名稱）")
        },
        DatabaseProviderKind.SqlServer => new[]
        {
            new TlsModeOption(ConnectionTlsMode.Default, "驅動程式預設（Mandatory，驗證憑證與主機）"),
            new TlsModeOption(ConnectionTlsMode.Optional, "Optional（server 要求時才加密）"),
            new TlsModeOption(ConnectionTlsMode.Mandatory, "Mandatory（驗證憑證與主機名稱）"),
            new TlsModeOption(ConnectionTlsMode.Strict, "Strict（TDS 8 嚴格加密）")
        },
        DatabaseProviderKind.Sqlite => new[]
        {
            new TlsModeOption(ConnectionTlsMode.Disabled, "SQLite 不使用 TLS")
        },
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };

    private void SetBusy(bool busy)
    {
        _importUriButton.IsEnabled = !busy;
        _testButton.IsEnabled = !busy;
        _saveButton.IsEnabled = !busy;
    }

    private sealed record TlsModeOption(ConnectionTlsMode Mode, string Label)
    {
        public override string ToString() => Label;
    }
}
