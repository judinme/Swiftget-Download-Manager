using System;
using System.Collections.Generic;

namespace SwiftGet.Core.IO;

public static class MimeTypeMap
{
    private static readonly Dictionary<string, string> MimeToExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        // Archives & Compressed
        ["application/zip"] = ".zip",
        ["application/x-zip-compressed"] = ".zip",
        ["application/x-zip"] = ".zip",
        ["application/x-rar-compressed"] = ".rar",
        ["application/vnd.rar"] = ".rar",
        ["application/x-rar"] = ".rar",
        ["application/x-7z-compressed"] = ".7z",
        ["application/x-tar"] = ".tar",
        ["application/tar"] = ".tar",
        ["application/gzip"] = ".gz",
        ["application/x-gzip"] = ".gz",
        ["application/x-bzip2"] = ".bz2",
        ["application/x-bz2"] = ".bz2",
        ["application/x-xz"] = ".xz",
        ["application/zstd"] = ".zst",
        ["application/x-zstd"] = ".zst",
        ["application/x-iso9660-image"] = ".iso",
        ["application/x-cd-image"] = ".iso",

        // Executables & Installers
        ["application/vnd.microsoft.portable-executable"] = ".exe",
        ["application/x-msi"] = ".msi",
        ["application/x-ms-installer"] = ".msi",
        ["application/msix"] = ".msix",
        ["application/vnd.android.package-archive"] = ".apk",
        ["application/x-apple-diskimage"] = ".dmg",
        ["application/x-debian-package"] = ".deb",
        ["application/x-redhat-package-manager"] = ".rpm",

        // Documents
        ["application/pdf"] = ".pdf",
        ["application/msword"] = ".doc",
        ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = ".docx",
        ["application/vnd.ms-excel"] = ".xls",
        ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] = ".xlsx",
        ["application/vnd.ms-powerpoint"] = ".ppt",
        ["application/vnd.openxmlformats-officedocument.presentationml.presentation"] = ".pptx",
        ["text/plain"] = ".txt",
        ["text/csv"] = ".csv",
        ["text/tab-separated-values"] = ".tsv",
        ["text/markdown"] = ".md",
        ["application/rtf"] = ".rtf",
        ["text/rtf"] = ".rtf",
        ["application/json"] = ".json",
        ["application/xml"] = ".xml",
        ["text/xml"] = ".xml",

        // Video
        ["video/mp4"] = ".mp4",
        ["video/x-matroska"] = ".mkv",
        ["video/mkv"] = ".mkv",
        ["video/webm"] = ".webm",
        ["video/x-msvideo"] = ".avi",
        ["video/quicktime"] = ".mov",
        ["video/x-flv"] = ".flv",
        ["video/x-ms-wmv"] = ".wmv",
        ["video/mp2t"] = ".ts",
        ["video/3gpp"] = ".3gp",

        // Audio
        ["audio/mpeg"] = ".mp3",
        ["audio/mp3"] = ".mp3",
        ["audio/flac"] = ".flac",
        ["audio/x-flac"] = ".flac",
        ["audio/wav"] = ".wav",
        ["audio/x-wav"] = ".wav",
        ["audio/aac"] = ".aac",
        ["audio/mp4"] = ".m4a",
        ["audio/x-m4a"] = ".m4a",
        ["audio/ogg"] = ".ogg",
        ["application/ogg"] = ".ogg",
        ["audio/opus"] = ".opus",
        ["audio/x-ms-wma"] = ".wma",

        // Images
        ["image/jpeg"] = ".jpg",
        ["image/jpg"] = ".jpg",
        ["image/png"] = ".png",
        ["image/gif"] = ".gif",
        ["image/webp"] = ".webp",
        ["image/svg+xml"] = ".svg",
        ["image/bmp"] = ".bmp",
        ["image/x-icon"] = ".ico",
        ["image/vnd.microsoft.icon"] = ".ico",
        ["image/tiff"] = ".tiff",
        ["image/avif"] = ".avif",

        // Web & Code
        ["text/html"] = ".html",
        ["text/css"] = ".css",
        ["text/javascript"] = ".js",
        ["application/javascript"] = ".js",
        ["application/x-bittorrent"] = ".torrent",
        ["application/x-torrent"] = ".torrent"
    };

    private static readonly HashSet<string> UnknownMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/octet-stream",
        "binary/octet-stream",
        "application/octetstream",
        "application/x-msdownload", // Treated as unknown per Phase 2 requirement
        "application/unknown",
        "unknown/unknown"
    };

    public static string? TryGetExtension(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        var clean = contentType.Split(';')[0].Trim().ToLowerInvariant();

        if (UnknownMimeTypes.Contains(clean))
        {
            return null;
        }

        if (MimeToExtension.TryGetValue(clean, out var ext))
        {
            return ext;
        }

        return null;
    }

    public static bool IsUnknownMimeType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return true;
        }

        var clean = contentType.Split(';')[0].Trim().ToLowerInvariant();
        return UnknownMimeTypes.Contains(clean);
    }
}
