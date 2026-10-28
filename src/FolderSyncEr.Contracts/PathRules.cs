using System.Text.RegularExpressions;

namespace FolderSyncEr.Contracts;

public static class PathRules
{
    private static readonly char[] InvalidChars = ['<', '>', ':', '"', '|', '?', '*'];
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        // Replace backslashes with slashes
        var normalized = path.Replace('\\', '/').Trim();

        // Remove leading and trailing slashes
        normalized = normalized.Trim('/');

        // Unicode normalization form C
        normalized = normalized.Normalize(System.Text.NormalizationForm.FormC);

        return normalized;
    }

    public static bool TryValidatePath(string rawPath, out string normalizedPath, out string errorMessage)
    {
        normalizedPath = NormalizePath(rawPath);

        if (string.IsNullOrEmpty(normalizedPath))
        {
            errorMessage = "Path cannot be empty.";
            return false;
        }

        if (normalizedPath.Length > 1024)
        {
            errorMessage = "Path exceeds maximum length of 1024 characters.";
            return false;
        }

        var segments = normalizedPath.Split('/');
        foreach (var segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment))
            {
                errorMessage = "Path cannot contain empty segments or consecutive slashes.";
                return false;
            }

            if (segment == "." || segment == "..")
            {
                errorMessage = "Relative path traversal ('..' or '.') is not allowed.";
                return false;
            }

            if (segment.EndsWith('.') || segment.EndsWith(' '))
            {
                errorMessage = $"Segment '{segment}' cannot end with a period or space.";
                return false;
            }

            if (segment.IndexOfAny(InvalidChars) >= 0)
            {
                errorMessage = $"Segment '{segment}' contains illegal characters (< > : \" | ? *).";
                return false;
            }

            // Check Windows reserved device names (e.g., NUL, AUX, COM1, or NUL.txt)
            var baseName = segment.Split('.')[0];
            if (ReservedNames.Contains(baseName))
            {
                errorMessage = $"Segment '{segment}' uses a reserved system name '{baseName}'.";
                return false;
            }
        }

        errorMessage = string.Empty;
        return true;
    }

    public static string ToLookupKey(string path)
    {
        return NormalizePath(path).ToUpperInvariant();
    }
}

