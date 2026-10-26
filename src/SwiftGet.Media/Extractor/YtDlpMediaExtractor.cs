using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SwiftGet.Media.Models;
using SwiftGet.Media.Sidecars;

namespace SwiftGet.Media.Extractor;

public interface IMediaExtractor
{
    Task<MediaMetadata> ExtractMetadataAsync(string mediaUrl, CancellationToken ct = default);
}

public sealed class YtDlpMediaExtractor : IMediaExtractor
{
    private readonly SidecarManager _sidecarManager;

    public YtDlpMediaExtractor(SidecarManager sidecarManager)
    {
        _sidecarManager = sidecarManager ?? throw new ArgumentNullException(nameof(sidecarManager));
    }

    public async Task<MediaMetadata> ExtractMetadataAsync(string mediaUrl, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaUrl);

        if (!Uri.TryCreate(mediaUrl, UriKind.Absolute, out _) ||
            mediaUrl.Contains('"') || mediaUrl.Contains('\0') || mediaUrl.Contains('\r') || mediaUrl.Contains('\n'))
        {
            throw new ArgumentException("Invalid media URL: must be a valid absolute URI without control characters or quotes.", nameof(mediaUrl));
        }

        var ytDlpPath = _sidecarManager.FindYtDlpPath();
        if (string.IsNullOrEmpty(ytDlpPath))
        {
            throw new FileNotFoundException("yt-dlp binary is required for media extraction but was not found.");
        }

        var args = new[] { "--dump-single-json", "--no-warnings", "--no-playlist", mediaUrl };
        var result = await SidecarManager.ExecuteSidecarAsync(ytDlpPath, args, ct: ct).ConfigureAwait(false);

        if (!result.Success)
        {
            var err = result.StandardError;
            if (err.Contains("DRM", StringComparison.OrdinalIgnoreCase) ||
                err.Contains("protected", StringComparison.OrdinalIgnoreCase) ||
                err.Contains("Widevine", StringComparison.OrdinalIgnoreCase))
            {
                throw new DrmProtectionDetectedException("DRM", $"DRM protected stream detected: {err}");
            }

            throw new InvalidOperationException($"yt-dlp extraction failed: {SidecarManager.FormatSidecarError(err, ytDlpPath)}");
        }

