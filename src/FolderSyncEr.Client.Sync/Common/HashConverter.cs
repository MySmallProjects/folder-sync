using System;

namespace FolderSyncEr.Client.Sync.Common;

/// <summary>
/// Converts SHA-256 representations between Hex (used by REST API) and Base64 (used by SharpSync).
/// </summary>
public static class HashConverter
{
    public static string? HexToBase64(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return null;

        try
        {
            var bytes = Convert.FromHexString(hex.Trim());
            return Convert.ToBase64String(bytes);
        }
        catch
        {
            return null;
        }
    }

    public static string? Base64ToHex(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
            return null;

        try
        {
            var bytes = Convert.FromBase64String(base64.Trim());
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }
}

