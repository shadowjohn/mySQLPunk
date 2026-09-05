using System.Security.Cryptography;
using System.Text.RegularExpressions;
using MySqlPunk.Core.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace MySqlPunk.Core.Services;

/// <summary>
/// Validation rules for the SSH tunnel settings of a <see cref="ConnectionProfile"/>. Like the TLS rules these
/// fail closed: an enabled tunnel always needs a pinned host key fingerprint, and TLS modes whose host-name
/// check would silently break behind a 127.0.0.1 forward are rejected instead of downgraded.
/// </summary>
public static class SshTunnelRules
{
    private static readonly Regex FingerprintPattern = new("^[A-Za-z0-9+/]{43}$", RegexOptions.CultureInvariant);

    public const int MaximumHostCharacters = 253;
    public const int MaximumUsernameCharacters = 256;

    public static void Validate(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        // Only plain spaces are trimmed so tabs／newlines are rejected below instead of silently dropped.
        profile.SshHost = (profile.SshHost ?? string.Empty).Trim(' ');
        profile.SshUsername = (profile.SshUsername ?? string.Empty).Trim(' ');
        profile.SshPrivateKeyPath = ConnectionTlsCertificateFiles.NormalizePath(profile.SshPrivateKeyPath, "SSH 私鑰");
        profile.SshHostKeyFingerprint = (profile.SshHostKeyFingerprint ?? string.Empty).Trim(' ');
        profile.SshPassword ??= string.Empty;
        profile.SshKeyPassphrase ??= string.Empty;

        if (profile.Provider == DatabaseProviderKind.Sqlite)
        {
            profile.SshEnabled = false;
            profile.SshPassword = string.Empty;
            profile.SshKeyPassphrase = string.Empty;
            return;
        }

        if (profile.SshHostKeyFingerprint.Length > 0)
        {
            profile.SshHostKeyFingerprint = NormalizeFingerprint(profile.SshHostKeyFingerprint);
        }

        if (profile.SshPort is < 1 or > 65535)
        {
            throw new InvalidOperationException("SSH 連接埠必須介於 1 到 65535。");
        }

        if (profile.SshHost.Length > MaximumHostCharacters ||
            profile.SshHost.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            throw new InvalidOperationException("SSH 主機名稱過長或包含空白／控制字元。");
        }

        if (profile.SshUsername.Length > MaximumUsernameCharacters ||
            profile.SshUsername.Any(char.IsControl))
        {
            throw new InvalidOperationException("SSH 使用者名稱過長或包含控制字元。");
        }

        if (!profile.SshEnabled)
        {
            return;
        }

        if (profile.SshHost.Length == 0)
        {
            throw new InvalidOperationException("啟用 SSH Tunnel 時必須填寫 SSH 主機。");
        }

        if (profile.SshUsername.Length == 0)
        {
            throw new InvalidOperationException("啟用 SSH Tunnel 時必須填寫 SSH 使用者名稱。");
        }

        if (profile.SshHostKeyFingerprint.Length == 0)
        {
            throw new InvalidOperationException(
                "啟用 SSH Tunnel 時必須填寫 SSH 主機金鑰 SHA256 指紋；請向管理員取得或用 ssh-keyscan 搭配 ssh-keygen -lf 核對後填入。");
        }

        if (profile.Provider != DatabaseProviderKind.SqlServer && profile.TlsMode == ConnectionTlsMode.VerifyFull)
        {
            throw new InvalidOperationException(
                "SSH Tunnel 會把資料庫端點改成 127.0.0.1，VerifyFull 的主機名稱比對必定失敗；請改用 VerifyCA，遠端身分由 SSH 主機金鑰指紋驗證。");
        }
    }

    /// <summary>
    /// Accepts <c>SHA256:xxxx</c> or the bare 43-character base64 digest, with or without trailing padding, and
    /// returns the canonical OpenSSH form. Anything else is rejected instead of being pinned as garbage.
    /// </summary>
    public static string NormalizeFingerprint(string value)
    {
        var fingerprint = (value ?? string.Empty).Trim(' ');
        if (fingerprint.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase))
        {
            fingerprint = fingerprint[7..].Trim(' ');
        }

        fingerprint = fingerprint.TrimEnd('=');
        if (!FingerprintPattern.IsMatch(fingerprint))
        {
            throw new InvalidOperationException(
                "SSH 主機金鑰指紋必須是 OpenSSH 的 SHA256:… 格式（43 個 base64 字元）；MD5 冒號格式不接受。");
        }

        return "SHA256:" + fingerprint;
    }

    public static string ComputeFingerprint(byte[] hostKey)
    {
        ArgumentNullException.ThrowIfNull(hostKey);
        return "SHA256:" + Convert.ToBase64String(SHA256.HashData(hostKey)).TrimEnd('=');
    }

    /// <summary>Confirms the optional private key file exists and is not readable by other users.</summary>
    public static void EnsureReadable(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!profile.SshEnabled || profile.SshPrivateKeyPath.Length == 0)
        {
            return;
        }

        ConnectionTlsCertificateFiles.EnsureRegularFile(profile.SshPrivateKeyPath, "SSH 私鑰");
        ConnectionTlsCertificateFiles.EnsurePrivateKeyPermissions(profile.SshPrivateKeyPath, "SSH 私鑰");
    }
}

