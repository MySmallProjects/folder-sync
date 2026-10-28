using Microsoft.Extensions.Logging;

namespace FolderSyncEr.Client.Platform;

public static class SecretStoreFactory
{
    public static ISecretStore Create(ILogger? logger = null, string? customFallbackPath = null)
    {
        if (OperatingSystem.IsWindows())
        {
            logger?.LogInformation("Using Windows Credential Manager for secure storage.");
            return new WindowsCredentialManagerSecretStore();
        }

        if (OperatingSystem.IsLinux())
        {
            if (LinuxSecretServiceSecretStore.IsAvailable())
            {
                logger?.LogInformation("Using Linux Secret Service (libsecret) for secure storage.");
                return new LinuxSecretServiceSecretStore();
            }

            logger?.LogWarning("Linux Secret Service (libsecret) or D-Bus session not available. Using encrypted file fallback.");
            return new EncryptedFileFallbackSecretStore(customFallbackPath);
        }

        logger?.LogWarning("Non-Windows/Linux OS detected. Using encrypted file fallback.");
        return new EncryptedFileFallbackSecretStore(customFallbackPath);
    }
}
