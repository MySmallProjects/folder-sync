using FolderSyncEr.Client.Platform;

namespace FolderSyncEr.Client.Tests;

public class SecretStoreTests : IDisposable
{
    private readonly string _tempFile;

    public SecretStoreTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"secrets_test_{Guid.NewGuid():N}.dat");
    }

    [Fact]
    public async Task EncryptedFileStore_CanSetGetAndDeleteSecrets()
    {
        var store = new EncryptedFileFallbackSecretStore(_tempFile);

        // Initially null
        var initial = await store.GetSecretAsync("non_existent_key");
        Assert.Null(initial);

        // Set
        await store.SetSecretAsync("api_key", "secret-value-12345");
        var retrieved = await store.GetSecretAsync("api_key");
        Assert.Equal("secret-value-12345", retrieved);

        // Update
        await store.SetSecretAsync("api_key", "updated-value-67890");
        var updated = await store.GetSecretAsync("api_key");
        Assert.Equal("updated-value-67890", updated);

        // Delete
        await store.DeleteSecretAsync("api_key");
        var deleted = await store.GetSecretAsync("api_key");
        Assert.Null(deleted);
    }

    [Fact]
    public async Task EncryptedFileStore_PersistsInEncryptedFormat()
    {
        var store = new EncryptedFileFallbackSecretStore(_tempFile);
        var sensitiveSecret = "SuperSensitivePasswordNeverStoreInPlaintext!";
        await store.SetSecretAsync("password", sensitiveSecret);

        // Read raw bytes on disk
        var rawBytes = await File.ReadAllBytesAsync(_tempFile);
        var rawString = System.Text.Encoding.UTF8.GetString(rawBytes);

        // Raw file on disk must NOT contain the sensitive string
        Assert.DoesNotContain(sensitiveSecret, rawString);

        // Store must be flagged as fallback
        Assert.True(store.IsInsecureFallback);
        Assert.Equal(SecretStoreType.EncryptedFileFallback, store.StoreType);
    }

    [Fact]
    public async Task EncryptedFileStore_SetsUserOnlyPermissionsOnUnix()
    {
        if (OperatingSystem.IsWindows()) return;

        var store = new EncryptedFileFallbackSecretStore(_tempFile);
        await store.SetSecretAsync("token", "xyz");

        var mode = File.GetUnixFileMode(_tempFile);
        // UserRead (0400) and UserWrite (0200) should be set, but Group/Other must NOT have read/write
        Assert.True(mode.HasFlag(UnixFileMode.UserRead));
        Assert.True(mode.HasFlag(UnixFileMode.UserWrite));
        Assert.False(mode.HasFlag(UnixFileMode.GroupRead));
        Assert.False(mode.HasFlag(UnixFileMode.OtherRead));
    }

    [Fact]
    public void SecretStoreFactory_ReturnsStoreWithDescription()
    {
        var store = SecretStoreFactory.Create();
        Assert.NotNull(store);
        Assert.False(string.IsNullOrWhiteSpace(store.Description));
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile))
        {
            try { File.Delete(_tempFile); } catch { }
        }
    }
}

