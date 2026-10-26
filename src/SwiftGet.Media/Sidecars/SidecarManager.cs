using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SwiftGet.Core.IO;

namespace SwiftGet.Media.Sidecars;

public sealed class SidecarProcessResult
{
    public int ExitCode { get; set; }
    public string StandardOutput { get; set; } = string.Empty;
    public string StandardError { get; set; } = string.Empty;
    public bool Success => ExitCode == 0;
}

public sealed class SidecarHealthReport
{
    public bool IsYtDlpHealthy { get; set; }
    public string YtDlpVersion { get; set; } = string.Empty;
    public bool IsFfmpegHealthy { get; set; }
    public string FfmpegVersion { get; set; } = string.Empty;
    public string DiagnosticMessage { get; set; } = string.Empty;
    public bool AllHealthy => IsYtDlpHealthy && IsFfmpegHealthy;
}

public sealed class SidecarManager
{
    private readonly string _sidecarsDir;
    private readonly ILogger<SidecarManager> _logger;

    public string SidecarsDirectory => _sidecarsDir;

    public SidecarManager(string? customSidecarsDirectory = null, ILogger<SidecarManager>? logger = null)
    {
        _logger = logger ?? NullLogger<SidecarManager>.Instance;
        if (!string.IsNullOrWhiteSpace(customSidecarsDirectory))
        {
            _sidecarsDir = PathResolver.GetPhysicalPath(customSidecarsDirectory);
        }
        else
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _sidecarsDir = PathResolver.GetPhysicalPath(Path.Combine(localAppData, "SwiftGet", "sidecars"));
        }

