using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace FolderSyncEr.Client.Platform;

public class WindowsCredentialManagerSecretStore : ISecretStore
{
    public SecretStoreType StoreType => SecretStoreType.WindowsCredentialManager;
    public bool IsInsecureFallback => false;
    public string Description => "Windows Credential Manager / DPAPI";

    private const string TargetPrefix = "FolderSyncEr:";
    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;

    public Task<string?> GetSecretAsync(string key, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Credential Manager is only available on Windows.");
        }

        var targetName = TargetPrefix + key;
        if (!CredRead(targetName, CRED_TYPE_GENERIC, 0, out var credPtr))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 1168) // ERROR_NOT_FOUND
            {
                // Check DPAPI fallback file if present
                return Task.FromResult(ReadDpapiFallback(key));
            }
            return Task.FromResult<string?>(null);
        }

        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
            if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize == 0)
            {
                return Task.FromResult<string?>(null);
            }

            var secret = Marshal.PtrToStringUni(cred.CredentialBlob, (int)cred.CredentialBlobSize / 2);
            return Task.FromResult<string?>(secret);
        }
        finally
        {
            CredFree(credPtr);
        }
    }

    public Task SetSecretAsync(string key, string secret, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Credential Manager is only available on Windows.");
        }

        var targetName = TargetPrefix + key;
        var secretBytes = Encoding.Unicode.GetBytes(secret);

        var cred = new CREDENTIAL
        {
            Type = CRED_TYPE_GENERIC,
            TargetName = targetName,
            CredentialBlobSize = (uint)secretBytes.Length,
            Persist = CRED_PERSIST_LOCAL_MACHINE,
            UserName = Environment.UserName
        };

        var blobPtr = Marshal.AllocHGlobal(secretBytes.Length);
        try
        {
            Marshal.Copy(secretBytes, 0, blobPtr, secretBytes.Length);
            cred.CredentialBlob = blobPtr;

            if (!CredWrite(ref cred, 0))
            {
                // Try DPAPI fallback
                WriteDpapiFallback(key, secret);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(blobPtr);
        }

        return Task.CompletedTask;
    }

    public Task DeleteSecretAsync(string key, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.CompletedTask;
        }

        var targetName = TargetPrefix + key;
        CredDelete(targetName, CRED_TYPE_GENERIC, 0);
        DeleteDpapiFallback(key);

        return Task.CompletedTask;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? ReadDpapiFallback(string key)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FolderSyncEr", "dpapi");
            var file = Path.Combine(dir, $"{key}.bin");
            if (!File.Exists(file)) return null;

            var encrypted = File.ReadAllBytes(file);
            var decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch
        {
            return null;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void WriteDpapiFallback(string key, string secret)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FolderSyncEr", "dpapi");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"{key}.bin");

        var plain = Encoding.UTF8.GetBytes(secret);
        var encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(file, encrypted);
    }

    private static void DeleteDpapiFallback(string key)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FolderSyncEr", "dpapi");
            var file = Path.Combine(dir, $"{key}.bin");
            if (File.Exists(file)) File.Delete(file);
        }
        catch
        {
            // ignore
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredRead(string target, uint type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredWrite([In] ref CREDENTIAL userCredential, [In] uint flags);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredDelete(string target, uint type, int flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree([In] IntPtr cred);
}
