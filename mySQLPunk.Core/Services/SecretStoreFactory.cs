namespace MySqlPunk.Core.Services;

public static class SecretStoreFactory
{
    public static ISecretStore CreateDefault()
    {
        if (OperatingSystem.IsLinux())
        {
            return new LinuxSecretServiceStore();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacOsKeychainSecretStore();
        }

        return new UnavailableSecretStore();
    }

    private sealed class UnavailableSecretStore : ISecretStore
    {
        public string DisplayName => "系統密碼庫";

        public bool IsAvailable => false;

        public string UnavailableReason => "目前平台尚未提供跨平台密碼庫整合。";

        public Task<string?> GetAsync(Guid profileId, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<string?> GetAsync(Guid profileId, SecretKind kind, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task StoreAsync(
            Guid profileId,
            string profileName,
            string secret,
            CancellationToken cancellationToken = default) =>
            Task.FromException(new SecretStoreException(UnavailableReason));

        public Task StoreAsync(
            Guid profileId,
            string profileName,
            string secret,
            SecretKind kind,
            CancellationToken cancellationToken = default) =>
            Task.FromException(new SecretStoreException(UnavailableReason));

        public Task DeleteAsync(Guid profileId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DeleteAsync(Guid profileId, SecretKind kind, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
