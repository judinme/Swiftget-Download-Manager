using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
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

    private static long _lastTempCleanupTicks;

    public static string GetSafeTempDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var tempDir = Path.Combine(localAppData, "SwiftGet", "temp");
        Directory.CreateDirectory(tempDir);
        var resolved = PathResolver.GetPhysicalPath(tempDir);
        TryScheduleStalePyInstallerCleanup(resolved);
        return resolved;
    }

    private static void TryScheduleStalePyInstallerCleanup(string tempDir)
    {
        var nowTicks = Environment.TickCount64;
        var prevTicks = Interlocked.Read(ref _lastTempCleanupTicks);
        if (prevTicks != 0 && (nowTicks - prevTicks) < 300_000)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastTempCleanupTicks, nowTicks, prevTicks) != prevTicks)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                var cutoffUtc = DateTime.UtcNow.AddMinutes(-20);
                foreach (var dir in Directory.EnumerateDirectories(tempDir, "_MEI*"))
                {
                    try
                    {
                        var info = new DirectoryInfo(dir);
                        if (info.LastWriteTimeUtc < cutoffUtc)
                        {
                            info.Delete(recursive: true);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        });
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

        // Prevent double-formatting and overwriting the original failing path
        if (rawError.Contains("is a redirected or junction folder", StringComparison.OrdinalIgnoreCase))
        {
            return rawError;
        }

        if (rawError.Contains("WinError 448", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("untrusted mount point", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("ERROR_UNTRUSTED_MOUNT_POINT", StringComparison.OrdinalIgnoreCase))
        {
            var extractedMatch = Regex.Match(rawError, @"['""](?<path>[A-Za-z]:\\[^'""\r\n]+)['""]");
            string? candidateContext = pathContext;
            if (!string.IsNullOrWhiteSpace(candidateContext) &&
                candidateContext.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                !PathResolver.ContainsReparsePoint(candidateContext))
            {
                candidateContext = null;
            }

            var target = extractedMatch.Success
                ? extractedMatch.Groups["path"].Value
                : (!string.IsNullOrWhiteSpace(candidateContext) ? candidateContext : "download or temporary folder");
            return $"The folder '{target}' is a redirected or junction folder (untrusted mount point). Choose another location or click 'Fix automatically' to resolve it to its physical path.";
        }

        return rawError;
    }

    public static string BuildSafePathEnvironment(string? primarySidecarDir = null)
    {
        var safeDirs = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAddSafeDir(string? rawDir)
        {
            if (string.IsNullOrWhiteSpace(rawDir))
                return;

            var cleaned = Path.TrimEndingDirectorySeparator(rawDir.Trim().Trim('"'));
            if (string.IsNullOrWhiteSpace(cleaned))
                return;

            // Skip known per-user symlink / AppExecLink directories that trigger WinError 448 under RedirectionGuard
            if (cleaned.Contains(@"\Microsoft\WindowsApps", StringComparison.OrdinalIgnoreCase) ||
                cleaned.Contains(@"\Microsoft\WinGet\Links", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Skip any directory that is or has an ancestor reparse point (junction/symlink/mount point)
            if (PathResolver.ContainsReparsePoint(cleaned))
            {
                return;
            }

            if (!Directory.Exists(cleaned))
                return;

            var physical = Path.TrimEndingDirectorySeparator(PathResolver.GetPhysicalPath(cleaned));
            if (PathResolver.ContainsReparsePoint(physical))
                return;

            if (seen.Add(physical))
            {
                safeDirs.Add(physical);
            }
        }

        TryAddSafeDir(primarySidecarDir);

        var sysDir = Environment.SystemDirectory;
        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        TryAddSafeDir(sysDir);
        TryAddSafeDir(winDir);
        if (!string.IsNullOrWhiteSpace(sysDir))
        {
            TryAddSafeDir(Path.Combine(sysDir, "Wbem"));
        }

        var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var entry in currentPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            TryAddSafeDir(entry);
        }

        return string.Join(Path.PathSeparator, safeDirs);
    }

    public static async Task<SidecarProcessResult> ExecuteSidecarAsync(
        string binaryPath,
        IEnumerable<string> arguments,
        Action<string>? onStdOut = null,
        Action<string>? onStdErr = null,
        TimeSpan? inactivityTimeout = null,
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

        startInfo.Environment["PATH"] = BuildSafePathEnvironment(safeWorkingDir);
        startInfo.Environment["TEMP"] = safeTemp;
        startInfo.Environment["TMP"] = safeTemp;
        startInfo.Environment["HOME"] = safeTemp;
        startInfo.Environment["USERPROFILE"] = safeTemp;
        startInfo.Environment["APPDATA"] = safeTemp;
        startInfo.Environment["LOCALAPPDATA"] = safeTemp;
        startInfo.Environment["XDG_CONFIG_HOME"] = safeTemp;
        startInfo.Environment["XDG_CACHE_HOME"] = safeCache;
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";

        var isYtDlp = Path.GetFileNameWithoutExtension(resolvedBinary).Equals("yt-dlp", StringComparison.OrdinalIgnoreCase);
        var argsList = arguments.ToList();

        // If yt-dlp, isolate cache-dir, config, plugins, ffmpeg-location, and JS runtimes to prevent touching user-profile junctions
        if (isYtDlp)
        {
            var isVersionQuery = argsList.Count == 1 && argsList[0].Equals("--version", StringComparison.OrdinalIgnoreCase);
            if (!isVersionQuery)
            {
                if (!argsList.Any(a => a.Equals("-- ffmpeg-location", StringComparison.OrdinalIgnoreCase) || a.Equals("--ffmpeg-location", StringComparison.OrdinalIgnoreCase)))
                {
                    var bundledFfmpeg = Path.Combine(safeWorkingDir, "ffmpeg.exe");
                    if (File.Exists(bundledFfmpeg))
                    {
                        argsList.Insert(0, safeWorkingDir);
                        argsList.Insert(0, "--ffmpeg-location");
                    }
                }

                if (!argsList.Any(a => a.Equals("--js-runtimes", StringComparison.OrdinalIgnoreCase) || a.Equals("--no-js-runtimes", StringComparison.OrdinalIgnoreCase)))
                {
                    var programFilesNode = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe");
                    if (File.Exists(programFilesNode) && !PathResolver.ContainsReparsePoint(programFilesNode))
                    {
                        argsList.Insert(0, "node");
                        argsList.Insert(0, "--js-runtimes");
                    }
                    else
                    {
                        argsList.Insert(0, "--no-js-runtimes");
                    }
                }
            }

            if (!argsList.Any(a => a.Equals("--cache-dir", StringComparison.OrdinalIgnoreCase) || a.Equals("--no-cache-dir", StringComparison.OrdinalIgnoreCase)))
            {
                argsList.Insert(0, "--no-cache-dir");
            }
            if (!argsList.Any(a => a.Equals("--no-plugin-dirs", StringComparison.OrdinalIgnoreCase)))
            {
                argsList.Insert(0, "--no-plugin-dirs");
            }
            if (!argsList.Any(a => a.Equals("--no-config-locations", StringComparison.OrdinalIgnoreCase)))
            {
                argsList.Insert(0, "--no-config-locations");
            }
            if (!argsList.Any(a => a.Equals("--ignore-config", StringComparison.OrdinalIgnoreCase) || a.Equals("--no-config", StringComparison.OrdinalIgnoreCase)))
            {
                argsList.Insert(0, "--ignore-config");
            }
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
        var outputLock = new object();
        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();
        var stdoutEof = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrEof = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        long lastActivityTicks = Environment.TickCount64;

        process.OutputDataReceived += (s, e) =>
        {
            if (e.Data == null)
            {
                stdoutEof.TrySetResult(true);
                return;
            }

            Interlocked.Exchange(ref lastActivityTicks, Environment.TickCount64);
            lock (outputLock)
            {
                stdoutBuilder.AppendLine(e.Data);
            }
            try
            {
                onStdOut?.Invoke(e.Data);
            }
            catch { }
        };

        process.ErrorDataReceived += (s, e) =>
        {
            if (e.Data == null)
            {
                stderrEof.TrySetResult(true);
                return;
            }

            Interlocked.Exchange(ref lastActivityTicks, Environment.TickCount64);
            lock (outputLock)
            {
                stderrBuilder.AppendLine(e.Data);
            }
            try
            {
                onStdErr?.Invoke(e.Data);
            }
            catch { }
        };

        try
        {
            process.Start();
            Interlocked.Exchange(ref lastActivityTicks, Environment.TickCount64);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (inactivityTimeout.HasValue && inactivityTimeout.Value > TimeSpan.Zero)
            {
                var timeoutMs = (long)inactivityTimeout.Value.TotalMilliseconds;
                var waitForExitTask = process.WaitForExitAsync(ct);
                while (!waitForExitTask.IsCompleted)
                {
                    var delayTask = Task.Delay(1000, ct);
                    var completed = await Task.WhenAny(waitForExitTask, delayTask).ConfigureAwait(false);
                    if (completed == waitForExitTask)
                    {
                        await waitForExitTask.ConfigureAwait(false);
                        break;
                    }

                    var idleMs = Environment.TickCount64 - Interlocked.Read(ref lastActivityTicks);
                    if (idleMs >= timeoutMs)
                    {
                        try
                        {
                            if (!process.HasExited)
                            {
                                KillProcessTree(process.Id);
                            }
                        }
                        catch { }
                        throw new TimeoutException($"Sidecar process stalled with no output for {inactivityTimeout.Value.TotalSeconds:F0} seconds.");
                    }
                }
            }
            else
            {
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
            }

            // Ensure async stdout/stderr pipe readers finish draining before reading StringBuilder
            await Task.WhenAny(
                Task.WhenAll(stdoutEof.Task, stderrEof.Task),
                Task.Delay(2000, CancellationToken.None)).ConfigureAwait(false);

            string stdOut;
            string stdErr;
            lock (outputLock)
            {
                stdOut = stdoutBuilder.ToString();
                stdErr = stderrBuilder.ToString();
            }

            return new SidecarProcessResult
            {
                ExitCode = process.ExitCode,
                StandardOutput = stdOut,
                StandardError = stdErr
            };
        }
        catch (System.ComponentModel.Win32Exception w32Ex)
        {
            string stdOut;
            string stdErr;
            lock (outputLock)
            {
                stdOut = stdoutBuilder.ToString();
                stdErr = stderrBuilder.ToString();
            }

            return new SidecarProcessResult
            {
                ExitCode = w32Ex.NativeErrorCode != 0 ? w32Ex.NativeErrorCode : -1,
                StandardOutput = stdOut,
                StandardError = string.IsNullOrWhiteSpace(stdErr)
                    ? $"Process execution failed: [WinError {w32Ex.NativeErrorCode}] {w32Ex.Message}"
                    : stdErr
            };
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    KillProcessTree(process.Id);
                }
            }
            catch { }
            throw;
        }
    }

    /// <summary>
    /// Forcefully terminates a process and all of its descendant child processes (e.g. ffmpeg spawned by yt-dlp).
    /// </summary>
    public static void KillProcessTree(int processId)
    {
        if (processId <= 0) return;

        try
        {
            using var proc = Process.GetProcessById(processId);
            proc.Kill(entireProcessTree: true);
        }
        catch { }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var taskkill = Process.Start(new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = $"/F /T /PID {processId}",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                taskkill?.WaitForExit(1000);
            }
            catch { }
        }
    }

    public static Task<SidecarProcessResult> ExecuteSidecarAsync(
        string binaryPath,
        string arguments,
        Action<string>? onStdOut = null,
        Action<string>? onStdErr = null,
        CancellationToken ct = default)
    {
        return ExecuteSidecarAsync(binaryPath, SplitCommandLineArguments(arguments), onStdOut, onStdErr, inactivityTimeout: null, ct: ct);
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
        // 1. Check application directory first (self-contained / installed application)
        var appPath = Path.Combine(AppContext.BaseDirectory, binaryName);
        if (File.Exists(appPath) && VerifyBinary(appPath)) return PathResolver.GetPhysicalPath(appPath);

        var subDirAppPath = Path.Combine(AppContext.BaseDirectory, "sidecars", binaryName);
        if (File.Exists(subDirAppPath) && VerifyBinary(subDirAppPath)) return PathResolver.GetPhysicalPath(subDirAppPath);

        // 2. Check local user sidecars directory
        var localPath = Path.Combine(_sidecarsDir, binaryName);
        if (File.Exists(localPath) && VerifyBinary(localPath)) return PathResolver.GetPhysicalPath(localPath);

        // 2b. Check bin directory under SwiftGet
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var binPath = Path.Combine(localAppData, "SwiftGet", "bin", binaryName);
        if (File.Exists(binPath) && VerifyBinary(binPath)) return PathResolver.GetPhysicalPath(binPath);

        // PATH search removed per Hard Constraint 5 to prevent binary planting
        return null;
    }
}
