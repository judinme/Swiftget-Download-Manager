using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SwiftGet.Core.IO;
using SwiftGet.Core.Models;
using SwiftGet.Core.Storage;
using SwiftGet.Engine.Torrent;

namespace SwiftGet.Engine.Services;

public interface IDownloadDetailsSnapshotService
{
    Task<DownloadDetailsSnapshot> CaptureSnapshotAsync(
        string downloadId,
        DownloadItem? fallbackItem = null,
        double currentSpeed = 0,
        TimeSpan? currentRemainingTime = null,
        double? currentProgressPercentage = null,
        CancellationToken ct = default);
}

public sealed class DownloadDetailsSnapshotService : IDownloadDetailsSnapshotService
{
    private readonly ISwiftGetDatabase _db;
    private readonly ITorrentEngine _torrentEngine;
    private readonly ILogger<DownloadDetailsSnapshotService> _logger;

    public DownloadDetailsSnapshotService(
        ISwiftGetDatabase db,
        ITorrentEngine torrentEngine,
        ILogger<DownloadDetailsSnapshotService>? logger = null)
    {
        _db = db;
        _torrentEngine = torrentEngine;
        _logger = logger ?? NullLogger<DownloadDetailsSnapshotService>.Instance;
    }

    public async Task<DownloadDetailsSnapshot> CaptureSnapshotAsync(
        string downloadId,
        DownloadItem? fallbackItem = null,
        double currentSpeed = 0,
        TimeSpan? currentRemainingTime = null,
        double? currentProgressPercentage = null,
        CancellationToken ct = default)
    {
        var dbItem = await _db.GetDownloadAsync(downloadId, ct).ConfigureAwait(false);
        var item = dbItem ?? fallbackItem;

        if (item == null)
        {
            return new DownloadDetailsSnapshot { DownloadId = downloadId };
        }

        double effectiveProgressPct = (currentProgressPercentage.HasValue && currentProgressPercentage.Value > 0)
            ? currentProgressPercentage.Value
            : (fallbackItem?.ProgressPercentage > 0 ? fallbackItem.ProgressPercentage : item.ProgressPercentage);

        // Overlay realtime metrics from live in-memory fallbackItem if provided
        if (fallbackItem != null)
        {
            if (fallbackItem.DownloadedBytes > item.DownloadedBytes)
            {
                item.DownloadedBytes = fallbackItem.DownloadedBytes;
            }
            if (fallbackItem.TotalBytes > 0)
            {
                item.TotalBytes = fallbackItem.TotalBytes;
            }
            if (fallbackItem.Status is DownloadStatus.Downloading or DownloadStatus.Paused or DownloadStatus.FetchingMetadata or DownloadStatus.Checking or DownloadStatus.Seeding)
            {
                item.Status = fallbackItem.Status;
            }
            if (!string.IsNullOrWhiteSpace(fallbackItem.FileName))
            {
                item.FileName = fallbackItem.FileName;
            }
            if (!string.IsNullOrWhiteSpace(fallbackItem.DestinationPath))
            {
                item.DestinationPath = fallbackItem.DestinationPath;
            }
        }

        if (item.DownloadedBytes <= 0 && item.TotalBytes > 0 && effectiveProgressPct > 0)
        {
            item.DownloadedBytes = (long)(item.TotalBytes * (effectiveProgressPct / 100.0));
        }
        else if (effectiveProgressPct <= 0 && item.TotalBytes > 0 && item.DownloadedBytes > 0)
        {
            effectiveProgressPct = Math.Min(100.0, (double)item.DownloadedBytes / item.TotalBytes * 100.0);
        }

        if (item.JobKind == JobKind.Torrent)
        {
            return await CaptureTorrentSnapshotAsync(item, currentSpeed, ct).ConfigureAwait(false);
        }

        return CaptureHttpOrMediaSnapshot(item, currentSpeed, currentRemainingTime, effectiveProgressPct);
    }

