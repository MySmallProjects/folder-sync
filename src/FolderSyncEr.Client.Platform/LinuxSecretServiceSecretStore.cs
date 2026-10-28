using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FolderSyncEr.Client.Platform;

public class LinuxSecretServiceSecretStore : ISecretStore
{
    public SecretStoreType StoreType => _usedFallback ? SecretStoreType.EncryptedFileFallback : SecretStoreType.LinuxSecretService;
    public bool IsInsecureFallback => _usedFallback;
    public string Description => _usedFallback
        ? "Encrypted file fallback (~/.config/foldersyncer/secrets.enc.bin)"
        : "GNOME Secret Service (libsecret)";

    private const string LibSecret = "libsecret-1.so.0";
    private static IntPtr _schemaPtr = IntPtr.Zero;
    private static readonly object _schemaLock = new();
    private readonly EncryptedFileFallbackSecretStore _fallback;
    private bool _usedFallback;

    public LinuxSecretServiceSecretStore(string? customFallbackPath = null)
    {
        _fallback = new EncryptedFileFallbackSecretStore(customFallbackPath);
    }

    public static bool IsAvailable()
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        try
        {
            if (!NativeLibrary.TryLoad(LibSecret, typeof(LinuxSecretServiceSecretStore).Assembly, null, out var handle))
            {
                return false;
            }
            NativeLibrary.Free(handle);

            var dbusSession = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
            return !string.IsNullOrWhiteSpace(dbusSession);
        }
        catch
        {
            return false;
        }
    }

    public async Task<string?> GetSecretAsync(string key, CancellationToken ct = default)
    {
        if (_usedFallback)
        {
            return await _fallback.GetSecretAsync(key, ct);
        }

        try
        {
            EnsureSchema();
            var error = IntPtr.Zero;
            var resultPtr = secret_password_lookup_sync(
                _schemaPtr,
                IntPtr.Zero,
                out error,
                "service", "FolderSyncEr",
                "account", key,
                IntPtr.Zero
            );

            if (error != IntPtr.Zero)
            {
                g_error_free(error);
                return await _fallback.GetSecretAsync(key, ct);
            }

            if (resultPtr == IntPtr.Zero)
            {
                // Also check fallback if key was stored there previously
                return await _fallback.GetSecretAsync(key, ct);
            }

            try
            {
                var secret = Marshal.PtrToStringUTF8(resultPtr);
                return secret;
            }
            finally
            {
                secret_password_free(resultPtr);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SECRET SERVICE WARNING] libsecret read failed, using fallback: {ex.Message}");
            _usedFallback = true;
            return await _fallback.GetSecretAsync(key, ct);
        }
    }

    public async Task SetSecretAsync(string key, string secret, CancellationToken ct = default)
    {
        if (_usedFallback)
        {
            await _fallback.SetSecretAsync(key, secret, ct);
            return;
        }

        try
        {
            EnsureSchema();
            var error = IntPtr.Zero;
            var success = secret_password_store_sync(
                _schemaPtr,
                "default",
                $"FolderSyncEr: {key}",
                secret,
                IntPtr.Zero,
                out error,
                "service", "FolderSyncEr",
                "account", key,
                IntPtr.Zero
            );

            if (!success && error != IntPtr.Zero)
            {
                var errMsg = GetGErrorMessage(error);
                g_error_free(error);
                Console.Error.WriteLine($"[SECRET SERVICE WARNING] libsecret store returned error: {errMsg}. Storing in fallback.");
                _usedFallback = true;
                await _fallback.SetSecretAsync(key, secret, ct);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SECRET SERVICE WARNING] libsecret store threw: {ex.Message}. Storing in fallback.");
            _usedFallback = true;
            await _fallback.SetSecretAsync(key, secret, ct);
        }
    }

    public async Task DeleteSecretAsync(string key, CancellationToken ct = default)
    {
        try
        {
            if (!_usedFallback)
            {
                EnsureSchema();
                var error = IntPtr.Zero;
                secret_password_clear_sync(
                    _schemaPtr,
                    IntPtr.Zero,
                    out error,
                    "service", "FolderSyncEr",
                    "account", key,
                    IntPtr.Zero
                );

                if (error != IntPtr.Zero)
                {
                    g_error_free(error);
                }
            }
        }
        catch
        {
            // Ignore libsecret delete error
        }

        await _fallback.DeleteSecretAsync(key, ct);
    }

    private static void EnsureSchema()
    {
        if (_schemaPtr != IntPtr.Zero) return;

        lock (_schemaLock)
        {
            if (_schemaPtr != IntPtr.Zero) return;

            // GCC 64-bit Linux struct SecretSchema alignment:
            // Offset 0:  const gchar *name (8 bytes)
            // Offset 8:  SecretSchemaFlags flags (4 bytes) + 4 bytes padding
            // Offset 16: attributes[0].name (8 bytes)
            // Offset 24: attributes[0].type (4 bytes) + 4 bytes padding
            // Offset 32: attributes[1].name (8 bytes)
            // Offset 40: attributes[1].type (4 bytes) + 4 bytes padding
            // Total allocation size for 32 attributes = 16 + 32 * 16 = 528 bytes
            const int totalSize = 528;
            _schemaPtr = Marshal.AllocHGlobal(totalSize);

            // Zero out memory
            for (int i = 0; i < totalSize; i++)
            {
                Marshal.WriteByte(_schemaPtr, i, 0);
            }

            var namePtr = Marshal.StringToHGlobalAnsi("org.foldersyncer.Password");
            var serviceAttr = Marshal.StringToHGlobalAnsi("service");
            var accountAttr = Marshal.StringToHGlobalAnsi("account");

            // Write schema name & flags
            Marshal.WriteIntPtr(_schemaPtr, 0, namePtr);
            Marshal.WriteInt32(_schemaPtr, 8, 0); // SECRET_SCHEMA_NONE

            // Attribute 0: "service" (SECRET_SCHEMA_ATTRIBUTE_STRING = 0)
            Marshal.WriteIntPtr(_schemaPtr, 16, serviceAttr);
            Marshal.WriteInt32(_schemaPtr, 24, 0);

            // Attribute 1: "account" (SECRET_SCHEMA_ATTRIBUTE_STRING = 0)
            Marshal.WriteIntPtr(_schemaPtr, 32, accountAttr);
            Marshal.WriteInt32(_schemaPtr, 40, 0);

            // Attribute 2..31 remain 0 (name is NULL), terminating the attribute array
        }
    }

    private static string GetGErrorMessage(IntPtr errorPtr)
    {
        if (errorPtr == IntPtr.Zero) return "Unknown error";
        // GError struct: GQuark domain (uint32), gint code (int32), gchar *message
        var msgPtr = Marshal.ReadIntPtr(errorPtr, 8);
        return Marshal.PtrToStringUTF8(msgPtr) ?? "Unknown GError";
    }

    [DllImport(LibSecret, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern bool secret_password_store_sync(
        IntPtr schema,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? collection,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string label,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string password,
        IntPtr cancellable,
        out IntPtr error,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string attr1, [MarshalAs(UnmanagedType.LPUTF8Str)] string val1,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string attr2, [MarshalAs(UnmanagedType.LPUTF8Str)] string val2,
        IntPtr end
    );

    [DllImport(LibSecret, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern IntPtr secret_password_lookup_sync(
        IntPtr schema,
        IntPtr cancellable,
        out IntPtr error,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string attr1, [MarshalAs(UnmanagedType.LPUTF8Str)] string val1,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string attr2, [MarshalAs(UnmanagedType.LPUTF8Str)] string val2,
        IntPtr end
    );

    [DllImport(LibSecret, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern bool secret_password_clear_sync(
        IntPtr schema,
        IntPtr cancellable,
        out IntPtr error,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string attr1, [MarshalAs(UnmanagedType.LPUTF8Str)] string val1,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string attr2, [MarshalAs(UnmanagedType.LPUTF8Str)] string val2,
        IntPtr end
    );

    [DllImport(LibSecret, CallingConvention = CallingConvention.Cdecl)]
    private static extern void secret_password_free(IntPtr password);

    [DllImport("libglib-2.0.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern void g_error_free(IntPtr error);
}
