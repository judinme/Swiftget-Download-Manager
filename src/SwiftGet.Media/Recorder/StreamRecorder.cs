using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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

    public event EventHandler<double>? ProgressChanged;

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

        var dir = Path.GetDirectoryName(destinationPath);
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
        args.Add("--no-part");
        args.Add("--no-warnings");
        args.Add("-o");
        args.Add(destinationPath);
        args.Add(streamUrl);

        _logger.LogInformation("Recording media stream to {DestinationPath}", destinationPath);

        var result = await SidecarManager.ExecuteSidecarAsync(
            ytDlpPath,
            args,
            onStdOut: line => ParseYtDlpOutput(line),
            onStdErr: line => ParseYtDlpOutput(line),
            ct: ct).ConfigureAwait(false);

        if (!result.Success)
        {
            throw new InvalidOperationException($"Stream recording failed: {SidecarManager.FormatSidecarError(result.StandardError, destinationPath)}");
        }

        return destinationPath;
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
        var match = YtDlpPercentRegex().Match(line);
        if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
        {
            ProgressChanged?.Invoke(this, percent);
        }
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
