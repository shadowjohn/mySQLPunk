using System.Text;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using MySqlPunk.Core.Models;
using Npgsql;

namespace MySqlPunk.Core.Services;

/// <summary>
/// Validates and applies the optional TLS certificate files (CA / server certificate, client certificate and
/// client private key) of a <see cref="ConnectionProfile"/>. All rules fail closed: a certificate path is
/// only accepted together with a TLS mode that actually uses it, so a configured file can never be
/// silently ignored by the driver.
/// </summary>
public static class ConnectionTlsCertificateFiles
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public const int MaximumPathCharacters = 1024;

    public static bool SupportsCertificateAuthority(DatabaseProviderKind provider) =>
        provider is DatabaseProviderKind.MySql or DatabaseProviderKind.PostgreSql or DatabaseProviderKind.SqlServer;

    public static bool SupportsClientCertificate(DatabaseProviderKind provider) =>
        provider is DatabaseProviderKind.MySql or DatabaseProviderKind.PostgreSql;

    /// <summary>
    /// TLS modes that verify the server certificate and therefore honour a CA / server certificate file.
    /// </summary>
    public static bool VerifiesServerCertificate(DatabaseProviderKind provider, ConnectionTlsMode mode) =>
        provider switch
        {
            DatabaseProviderKind.MySql or DatabaseProviderKind.PostgreSql => mode is
                ConnectionTlsMode.VerifyCertificateAuthority or ConnectionTlsMode.VerifyFull,
            DatabaseProviderKind.SqlServer => mode is
                ConnectionTlsMode.Default or ConnectionTlsMode.Mandatory or ConnectionTlsMode.Strict,
            _ => false
        };

    /// <summary>
    /// TLS modes that never fall back to an unencrypted channel, which is the minimum for sending a
    /// client certificate.
    /// </summary>
    public static bool EnforcesTls(DatabaseProviderKind provider, ConnectionTlsMode mode) =>
        provider switch
        {
            DatabaseProviderKind.MySql or DatabaseProviderKind.PostgreSql => mode is
                ConnectionTlsMode.Required or
                ConnectionTlsMode.VerifyCertificateAuthority or
                ConnectionTlsMode.VerifyFull,
            DatabaseProviderKind.SqlServer => mode is
                ConnectionTlsMode.Default or ConnectionTlsMode.Mandatory or ConnectionTlsMode.Strict,
            _ => false
        };

    /// <summary>
    /// Normalises the certificate paths of <paramref name="profile"/> and validates their format and
    /// their compatibility with the provider and TLS mode. File existence is checked separately by
    /// <see cref="EnsureReadable"/> so that a profile file can still be loaded when a certificate lives on
    /// media that is not mounted right now.
    /// </summary>
    public static void Validate(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.TlsCaCertificatePath = NormalizePath(profile.TlsCaCertificatePath, "CA 憑證");
        profile.TlsClientCertificatePath = NormalizePath(profile.TlsClientCertificatePath, "客戶端憑證");
        profile.TlsClientKeyPath = NormalizePath(profile.TlsClientKeyPath, "客戶端私鑰");

        if (profile.Provider == DatabaseProviderKind.Sqlite)
        {
            profile.TlsCaCertificatePath = string.Empty;
            profile.TlsClientCertificatePath = string.Empty;
            profile.TlsClientKeyPath = string.Empty;
            return;
        }

        var hasCa = profile.TlsCaCertificatePath.Length > 0;
        var hasClientCertificate = profile.TlsClientCertificatePath.Length > 0;
        var hasClientKey = profile.TlsClientKeyPath.Length > 0;
        if (!hasCa && !hasClientCertificate && !hasClientKey)
        {
            return;
        }

        if (hasCa && !VerifiesServerCertificate(profile.Provider, profile.TlsMode))
        {
            throw new InvalidOperationException(profile.Provider == DatabaseProviderKind.SqlServer
                ? "指定伺服器憑證檔時，SQL Server TLS 模式必須是 Mandatory 或 Strict；Optional 會讓憑證檔被忽略。"
                : "指定 CA 憑證檔時，TLS 模式必須是 VerifyCA 或 VerifyFull；其他模式不會驗證憑證，憑證檔會被忽略。");
        }

        if (hasClientCertificate || hasClientKey)
        {
            if (!SupportsClientCertificate(profile.Provider))
            {
                throw new InvalidOperationException(
                    $"{profile.ProviderDisplayName} 跨平台版尚不支援客戶端憑證驗證，請清除客戶端憑證與私鑰欄位。");
            }

            if (hasClientCertificate != hasClientKey)
            {
                throw new InvalidOperationException("客戶端憑證與客戶端私鑰必須同時指定（PEM 格式）。");
            }

            if (!EnforcesTls(profile.Provider, profile.TlsMode))
            {
                throw new InvalidOperationException(
                    "指定客戶端憑證時，TLS 模式必須是 Required、VerifyCA 或 VerifyFull，避免退回未加密連線。");
            }

            if (string.Equals(profile.TlsClientCertificatePath, profile.TlsClientKeyPath, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("客戶端憑證與私鑰必須是不同檔案；跨平台版不支援合併的 PFX／PKCS#12 檔。");
            }
        }
    }

    /// <summary>
    /// Confirms every configured certificate file exists, is a regular file and (for the private key on
    /// Unix-like systems) is not readable by other users. Called right before a connection is created.
    /// </summary>
    public static void EnsureReadable(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Provider == DatabaseProviderKind.Sqlite)
        {
            return;
        }

        EnsureRegularFile(profile.TlsCaCertificatePath,
            profile.Provider == DatabaseProviderKind.SqlServer ? "伺服器憑證" : "CA 憑證");
        EnsureRegularFile(profile.TlsClientCertificatePath, "客戶端憑證");
        EnsureRegularFile(profile.TlsClientKeyPath, "客戶端私鑰");
        EnsurePrivateKeyPermissions(profile.TlsClientKeyPath);
    }

    public static void ApplyToMySql(MySqlConnectionStringBuilder builder, ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.TlsCaCertificatePath.Length > 0)
        {
            builder.SslCa = profile.TlsCaCertificatePath;
        }

        if (profile.TlsClientCertificatePath.Length > 0)
        {
            builder.SslCert = profile.TlsClientCertificatePath;
            builder.SslKey = profile.TlsClientKeyPath;
        }
    }

    public static void ApplyToPostgreSql(NpgsqlConnectionStringBuilder builder, ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.TlsCaCertificatePath.Length > 0)
        {
            builder.RootCertificate = profile.TlsCaCertificatePath;
        }

        if (profile.TlsClientCertificatePath.Length > 0)
        {
            builder.SslCertificate = profile.TlsClientCertificatePath;
            builder.SslKey = profile.TlsClientKeyPath;
        }
    }

    public static void ApplyToSqlServer(SqlConnectionStringBuilder builder, ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.TlsCaCertificatePath.Length > 0)
        {
            builder.ServerCertificate = profile.TlsCaCertificatePath;
        }
    }

    internal static string NormalizePath(string? value, string label)
    {
        // Only plain spaces are trimmed; tabs, newlines and other control characters are rejected below so
        // a pasted path can never be silently rewritten into a different one.
        var path = value?.Trim(' ') ?? string.Empty;
        if (path.Length == 0)
        {
            return string.Empty;
        }

        if (path.Length > MaximumPathCharacters)
        {
            throw new InvalidOperationException($"{label}路徑不可超過 {MaximumPathCharacters:N0} 個字元。");
        }

        if (path.Any(char.IsControl) ||
            path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 ||
            !IsWellFormedUnicode(path))
        {
            throw new InvalidOperationException($"{label}路徑包含控制字元或無效字元。");
        }

        if (!Path.IsPathFullyQualified(path))
        {
            throw new InvalidOperationException($"{label}路徑必須是本機絕對路徑。");
        }

        if (Path.EndsInDirectorySeparator(path))
        {
            throw new InvalidOperationException($"{label}路徑必須指向檔案，不可指向目錄。");
        }

        return Path.GetFullPath(path);
    }

    private static bool IsWellFormedUnicode(string value)
    {
        try
        {
            _ = StrictUtf8.GetByteCount(value);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    internal static void EnsureRegularFile(string path, string label)
    {
        if (path.Length == 0)
        {
            return;
        }

        if (Directory.Exists(path))
        {
            throw new InvalidOperationException($"{label}路徑指向目錄，不是檔案：{path}");
        }

        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"找不到{label}檔案：{path}");
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.None);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            throw new InvalidOperationException($"無法讀取{label}檔案：{path}", exception);
        }
    }

    internal static void EnsurePrivateKeyPermissions(string keyPath, string label = "客戶端私鑰")
    {
        if (keyPath.Length == 0 || OperatingSystem.IsWindows())
        {
            return;
        }

        UnixFileMode mode;
        try
        {
            mode = File.GetUnixFileMode(keyPath);
        }
        catch (PlatformNotSupportedException)
        {
            return;
        }

        const UnixFileMode sharedBits = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                                        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        if ((mode & sharedBits) != 0)
        {
            throw new InvalidOperationException(
                $"{label}檔案權限過寬，請改為只有目前使用者可讀（例如 chmod 600）：{keyPath}");
        }
    }
}
