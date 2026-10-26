using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace SwiftGet.Engine.IO;

public static partial class FileNameSanitizer
{
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    public static string Sanitize(string fileName, string fallbackName = "download.bin")
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return fallbackName;
        }

        // 1. Strip directory traversal sequences, convert slashes and NTFS alternate data stream colons
        fileName = fileName
            .Replace("../", "_/", StringComparison.Ordinal)
            .Replace("..\\", "_\\", StringComparison.Ordinal)
            .Replace("/..", "/_", StringComparison.Ordinal)
            .Replace("\\..", "\\_", StringComparison.Ordinal)
            .Replace('\\', '_')
            .Replace('/', '_');

        // 2. Remove invalid chars, control chars (< 32), and bidirectional override spoofing characters (RLO/LRO/etc)
        var chars = fileName.Where(c =>
            !InvalidChars.Contains(c) &&
            c >= 32 &&
            c != 127 &&
            !IsSpoofingControlChar(c)
        ).ToArray();

        var clean = new string(chars).Trim();

        if (string.IsNullOrWhiteSpace(clean))
        {
            return fallbackName;
        }

        // 3. Avoid leading/trailing dots and spaces
        clean = clean.Trim('.', ' ');

        if (string.IsNullOrWhiteSpace(clean))
        {
            clean = fallbackName;
        }

        // 4. Neutralize Windows reserved device names (including CON.txt, PRN.tar.gz, aux ., etc.)
        var dotIndex = clean.IndexOf('.', StringComparison.Ordinal);
        var baseName = dotIndex >= 0 ? clean[..dotIndex] : clean;
        var ext = dotIndex >= 0 ? clean[dotIndex..] : string.Empty;
        var trimmedBase = baseName.TrimEnd(' ', '.');

        if (ReservedDeviceNames.Contains(trimmedBase))
        {
            clean = "_" + clean;
            dotIndex = clean.IndexOf('.', StringComparison.Ordinal);
            baseName = dotIndex >= 0 ? clean[..dotIndex] : clean;
            ext = dotIndex >= 0 ? clean[dotIndex..] : string.Empty;
        }

        // 5. Truncate to maximum standard filename length (250 chars) while preserving extension
        if (clean.Length > 250)
        {
            var allowedBaseLen = Math.Max(1, 250 - ext.Length);
            if (baseName.Length > allowedBaseLen)
            {
                baseName = baseName[..allowedBaseLen];
            }
            clean = baseName + ext;
        }

        return clean;
    }

    private static bool IsSpoofingControlChar(char c)
    {
        // Check for Unicode bidirectional overrides and zero-width spaces that spoof file extensions
        return c switch
        {
            '\u200B' => true, // Zero-width space
            '\u200C' => true, // Zero-width non-joiner
            '\u200D' => true, // Zero-width joiner
            '\u200E' => true, // Left-to-right mark
            '\u200F' => true, // Right-to-left mark
            '\u202A' => true, // LRE
            '\u202B' => true, // RLE
            '\u202C' => true, // PDF
            '\u202D' => true, // LRO
            '\u202E' => true, // RLO (Right-to-Left Override)
            '\u2066' => true, // LRI
            '\u2067' => true, // RLI
            '\u2068' => true, // FSI
            '\u2069' => true, // PDI
            '\uFEFF' => true, // Zero-width no-break space (BOM)
            _ => char.GetUnicodeCategory(c) == UnicodeCategory.Format
        };
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*\(\d+\)\s*")]
    private static partial System.Text.RegularExpressions.Regex NotificationCounterRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\s*-\s*(YouTube|Vimeo|Dailymotion|TikTok|Twitch|SoundCloud)\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex SiteSuffixRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\s+")]
    private static partial System.Text.RegularExpressions.Regex MultiWhitespaceRegex();

    public static string SanitizeMediaTitle(string title, string fallback = "video")
    {
        if (string.IsNullOrWhiteSpace(title))
            return fallback;

        // 1. Strip leading notification counter like (50)
        var result = NotificationCounterRegex().Replace(title, string.Empty);

        // 2. Strip trailing site suffix like - YouTube
        result = SiteSuffixRegex().Replace(result, string.Empty);

        // 3. Replace colons with readable separator instead of bare underscore
        result = result.Replace(":", " - ", StringComparison.Ordinal);

        // 4. Clean invalid chars and spoofing chars
        var chars = result.Where(c =>
            !InvalidChars.Contains(c) &&
            c >= 32 &&
            c != 127 &&
            !IsSpoofingControlChar(c)
        ).ToArray();

        result = new string(chars);

        // 5. Normalize whitespace and hyphens
        result = MultiWhitespaceRegex().Replace(result, " ");
        result = result.Replace(" - - ", " - ", StringComparison.Ordinal);

        // 6. Avoid trailing dots and spaces before extension is appended
        result = result.Trim('.', ' ', '-');

        if (string.IsNullOrWhiteSpace(result))
            return fallback;

        if (result.Length > 240)
            result = result[..240].Trim('.', ' ');

        return result;
    }

    public static string SanitizeFileNameWithExtension(string baseName, string extension, string fallback = "video")
    {
        var cleanBase = SanitizeMediaTitle(baseName, fallback);
        cleanBase = cleanBase.TrimEnd('.', ' ');

        var cleanExt = (extension ?? string.Empty).Trim().TrimStart('.').TrimEnd('.');
        if (string.IsNullOrEmpty(cleanExt))
            return cleanBase;

        return $"{cleanBase}.{cleanExt}";
    }

    public static string FormatFileSizeWithExactBytes(long bytes)
    {
        if (bytes <= 0) return "0 B (0 bytes)";

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        int unitIndex = 0;

        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        var formattedUnit = unitIndex == 0
            ? $"{bytes} B"
            : $"{size.ToString("F2", CultureInfo.InvariantCulture)} {units[unitIndex]}";

        return $"{formattedUnit} ({bytes:N0} bytes)";
    }
}