    private async Task<DownloadDetailsSnapshot> CaptureTorrentSnapshotAsync(
        DownloadItem item,
        double currentSpeed,
        CancellationToken ct)
    {
        var stats = _torrentEngine.GetJobStats(item.Id);
        var peers = await _torrentEngine.GetPeersAsync(item.Id, ct).ConfigureAwait(false);
        var trackers = _torrentEngine.GetTrackers(item.Id);
        var pieceStates = _torrentEngine.GetPieceStates(item.Id);

        var downsampledCells = PieceGridDownsampler.Downsample(pieceStates, maxCells: 1500);

        long downloadedBytes = stats?.DownloadedBytes ?? item.DownloadedBytes;
        long totalBytes = stats?.TotalBytes ?? item.TotalBytes;
        long uploadedBytes = stats?.UploadedBytes ?? 0;
        double speed = stats?.DownloadSpeedBytesPerSec ?? (currentSpeed > 0 ? currentSpeed : 0);
        double uploadSpeed = stats?.UploadSpeedBytesPerSec ?? 0;

        double calculatedBytePct = totalBytes > 0 ? Math.Min(100.0, (double)downloadedBytes / totalBytes * 100.0) : 0.0;
        double progressPct = Math.Max(stats?.ProgressPercentage ?? 0.0, Math.Max(item.ProgressPercentage, calculatedBytePct));

        TimeSpan eta = TimeSpan.Zero;
        if (speed > 0 && totalBytes > downloadedBytes)
        {
            eta = TimeSpan.FromSeconds((totalBytes - downloadedBytes) / speed);
        }

        var saveFolder = !string.IsNullOrWhiteSpace(item.DestinationPath)
            ? (Directory.Exists(item.DestinationPath) ? item.DestinationPath : Path.GetDirectoryName(item.DestinationPath) ?? item.DestinationPath)
            : string.Empty;

        var fullPath = item.DestinationPath ?? string.Empty;
        var fileName = !string.IsNullOrWhiteSpace(stats?.Name) && stats.Name != "Torrent"
            ? stats.Name
            : (!string.IsNullOrWhiteSpace(item.FileName) ? item.FileName : "Torrent");

        int connectedSeeds = stats?.ConnectedSeeds ?? peers.Count(p => p.Flags.Contains('S') || p.Flags.Contains('s'));
        int totalSeeds = stats?.TotalSeeds ?? 0;
        int connectedPeers = stats?.ConnectedPeers ?? peers.Count;
        int totalPeers = stats?.TotalPeers ?? 0;

        var trackerSnapshots = trackers.Select(t => new TrackerDetailSnapshot
        {
            AnnounceUrl = t.AnnounceUrl,
            Status = t.Status,
            IsError = t.Status.Contains("unreachable", StringComparison.OrdinalIgnoreCase) ||
                      t.Status.Contains("No such host", StringComparison.OrdinalIgnoreCase) ||
                      t.Status.Contains("offline", StringComparison.OrdinalIgnoreCase) ||
                      !string.IsNullOrWhiteSpace(t.ErrorMessage)
        }).ToList();

        var connectionSnapshots = peers.Select(p => new ConnectionDetailSnapshot
        {
            Host = p.IpAddress,
            Port = p.Port,
            ConnectionCount = 1,
            Direction = (p.Flags.Contains('S') || p.Flags.Contains('s')) ? "In" : "In/Out",
            Client = p.Client
        }).ToList();

        int downloadedPieces = pieceStates.Count(s => s == PieceState.Downloaded);

        return new DownloadDetailsSnapshot
        {
            DownloadId = item.Id,
            JobKind = JobKind.Torrent,
            Status = stats != null ? stats.State : item.Status,
            General = new GeneralDetailSnapshot
            {
                FileName = fileName,
                SaveFolder = saveFolder,
                FullSavePath = fullPath,
                ProgressPercentage = progressPct > 0 && progressPct < 0.1
                    ? Math.Round(Math.Clamp(progressPct, 0.0, 100.0), 2)
                    : Math.Round(Math.Clamp(progressPct, 0.0, 100.0), 1),
                RemainingTime = eta,
                DownloadedBytes = downloadedBytes,
                TotalBytes = totalBytes,
                UploadedBytes = uploadedBytes,
                ShareRatio = downloadedBytes > 0 ? Math.Round((double)uploadedBytes / downloadedBytes, 2) : 0,
                AddedAt = item.CreatedAtUtc,
                DownloadSpeedBytesPerSec = speed,
                UploadSpeedBytesPerSec = uploadSpeed,
                Priority = "normal",
                SourcePageUrl = item.Url,
                IsFolder = true
            },
            Torrent = new TorrentDetailSnapshot
            {
                TotalPeers = totalPeers,
                ConnectedPeers = connectedPeers,
                TotalSeeds = totalSeeds,
                ConnectedSeeds = connectedSeeds,
                Availability = stats?.Availability ?? 0,
                LastSeenComplete = stats?.LastSeenComplete,
                Trackers = trackerSnapshots
            },
            ProgressGrid = new PieceGridSnapshot
            {
                TotalPiecesOrSegments = pieceStates.Length,
                DownloadedPieces = downloadedPieces,
                Cells = downsampledCells
            },
            Connections = connectionSnapshots
        };
    }

