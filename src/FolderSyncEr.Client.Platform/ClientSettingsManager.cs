using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderSyncEr.Client.Platform;

public class ClientSettingsManager
{
    private readonly string _settingsFilePath;
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _lock = new(1, 1);

    public string SettingsFilePath => _settingsFilePath;

    public ClientSettingsManager(string? customPath = null)
    {
        _settingsFilePath = customPath ?? GetDefaultSettingsFilePath();
    }

    public static string GetDefaultSettingsFilePath()
    {
        var configDir = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FolderSyncEr")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "foldersyncer");

        try
        {
            if (!Directory.Exists(configDir))
            {
                Directory.CreateDirectory(configDir);
            }
            return Path.Combine(configDir, "settings.json");
        }
        catch
        {
            var fallbackDir = Path.Combine(Path.GetTempPath(), "foldersyncer");
            if (!Directory.Exists(fallbackDir))
            {
                Directory.CreateDirectory(fallbackDir);
            }
            return Path.Combine(fallbackDir, "settings.json");
        }
    }

    public ClientSettings LoadSettings()
    {
        try
        {
            if (!File.Exists(_settingsFilePath))
            {
                var defaults = new ClientSettings();
                return defaults;
            }

            var json = File.ReadAllText(_settingsFilePath);
            var settings = JsonSerializer.Deserialize<ClientSettings>(json, _jsonOptions);
            return settings ?? new ClientSettings();
        }
        catch
        {
            return new ClientSettings();
        }
    }

    public async Task<ClientSettings> LoadSettingsAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_settingsFilePath))
            {
                var defaults = new ClientSettings();
                await SaveSettingsInternalAsync(defaults, ct).ConfigureAwait(false);
                return defaults;
            }

            var json = await File.ReadAllTextAsync(_settingsFilePath, ct).ConfigureAwait(false);
            var settings = JsonSerializer.Deserialize<ClientSettings>(json, _jsonOptions);
            return settings ?? new ClientSettings();
        }
        catch
        {
            return new ClientSettings();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveSettingsAsync(ClientSettings settings, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            settings.LastUpdatedUtc = DateTime.UtcNow;
            await SaveSettingsInternalAsync(settings, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task SaveSettingsInternalAsync(ClientSettings settings, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(_settingsFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(settings, _jsonOptions);
        var tempFile = _settingsFilePath + ".tmp." + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(tempFile, json, ct).ConfigureAwait(false);

        try
        {
            if (File.Exists(_settingsFilePath))
            {
                File.Replace(tempFile, _settingsFilePath, null);
            }
            else
            {
                File.Move(tempFile, _settingsFilePath);
            }
        }
        catch
        {
            // Fallback to simple copy/delete if filesystem does not support atomic replace
            File.Copy(tempFile, _settingsFilePath, overwrite: true);
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    public static bool OpenSettingsFileInDefaultViewer(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return false;
            }

            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = filePath,
                    UseShellExecute = true
                });
                return true;
            }

            if (OperatingSystem.IsLinux())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "xdg-open",
                    Arguments = $"\"{filePath}\"",
                    UseShellExecute = false
                });
                return true;
            }

            if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "open",
                    Arguments = $"\"{filePath}\"",
                    UseShellExecute = false
                });
                return true;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = filePath,
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }
}

