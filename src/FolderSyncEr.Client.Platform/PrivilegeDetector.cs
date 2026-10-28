using System;
using System.Runtime.InteropServices;

namespace FolderSyncEr.Client.Platform;

public static class PrivilegeDetector
{
    [DllImport("libc", EntryPoint = "geteuid", SetLastError = true)]
    private static extern uint GetEffectiveUserId();

    /// <summary>
    /// Returns true if the current process is running with root/administrator privileges.
    /// </summary>
    public static bool IsRunningAsRoot()
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            try
            {
                return GetEffectiveUserId() == 0;
            }
            catch
            {
                return Environment.UserName.Equals("root", StringComparison.OrdinalIgnoreCase);
            }
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true if the current process was executed through 'sudo' on Linux/macOS.
    /// </summary>
    public static bool IsInvokedViaSudo()
    {
        return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SUDO_USER")) ||
               !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SUDO_UID"));
    }

    /// <summary>
    /// Returns the original username that invoked sudo, or Environment.UserName if not sudo.
    /// </summary>
    public static string GetInvokingUsername()
    {
        return Environment.GetEnvironmentVariable("SUDO_USER") ?? Environment.UserName;
    }

    /// <summary>
    /// Returns a human-friendly privilege status summary.
    /// </summary>
    public static string GetPrivilegeDescription()
    {
        if (IsInvokedViaSudo())
        {
            return $"Running with elevated privileges via sudo (Invoking user: {GetInvokingUsername()})";
        }

        if (IsRunningAsRoot())
        {
            return "Running as root user";
        }

        return $"Standard user ({Environment.UserName})";
    }
}

