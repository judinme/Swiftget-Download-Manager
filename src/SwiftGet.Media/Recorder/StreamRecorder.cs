using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SwiftGet.Core.IO;
using SwiftGet.Media.Sidecars;

namespace SwiftGet.Media.Recorder;

public sealed partial class StreamRecorder
{
    private readonly SidecarManager _sidecarManager;
    private readonly ILogger<StreamRecorder> _logger;

    [GeneratedRegex(@"time=(\d{2}):(\d{2}):(\d{2}\.\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex FfmpegTimeRegex();

    [GeneratedRegex(@"speed=\s*([\d\.]+)x", RegexOptions.IgnoreCase)]
    private static partial Regex FfmpegSpeedRegex();

    [GeneratedRegex(@"\[download\]\s+([\d\.]+)%", RegexOptions.IgnoreCase)]
    private static partial Regex YtDlpPercentRegex();

    [GeneratedRegex(@"at\s+([\d\.]+)\s*([KMGTP]?i?B)/s", RegexOptions.IgnoreCase)]
    private static partial Regex YtDlpSpeedRegex();

    [GeneratedRegex(@"ETA\s+(\d{1,2}):(\d{2})(?::(\d{2}))?", RegexOptions.IgnoreCase)]
    private static partial Regex YtDlpEtaRegex();

    [GeneratedRegex(@"of\s+~?([\d\.]+)\s*([KMGTP]?i?B)", RegexOptions.IgnoreCase)]
    private static partial Regex YtDlpTotalSizeRegex();

    public event EventHandler<double>? ProgressChanged;
    public event EventHandler<StreamProgressEventArgs>? DetailedProgressChanged;

    public StreamRecorder(SidecarManager sidecarManager, ILogger<StreamRecorder>? logger = null)
    {
        _sidecarManager = sidecarManager ?? throw new ArgumentNullException(nameof(sidecarManager));
        _logger = logger ?? NullLogger<StreamRecorder>.Instance;
    }

    public async Task<string> RecordStreamAsync(
        string streamUrl,
        string destinationPath,
        string? formatId = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        if (!Uri.TryCreate(streamUrl, UriKind.Absolute, out _) ||
            streamUrl.Contains('"') || streamUrl.Contains('\0') || streamUrl.Contains('\r') || streamUrl.Contains('\n'))
        {
            throw new ArgumentException("Invalid stream URL: must be a valid absolute URI without control characters or quotes.", nameof(streamUrl));
        }

        var ytDlpPath = _sidecarManager.FindYtDlpPath();
        if (string.IsNullOrEmpty(ytDlpPath))
        {
            throw new FileNotFoundException("yt-dlp binary is required to record streams.");
        }

        var resolvedDest = PathResolver.GetPhysicalPath(destinationPath);
        var dir = Path.GetDirectoryName(resolvedDest);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var args = new List<string>();
        if (!string.IsNullOrWhiteSpace(formatId))
        {
            args.Add("-f");
            args.Add(formatId);
        }
        else
        {
            args.Add("-f");
            args.Add("bv*+ba/b");
        }

        var ffmpegPath = _sidecarManager.FindFfmpegPath();
        if (!string.IsNullOrEmpty(ffmpegPath))
        {
            args.Add("--ffmpeg-location");
            args.Add(ffmpegPath);
        }

        var requestedExt = Path.GetExtension(resolvedDest).TrimStart('.').ToLowerInvariant();
        if (requestedExt is "mp4" or "mkv" or "webm" or "mov" or "flv")
        {
            args.Add("--merge-output-format");
            args.Add(requestedExt);
        }

        args.Add("--no-part");
        args.Add("--no-warnings");
        args.Add("-o");
        args.Add(resolvedDest);
        args.Add(streamUrl);

        _logger.LogInformation("Recording media stream to {DestinationPath}", resolvedDest);

        var result = await SidecarManager.ExecuteSidecarAsync(
            ytDlpPath,
            args,
            onStdOut: line => ParseYtDlpOutput(line),
            onStdErr: line => ParseYtDlpOutput(line),
            ct: ct).ConfigureAwait(false);

        if (!result.Success)
        {
            throw new InvalidOperationException($"Stream recording failed: {SidecarManager.FormatSidecarError(result.StandardError, resolvedDest)}");
        }

        return ResolveProducedOutputPath(resolvedDest);
    }

    public static string ResolveProducedOutputPath(string expectedPath)
    {
        if (File.Exists(expectedPath) && new FileInfo(expectedPath).Length > 0)
        {
            return expectedPath;
        }

        string[] candidateExtensions = [".mp4", ".mkv", ".webm", ".m4a", ".mp3", ".opus", ".mov", ".flv"];

        // 1. Check if yt-dlp appended an extension onto the full output filename (e.g. "video.mp4.mkv" or "video.mp4.webm")
        foreach (var ext in candidateExtensions)
        {
            var appendedCandidate = expectedPath + ext;
            if (File.Exists(appendedCandidate) && new FileInfo(appendedCandidate).Length > 0)
            {
                var cleanPath = Path.ChangeExtension(expectedPath, ext);
                if (!string.Equals(appendedCandidate, cleanPath, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        File.Move(appendedCandidate, cleanPath, overwrite: true);
                        return cleanPath;
                    }
                    catch
                    {
                        return appendedCandidate;
                    }
                }

                return appendedCandidate;
            }
        }

        // 2. Check if yt-dlp replaced the extension (e.g. "video.mkv" instead of "video.mp4")
        foreach (var ext in candidateExtensions)
        {
            var replacedCandidate = Path.ChangeExtension(expectedPath, ext);
            if (!string.Equals(replacedCandidate, expectedPath, StringComparison.OrdinalIgnoreCase) &&
                File.Exists(replacedCandidate) &&
                new FileInfo(replacedCandidate).Length > 0)
            {
                return replacedCandidate;
            }
        }

        return expectedPath;
    }

    public async Task<string> MuxStreamsAsync(
        string videoFilePath,
        string audioFilePath,
        string destinationFilePath,
        bool deleteSourceFiles = false,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(audioFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFilePath);

        if (!File.Exists(videoFilePath))
        {
            throw new FileNotFoundException("Source video file not found for muxing.", videoFilePath);
        }

        if (!File.Exists(audioFilePath))
        {
            throw new FileNotFoundException("Source audio file not found for muxing.", audioFilePath);
        }

        var ffmpegPath = _sidecarManager.FindFfmpegPath();
        if (string.IsNullOrEmpty(ffmpegPath))
        {
            throw new FileNotFoundException("ffmpeg binary is required for media container muxing.");
        }

        var dir = Path.GetDirectoryName(destinationFilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var args = BuildFfmpegMuxArgumentList(videoFilePath, audioFilePath, destinationFilePath);
        _logger.LogInformation("Muxing video and audio streams to {DestinationFilePath}", destinationFilePath);

        var result = await SidecarManager.ExecuteSidecarAsync(
            ffmpegPath,
            args,
            onStdErr: line => ParseFfmpegProgress(line),
            ct: ct).ConfigureAwait(false);

        if (!result.Success || !File.Exists(destinationFilePath))
        {
            throw new InvalidOperationException($"Lossless container muxing failed: {result.StandardError}");
        }

        if (deleteSourceFiles)
        {
            try
            {
                File.Delete(videoFilePath);
                File.Delete(audioFilePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean up temporary muxing sources");
            }
        }

        return destinationFilePath;
    }

    public static IReadOnlyList<string> BuildFfmpegMuxArgumentList(string videoPath, string audioPath, string outputPath)
    {
        return ["-y", "-i", videoPath, "-i", audioPath, "-c", "copy", "-movflags", "+faststart", outputPath];
    }

    public static string BuildFfmpegMuxArguments(string videoPath, string audioPath, string outputPath)
    {
        return $"-y -i \"{videoPath}\" -i \"{audioPath}\" -c copy -movflags +faststart \"{outputPath}\"";
    }

    private void ParseYtDlpOutput(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        var percentMatch = YtDlpPercentRegex().Match(line);
        if (!percentMatch.Success || !double.TryParse(percentMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
        {
            return;
        }

        ProgressChanged?.Invoke(this, percent);

        double speed = 0;
        var speedMatch = YtDlpSpeedRegex().Match(line);
        if (speedMatch.Success)
        {
            speed = ParseSpeedBytesPerSec(speedMatch.Groups[1].Value, speedMatch.Groups[2].Value);
        }

        TimeSpan? eta = null;
        var etaMatch = YtDlpEtaRegex().Match(line);
        if (etaMatch.Success)
        {
            eta = ParseEta(etaMatch);
        }

        long totalBytes = 0;
        var totalMatch = YtDlpTotalSizeRegex().Match(line);
        if (totalMatch.Success)
        {
            totalBytes = ParseBytes(totalMatch.Groups[1].Value, totalMatch.Groups[2].Value);
        }

        long downloadedBytes = totalBytes > 0 ? (long)(totalBytes * (percent / 100.0)) : 0;

        DetailedProgressChanged?.Invoke(this, new StreamProgressEventArgs
        {
            ProgressPercentage = percent,
            SpeedBytesPerSec = speed,
            RemainingTime = eta,
            DownloadedBytes = downloadedBytes,
            TotalBytes = totalBytes
        });
    }

    public static double ParseSpeedBytesPerSec(string numberStr, string unitStr)
    {
        if (!double.TryParse(numberStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
            return 0;

        return unitStr.ToLowerInvariant() switch
        {
            "kib" or "kb" => num * 1024.0,
            "mib" or "mb" => num * 1024.0 * 1024.0,
            "gib" or "gb" => num * 1024.0 * 1024.0 * 1024.0,
            "tib" or "tb" => num * 1024.0 * 1024.0 * 1024.0 * 1024.0,
            "b" => num,
            _ => num * 1024.0 * 1024.0
        };
    }

    public static TimeSpan? ParseEta(Match match)
    {
        if (!match.Success) return null;
        if (match.Groups[2].Success && !string.IsNullOrEmpty(match.Groups[2].Value))
        {
            if (int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var first) &&
                int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var second))
            {
                if (match.Groups.Count > 3 && match.Groups[3].Success && int.TryParse(match.Groups[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var third))
                {
                    return new TimeSpan(first, second, third);
                }
                return new TimeSpan(0, first, second);
            }
        }
        return null;
    }

    public static long ParseBytes(string numberStr, string unitStr)
    {
        if (!double.TryParse(numberStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
            return 0;

        var bytes = unitStr.ToLowerInvariant() switch
        {
            "kib" or "kb" => num * 1024.0,
            "mib" or "mb" => num * 1024.0 * 1024.0,
            "gib" or "gb" => num * 1024.0 * 1024.0 * 1024.0,
            "tib" or "tb" => num * 1024.0 * 1024.0 * 1024.0 * 1024.0,
            "b" => num,
            _ => num * 1024.0 * 1024.0
        };
        return (long)Math.Round(bytes);
    }

    private void ParseFfmpegProgress(string line)
    {
        // FFmpeg writes progress updates to stderr
        var timeMatch = FfmpegTimeRegex().Match(line);
        if (timeMatch.Success)
        {
            _logger.LogDebug("FFmpeg encoding/muxing timestamp: {Time}", timeMatch.Value);
        }
    }
}

public sealed class StreamProgressEventArgs : EventArgs
{
    public double ProgressPercentage { get; init; }
    public double SpeedBytesPerSec { get; init; }
    public TimeSpan? RemainingTime { get; init; }
    public long DownloadedBytes { get; init; }
    public long TotalBytes { get; init; }
}
