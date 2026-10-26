using System;
using System.IO;
using System.Text.RegularExpressions;
using SwiftGet.Engine.Security;

namespace SwiftGet.Engine.Torrent;

public static class TorrentPathSanitizer
{
    private static readonly Regex WindowsReservedNamesRegex = new(
        @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\..*)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string SanitizeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return "unnamed_file.bin";
        }

        // Normalize slashes
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

        // Split into path segments
        var segments = normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var cleanSegments = new List<string>();

        foreach (var rawSegment in segments)
        {
            var segment = rawSegment.Trim();

            // Reject traversal tokens
            if (segment == ".")
            {
                continue;
            }
            if (segment == "..")
            {
                if (cleanSegments.Count > 0)
                {
                    cleanSegments.RemoveAt(cleanSegments.Count - 1);
                }
                continue;
            }

            // Strip Alternate Data Streams
            int colonIdx = segment.IndexOf(':');
            if (colonIdx >= 0)
            {
                segment = segment[..colonIdx];
            }

            // Remove invalid filename characters
            foreach (var invalidChar in Path.GetInvalidFileNameChars())
            {
                segment = segment.Replace(invalidChar.ToString(), string.Empty);
            }

            // Check Windows reserved device names
            if (WindowsReservedNamesRegex.IsMatch(segment))
            {
                segment = "_" + segment;
            }

            if (string.IsNullOrWhiteSpace(segment))
            {
                segment = "_";
            }

            cleanSegments.Add(segment);
        }

        if (cleanSegments.Count == 0)
        {
            return "unnamed_file.bin";
        }

        return Path.Combine([.. cleanSegments]);
    }

    public static string GetSafeFullPath(string baseDirectory, string relativePath)
    {
        var cleanRelative = SanitizeRelativePath(relativePath);
        var baseFullPath = Path.GetFullPath(baseDirectory);
        if (!baseFullPath.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) &&
            !baseFullPath.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal))
        {
            baseFullPath += Path.DirectorySeparatorChar;
        }

        var targetFullPath = Path.GetFullPath(Path.Combine(baseFullPath, cleanRelative));

        if (!targetFullPath.StartsWith(baseFullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Path traversal attempt blocked: '{relativePath}' resolves outside '{baseDirectory}'");
        }

        return targetFullPath;
    }
}
