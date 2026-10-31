using System;
using System.Collections.Generic;

namespace SwiftGet.Core.Models;

public sealed record DownloadDetailsSnapshot
{
    public string DownloadId { get; init; } = string.Empty;
    public JobKind JobKind { get; init; } = JobKind.Http;
    public DownloadStatus Status { get; init; } = DownloadStatus.Queued;

    public GeneralDetailSnapshot General { get; init; } = new();
    public TorrentDetailSnapshot? Torrent { get; init; }
    public HttpDetailSnapshot? Http { get; init; }
    public PieceGridSnapshot ProgressGrid { get; init; } = new();
    public IReadOnlyList<ConnectionDetailSnapshot> Connections { get; init; } = Array.Empty<ConnectionDetailSnapshot>();
}

public sealed record GeneralDetailSnapshot
{
    public string FileName { get; init; } = string.Empty;
    public string SaveFolder { get; init; } = string.Empty;
    public string FullSavePath { get; init; } = string.Empty;
    public double ProgressPercentage { get; init; }
    public TimeSpan RemainingTime { get; init; } = TimeSpan.Zero;
    public long DownloadedBytes { get; init; }
    public long TotalBytes { get; init; }
    public long UploadedBytes { get; init; }
    public double ShareRatio { get; init; }
    public DateTime AddedAt { get; init; } = DateTime.UtcNow;
    public double DownloadSpeedBytesPerSec { get; init; }
    public double UploadSpeedBytesPerSec { get; init; }
    public string Priority { get; init; } = "normal";
    public string SourcePageUrl { get; init; } = string.Empty;
    public bool IsFolder { get; init; }
}

public sealed record TorrentDetailSnapshot
{
    public int TotalPeers { get; init; }
    public int ConnectedPeers { get; init; }
    public int TotalSeeds { get; init; }
    public int ConnectedSeeds { get; init; }
    public double Availability { get; init; }
    public DateTime? LastSeenComplete { get; init; }
    public IReadOnlyList<TrackerDetailSnapshot> Trackers { get; init; } = Array.Empty<TrackerDetailSnapshot>();
}

public sealed record TrackerDetailSnapshot
{
    public string AnnounceUrl { get; init; } = string.Empty;
    public string Status { get; init; } = "Working";
    public bool IsError { get; init; }
}

public sealed record HttpDetailSnapshot
{
    public string Server { get; init; } = string.Empty;
    public bool ResumeSupported { get; init; }
    public string ContentType { get; init; } = string.Empty;
    public string RedirectTarget { get; init; } = string.Empty;
    public int SegmentCount { get; init; }
}

public sealed record PieceGridSnapshot
{
    public int TotalPiecesOrSegments { get; init; }
    public int DownloadedPieces { get; init; }
    public PieceState[] Cells { get; init; } = Array.Empty<PieceState>();
}

public sealed record ConnectionDetailSnapshot
{
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; }
    public int ConnectionCount { get; init; } = 1;
    public string Direction { get; init; } = "In/Out";
    public string Client { get; init; } = string.Empty;
}
