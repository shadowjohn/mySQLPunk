using System.Text;

namespace MySqlPunk.Core.Services;

public sealed class MacOsKeychainSecretStore : ISecretStore
{
    private const string ServiceName = "com.mysqlpunk.connection";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string? _executablePath;

    public MacOsKeychainSecretStore(string? executablePath = null)
    {
        _executablePath = executablePath is null
            ? (OperatingSystem.IsMacOS() ? SecretProcessRunner.FindExecutable("/usr/bin/security") : null)
            : SecretProcessRunner.FindExecutable(executablePath);
    }

    public string DisplayName => "macOS Keychain";

    public bool IsAvailable => _executablePath is not null;

    public string UnavailableReason => IsAvailable
        ? string.Empty
        : "找不到 macOS security 工具；密碼只會保留在本次程式記憶體中。";

    public async Task<string?> GetAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            new[]
            {
                "find-generic-password",
                "-a",
                FormatId(profileId),
                "-s",
                ServiceName,
                "-w"
            },
            standardInput: null,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode == 44)
        {
            return null;
        }

        if (result.ExitCode != 0)
        {
            throw CreateOperationException("讀取");
        }

        var encoded = result.StandardOutput.TrimEnd('\r', '\n');
        try
        {
            return StrictUtf8.GetString(Convert.FromBase64String(encoded));
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            throw new SecretStoreException("macOS Keychain 內容格式無法辨識；未將內容當作資料庫密碼。", exception);
        }
    }

    public async Task StoreAsync(
        Guid profileId,
        string profileName,
        string secret,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(secret))
        {
            throw new ArgumentException("密碼不可空白。", nameof(secret));
        }

        var encoded = Convert.ToBase64String(StrictUtf8.GetBytes(secret));
        var command = $"add-generic-password -a {FormatId(profileId)} -s {ServiceName} -U -w {encoded}\n";
        var result = await RunAsync(new[] { "-i" }, command, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 || result.StandardError.Contains("SecKeychain", StringComparison.OrdinalIgnoreCase))
        {
            throw CreateOperationException("儲存");
        }

        var stored = await GetAsync(profileId, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(stored, secret, StringComparison.Ordinal))
        {
            throw new SecretStoreException("macOS Keychain 寫入後驗證失敗；密碼未視為已保存。");
        }
    }

    public async Task DeleteAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            new[]
            {
                "delete-generic-password",
                "-a",
                FormatId(profileId),
                "-s",
                ServiceName
            },
            standardInput: null,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode is not (0 or 44))
        {
            throw CreateOperationException("刪除");
        }
    }

    private Task<SecretProcessResult> RunAsync(
        IEnumerable<string> arguments,
        string? standardInput,
        CancellationToken cancellationToken)
    {
        if (_executablePath is null)
        {
            throw new SecretStoreException(UnavailableReason);
        }

        return SecretProcessRunner.RunAsync(_executablePath, arguments, standardInput, cancellationToken);
    }

    private static string FormatId(Guid profileId) => profileId.ToString("N");

    private static SecretStoreException CreateOperationException(string operation) =>
        new($"macOS Keychain {operation}失敗；密碼只會保留在本次程式記憶體中。");
}