    private static DownloadDetailsSnapshot CaptureHttpOrMediaSnapshot(
        DownloadItem item,
        double currentSpeed,
        TimeSpan? currentRemainingTime,
        double? currentProgressPercentage = null)
    {
        long downloadedBytes = item.DownloadedBytes;
        long totalBytes = item.TotalBytes;
        double speed = currentSpeed;
        double progressPct = currentProgressPercentage ?? item.ProgressPercentage;

        if (progressPct <= 0 && totalBytes > 0 && downloadedBytes > 0)
        {
            progressPct = Math.Min(100.0, (double)downloadedBytes / totalBytes * 100.0);
        }
        else if (downloadedBytes <= 0 && totalBytes > 0 && progressPct > 0)
        {
            downloadedBytes = (long)(totalBytes * (progressPct / 100.0));
        }

        TimeSpan eta = currentRemainingTime ?? TimeSpan.Zero;
        if (eta == TimeSpan.Zero && speed > 0 && totalBytes > downloadedBytes)
        {
            eta = TimeSpan.FromSeconds((totalBytes - downloadedBytes) / speed);
        }

        var saveFolder = !string.IsNullOrWhiteSpace(item.DestinationPath)
            ? Path.GetDirectoryName(item.DestinationPath) ?? item.DestinationPath
            : string.Empty;

        string host = string.Empty;
        int port = 80;
        if (Uri.TryCreate(item.FinalUrl.Length > 0 ? item.FinalUrl : item.Url, UriKind.Absolute, out var uri))
        {
            host = uri.Host;
            port = uri.Port;
        }

        int segmentCount = item.Segments != null && item.Segments.Count > 0
            ? item.Segments.Count
            : (item.SupportsRanges ? 8 : 1);

        var isActivelyDownloading = item.Status == DownloadStatus.Downloading;

        var connections = new List<ConnectionDetailSnapshot>();
        if (!string.IsNullOrWhiteSpace(host) && isActivelyDownloading)
        {
            connections.Add(new ConnectionDetailSnapshot
            {
                Host = host,
                Port = port,
                ConnectionCount = segmentCount,
                Direction = "In/Out",
                Client = "HTTP/1.1 or HTTP/2"
            });
        }

        var cells = PieceGridDownsampler.GenerateForHttp(
            downloadedBytes,
            totalBytes,
            maxCells: 100,
            isActivelyDownloading: isActivelyDownloading);

        return new DownloadDetailsSnapshot
        {
            DownloadId = item.Id,
            JobKind = item.JobKind,
            Status = item.Status,
            General = new GeneralDetailSnapshot
            {
                FileName = item.FileName,
                SaveFolder = saveFolder,
                FullSavePath = item.DestinationPath,
                ProgressPercentage = progressPct > 0 && progressPct < 0.1
                    ? Math.Round(Math.Clamp(progressPct, 0.0, 100.0), 2)
                    : Math.Round(Math.Clamp(progressPct, 0.0, 100.0), 1),
                RemainingTime = eta,
                DownloadedBytes = downloadedBytes,
                TotalBytes = totalBytes,
                UploadedBytes = 0,
                ShareRatio = 0,
                AddedAt = item.CreatedAtUtc,
                DownloadSpeedBytesPerSec = speed,
                UploadSpeedBytesPerSec = 0,
                Priority = "normal",
                SourcePageUrl = item.Url,
                IsFolder = false
            },
            Http = new HttpDetailSnapshot
            {
                Server = host,
                ResumeSupported = item.SupportsRanges,
                ContentType = item.ContentType ?? "application/octet-stream",
                RedirectTarget = item.FinalUrl != item.Url ? item.FinalUrl : string.Empty,
                SegmentCount = segmentCount
            },
            ProgressGrid = new PieceGridSnapshot
            {
                TotalPiecesOrSegments = cells.Length,
                DownloadedPieces = cells.Count(c => c == PieceState.Downloaded),
                Cells = cells
            },
            Connections = connections
        };
    }
}
