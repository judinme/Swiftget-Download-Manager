using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SwiftGet.Engine.Security;

public record AntivirusScanResult(
    bool IsClean,
    bool ThreatDetected,
    int ExitCode,
    string Output,
    TimeSpan Duration,
    string? ErrorMessage = null);

public interface IAntivirusScanner
{
    bool IsEnabled { get; set; }
    string? ScannerExecutablePath { get; set; }
    string? CommandLineArguments { get; set; }
    TimeSpan Timeout { get; set; }

    Task<AntivirusScanResult> ScanFileAsync(string filePath, CancellationToken ct = default);
}

public class AntivirusScanner : IAntivirusScanner
{
    private readonly ILogger<AntivirusScanner> _logger;

    public bool IsEnabled { get; set; }
    public string? ScannerExecutablePath { get; set; }
    public string? CommandLineArguments { get; set; }
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);

    public AntivirusScanner(ILogger<AntivirusScanner>? logger = null)
    {
        _logger = logger ?? NullLogger<AntivirusScanner>.Instance;
    }

    public static string? DetectDefaultWindowsDefenderPath()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return null;
        }

        // Standard Program Files location
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var directPath = Path.Combine(pf, "Windows Defender", "MpCmdRun.exe");
        if (File.Exists(directPath))
        {
            return directPath;
        }

        // Check Windows Defender Platform updates directory
        var pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var platformDir = Path.Combine(pd, "Microsoft", "Windows Defender", "Platform");
        if (Directory.Exists(platformDir))
        {
            var latestSubdir = Directory.GetDirectories(platformDir)
                .OrderByDescending(d => d)
                .FirstOrDefault();

            if (latestSubdir != null)
            {
                var platformExe = Path.Combine(latestSubdir, "MpCmdRun.exe");
                if (File.Exists(platformExe))
                {
                    return platformExe;
                }
            }
        }

        return null;
    }

    public async Task<AntivirusScanResult> ScanFileAsync(string filePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return new AntivirusScanResult(false, false, -1, string.Empty, TimeSpan.Zero, "File does not exist.");
        }

        var exePath = ScannerExecutablePath;
        var argsTemplate = CommandLineArguments;

        // Fallback to Windows Defender if path not specified
        if (string.IsNullOrWhiteSpace(exePath))
        {
            exePath = DetectDefaultWindowsDefenderPath();
            argsTemplate ??= "-Scan -ScanType 3 -File \"{path}\"";
        }

        var resolvedExe = exePath;
        if (!string.IsNullOrWhiteSpace(resolvedExe) && !Path.IsPathRooted(resolvedExe))
        {
            var systemExe = Path.Combine(Environment.SystemDirectory, resolvedExe);
            if (File.Exists(systemExe))
            {
                resolvedExe = systemExe;
            }
        }

        if (string.IsNullOrWhiteSpace(resolvedExe) || !File.Exists(resolvedExe))
        {
            if (string.IsNullOrWhiteSpace(ScannerExecutablePath))
            {
                return new AntivirusScanResult(true, false, 0, string.Empty, TimeSpan.Zero, "No antivirus scanner found or configured. Skipping scan.");
            }
            return new AntivirusScanResult(false, false, -1, string.Empty, TimeSpan.Zero, $"Scanner executable not found: {exePath}");
        }

        // Token substitution for file path
        var args = (argsTemplate ?? "\"{path}\"")
            .Replace("{path}", filePath, StringComparison.OrdinalIgnoreCase)
            .Replace("{file}", filePath, StringComparison.OrdinalIgnoreCase);

        var sw = Stopwatch.StartNew();
        Process? process = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = resolvedExe,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            process = new Process { StartInfo = psi };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);

            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            sw.Stop();

            var combinedOutput = $"{stdout}\n{stderr}".Trim();
            var exitCode = process.ExitCode;

            // Windows Defender exit codes: 0 = clean, 2 = threat detected
            var isDefender = Path.GetFileNameWithoutExtension(resolvedExe).Equals("MpCmdRun", StringComparison.OrdinalIgnoreCase);
            var isClean = isDefender ? exitCode == 0 : exitCode == 0;
            var threatDetected = isDefender ? exitCode == 2 : exitCode != 0;

            _logger.LogInformation("Antivirus scan completed for {File} with exit code {ExitCode} in {Duration}ms", filePath, exitCode, sw.ElapsedMilliseconds);

            return new AntivirusScanResult(isClean, threatDetected, exitCode, combinedOutput, sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            try { process?.Kill(entireProcessTree: true); } catch { }
            _logger.LogWarning("Antivirus scan timed out after {Timeout}s for {File}", Timeout.TotalSeconds, filePath);
            return new AntivirusScanResult(false, false, -1, string.Empty, sw.Elapsed, "Scan timed out.");
        }
        catch (Exception ex)
        {
            sw.Stop();
            try { process?.Kill(entireProcessTree: true); } catch { }
            _logger.LogError(ex, "Failed to run antivirus scan for {File}", filePath);
            return new AntivirusScanResult(false, false, -1, string.Empty, sw.Elapsed, ex.Message);
        }
        finally
        {
            process?.Dispose();
        }
    }
}
