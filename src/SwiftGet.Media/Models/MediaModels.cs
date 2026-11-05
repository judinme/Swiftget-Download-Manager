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
    public int? Height { get; set; }
    public int? Width { get; set; }
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

            string label;
            if (HasVideo)
            {
                var h = Height ?? ParseHeightFromResolution(Resolution);
                label = h switch
                {
                    >= 4320 => "8K Ultra HD 4320p",
                    >= 2160 => "4K Ultra HD 2160p",
                    >= 1440 => "2K Quad HD 1440p",
                    >= 1080 => "Full HD 1080p",
                    >= 720 => "HD 720p",
                    >= 480 => "SD 480p",
                    >= 360 => "Medium 360p",
                    >= 240 => "Low 240p",
                    > 0 => $"{h}p",
                    _ => !string.IsNullOrWhiteSpace(Resolution) ? Resolution : "Video"
                };
            }
            else
            {
                label = !string.IsNullOrWhiteSpace(ACodec) && !ACodec.Equals("none", StringComparison.OrdinalIgnoreCase)
                    ? $"Audio Only ({ACodec})"
                    : "Audio Only";
            }

            var ext = Extension.ToUpperInvariant();
            var metaParts = new List<string>();
            if (Fps is > 30)
            {
                metaParts.Add($"{Fps}fps");
            }
            if (sizeStr != null)
            {
                metaParts.Add($"~{sizeStr}");
            }

            return metaParts.Count > 0
                ? $"{label} ({ext}) - {string.Join(", ", metaParts)}"
                : $"{label} ({ext})";
        }
    }

    public static int ParseHeightFromResolution(string? res)
    {
        if (string.IsNullOrWhiteSpace(res)) return 0;
        var match = System.Text.RegularExpressions.Regex.Match(res, @"(?:x|p|^)(\d{3,4})");
        if (match.Success && int.TryParse(match.Groups[1].Value, out var val))
        {
            return val;
        }
        return 0;
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
