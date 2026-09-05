namespace MySqlPunk.Core.Services;

/// <summary>Which secret of a connection profile a secret-store entry holds. Each kind is a separate item.</summary>
public enum SecretKind
{
    DatabasePassword,
    SshPassword,
    SshKeyPassphrase
}

public interface ISecretStore
{
    string DisplayName { get; }

    bool IsAvailable { get; }

    string UnavailableReason { get; }

    Task<string?> GetAsync(Guid profileId, CancellationToken cancellationToken = default);

    Task<string?> GetAsync(Guid profileId, SecretKind kind, CancellationToken cancellationToken = default);

    Task StoreAsync(
        Guid profileId,
        string profileName,
        string secret,
        CancellationToken cancellationToken = default);

    Task StoreAsync(
        Guid profileId,
        string profileName,
        string secret,
        SecretKind kind,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid profileId, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid profileId, SecretKind kind, CancellationToken cancellationToken = default);
}

public static class SecretStoreExtensions
{
    /// <summary>Removes every secret kind stored for a profile; used when the profile is deleted or opts out.</summary>
    public static async Task DeleteAllAsync(
        this ISecretStore store,
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        foreach (var kind in Enum.GetValues<SecretKind>())
        {
            await store.DeleteAsync(profileId, kind, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Stable per-kind identifier; the database password keeps the legacy bare id so existing entries still resolve.</summary>
    public static string FormatSecretId(Guid profileId, SecretKind kind) => kind switch
    {
        SecretKind.DatabasePassword => profileId.ToString("N"),
        SecretKind.SshPassword => profileId.ToString("N") + "-ssh-password",
        SecretKind.SshKeyPassphrase => profileId.ToString("N") + "-ssh-key-passphrase",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static string DescribeSecretKind(SecretKind kind) => kind switch
    {
        SecretKind.DatabasePassword => "資料庫密碼",
        SecretKind.SshPassword => "SSH 密碼",
        SecretKind.SshKeyPassphrase => "SSH 私鑰密語",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}

public sealed class SecretStoreException : Exception
{
    public SecretStoreException(string message)
        : base(message)
    {
    }

    public SecretStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
