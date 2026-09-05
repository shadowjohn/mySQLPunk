using System.Text.Json.Serialization;
using MySqlPunk.Core.Services;

namespace MySqlPunk.Core.Models;

public enum DatabaseProviderKind
{
    MySql,
    PostgreSql,
    Sqlite,
    SqlServer
}

public enum ConnectionTlsMode
{
    Default,
    Disabled,
    Optional,
    Allow,
    Preferred,
    Required,
    Mandatory,
    VerifyCertificateAuthority,
    VerifyFull,
    Strict
}

public sealed class ConnectionProfile
{
    private bool? _legacyUseSsl;
    private ConnectionTlsMode _tlsMode = ConnectionTlsMode.Default;
    private bool _tlsModeWasSpecified;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "新增連線";

    public DatabaseProviderKind Provider { get; set; } = DatabaseProviderKind.MySql;

    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 3306;

    public string Username { get; set; } = string.Empty;

    [JsonIgnore]
    public string Password { get; set; } = string.Empty;

    public bool UseSecretStore { get; set; }

    [JsonIgnore]
    public bool PasswordChanged { get; set; }

    public string Database { get; set; } = string.Empty;

    public ConnectionTlsMode TlsMode
    {
        get => _tlsMode;
        set
        {
            _tlsMode = value;
            _tlsModeWasSpecified = true;
        }
    }

    [JsonPropertyName("useSsl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyUseSsl
    {
        get => null;
        set => _legacyUseSsl = value;
    }

    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>CA certificate (MySQL／PostgreSQL) or expected server certificate (SQL Server) file; PEM／DER.</summary>
    public string TlsCaCertificatePath { get; set; } = string.Empty;

    /// <summary>PEM client certificate for mutual TLS (MySQL／PostgreSQL only).</summary>
    public string TlsClientCertificatePath { get; set; } = string.Empty;

    /// <summary>PEM private key matching <see cref="TlsClientCertificatePath"/>.</summary>
    public string TlsClientKeyPath { get; set; } = string.Empty;

    /// <summary>Route the database connection through an SSH local port forward.</summary>
    public bool SshEnabled { get; set; }

    public string SshHost { get; set; } = string.Empty;

    public int SshPort { get; set; } = 22;

    public string SshUsername { get; set; } = string.Empty;

    /// <summary>Optional OpenSSH／PEM private key; the passphrase is never persisted.</summary>
    public string SshPrivateKeyPath { get; set; } = string.Empty;

    /// <summary>Pinned server host key fingerprint in OpenSSH <c>SHA256:…</c> form; required when SSH is enabled.</summary>
    public string SshHostKeyFingerprint { get; set; } = string.Empty;

    [JsonIgnore]
    public string SshPassword { get; set; } = string.Empty;

    [JsonIgnore]
    public string SshKeyPassphrase { get; set; } = string.Empty;

    [JsonIgnore]
    public string ProviderDisplayName => Provider switch
    {
        DatabaseProviderKind.MySql => "MySQL / MariaDB",
        DatabaseProviderKind.PostgreSql => "PostgreSQL",
        DatabaseProviderKind.Sqlite => "SQLite",
        DatabaseProviderKind.SqlServer => "SQL Server",
        _ => Provider.ToString()
    };

    public ConnectionProfile Clone() => new()
    {
        Id = Id,
        Name = Name,
        Provider = Provider,
        Host = Host,
        Port = Port,
        Username = Username,
        Password = Password,
        UseSecretStore = UseSecretStore,
        PasswordChanged = PasswordChanged,
        Database = Database,
        TlsMode = TlsMode,
        TimeoutSeconds = TimeoutSeconds,
        TlsCaCertificatePath = TlsCaCertificatePath,
        TlsClientCertificatePath = TlsClientCertificatePath,
        TlsClientKeyPath = TlsClientKeyPath,
        SshEnabled = SshEnabled,
        SshHost = SshHost,
        SshPort = SshPort,
        SshUsername = SshUsername,
        SshPrivateKeyPath = SshPrivateKeyPath,
        SshHostKeyFingerprint = SshHostKeyFingerprint,
        SshPassword = SshPassword,
        SshKeyPassphrase = SshKeyPassphrase
    };

    public void ApplyProviderDefaults(bool resetPort = false)
    {
        if (Provider == DatabaseProviderKind.Sqlite)
        {
            Host = string.Empty;
            Port = 0;
            Username = string.Empty;
            Password = string.Empty;
            UseSecretStore = false;
            return;
        }

        Host = string.IsNullOrWhiteSpace(Host) ? "localhost" : Host.Trim();
        if (resetPort || Port <= 0)
        {
            Port = Provider switch
            {
                DatabaseProviderKind.PostgreSql => 5432,
                DatabaseProviderKind.SqlServer => 1433,
                _ => 3306
            };
        }
    }

    public void Validate()
    {
        Name = Name?.Trim() ?? string.Empty;
        TimeoutSeconds = Math.Clamp(TimeoutSeconds, 1, 300);

        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new InvalidOperationException("連線名稱不可空白。");
        }

        ApplyProviderDefaults();

        if (!ConnectionTlsModeRules.IsSupported(Provider, TlsMode))
        {
            throw new InvalidOperationException(
                $"{ProviderDisplayName} 不支援 TLS 模式 {TlsMode}。");
        }

        if (Provider == DatabaseProviderKind.Sqlite)
        {
            TlsMode = ConnectionTlsMode.Disabled;
            ConnectionTlsCertificateFiles.Validate(this);
            SshTunnelRules.Validate(this);
            if (string.IsNullOrWhiteSpace(Database))
            {
                throw new InvalidOperationException("SQLite 必須指定資料庫檔案。");
            }

            return;
        }

        Database = Database?.Trim() ?? string.Empty;
        Username = Username?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(Host))
        {
            throw new InvalidOperationException("主機不可空白。");
        }

        if (Port is < 1 or > 65535)
        {
            throw new InvalidOperationException("連接埠必須介於 1 到 65535。");
        }

        if (string.IsNullOrWhiteSpace(Username))
        {
            throw new InvalidOperationException("使用者名稱不可空白。");
        }

        ConnectionTlsCertificateFiles.Validate(this);
        SshTunnelRules.Validate(this);
    }