        Directory.CreateDirectory(_sidecarsDir);
    }

    public static string GetSafeTempDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var tempDir = Path.Combine(localAppData, "SwiftGet", "temp");
        Directory.CreateDirectory(tempDir);
        return PathResolver.GetPhysicalPath(tempDir);
    }

    public static string GetSafeCacheDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var cacheDir = Path.Combine(localAppData, "SwiftGet", "cache", "yt-dlp");
        Directory.CreateDirectory(cacheDir);
        return PathResolver.GetPhysicalPath(cacheDir);
    }

    private readonly Dictionary<string, string> _allowedHashes = new(StringComparer.OrdinalIgnoreCase);

    public void RegisterAllowedHash(string binaryName, string expectedSha256)
    {
        _allowedHashes[binaryName] = expectedSha256;
    }

    public bool VerifyBinary(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return false;

        var name = Path.GetFileName(filePath);
        if (_allowedHashes.TryGetValue(name, out var expectedHash))
        {
            return VerifyFileSha256(filePath, expectedHash);
        }

        return true;
    }

    public string? FindYtDlpPath()
    {
        return FindBinary("yt-dlp.exe") ?? FindBinary("yt-dlp");
    }

    public string? FindFfmpegPath()
    {
        return FindBinary("ffmpeg.exe") ?? FindBinary("ffmpeg");
    }

    public bool IsYtDlpAvailable() => !string.IsNullOrEmpty(FindYtDlpPath());

    public bool IsFfmpegAvailable() => !string.IsNullOrEmpty(FindFfmpegPath());

    public async Task<string> GetYtDlpVersionAsync(CancellationToken ct = default)
    {
        var path = FindYtDlpPath();
        if (string.IsNullOrEmpty(path))
        {
            throw new FileNotFoundException("yt-dlp binary was not found in sidecars directory or app folder.");
        }

        var result = await ExecuteSidecarAsync(path, ["--version"], ct: ct).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException($"Failed to query yt-dlp version: {FormatSidecarError(result.StandardError, path)}");
        }

        return result.StandardOutput.Trim();
    }

    public async Task<string> GetFfmpegVersionAsync(CancellationToken ct = default)
    {
        var path = FindFfmpegPath();
        if (string.IsNullOrEmpty(path))
        {
            throw new FileNotFoundException("ffmpeg binary was not found in sidecars directory or app folder.");
        }

        var result = await ExecuteSidecarAsync(path, ["-version"], ct: ct).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException($"Failed to query ffmpeg version: {FormatSidecarError(result.StandardError, path)}");
        }

        var firstLine = result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return firstLine.Length > 0 ? firstLine[0].Trim() : "ffmpeg unknown version";
    }

    public async Task<SidecarHealthReport> CheckSidecarsHealthAsync(CancellationToken ct = default)
    {
        var report = new SidecarHealthReport();
        try
        {
            report.YtDlpVersion = await GetYtDlpVersionAsync(ct).ConfigureAwait(false);
            report.IsYtDlpHealthy = !string.IsNullOrWhiteSpace(report.YtDlpVersion);
        }
        catch (Exception ex)
        {
            report.IsYtDlpHealthy = false;
            report.DiagnosticMessage += $"yt-dlp check failed: {ex.Message}; ";
        }

        try
        {
            report.FfmpegVersion = await GetFfmpegVersionAsync(ct).ConfigureAwait(false);
            report.IsFfmpegHealthy = !string.IsNullOrWhiteSpace(report.FfmpegVersion);
        }
        catch (Exception ex)
        {
            report.IsFfmpegHealthy = false;
            report.DiagnosticMessage += $"ffmpeg check failed: {ex.Message}; ";
        }

        if (report.AllHealthy)
        {
            report.DiagnosticMessage = "All media sidecars are healthy and operational.";
        }

        return report;
    }

    public static string FormatSidecarError(string rawError, string? pathContext = null)
    {
        if (string.IsNullOrWhiteSpace(rawError))
            return "An unknown sidecar error occurred.";

        if (rawError.Contains("WinError 448", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("untrusted mount point", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("ERROR_UNTRUSTED_MOUNT_POINT", StringComparison.OrdinalIgnoreCase))
        {
            var target = !string.IsNullOrWhiteSpace(pathContext) ? pathContext : "download or temporary folder";
            return $"The folder '{target}' is a redirected or junction folder (untrusted mount point). Choose another location or click 'Fix automatically' to resolve it to its physical path.";
        }

        return rawError;
    }

    public static async Task<SidecarProcessResult> ExecuteSidecarAsync(
        string binaryPath,
        IEnumerable<string> arguments,
        Action<string>? onStdOut = null,
        Action<string>? onStdErr = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binaryPath);
        ArgumentNullException.ThrowIfNull(arguments);

        var resolvedBinary = Path.IsPathRooted(binaryPath) || File.Exists(binaryPath)
            ? PathResolver.GetPhysicalPath(binaryPath)
            : binaryPath;
        var binaryDir = Path.GetDirectoryName(resolvedBinary);
        var safeWorkingDir = !string.IsNullOrEmpty(binaryDir) && Directory.Exists(binaryDir)
            ? PathResolver.GetPhysicalPath(binaryDir)
            : GetSafeTempDirectory();

        var safeTemp = GetSafeTempDirectory();
        var safeCache = GetSafeCacheDirectory();

        var startInfo = new ProcessStartInfo
        {
            FileName = resolvedBinary,
            WorkingDirectory = safeWorkingDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        startInfo.Environment["TEMP"] = safeTemp;
        startInfo.Environment["TMP"] = safeTemp;

        var isYtDlp = Path.GetFileNameWithoutExtension(resolvedBinary).Equals("yt-dlp", StringComparison.OrdinalIgnoreCase);
        var argsList = arguments.ToList();

        // If yt-dlp, isolate cache-dir to prevent touching user-profile junctions / cloud folders
        if (isYtDlp && !argsList.Any(a => a.Equals("--cache-dir", StringComparison.OrdinalIgnoreCase)))
        {
            argsList.Insert(0, safeCache);
            argsList.Insert(0, "--cache-dir");
        }

        foreach (var arg in argsList)
        {
            if (arg.Contains(Path.DirectorySeparatorChar) || arg.Contains(Path.AltDirectorySeparatorChar))
            {
                if (Path.IsPathRooted(arg) && !arg.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !arg.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    startInfo.ArgumentList.Add(PathResolver.GetPhysicalPath(arg));
                    continue;
                }
            }
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };
        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();

        process.OutputDataReceived += (s, e) =>
        {
            if (e.Data != null)
            {
                stdoutBuilder.AppendLine(e.Data);
                onStdOut?.Invoke(e.Data);
            }
        };

        process.ErrorDataReceived += (s, e) =>
        {
            if (e.Data != null)
            {
                stderrBuilder.AppendLine(e.Data);
                onStdErr?.Invoke(e.Data);
            }
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            return new SidecarProcessResult
            {
                ExitCode = process.ExitCode,
                StandardOutput = stdoutBuilder.ToString(),
                StandardError = stderrBuilder.ToString()
            };
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
            }
            catch { }
            throw;
        }
    }

    public static Task<SidecarProcessResult> ExecuteSidecarAsync(
        string binaryPath,
        string arguments,
        Action<string>? onStdOut = null,
        Action<string>? onStdErr = null,
        CancellationToken ct = default)
    {
        return ExecuteSidecarAsync(binaryPath, SplitCommandLineArguments(arguments), onStdOut, onStdErr, ct);
    }

    private static IEnumerable<string> SplitCommandLineArguments(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            yield break;

        var inQuotes = false;
        var token = new StringBuilder();

        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (token.Length > 0)
                {
                    yield return token.ToString();
                    token.Clear();
                }
            }
            else
            {
                token.Append(c);
            }
        }

        if (token.Length > 0)
        {
            yield return token.ToString();
        }
    }

    public static bool VerifyFileSha256(string filePath, string expectedHexHash)
    {
        if (!File.Exists(filePath)) return false;

        using var stream = File.OpenRead(filePath);
        var hashBytes = SHA256.HashData(stream);
        var actualHex = Convert.ToHexString(hashBytes).ToLowerInvariant();
        var expectedClean = expectedHexHash.Trim().ToLowerInvariant();

        return string.Equals(actualHex, expectedClean, StringComparison.OrdinalIgnoreCase);
    }

    private string? FindBinary(string binaryName)
    {
        // 1. Check sidecars directory
        var localPath = Path.Combine(_sidecarsDir, binaryName);
        if (File.Exists(localPath) && VerifyBinary(localPath)) return PathResolver.GetPhysicalPath(localPath);

        // 1b. Check bin directory under SwiftGet
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var binPath = Path.Combine(localAppData, "SwiftGet", "bin", binaryName);
        if (File.Exists(binPath) && VerifyBinary(binPath)) return PathResolver.GetPhysicalPath(binPath);

        // 2. Check application directory
        var appPath = Path.Combine(AppContext.BaseDirectory, binaryName);
        if (File.Exists(appPath) && VerifyBinary(appPath)) return PathResolver.GetPhysicalPath(appPath);

        var subDirAppPath = Path.Combine(AppContext.BaseDirectory, "sidecars", binaryName);
        if (File.Exists(subDirAppPath) && VerifyBinary(subDirAppPath)) return PathResolver.GetPhysicalPath(subDirAppPath);

        // PATH search removed per Hard Constraint 5 to prevent binary planting
        return null;
    }
}
