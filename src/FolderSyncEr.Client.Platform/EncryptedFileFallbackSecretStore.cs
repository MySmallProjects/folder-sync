using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FolderSyncEr.Client.Platform;

public class EncryptedFileFallbackSecretStore : ISecretStore
{
    private readonly string _filePath;
    private readonly byte[] _encryptionKey;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SecretStoreType StoreType => SecretStoreType.EncryptedFileFallback;
    public bool IsInsecureFallback => true;
    public string Description => "Encrypted file fallback (OS Secret Service/Credential Manager unavailable)";

    public EncryptedFileFallbackSecretStore(string? customPath = null)
    {
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            _filePath = customPath;
        }
        else
        {
            var configDir = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FolderSyncEr")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "foldersyncer");

            try
            {
                Directory.CreateDirectory(configDir);
                _filePath = Path.Combine(configDir, "secrets.dat");
            }
            catch (Exception)
            {
                // Fallback to temp path if user profile/.config directory is read-only or restricted
                var fallbackDir = Path.Combine(Path.GetTempPath(), "foldersyncer");
                Directory.CreateDirectory(fallbackDir);
                _filePath = Path.Combine(fallbackDir, "secrets.dat");
            }
        }

        // Derive key from user profile & machine name
        var entropy = Encoding.UTF8.GetBytes($"{Environment.UserName}@{Environment.MachineName}:FolderSyncEr:Fallback");
        using var sha = SHA256.Create();
        _encryptionKey = sha.ComputeHash(entropy);
    }

    public async Task<string?> GetSecretAsync(string key, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var dict = await LoadSecretsInternalAsync(ct);
            return dict.TryGetValue(key, out var val) ? val : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SetSecretAsync(string key, string secret, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var dict = await LoadSecretsInternalAsync(ct);
            dict[key] = secret;
            await SaveSecretsInternalAsync(dict, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeleteSecretAsync(string key, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var dict = await LoadSecretsInternalAsync(ct);
            if (dict.Remove(key))
            {
                await SaveSecretsInternalAsync(dict, ct);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<Dictionary<string, string>> LoadSecretsInternalAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            var encrypted = await File.ReadAllBytesAsync(_filePath, ct);
            if (encrypted.Length < 12 + 16) // Nonce (12) + Tag (16)
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            var nonce = encrypted[..12];
            var tag = encrypted[12..28];
            var ciphertext = encrypted[28..];

            var plaintext = new byte[ciphertext.Length];
            using var aesGcm = new AesGcm(_encryptionKey, 16);
            aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);

            var json = Encoding.UTF8.GetString(plaintext);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private async Task SaveSecretsInternalAsync(Dictionary<string, string> dict, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(dict);
        var plaintext = Encoding.UTF8.GetBytes(json);

        var nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];

        using var aesGcm = new AesGcm(_encryptionKey, 16);
        aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);

        using var ms = new MemoryStream();
        await ms.WriteAsync(nonce, ct);
        await ms.WriteAsync(tag, ct);
        await ms.WriteAsync(ciphertext, ct);

        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllBytesAsync(_filePath, ms.ToArray(), ct);

        // Restrict file access to current user only on Linux/macOS
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(_filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch
            {
                // Best effort if filesystem doesn't support unix modes
            }
        }
    }
}
