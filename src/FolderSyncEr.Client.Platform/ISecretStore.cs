namespace FolderSyncEr.Client.Platform;

public enum SecretStoreType
{
    WindowsCredentialManager,
    WindowsDpapi,
    LinuxSecretService,
    EncryptedFileFallback
}

public interface ISecretStore
{
    SecretStoreType StoreType { get; }
    bool IsInsecureFallback { get; }
    string Description { get; }

    Task<string?> GetSecretAsync(string key, CancellationToken ct = default);
    Task SetSecretAsync(string key, string secret, CancellationToken ct = default);
    Task DeleteSecretAsync(string key, CancellationToken ct = default);
}

