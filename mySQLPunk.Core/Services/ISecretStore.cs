namespace MySqlPunk.Core.Services;

public interface ISecretStore
{
    string DisplayName { get; }

    bool IsAvailable { get; }

    string UnavailableReason { get; }

    Task<string?> GetAsync(Guid profileId, CancellationToken cancellationToken = default);

    Task StoreAsync(
        Guid profileId,
        string profileName,
        string secret,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid profileId, CancellationToken cancellationToken = default);
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
