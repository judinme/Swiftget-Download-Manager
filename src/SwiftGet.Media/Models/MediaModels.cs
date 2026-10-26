using System;
using System.Collections.Generic;

namespace SwiftGet.Media.Models;

public sealed class DrmProtectionDetectedException : InvalidOperationException
{
    public const string DefaultNotice = "This media stream is protected by Digital Rights Management (DRM). In accordance with digital copyright laws, SwiftGet does not circumvent DRM protections.";

    public string DrmScheme { get; }

    public DrmProtectionDetectedException(string drmScheme = "Unknown", string? message = null)
        : base(message ?? DefaultNotice)
    {
        DrmScheme = drmScheme;
    }
}

public sealed class MediaFormat
{
    public string FormatId { get; set; } = string.Empty;
    public string Extension { get; set; } = "mp4";
    public string Resolution { get; set; } = string.Empty;
    public string VCodec { get; set; } = string.Empty;
    public string ACodec { get; set; } = string.Empty;
    public long? FileSizeBytes { get; set; }
    public double? BitrateKbps { get; set; }
    public int? Fps { get; set; }
    public bool HasVideo { get; set; } = true;
    public bool HasAudio { get; set; } = true;

    public string QualityLabel
    {
        get
        {
            var sizeStr = FileSizeBytes switch
            {
                > 0 and < 1024 * 1024 => $"{FileSizeBytes / 1024.0:0.#} KB",
                > 0 and < 1024 * 1024 * 1024 => $"{FileSizeBytes / (1024.0 * 1024.0):0.#} MB",
                > 0 => $"{FileSizeBytes / (1024.0 * 1024.0 * 1024.0):0.##} GB",
                _ => null
            };

            var label = !string.IsNullOrWhiteSpace(Resolution)
                ? Resolution
                : (HasVideo ? "Video" : "Audio Only");

            var ext = Extension.ToUpperInvariant();
            return sizeStr != null ? $"{label} ({ext}) - ~{sizeStr}" : $"{label} ({ext})";
        }
    }
}

public sealed class MediaSubtitleTrack
{
    public string Language { get; set; } = "en";
    public string DisplayName { get; set; } = "English";
    public string Url { get; set; } = string.Empty;
    public string Extension { get; set; } = "vtt";
}

public sealed class MediaMetadata
{
    public string Url { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public TimeSpan Duration { get; set; } = TimeSpan.Zero;
    public string? ThumbnailUrl { get; set; }
    public string? Uploader { get; set; }
    public string? Description { get; set; }
    public List<MediaFormat> Formats { get; set; } = [];
    public List<MediaSubtitleTrack> Subtitles { get; set; } = [];
    public bool IsDrmProtected { get; set; }
    public string? DrmScheme { get; set; }
}
