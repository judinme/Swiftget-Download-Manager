using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SwiftGet.Core.Models;
using SwiftGet.Core.Storage;

namespace SwiftGet.Engine.Integration;

public sealed record MigrationCleanupResult(int CleanedCount, bool WasOneTimeMigrationRun);

/// <summary>
/// Cleans up stale/phantom unconfirmed captures on startup (and executes a versioned one-time
/// migration for already-polluted databases while preserving genuine completed downloads).
/// </summary>
public sealed class CaptureStateMigrationService
{
    public const string MigrationMarkerFileName = ".migration_phantom_captures_v1_done";

    private readonly ISwiftGetDatabase _db;
    private readonly CaptureGatekeeper _gatekeeper;

    public CaptureStateMigrationService(ISwiftGetDatabase db, CaptureGatekeeper gatekeeper)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _gatekeeper = gatekeeper ?? throw new ArgumentNullException(nameof(gatekeeper));
    }

    public static string GetMigrationMarkerPath(string? customDir = null)
    {
        var dir = !string.IsNullOrWhiteSpace(customDir)
            ? customDir!
            : (!string.IsNullOrWhiteSpace(CaptureGatekeeper.CustomSettingsDirectory)
                ? CaptureGatekeeper.CustomSettingsDirectory!
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwiftGet"));
        return Path.Combine(dir, MigrationMarkerFileName);
    }

    /// <summary>
    /// Runs on every startup:
    /// 1. Purges any transient/unconfirmed rows (Probing, Allocating, or 0-byte unconfirmed Queued items without a partial file).
    /// 2. If the one-time v1 migration marker is not present, also purges already-polluted phantom rows
    ///    (e.g., claude.ai/api, gradle.properties, .appinstaller, 0-byte Failed/Paused/Queued phantom items)
    ///    and writes the migration marker so a one-time notice ("Cleaned up N stale items.") can be shown.
    /// </summary>
    public async Task<MigrationCleanupResult> RunStartupCleanupAsync(string? customMarkerDir = null, CancellationToken ct = default)
    {
        var markerPath = GetMigrationMarkerPath(customMarkerDir);
        bool isFirstMigrationRun = !File.Exists(markerPath);
        int cleanedCount = 0;

        var allItems = await _db.GetAllDownloadsAsync(ct).ConfigureAwait(false);
        var toDelete = new List<DownloadItem>();

        foreach (var item in allItems)
        {
            // Never delete user's genuine completed or seeding downloads!
            if (item.Status is DownloadStatus.Completed or DownloadStatus.Seeding)
            {
                continue;
            }

            // Never delete torrents that have metadata or progress
            if (item.JobKind == JobKind.Torrent && item.DownloadedBytes > 0)
            {
                continue;
            }

            // 1. Always purge transient pre-confirmation states on startup
            if (item.Status is DownloadStatus.Probing or DownloadStatus.Allocating)
            {
                toDelete.Add(item);
                continue;
            }

            // 2. Always purge unconfirmed Queued capture placeholders (0 downloaded bytes, no partial/destination file on disk)
            if (item.Status == DownloadStatus.Queued && item.DownloadedBytes <= 0 && !HasExistingFileOnDisk(item.DestinationPath))
            {
                // If it matches phantom patterns OR has 0 total bytes (unprobed pre-persisted row from NativeMessageRouter/LoopbackServer)
                if (item.TotalBytes <= 0 || IsKnownPhantomCapture(item))
                {
                    toDelete.Add(item);
                    continue;
                }
            }

            // 3. One-time v1 migration: clean up already-polluted phantom items (even if status became Failed, Paused, or Downloading with 0 progress)
            if (isFirstMigrationRun)
            {
                if (IsKnownPhantomCapture(item) && item.DownloadedBytes <= 0)
                {
                    toDelete.Add(item);
                    continue;
                }

                // Unfinished YouTube Mix/watch items that were auto-captured without progress
                if (item.JobKind == JobKind.Media &&
                    item.DownloadedBytes <= 0 &&
                    !HasExistingFileOnDisk(item.DestinationPath) &&
                    (item.Status is DownloadStatus.Queued or DownloadStatus.Paused or DownloadStatus.Failed or DownloadStatus.Downloading))
                {
                    toDelete.Add(item);
                    continue;
                }
            }
        }

        foreach (var item in toDelete)
        {
            await _db.DeleteDownloadAsync(item.Id, ct).ConfigureAwait(false);
            cleanedCount++;
        }

        if (isFirstMigrationRun)
        {
            try
            {
                var dir = Path.GetDirectoryName(markerPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                await File.WriteAllTextAsync(markerPath, $"cleaned={cleanedCount};utc={DateTime.UtcNow:O}", ct).ConfigureAwait(false);
            }
            catch
            {
                // Ignore marker write error in restricted environments
            }
        }

        return new MigrationCleanupResult(cleanedCount, isFirstMigrationRun);
    }

    public static bool IsKnownPhantomCapture(DownloadItem item)
    {
        var url = (item.Url ?? string.Empty).ToLowerInvariant();
        var fileName = (item.FileName ?? string.Empty).ToLowerInvariant();

        if (url.StartsWith("blob:", StringComparison.Ordinal) ||
            url.StartsWith("data:", StringComparison.Ordinal) ||
            url.StartsWith("chrome-extension:", StringComparison.Ordinal))
        {
            return true;
        }

        if (url.Contains("claude.ai/api", StringComparison.Ordinal) ||
            url.Contains("/api/organizations/", StringComparison.Ordinal) ||
            url.Contains("/api/", StringComparison.Ordinal))
        {
            return true;
        }

        if (fileName.Equals("gradle.properties", StringComparison.Ordinal) ||
            fileName.EndsWith(".appinstaller", StringComparison.Ordinal) ||
            fileName.Equals("docuforge-pdf-toolbox-documentation.md", StringComparison.Ordinal) ||
            fileName.EndsWith(".md", StringComparison.Ordinal) ||
            fileName.EndsWith(".json", StringComparison.Ordinal))
        {
            return true;
        }

        if (url.Contains("youtube.com/watch", StringComparison.Ordinal) &&
            url.Contains("list=rd", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static bool HasExistingFileOnDisk(string? destinationPath)
    {
        if (string.IsNullOrWhiteSpace(destinationPath)) return false;
        try
        {
            if (File.Exists(destinationPath) && new FileInfo(destinationPath).Length > 0)
            {
                return true;
            }
            var partPath = destinationPath + ".swiftget";
            if (File.Exists(partPath) && new FileInfo(partPath).Length > 0)
            {
                return true;
            }
        }
        catch
        {
            // Ignore path errors
        }
        return false;
    }
}
