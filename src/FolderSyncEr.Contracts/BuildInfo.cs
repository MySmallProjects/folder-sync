using System;
using System.IO;
using System.Reflection;

namespace FolderSyncEr.Contracts;

public static class BuildInfo
{
    public static DateTime GetBuildDateUtc(Assembly assembly)
    {
        try
        {
            var loc = assembly.Location;
            if (!string.IsNullOrEmpty(loc) && File.Exists(loc))
            {
                return File.GetLastWriteTimeUtc(loc);
            }
        }
        catch { }

        try
        {
            var name = assembly.GetName().Name;
            if (!string.IsNullOrEmpty(name))
            {
                var candidate = Path.Combine(AppContext.BaseDirectory, name + ".dll");
                if (File.Exists(candidate))
                {
                    return File.GetLastWriteTimeUtc(candidate);
                }
            }
        }
        catch { }

        try
        {
            var proc = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(proc) && File.Exists(proc))
            {
                return File.GetLastWriteTimeUtc(proc);
            }
        }
        catch { }

        return DateTime.UtcNow;
    }

    public static string GetVersionString(Assembly assembly)
    {
        var ver = assembly.GetName().Version;
        return ver != null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "1.0.0";
    }
}