/// <summary>
/// A local port forward (<c>127.0.0.1:&lt;dynamic&gt;</c> → database host:port) over an SSH connection whose host key
/// must match the fingerprint pinned in the profile. The forward is bound to loopback only and lives until the
/// owning session disposes it.
/// </summary>
public sealed class SshTunnel : IDisposable
{
    private SshClient? _client;
    private ForwardedPortLocal? _forwardedPort;

    private SshTunnel()
    {
    }

    public string LocalHost => "127.0.0.1";

    public int LocalPort { get; private set; }

    public static async Task<SshTunnel> StartAsync(ConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!profile.SshEnabled)
        {
            throw new InvalidOperationException("此連線未啟用 SSH Tunnel。");
        }

        SshTunnelRules.Validate(profile);
        SshTunnelRules.EnsureReadable(profile);
        var expectedFingerprint = profile.SshHostKeyFingerprint;

        var methods = new List<AuthenticationMethod>();
        PrivateKeyFile? keyFile = null;
        if (profile.SshPrivateKeyPath.Length > 0)
        {
            try
            {
                keyFile = profile.SshKeyPassphrase.Length == 0
                    ? new PrivateKeyFile(profile.SshPrivateKeyPath)
                    : new PrivateKeyFile(profile.SshPrivateKeyPath, profile.SshKeyPassphrase);
            }
            catch (Exception exception) when (exception is SshException or IOException or
                                                  UnauthorizedAccessException or ArgumentException or
                                                  InvalidOperationException or NotSupportedException or
                                                  CryptographicException)
            {
                throw new InvalidOperationException(
                    profile.SshKeyPassphrase.Length == 0
                        ? "無法讀取 SSH 私鑰；若私鑰已加密請填寫私鑰密語，並確認格式為 OpenSSH／PEM。"
                        : "無法解密 SSH 私鑰；請確認私鑰密語與檔案格式。",
                    exception);
            }

            methods.Add(new PrivateKeyAuthenticationMethod(profile.SshUsername, keyFile));
        }

        if (profile.SshPassword.Length > 0)
        {
            methods.Add(new PasswordAuthenticationMethod(profile.SshUsername, profile.SshPassword));
        }

        if (methods.Count == 0)
        {
            keyFile?.Dispose();
            throw new InvalidOperationException("SSH Tunnel 至少需要 SSH 密碼或 SSH 私鑰其中一種驗證方式。");
        }

        var connectionInfo = new ConnectionInfo(profile.SshHost, profile.SshPort, profile.SshUsername, methods.ToArray())
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(profile.TimeoutSeconds, 1, 300))
        };

        var tunnel = new SshTunnel();
        string? presentedFingerprint = null;
        try
        {
            tunnel._client = new SshClient(connectionInfo) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
            tunnel._client.HostKeyReceived += (_, arguments) =>
            {
                presentedFingerprint = SshTunnelRules.ComputeFingerprint(arguments.HostKey);
                arguments.CanTrust = string.Equals(presentedFingerprint, expectedFingerprint, StringComparison.Ordinal);
            };

            try
            {
                await tunnel._client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SshConnectionException exception) when (
                presentedFingerprint is not null &&
                !string.Equals(presentedFingerprint, expectedFingerprint, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"SSH 主機金鑰指紋不符：設定為 {expectedFingerprint}，伺服器出示 {presentedFingerprint}。已中止連線；請確認主機身分後再更新指紋。",
                    exception);
            }
            catch (SshAuthenticationException exception)
            {
                throw new InvalidOperationException("SSH 驗證失敗：請確認使用者名稱、密碼或私鑰。", exception);
            }
            catch (Exception exception) when (exception is SshException or System.Net.Sockets.SocketException or
                                                  IOException or ProxyException)
            {
                throw new InvalidOperationException($"無法建立 SSH 連線：{exception.Message}", exception);
            }

            tunnel._forwardedPort = new ForwardedPortLocal("127.0.0.1", 0, profile.Host, (uint)profile.Port);
            tunnel._client.AddForwardedPort(tunnel._forwardedPort);
            tunnel._forwardedPort.Start();
            tunnel.LocalPort = (int)tunnel._forwardedPort.BoundPort;
            if (tunnel.LocalPort is < 1 or > 65535)
            {
                throw new InvalidOperationException("SSH 本機轉送連接埠無效。");
            }

            return tunnel;
        }
        catch
        {
            tunnel.Dispose();
            throw;
        }
        finally
        {
            keyFile?.Dispose();
        }
    }

    public void Dispose()
    {
        if (_forwardedPort is not null)
        {
            try
            {
                if (_forwardedPort.IsStarted)
                {
                    _forwardedPort.Stop();
                }
            }
            catch (Exception exception) when (exception is SshException or ObjectDisposedException or IOException)
            {
            }

            _forwardedPort.Dispose();
            _forwardedPort = null;
        }

        if (_client is not null)
        {
            try
            {
                if (_client.IsConnected)
                {
                    _client.Disconnect();
                }
            }
            catch (Exception exception) when (exception is SshException or ObjectDisposedException or IOException)
            {
            }

            _client.Dispose();
            _client = null;
        }

        LocalPort = 0;
    }
}
