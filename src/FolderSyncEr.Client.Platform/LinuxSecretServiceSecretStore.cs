using System.Runtime.InteropServices;
using System.Text;

namespace FolderSyncEr.Client.Platform;

public class LinuxSecretServiceSecretStore : ISecretStore
{
    public SecretStoreType StoreType => SecretStoreType.LinuxSecretService;
    public bool IsInsecureFallback => false;
    public string Description => "GNOME Secret Service (libsecret)";

    private const string LibSecret = "libsecret-1.so.0";
    private static IntPtr _schemaPtr = IntPtr.Zero;
    private static readonly object _schemaLock = new();

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

            // Check if D-Bus session address is set (required for SecretService)
            var dbusSession = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
            return !string.IsNullOrWhiteSpace(dbusSession);
        }
        catch
        {
            return false;
        }
    }

    public Task<string?> GetSecretAsync(string key, CancellationToken ct = default)
    {
        EnsureSchema();
        var error = IntPtr.Zero;
        try
        {
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
                return Task.FromResult<string?>(null);
            }

            if (resultPtr == IntPtr.Zero)
            {
                return Task.FromResult<string?>(null);
            }

            try
            {
                var secret = Marshal.PtrToStringUTF8(resultPtr);
                return Task.FromResult(secret);
            }
            finally
            {
                secret_password_free(resultPtr);
            }
        }
        catch
        {
            return Task.FromResult<string?>(null);
        }
    }

    public Task SetSecretAsync(string key, string secret, CancellationToken ct = default)
    {
        EnsureSchema();
        var error = IntPtr.Zero;
        try
        {
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
                throw new InvalidOperationException($"Secret Service store failed: {errMsg}");
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException("Failed to save secret to Linux Secret Service.", ex);
        }

        return Task.CompletedTask;
    }

    public Task DeleteSecretAsync(string key, CancellationToken ct = default)
    {
        EnsureSchema();
        var error = IntPtr.Zero;
        try
        {
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
        catch
        {
            // Ignore on delete
        }

        return Task.CompletedTask;
    }

    private static void EnsureSchema()
    {
        if (_schemaPtr != IntPtr.Zero) return;

        lock (_schemaLock)
        {
            if (_schemaPtr != IntPtr.Zero) return;

            // Define SecretSchema dynamically in unmanaged memory
            // struct SecretSchema: name (ptr), flags (int), attributes[32] (name ptr, type int)
            var size = IntPtr.Size + sizeof(int) + 32 * (IntPtr.Size + sizeof(int));
            _schemaPtr = Marshal.AllocHGlobal(size);

            // Zero out memory
            for (int i = 0; i < size; i++)
            {
                Marshal.WriteByte(_schemaPtr, i, 0);
            }

            var namePtr = Marshal.StringToHGlobalAnsi("org.foldersyncer.Password");
            var serviceAttr = Marshal.StringToHGlobalAnsi("service");
            var accountAttr = Marshal.StringToHGlobalAnsi("account");

            var offset = 0;
            Marshal.WriteIntPtr(_schemaPtr, offset, namePtr);
            offset += IntPtr.Size;

            Marshal.WriteInt32(_schemaPtr, offset, 0); // SECRET_SCHEMA_NONE
            offset += sizeof(int);

            // Attribute 0: service (STRING = 0)
            Marshal.WriteIntPtr(_schemaPtr, offset, serviceAttr);
            offset += IntPtr.Size;
            Marshal.WriteInt32(_schemaPtr, offset, 0);
            offset += sizeof(int);

            // Attribute 1: account (STRING = 0)
            Marshal.WriteIntPtr(_schemaPtr, offset, accountAttr);
            offset += IntPtr.Size;
            Marshal.WriteInt32(_schemaPtr, offset, 0);
            offset += sizeof(int);
        }
    }

    private static string GetGErrorMessage(IntPtr errorPtr)
    {
        if (errorPtr == IntPtr.Zero) return "Unknown error";
        // GError struct: GQuark domain (uint32), gint code (int32), gchar *message
        var msgPtr = Marshal.ReadIntPtr(errorPtr, 8);
        return Marshal.PtrToStringUTF8(msgPtr) ?? "Unknown GError";
    }

    [DllImport(LibSecret, CallingConvention = CallingConvention.Cdecl)]
    private static extern bool secret_password_store_sync(
        IntPtr schema,
        string? collection,
        string label,
        string password,
        IntPtr cancellable,
        out IntPtr error,
        string attr1, string val1,
        string attr2, string val2,
        IntPtr end
    );

    [DllImport(LibSecret, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr secret_password_lookup_sync(
        IntPtr schema,
        IntPtr cancellable,
        out IntPtr error,
        string attr1, string val1,
        string attr2, string val2,
        IntPtr end
    );

    [DllImport(LibSecret, CallingConvention = CallingConvention.Cdecl)]
    private static extern bool secret_password_clear_sync(
        IntPtr schema,
        IntPtr cancellable,
        out IntPtr error,
        string attr1, string val1,
        string attr2, string val2,
        IntPtr end
    );

    [DllImport(LibSecret, CallingConvention = CallingConvention.Cdecl)]
    private static extern void secret_password_free(IntPtr password);

    [DllImport("libglib-2.0.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern void g_error_free(IntPtr error);
}

