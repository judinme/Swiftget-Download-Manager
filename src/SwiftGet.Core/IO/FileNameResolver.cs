using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SwiftGet.Core.IO;

public class FileNameResolver : IFileNameResolver
{
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private static readonly HashSet<string> GenericOrScriptExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".php", ".aspx", ".asp", ".jsp", ".cgi", ".pl", ".do", ".action",
        ".html", ".htm", ".bin", ".download", ".part", ".tmp"
    };

    private static readonly HashSet<string> DangerousExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".scr",
        ".com", ".hta", ".cpl", ".wsf", ".jar", ".vbe", ".jse", ".reg"
    };

    private static readonly string[] CompoundExtensions =
    [
        ".tar.gz",
        ".tar.bz2",
        ".tar.xz",
        ".tar.zst",
        ".user.js"
    ];

    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

    public ResolvedFileName Resolve(FileNameResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        string? resolvedName = null;
        var source = FileNameSource.Fallback;
        bool userExplicit = context.UserExplicitlyNamed;

        // 1. User edited name (Authoritative)
        if (!string.IsNullOrWhiteSpace(context.UserEditedName))
        {
            resolvedName = context.UserEditedName.Trim();
            source = FileNameSource.UserEdited;
            userExplicit = true;
        }

        // 2. Browser captured name (if present, valid, and not an extensionless path-slug when probe has better)
        if (resolvedName == null && !string.IsNullOrWhiteSpace(context.BrowserCapturedName))
        {
            var browserCandidate = SanitizeFileName(context.BrowserCapturedName);
            var browserExt = GetCompoundExtension(browserCandidate);

            // If browser captured name has a real non-generic extension, or if Content-Disposition is absent
            if (!string.IsNullOrEmpty(browserExt) && !IsGenericOrScriptExtension(browserExt))
            {
                resolvedName = browserCandidate;
                source = FileNameSource.BrowserCaptured;
            }
            else if (string.IsNullOrWhiteSpace(context.ContentDisposition))
            {
                // No Content-Disposition available, use browser candidate
                resolvedName = browserCandidate;
                source = FileNameSource.BrowserCaptured;
            }
        }

        // 3. Content-Disposition from final response (RFC 5987 / RFC 6266)
        if (resolvedName == null && !string.IsNullOrWhiteSpace(context.ContentDisposition))
        {
            var cdName = ParseContentDisposition(context.ContentDisposition);
            if (!string.IsNullOrWhiteSpace(cdName))
            {
                resolvedName = cdName;
                source = FileNameSource.ContentDisposition;
            }
        }

        // 4. If BrowserCapturedName was deferred in step 2 because it was extensionless, but step 3 had no Content-Disposition
        if (resolvedName == null && !string.IsNullOrWhiteSpace(context.BrowserCapturedName))
        {
            resolvedName = context.BrowserCapturedName;
            source = FileNameSource.BrowserCaptured;
        }

        // 5. Final URL path segment after redirects, percent-decoded
        var effectiveUrl = !string.IsNullOrWhiteSpace(context.FinalUrl) ? context.FinalUrl : context.OriginalUrl;
        if (resolvedName == null && !string.IsNullOrWhiteSpace(effectiveUrl))
        {
            var segmentName = ExtractFinalUrlSegment(effectiveUrl);
            if (!string.IsNullOrWhiteSpace(segmentName))
            {
                // If segment has a real extension or query string has nothing better
                resolvedName = segmentName;
                source = FileNameSource.FinalUrlPath;
            }
        }

        // 6. Query string hints
        if ((resolvedName == null || IsGenericOrScriptExtension(GetCompoundExtension(resolvedName))) &&
            !string.IsNullOrWhiteSpace(effectiveUrl))
        {
            var queryName = ExtractQueryStringHint(effectiveUrl);
            if (!string.IsNullOrWhiteSpace(queryName))
            {
                resolvedName = queryName;
                source = FileNameSource.QueryString;
            }
        }

        // 7. Page title or Fallback
        if (resolvedName == null && !string.IsNullOrWhiteSpace(context.PageTitle))
        {
            resolvedName = context.PageTitle;
            source = FileNameSource.PageTitle;
        }

        if (string.IsNullOrWhiteSpace(resolvedName))
        {
            resolvedName = $"download_{DateTime.UtcNow:yyyyMMdd_HHmmss}.bin";
            source = FileNameSource.Fallback;
        }

        // Sanitize the candidate
        var sanitized = SanitizeFileName(resolvedName, $"download_{DateTime.UtcNow:yyyyMMdd_HHmmss}.bin");

        // Recover extension if needed (unless the user explicitly typed the name)
        bool extensionRecovered = false;
        if (!userExplicit)
        {
            var currentExt = GetCompoundExtension(sanitized);
            if (string.IsNullOrEmpty(currentExt) || IsGenericOrScriptExtension(currentExt))
            {
                var initialSpan = context.InitialBytes.HasValue ? context.InitialBytes.Value.Span : ReadOnlySpan<byte>.Empty;
                var recoveredExt = RecoverExtensionInternal(sanitized, context.ContentType, initialSpan);
                if (!string.IsNullOrEmpty(recoveredExt) && !recoveredExt.Equals(currentExt, StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(currentExt))
                    {
                        // Replace generic extension (e.g. .php -> .zip, .bin -> .pdf)
                        var basePart = sanitized[..^currentExt.Length];
                        sanitized = basePart + recoveredExt;
                    }
                    else
                    {
                        // Append recovered extension to extensionless file
                        sanitized += recoveredExt;
                    }
                    extensionRecovered = true;
                }
            }
        }

        var finalExt = GetCompoundExtension(sanitized);
        var baseName = GetSafeBaseName(sanitized);
        var isDangerous = IsDangerousExtension(finalExt);

        return new ResolvedFileName
        {
            FileName = sanitized,
            BaseName = baseName,
            Extension = finalExt,
            Source = source,
            ExtensionRecovered = extensionRecovered,
            IsDangerous = isDangerous
        };
    }

    public string SanitizeFileName(string rawName, string fallback = "download.bin")
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return fallback;
        }

        // Strip path traversal and directory separators
        var clean = rawName
            .Replace("../", "_/", StringComparison.Ordinal)
            .Replace("..\\", "_\\", StringComparison.Ordinal)
            .Replace("/..", "/_", StringComparison.Ordinal)
            .Replace("\\..", "\\_", StringComparison.Ordinal)
            .Replace('\\', '_')
            .Replace('/', '_')
            .Replace(':', '_');

        // Filter invalid and control chars
        var sb = new StringBuilder(clean.Length);
        foreach (var c in clean)
        {
            if (!InvalidFileNameChars.Contains(c) && c >= 32 && c != 127 && !IsSpoofingControlChar(c))
            {
                sb.Append(c);
            }
        }

        clean = sb.ToString().Trim();

        // Strip trailing dots and spaces
        clean = clean.Trim('.', ' ');

        if (string.IsNullOrWhiteSpace(clean))
        {
            clean = fallback;
        }

        // Neutralize Windows reserved device names (CON, PRN, etc.)
        var compoundExt = GetCompoundExtension(clean);
        var basePart = string.IsNullOrEmpty(compoundExt) ? clean : clean[..^compoundExt.Length];
        var trimmedBase = basePart.TrimEnd(' ', '.');

        if (ReservedDeviceNames.Contains(trimmedBase))
        {
            clean = "_" + clean;
            compoundExt = GetCompoundExtension(clean);
            basePart = string.IsNullOrEmpty(compoundExt) ? clean : clean[..^compoundExt.Length];
        }

        // Shorten to MAX_PATH / safe limit (245 chars) while preserving extension
        if (clean.Length > 245)
        {
            var allowedBaseLen = Math.Max(1, 245 - compoundExt.Length);
            if (basePart.Length > allowedBaseLen)
            {
                basePart = basePart[..allowedBaseLen].TrimEnd('.', ' ');
            }
            clean = basePart + compoundExt;
        }

        return clean;
    }

    public string ResolveInitialCandidate(string url, string? browserSuggestedName = null)
    {
        if (!string.IsNullOrWhiteSpace(browserSuggestedName))
        {
            return SanitizeFileName(browserSuggestedName);
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            return "download.bin";
        }

        var segment = ExtractFinalUrlSegment(url);
        if (!string.IsNullOrWhiteSpace(segment))
        {
            return SanitizeFileName(segment);
        }

        var hint = ExtractQueryStringHint(url);
        if (!string.IsNullOrWhiteSpace(hint))
        {
            return SanitizeFileName(hint);
        }

        return "download.bin";
    }

    public string RecoverExtension(string fileName, string? contentType, ReadOnlySpan<byte> initialBytes = default)
    {
        var compoundExt = GetCompoundExtension(fileName);
        if (!string.IsNullOrEmpty(compoundExt) && !IsGenericOrScriptExtension(compoundExt))
        {
            return compoundExt;
        }

        var recovered = RecoverExtensionInternal(fileName, contentType, initialBytes);
        return string.IsNullOrEmpty(recovered) ? compoundExt : recovered;
    }

    private static string? RecoverExtensionInternal(string fileName, string? contentType, ReadOnlySpan<byte> initialBytes)
    {
        // 1. Check Content-Type map
        var extFromMime = MimeTypeMap.TryGetExtension(contentType);
        if (!string.IsNullOrEmpty(extFromMime))
        {
            return extFromMime;
        }

        // 2. Check Magic bytes sniffing
        if (!initialBytes.IsEmpty)
        {
            var extFromMagic = MagicByteSniffer.SniffExtension(initialBytes);
            if (!string.IsNullOrEmpty(extFromMagic))
            {
                return extFromMagic;
            }
        }

        return null;
    }

    public string ResolveUniquePath(string directory, string desiredFileName, bool overwrite = false)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Environment.CurrentDirectory;
        }

        var fullPath = Path.Combine(directory, desiredFileName);
        if (overwrite || !File.Exists(fullPath))
        {
            return fullPath;
        }

        var ext = GetCompoundExtension(desiredFileName);
        var baseName = string.IsNullOrEmpty(ext) ? desiredFileName : desiredFileName[..^ext.Length];

        int counter = 1;
        while (true)
        {
            var candidateName = $"{baseName} ({counter}){ext}";
            var candidatePath = Path.Combine(directory, candidateName);
            if (!File.Exists(candidatePath))
            {
                return candidatePath;
            }
            counter++;
        }
    }

    public bool IsGenericOrScriptExtension(string ext)
    {
        if (string.IsNullOrWhiteSpace(ext)) return true;
        return GenericOrScriptExtensions.Contains(ext.ToLowerInvariant());
    }

    public static bool IsDangerous(string ext)
    {
        if (string.IsNullOrWhiteSpace(ext)) return false;
        return DangerousExtensions.Contains(ext.ToLowerInvariant());
    }

    public bool IsDangerousExtension(string ext) => IsDangerous(ext);

    public static string ResolveCategory(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return "General";
        var lower = fileName.ToLowerInvariant();
        string ext = Path.GetExtension(lower);
        foreach (var compound in CompoundExtensions)
        {
            if (lower.EndsWith(compound, StringComparison.OrdinalIgnoreCase))
            {
                ext = compound;
                break;
            }
        }

        return ext switch
        {
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2" or ".xz" or ".zst" or ".iso" or ".tgz"
                or ".tar.gz" or ".tar.bz2" or ".tar.xz" or ".tar.zst" => "Compressed",
            ".pdf" or ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".txt" or ".epub" or ".csv" or ".json" or ".xml" => "Documents",
            ".mp3" or ".flac" or ".wav" or ".aac" or ".m4a" or ".ogg" or ".wma" or ".opus" => "Music",
            ".exe" or ".msi" or ".bat" or ".cmd" or ".ps1" or ".apk" or ".appx" or ".msix" or ".dmg" => "Programs",
            ".mp4" or ".mkv" or ".avi" or ".mov" or ".webm" or ".wmv" or ".flv" or ".m4v" or ".ts" => "Video",
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" or ".svg" or ".ico" or ".tiff" => "Images",
            ".torrent" => "Torrents",
            _ => "General"
        };
    }

    public string GetCompoundExtension(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        var lower = fileName.ToLowerInvariant();
        foreach (var compound in CompoundExtensions)
        {
            if (lower.EndsWith(compound, StringComparison.OrdinalIgnoreCase))
            {
                return compound;
            }
        }

        return Path.GetExtension(fileName) ?? string.Empty;
    }

    public string GetSafeBaseName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        var ext = GetCompoundExtension(fileName);
        return string.IsNullOrEmpty(ext) ? fileName : fileName[..^ext.Length];
    }

    public static string? ParseContentDisposition(string headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return null;
        }

        // Support RFC 5987 / 6266 filename*=charset'lang'encoded-value
        // Example: filename*=UTF-8''%E0%AE%A4%E0%AE%BF.pdf
        var matchStar = Regex.Match(headerValue, @"filename\*\s*=\s*([^';]+)'([^']*)'([^;\r\n]+)", RegexOptions.IgnoreCase);
        if (matchStar.Success)
        {
            var charset = matchStar.Groups[1].Value.Trim();
            var encodedValue = matchStar.Groups[3].Value.Trim().Trim('"', '\'');
            try
            {
                var decoded = DecodeRfc5987(encodedValue, charset);
                if (!string.IsNullOrWhiteSpace(decoded))
                {
                    return decoded;
                }
            }
            catch
            {
                // Fallback to filename= if decoding fails
            }
        }

        // 1. Single-quoted filename='...'
        var matchSingle = Regex.Match(headerValue, @"filename\s*=\s*'([^'\r\n]+)'", RegexOptions.IgnoreCase);
        if (matchSingle.Success)
        {
            var value = matchSingle.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        // 2. Double-quoted filename="..." with support for quotes, escaped quotes, or malformed inner quotes
        var matchQuoted = Regex.Match(headerValue, @"filename\s*=\s*""([^;\r\n]+)""", RegexOptions.IgnoreCase);
        if (matchQuoted.Success)
        {
            var value = matchQuoted.Groups[1].Value;
            value = value.Replace("\"", "");
            try
            {
                value = Regex.Unescape(value);
            }
            catch { }

            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        else
        {
            var standardQuoted = Regex.Match(headerValue, @"filename\s*=\s*""((?:[^""\\]|\\.)*)""", RegexOptions.IgnoreCase);
            if (standardQuoted.Success)
            {
                var value = standardQuoted.Groups[1].Value;
                try
                {
                    value = Regex.Unescape(value);
                }
                catch { }

                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        // 3. Unquoted filename=value (terminated by semicolon or end of line)
        var matchUnquoted = Regex.Match(headerValue, @"filename\s*=\s*([^;\r\n""]+)", RegexOptions.IgnoreCase);
        if (matchUnquoted.Success)
        {
            var value = matchUnquoted.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string DecodeRfc5987(string encoded, string charset)
    {
        Encoding encoding;
        try
        {
            encoding = Encoding.GetEncoding(charset);
        }
        catch
        {
            encoding = Encoding.UTF8;
        }

        var bytes = new List<byte>();
        for (int i = 0; i < encoded.Length; i++)
        {
            if (encoded[i] == '%' && i + 2 < encoded.Length &&
                Uri.IsHexDigit(encoded[i + 1]) && Uri.IsHexDigit(encoded[i + 2]))
            {
                var hex = encoded.Substring(i + 1, 2);
                bytes.Add(Convert.ToByte(hex, 16));
                i += 2;
            }
            else
            {
                bytes.Add((byte)encoded[i]);
            }
        }

        return encoding.GetString([.. bytes]);
    }

    private static string? ExtractFinalUrlSegment(string url)
    {
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                var path = uri.AbsolutePath.TrimEnd('/');
                if (!string.IsNullOrEmpty(path))
                {
                    var lastSlash = path.LastIndexOf('/');
                    var segment = lastSlash >= 0 ? path[(lastSlash + 1)..] : path;
                    if (!string.IsNullOrWhiteSpace(segment))
                    {
                        return Uri.UnescapeDataString(segment);
                    }
                }
            }
        }
        catch
        {
            // Ignore malformed URIs
        }

        return null;
    }

    private static string? ExtractQueryStringHint(string url)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Query))
            {
                return null;
            }

            var query = uri.Query.TrimStart('?');
            var pairs = query.Split('&', StringSplitOptions.RemoveEmptyEntries);

            // First check for response-content-disposition parameter
            foreach (var pair in pairs)
            {
                var parts = pair.Split('=', 2);
                var key = Uri.UnescapeDataString(parts[0]).ToLowerInvariant();
                var val = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;

                if (key == "response-content-disposition" && !string.IsNullOrWhiteSpace(val))
                {
                    var parsed = ParseContentDisposition(val);
                    if (!string.IsNullOrWhiteSpace(parsed))
                    {
                        return parsed;
                    }
                }
            }

            // Check standard filename hints
            string[] candidateKeys = ["filename", "file", "name", "r-filename", "download_filename"];
            foreach (var candidateKey in candidateKeys)
            {
                foreach (var pair in pairs)
                {
                    var parts = pair.Split('=', 2);
                    var key = Uri.UnescapeDataString(parts[0]).ToLowerInvariant();
                    if (key == candidateKey && parts.Length > 1)
                    {
                        var val = Uri.UnescapeDataString(parts[1]).Trim();
                        if (!string.IsNullOrWhiteSpace(val) && val.Contains('.'))
                        {
                            return val;
                        }
                    }
                }
            }
        }
        catch
        {
            // Ignore
        }

        return null;
    }

    private static bool IsSpoofingControlChar(char c)
    {
        return c switch
        {
            '\u200B' => true,
            '\u200C' => true,
            '\u200D' => true,
            '\u200E' => true,
            '\u200F' => true,
            '\u202A' => true,
            '\u202B' => true,
            '\u202C' => true,
            '\u202D' => true,
            '\u202E' => true, // RLO
            '\u2066' => true,
            '\u2067' => true,
            '\u2068' => true,
            '\u2069' => true,
            '\uFEFF' => true,
            _ => char.GetUnicodeCategory(c) == UnicodeCategory.Format
        };
    }
}
