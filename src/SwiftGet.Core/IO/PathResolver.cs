using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SwiftGet.Core.IO;

public static class PathResolver
{
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle hFile,
        [Out] char[] lpszFilePath,
        uint cchFilePath,
        uint dwFlags);

    private const uint FILE_READ_ATTRIBUTES = 0x80;
    private const uint FILE_SHARE_READ = 1;
    private const uint FILE_SHARE_WRITE = 2;
    private const uint FILE_SHARE_DELETE = 4;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint VOLUME_NAME_DOS = 0;

    /// <summary>
    /// Resolves any symbolic links, directory junctions, or reparse points to the underlying real physical path.
    /// If the target file or subdirectory does not exist yet, resolves the nearest existing parent directory and appends the remainder.
    /// </summary>
    public static string GetPhysicalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        var fullPath = Path.GetFullPath(path);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return fullPath;
        }

        // Try resolving full path directly if it exists
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
        {
            var resolved = ResolveHandlePath(fullPath);
            if (!string.IsNullOrEmpty(resolved))
            {
                return resolved;
            }
        }

        // Target path does not exist yet; find nearest existing ancestor
        var current = fullPath;
        var suffixSegments = new System.Collections.Generic.List<string>();

        while (!string.IsNullOrEmpty(current) && !File.Exists(current) && !Directory.Exists(current))
        {
            var fileName = Path.GetFileName(current);
            if (!string.IsNullOrEmpty(fileName))
            {
                suffixSegments.Insert(0, fileName);
            }
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            current = parent;
        }

        if (!string.IsNullOrEmpty(current) && (File.Exists(current) || Directory.Exists(current)))
        {
            var resolvedParent = ResolveHandlePath(current) ?? current;
            var combined = resolvedParent;
            foreach (var seg in suffixSegments)
            {
                combined = Path.Combine(combined, seg);
            }
            return combined;
        }

        return fullPath;
    }

    private static string? ResolveHandlePath(string existingPath)
    {
        try
        {
            using var handle = CreateFile(
                existingPath,
                FILE_READ_ATTRIBUTES,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_FLAG_BACKUP_SEMANTICS,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                return null;
            }

            var buffer = new char[1024];
            var len = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, VOLUME_NAME_DOS);
            if (len == 0)
            {
                return null;
            }

            var result = new string(buffer, 0, (int)len);
            if (result.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            {
                return @"\\" + result[8..];
            }
            if (result.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
            {
                return result[4..];
            }

            return result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Checks whether the specified path itself is a reparse point (junction, symlink, or mount point).
    /// </summary>
    public static bool IsReparsePoint(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var trimmed = Path.TrimEndingDirectorySeparator(path.Trim());
            var attr = File.GetAttributes(trimmed);
            return (attr & FileAttributes.ReparsePoint) != 0;
        }
        catch (IOException ioEx) when ((ioEx.HResult & 0xFFFF) == 448) // ERROR_UNTRUSTED_MOUNT_POINT
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks whether the path or any of its ancestor directories is a reparse point.
    /// </summary>
    public static bool ContainsReparsePoint(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string current;
        try
        {
            current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
        }
        catch
        {
            return false;
        }

        while (!string.IsNullOrEmpty(current))
        {
            if (IsReparsePoint(current))
            {
                return true;
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            current = parent;
        }

        return false;
    }
}
