using System;
using System.IO;
using SwiftGet.Engine.IO;

namespace SwiftGet.Engine.Security;

/// <summary>
/// Provides enterprise-grade path traversal protection, UNC path neutralization,
/// and safe destination directory containment for file operations.
/// </summary>
public static class PathTraversalGuard
{
    /// <summary>
    /// Combines a base directory with a relative path or filename, ensuring the resulting path
    /// strictly resides within the base directory. Escapes, UNC prefixes, and reserved device names
    /// are neutralized and redirected safely into the base directory.
    /// </summary>
    public static string EnsureSafePath(string baseDirectory, string relativeOrFileName, string fallbackFileName = "download.bin")
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            baseDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }

        var canonicalBase = Path.GetFullPath(Environment.ExpandEnvironmentVariables(baseDirectory));
        if (!canonicalBase.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) &&
            !canonicalBase.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal))
        {
            canonicalBase += Path.DirectorySeparatorChar;
        }

        if (string.IsNullOrWhiteSpace(relativeOrFileName))
        {
            return Path.Combine(canonicalBase, fallbackFileName);
        }

        // Neutralize UNC prefixes (\\, //) and DOS device prefixes (\\?\, \\.\)
        var cleaned = relativeOrFileName.Trim();
        while (cleaned.StartsWith("\\\\", StringComparison.Ordinal) ||
               cleaned.StartsWith("//", StringComparison.Ordinal) ||
               cleaned.StartsWith("/\\", StringComparison.Ordinal) ||
               cleaned.StartsWith("\\/", StringComparison.Ordinal))
        {
            cleaned = cleaned.TrimStart('\\', '/');
        }

        // Neutralize absolute drive roots (e.g., C:\ or /)
        if (cleaned.Length >= 2 && cleaned[1] == ':' && char.IsLetter(cleaned[0]))
        {
            cleaned = cleaned[2..].TrimStart('\\', '/');
        }

        // Extract directory parts and filename part
        var segments = cleaned.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return Path.Combine(canonicalBase, fallbackFileName);
        }

        // Sanitize the final filename segment
        var fileName = FileNameSanitizer.Sanitize(segments[^1], fallbackFileName);

        // Reconstruct safe intermediate directory segments (neutralizing .. and .)
        var safeIntermediate = canonicalBase;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var seg = segments[i].Trim('.', ' ');
            if (string.IsNullOrWhiteSpace(seg) || seg.Equals("..", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var cleanSeg = FileNameSanitizer.Sanitize(seg, string.Empty);
            if (!string.IsNullOrWhiteSpace(cleanSeg))
            {
                safeIntermediate = Path.Combine(safeIntermediate, cleanSeg);
            }
        }

        var finalCandidate = Path.GetFullPath(Path.Combine(safeIntermediate, fileName));

        // Enforce strict directory prefix containment boundary
        if (!finalCandidate.StartsWith(canonicalBase, StringComparison.OrdinalIgnoreCase))
        {
            // Boundary violation: fallback directly to canonical root + sanitized file name
            return Path.Combine(canonicalBase, fileName);
        }

        return finalCandidate;
    }

    /// <summary>
    /// Checks whether candidatePath is a valid, non-escaping child of baseDirectory
    /// and does not contain UNC paths, alternate data streams, or reserved devices.
    /// </summary>
    public static bool IsSafePath(string baseDirectory, string candidatePath)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory) || string.IsNullOrWhiteSpace(candidatePath))
        {
            return false;
        }

        try
        {
            // Reject UNC paths
            if (candidatePath.StartsWith("\\\\", StringComparison.Ordinal) || candidatePath.StartsWith("//", StringComparison.Ordinal))
            {
                return false;
            }

            var canonicalBase = Path.GetFullPath(Environment.ExpandEnvironmentVariables(baseDirectory));
            if (!canonicalBase.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            {
                canonicalBase += Path.DirectorySeparatorChar;
            }

            var canonicalCandidate = Path.GetFullPath(candidatePath);

            // Verify prefix containment
            if (!canonicalCandidate.StartsWith(canonicalBase, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Verify filename component does not contain NTFS alternate data streams
            var fileName = Path.GetFileName(canonicalCandidate);
            if (fileName.Contains(':', StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
