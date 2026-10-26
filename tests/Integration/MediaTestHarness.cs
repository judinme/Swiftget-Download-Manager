using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SwiftGet.Integration;

public sealed record MediaStreamMetrics(
    int Width,
    int Height,
    double Fps,
    string VideoCodec,
    string AudioCodec,
    double DurationSeconds,
    string PixelFormat);

public static class MediaTestHarness
{
    public static string FindFfmpegPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var sidecarPath = Path.Combine(localAppData, "SwiftGet", "sidecars", "ffmpeg.exe");
        if (File.Exists(sidecarPath)) return sidecarPath;

        return "ffmpeg";
    }

    public static string FindFfprobePath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var sidecarPath = Path.Combine(localAppData, "SwiftGet", "sidecars", "ffprobe.exe");
        if (File.Exists(sidecarPath)) return sidecarPath;

        var wingetLink = Path.Combine(localAppData, "Microsoft", "WinGet", "Links", "ffprobe.exe");
        if (File.Exists(wingetLink)) return wingetLink;

        return "ffprobe";
    }

    public static async Task GenerateTestVideoAsync(
        string outputPath,
        int width,
        int height,
        string vcodec = "libx264",
        string acodec = "aac",
        int durationSec = 2,
        CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var ffmpeg = FindFfmpegPath();
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("lavfi");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add($"testsrc=size={width}x{height}:rate=30");

        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("lavfi");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add("sine=frequency=1000:sample_rate=48000");

        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add(durationSec.ToString(System.Globalization.CultureInfo.InvariantCulture));

        psi.ArgumentList.Add("-c:v");
        psi.ArgumentList.Add(vcodec);
        psi.ArgumentList.Add("-pix_fmt");
        psi.ArgumentList.Add("yuv420p");

        psi.ArgumentList.Add("-c:a");
        psi.ArgumentList.Add(acodec);

        psi.ArgumentList.Add(outputPath);

        using var process = new Process { StartInfo = psi };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var err = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"FFmpeg failed to generate test video ({width}x{height}, {vcodec}): {err}");
        }
    }

    public static async Task<MediaStreamMetrics> FfprobeInspectAsync(string filePath, CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("File not found for ffprobe inspection.", filePath);
        }

        var ffprobe = FindFfprobePath();
        var psi = new ProcessStartInfo
        {
            FileName = ffprobe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("quiet");
        psi.ArgumentList.Add("-print_format");
        psi.ArgumentList.Add("json");
        psi.ArgumentList.Add("-show_format");
        psi.ArgumentList.Add("-show_streams");
        psi.ArgumentList.Add(filePath);

        using var process = new Process { StartInfo = psi };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var err = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            throw new InvalidOperationException($"ffprobe failed: {err}");
        }

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;

        int width = 0, height = 0;
        double fps = 0, duration = 0;
        string vcodec = "none", acodec = "none", pixFmt = "none";

        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var stream in streams.EnumerateArray())
            {
                var type = stream.TryGetProperty("codec_type", out var tProp) ? tProp.GetString() : null;
                var codecName = stream.TryGetProperty("codec_name", out var cProp) ? cProp.GetString() : "none";

                if (string.Equals(type, "video", StringComparison.OrdinalIgnoreCase))
                {
                    vcodec = codecName ?? "none";
                    if (stream.TryGetProperty("width", out var wProp)) width = wProp.GetInt32();
                    if (stream.TryGetProperty("height", out var hProp)) height = hProp.GetInt32();
                    if (stream.TryGetProperty("pix_fmt", out var pProp)) pixFmt = pProp.GetString() ?? "none";
                    if (stream.TryGetProperty("r_frame_rate", out var rProp))
                    {
                        var rStr = rProp.GetString();
                        if (!string.IsNullOrEmpty(rStr) && rStr.Contains('/'))
                        {
                            var parts = rStr.Split('/');
                            if (double.TryParse(parts[0], out var num) && double.TryParse(parts[1], out var den) && den > 0)
                            {
                                fps = num / den;
                            }
                        }
                    }
                }
                else if (string.Equals(type, "audio", StringComparison.OrdinalIgnoreCase))
                {
                    acodec = codecName ?? "none";
                }
            }
        }

        if (root.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var dProp))
        {
            var dStr = dProp.GetString();
            if (!string.IsNullOrEmpty(dStr) && double.TryParse(dStr, System.Globalization.CultureInfo.InvariantCulture, out var dVal))
            {
                duration = dVal;
            }
        }

        return new MediaStreamMetrics(width, height, fps, vcodec, acodec, duration, pixFmt);
    }

    public static async Task<(bool IsValid, string? ErrorMessage)> FfmpegDecodeCheckAsync(string filePath, CancellationToken ct = default)
    {
        var ffmpeg = FindFfmpegPath();
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(filePath);
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("null");
        psi.ArgumentList.Add("-");

        using var process = new Process { StartInfo = psi };
        process.Start();
        var stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        if (process.ExitCode != 0 || !string.IsNullOrWhiteSpace(stderr))
        {
            return (false, stderr.Trim());
        }

        return (true, null);
    }
}