    internal void ApplyPersistedCompatibility()
    {
        if (_legacyUseSsl.HasValue && _tlsModeWasSpecified)
        {
            throw new InvalidDataException(
                "連線設定不可同時包含舊 useSsl 與新 tlsMode；請移除其中一個後重試。");
        }

        if (_legacyUseSsl.HasValue)
        {
            TlsMode = _legacyUseSsl.Value
                ? Provider == DatabaseProviderKind.SqlServer
                    ? ConnectionTlsMode.Mandatory
                    : ConnectionTlsMode.Preferred
                : Provider == DatabaseProviderKind.SqlServer
                    ? ConnectionTlsMode.Optional
                    : ConnectionTlsMode.Disabled;
        }
        else if (!_tlsModeWasSpecified)
        {
            // Older or hand-written profiles without either field behaved like UseSsl=false.
            TlsMode = Provider == DatabaseProviderKind.SqlServer
                ? ConnectionTlsMode.Optional
                : ConnectionTlsMode.Disabled;
        }

        _legacyUseSsl = null;
    }
}

public static class ConnectionTlsModeRules
{
    public static bool IsSupported(DatabaseProviderKind provider, ConnectionTlsMode mode) => provider switch
    {
        DatabaseProviderKind.MySql => mode is
            ConnectionTlsMode.Default or
            ConnectionTlsMode.Disabled or
            ConnectionTlsMode.Preferred or
            ConnectionTlsMode.Required or
            ConnectionTlsMode.VerifyCertificateAuthority or
            ConnectionTlsMode.VerifyFull,
        DatabaseProviderKind.PostgreSql => mode is
            ConnectionTlsMode.Default or
            ConnectionTlsMode.Disabled or
            ConnectionTlsMode.Allow or
            ConnectionTlsMode.Preferred or
            ConnectionTlsMode.Required or
            ConnectionTlsMode.VerifyCertificateAuthority or
            ConnectionTlsMode.VerifyFull,
        DatabaseProviderKind.SqlServer => mode is
            ConnectionTlsMode.Default or
            ConnectionTlsMode.Optional or
            ConnectionTlsMode.Mandatory or
            ConnectionTlsMode.Strict,
        DatabaseProviderKind.Sqlite => mode is ConnectionTlsMode.Default or ConnectionTlsMode.Disabled,
        _ => false
    };
}
