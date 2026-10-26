using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SwiftGet.Engine.Updating;

public interface IUpdateService
{
    Task<UpdateCheckResult> CheckForUpdatesAsync(string feedUrl, Version currentVersion, CancellationToken ct = default);
    Task<string> DownloadUpdateAsync(UpdateManifest manifest, string destinationDirectory, IProgress<double>? progress = null, CancellationToken ct = default);
    bool VerifyInstallerHash(string filePath, string expectedSha256);
    Process? LaunchInstaller(string installerPath, string? arguments = null, bool silent = false);
}

public class UpdateService : IUpdateService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<UpdateService> _logger;

    public UpdateService(HttpClient? httpClient = null, ILogger<UpdateService>? logger = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _logger = logger ?? NullLogger<UpdateService>.Instance;
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(string feedUrl, Version currentVersion, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(feedUrl))
        {
            return UpdateCheckResult.Error(currentVersion, "Feed URL cannot be null or empty.");
        }

        if (!feedUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            !feedUrl.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase) &&
            !feedUrl.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase))
        {
            return UpdateCheckResult.Error(currentVersion, "Insecure update feed: only HTTPS or loopback update feeds are permitted.");
        }

        try
        {
            using var response = await _httpClient.GetAsync(feedUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return UpdateCheckResult.Error(currentVersion, $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}");
            }

            await using var contentStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var manifest = await JsonSerializer.DeserializeAsync<UpdateManifest>(contentStream, cancellationToken: ct).ConfigureAwait(false);

            if (manifest == null || string.IsNullOrWhiteSpace(manifest.Version))
            {
                return UpdateCheckResult.Error(currentVersion, "Invalid update manifest format.");
            }

            if (!Version.TryParse(manifest.Version, out var latestVersion))
            {
                return UpdateCheckResult.Error(currentVersion, $"Unparseable version in manifest: {manifest.Version}");
            }

            if (!string.IsNullOrWhiteSpace(manifest.MinSupportedVersion) &&
                Version.TryParse(manifest.MinSupportedVersion, out var minSupported))
            {
                if (latestVersion < minSupported)
                {
                    return UpdateCheckResult.Error(currentVersion, $"Invalid update manifest: latest version {latestVersion} is below minimum supported version {minSupported}.");
                }
            }

            if (latestVersion <= currentVersion)
            {
                _logger.LogInformation("SwiftGet is up to date at version {CurrentVersion} (feed version: {LatestVersion}).", currentVersion, latestVersion);
                return UpdateCheckResult.NoUpdate(currentVersion);
            }

            if (string.IsNullOrWhiteSpace(manifest.Sha256Checksum) || manifest.Sha256Checksum.Trim().Length != 64)
            {
                return UpdateCheckResult.Error(currentVersion, "Invalid or missing SHA-256 checksum in update manifest.");
            }

            _logger.LogInformation("New version {LatestVersion} available (current: {CurrentVersion}).", latestVersion, currentVersion);
            return UpdateCheckResult.UpdateAvailable(currentVersion, latestVersion, manifest);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to check for updates from {FeedUrl}.", feedUrl);
            return UpdateCheckResult.Error(currentVersion, ex.Message);
        }
    }

    public async Task<string> DownloadUpdateAsync(UpdateManifest manifest, string destinationDirectory, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        Directory.CreateDirectory(destinationDirectory);

        var fileName = Path.GetFileName(new Uri(manifest.DownloadUrl).LocalPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = $"SwiftGet-{manifest.Version}-x64.msi";
        }

        var destinationPath = Path.Combine(destinationDirectory, fileName);

        using var response = await _httpClient.GetAsync(manifest.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        await using (var contentStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920];
            long totalRead = 0;
            int read;

            while ((read = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                totalRead += read;

                if (totalBytes > 0 && progress != null)
                {
                    progress.Report((double)totalRead / totalBytes * 100.0);
                }
            }
        }

        // Verify SHA-256 Checksum
        if (!VerifyInstallerHash(destinationPath, manifest.Sha256Checksum))
        {
            try { File.Delete(destinationPath); } catch { }
            throw new SecurityException($"Installer hash verification failed! Expected: {manifest.Sha256Checksum}");
        }

        _logger.LogInformation("Update downloaded and verified successfully: {Path}", destinationPath);
        return destinationPath;
    }

    public bool VerifyInstallerHash(string filePath, string expectedSha256)
    {
        if (!File.Exists(filePath) || string.IsNullOrWhiteSpace(expectedSha256))
        {
            return false;
        }

        using var stream = File.OpenRead(filePath);
        var actualBytes = SHA256.HashData(stream);
        var actualHex = Convert.ToHexString(actualBytes);

        return string.Equals(actualHex, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static ProcessStartInfo CreateInstallerStartInfo(string installerPath, string? arguments = null, bool silent = false)
    {
        if (!File.Exists(installerPath))
        {
            throw new FileNotFoundException("Installer package file not found.", installerPath);
        }

        var ext = Path.GetExtension(installerPath).ToLowerInvariant();
        ProcessStartInfo psi;

        if (ext == ".msi")
        {
            psi = new ProcessStartInfo
            {
                FileName = "msiexec.exe",
                UseShellExecute = false
            };
            psi.ArgumentList.Add("/i");
            psi.ArgumentList.Add(installerPath);

            if (silent)
            {
                psi.ArgumentList.Add("/qn");
            }
            else if (!string.IsNullOrWhiteSpace(arguments))
            {
                foreach (var arg in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    psi.ArgumentList.Add(arg);
                }
            }
            else
            {
                psi.ArgumentList.Add("/passive");
            }
        }
        else
        {
            psi = new ProcessStartInfo
            {
                FileName = installerPath,
                UseShellExecute = false
            };

            if (!string.IsNullOrWhiteSpace(arguments))
            {
                foreach (var arg in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    psi.ArgumentList.Add(arg);
                }
            }
            else if (silent)
            {
                psi.ArgumentList.Add("/silent");
            }
        }

        return psi;
    }

    public Process? LaunchInstaller(string installerPath, string? arguments = null, bool silent = false)
    {
        var psi = CreateInstallerStartInfo(installerPath, arguments, silent);
        _logger.LogInformation("Launching installer: {FileName} with {Count} arguments", psi.FileName, psi.ArgumentList.Count);
        return Process.Start(psi);
    }
}
