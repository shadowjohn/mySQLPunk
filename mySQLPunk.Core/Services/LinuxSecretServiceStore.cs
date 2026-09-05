namespace MySqlPunk.Core.Services;

public sealed class LinuxSecretServiceStore : ISecretStore
{
    private const string ApplicationAttribute = "mySQLPunk";
    private readonly string? _executablePath;

    public LinuxSecretServiceStore(string? executablePath = null)
    {
        _executablePath = executablePath is null
            ? FindDefaultExecutable()
            : SecretProcessRunner.FindExecutable(executablePath);
    }

    public string DisplayName => "Linux Secret Service";

    public bool IsAvailable => _executablePath is not null;

    public string UnavailableReason => IsAvailable
        ? string.Empty
        : "找不到 secret-tool；請安裝 libsecret-tools，或維持本次執行期間的記憶體保存。";

    public Task<string?> GetAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        GetAsync(profileId, SecretKind.DatabasePassword, cancellationToken);

    public async Task<string?> GetAsync(Guid profileId, SecretKind kind, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            new[] { "lookup", "application", ApplicationAttribute, "profile-id", FormatId(profileId, kind) },
            standardInput: null,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode == 0)
        {
            return result.StandardOutput;
        }

        if (result.ExitCode == 1 && string.IsNullOrWhiteSpace(result.StandardError))
        {
            return null;
        }

        throw CreateOperationException("讀取");
    }

    public Task StoreAsync(
        Guid profileId,
        string profileName,
        string secret,
        CancellationToken cancellationToken = default) =>
        StoreAsync(profileId, profileName, secret, SecretKind.DatabasePassword, cancellationToken);

    public async Task StoreAsync(
        Guid profileId,
        string profileName,
        string secret,
        SecretKind kind,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(secret))
        {
            throw new ArgumentException("密碼不可空白。", nameof(secret));
        }

        var label = SanitizeLabel(profileName);
        var kindLabel = kind == SecretKind.DatabasePassword
            ? string.Empty
            : $" · {SecretStoreExtensions.DescribeSecretKind(kind)}";
        var result = await RunAsync(
            new[]
            {
                "store",
                $"--label=mySQLPunk · {label}{kindLabel}",
                "application",
                ApplicationAttribute,
                "profile-id",
                FormatId(profileId, kind)
            },
            secret,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw CreateOperationException("儲存");
        }

        var stored = await GetAsync(profileId, kind, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(stored, secret, StringComparison.Ordinal))
        {
            throw new SecretStoreException("系統密碼庫寫入後驗證失敗；密碼未視為已保存。");
        }
    }

    public Task DeleteAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        DeleteAsync(profileId, SecretKind.DatabasePassword, cancellationToken);

    public async Task DeleteAsync(Guid profileId, SecretKind kind, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            new[] { "clear", "application", ApplicationAttribute, "profile-id", FormatId(profileId, kind) },
            standardInput: null,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode is not (0 or 1))
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

    private static string FormatId(Guid profileId, SecretKind kind) =>
        SecretStoreExtensions.FormatSecretId(profileId, kind);

    private static string? FindDefaultExecutable()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        return SecretProcessRunner.FindExecutable("/usr/bin/secret-tool") ??
               SecretProcessRunner.FindExecutable("/usr/local/bin/secret-tool");
    }

    private static string SanitizeLabel(string profileName)
    {
        var sanitized = new string(profileName
            .Where(character => !char.IsControl(character))
            .Take(80)
            .ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "資料庫連線" : sanitized;
    }

    private static SecretStoreException CreateOperationException(string operation) =>
        new($"Linux Secret Service {operation}失敗；密碼只會保留在本次程式記憶體中。");
}