        return ParseMetadataFromJson(result.StandardOutput, mediaUrl);
    }

    public static MediaMetadata ParseMetadataFromJson(string jsonText, string fallbackUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonText);

        using var doc = JsonDocument.Parse(jsonText);
        var root = doc.RootElement;

        // 1. DRM detection checks
        if (root.TryGetProperty("_has_drm", out var drmProp) && IsJsonTrue(drmProp))
        {
            throw new DrmProtectionDetectedException("Widevine/PlayReady");
        }

        var metadata = new MediaMetadata
        {
            Url = fallbackUrl,
            Title = GetJsonString(root, "title") ?? "Unknown Title",
            ThumbnailUrl = GetJsonString(root, "thumbnail"),
            Uploader = GetJsonString(root, "uploader"),
            Description = GetJsonString(root, "description")
        };

        var durSec = GetJsonDouble(root, "duration");
        if (durSec.HasValue && durSec.Value > 0)
        {
            metadata.Duration = TimeSpan.FromSeconds(durSec.Value);
        }

        // 2. Parse Formats
        if (root.TryGetProperty("formats", out var formatsProp) && formatsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in formatsProp.EnumerateArray())
            {
                // Check format-level DRM
                if (f.TryGetProperty("has_drm", out var fDrm) && IsJsonTrue(fDrm))
                {
                    continue; // Skip DRM stream
                }

                var formatId = GetJsonString(f, "format_id") ?? "";
                var ext = GetJsonString(f, "ext") ?? "mp4";
                var resolution = GetJsonString(f, "resolution");
                var vcodec = GetJsonString(f, "vcodec") ?? "none";
                var acodec = GetJsonString(f, "acodec") ?? "none";

                // Storyboard formats filter
                if (vcodec.Contains("mhtml", StringComparison.OrdinalIgnoreCase) || formatId.StartsWith("sb", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                long? fileSize = GetJsonInt64(f, "filesize") ?? GetJsonInt64(f, "filesize_approx");
                double? bitrate = GetJsonDouble(f, "tbr");
                int? fps = GetJsonInt32(f, "fps");
                int? height = GetJsonInt32(f, "height");

                var hasVideo = !string.Equals(vcodec, "none", StringComparison.OrdinalIgnoreCase);
                var hasAudio = !string.Equals(acodec, "none", StringComparison.OrdinalIgnoreCase);

                if (string.IsNullOrWhiteSpace(resolution))
                {
                    if (height.HasValue && height.Value > 0)
                    {
                        resolution = $"{height.Value}p";
                    }
                    else if (!hasVideo && hasAudio)
                    {
                        resolution = "Audio Only";
                    }
                }

                var item = new MediaFormat
                {
                    FormatId = formatId,
                    Extension = ext,
                    Resolution = resolution ?? (hasVideo ? "Video" : "Audio Only"),
                    VCodec = vcodec,
                    ACodec = acodec,
                    FileSizeBytes = fileSize,
                    BitrateKbps = bitrate,
                    Fps = fps,
                    HasVideo = hasVideo,
                    HasAudio = hasAudio
                };

                metadata.Formats.Add(item);
            }
        }

        // Fallback for single format / direct stream outputs where formats array is absent or empty
        if (metadata.Formats.Count == 0)
        {
            var ext = GetJsonString(root, "ext") ?? "mp4";
            var vcodec = GetJsonString(root, "vcodec") ?? "none";
            var acodec = GetJsonString(root, "acodec") ?? "none";
            var hasVideo = !string.Equals(vcodec, "none", StringComparison.OrdinalIgnoreCase);
            var hasAudio = !string.Equals(acodec, "none", StringComparison.OrdinalIgnoreCase);

            long? fileSize = GetJsonInt64(root, "filesize") ?? GetJsonInt64(root, "filesize_approx");

            metadata.Formats.Add(new MediaFormat
            {
                FormatId = GetJsonString(root, "format_id") ?? "best",
                Extension = ext,
                Resolution = GetJsonString(root, "resolution") ?? (hasVideo ? "Default Quality" : "Audio Only"),
                VCodec = vcodec,
                ACodec = acodec,
                FileSizeBytes = fileSize,
                HasVideo = hasVideo || !hasAudio,
                HasAudio = hasAudio
            });
        }

        // 3. Parse Subtitles
        if (root.TryGetProperty("subtitles", out var subsProp) && subsProp.ValueKind == JsonValueKind.Object)
        {
            foreach (var langProp in subsProp.EnumerateObject())
            {
                var lang = langProp.Name;
                if (langProp.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var track in langProp.Value.EnumerateArray())
                    {
                        var trackUrl = GetJsonString(track, "url");
                        var trackExt = GetJsonString(track, "ext") ?? "vtt";
                        if (!string.IsNullOrEmpty(trackUrl))
                        {
                            metadata.Subtitles.Add(new MediaSubtitleTrack
                            {
                                Language = lang,
                                DisplayName = GetLanguageDisplayName(lang),
                                Url = trackUrl,
                                Extension = trackExt
                            });
                            break;
                        }
                    }
                }
            }
        }

        return metadata;
    }

    private static bool IsJsonTrue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => bool.TryParse(element.GetString(), out var b) && b,
            _ => false
        };
    }

    private static string? GetJsonString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop))
        {
            return null;
        }

        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Number => prop.GetRawText(),
            _ => null
        };
    }

    private static long? GetJsonInt64(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var val))
            {
                return val;
            }
            if (prop.ValueKind == JsonValueKind.String && long.TryParse(prop.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var valStr))
            {
                return valStr;
            }
        }
        return null;
    }

    private static int? GetJsonInt32(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var val))
            {
                return val;
            }
            if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var valStr))
            {
                return valStr;
            }
        }
        return null;
    }

    private static double? GetJsonDouble(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out var val))
            {
                return val;
            }
            if (prop.ValueKind == JsonValueKind.String && double.TryParse(prop.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var valStr))
            {
                return valStr;
            }
        }
        return null;
    }

    private static string GetLanguageDisplayName(string code)
    {
        return code.ToLowerInvariant() switch
        {
            "en" => "English",
            "es" => "Spanish",
            "fr" => "French",
            "de" => "German",
            "it" => "Italian",
            "pt" => "Portuguese",
            "ru" => "Russian",
            "ja" => "Japanese",
            "ko" => "Korean",
            "zh" or "zh-hans" => "Chinese (Simplified)",
            "zh-hant" => "Chinese (Traditional)",
            "ar" => "Arabic",
            _ => code.ToUpperInvariant()
        };
    }
}
