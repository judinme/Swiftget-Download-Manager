using System;
using System.Collections.Generic;
using System.Net;
using SwiftGet.Core.Models;

namespace SwiftGet.Engine.Torrent;

public sealed class TorrentEngineOptions
{
    public int ListenPort { get; set; }
    public bool EnableUpnp { get; set; }
    public bool EnableDht { get; set; } = true;
    public bool EnablePex { get; set; } = true;
    public bool EnableLocalPeerDiscovery { get; set; } = true;
    public bool PreferEncryption { get; set; } = true;
    public IPAddress? BoundInterfaceAddress { get; set; }
    public int MaxConnections { get; set; } = 150;
    public long GlobalMaxDownloadRateBytesPerSec { get; set; }
    public long GlobalMaxUploadRateBytesPerSec { get; set; }
    public bool AllowLocalTrackers { get; set; }
    public string SaveDirectory { get; set; } = string.Empty;
    public string CacheDirectory { get; set; } = string.Empty;
}

public sealed class TorrentAddOptions
{
    public string DownloadId { get; set; } = Guid.NewGuid().ToString("N");
    public string SaveDirectory { get; set; } = string.Empty;
    public string CategoryId { get; set; } = "torrents";
    public string QueueId { get; set; } = "default";
    public bool StartImmediately { get; set; } = true;
    public bool SequentialDownload { get; set; }
    public double SeedRatioLimit { get; set; } = 1.0;
    public int SeedTimeLimitMins { get; set; }
    public IReadOnlyList<int>? SelectedFileIndices { get; set; }
    public IReadOnlyDictionary<int, TorrentFilePriority>? FilePriorities { get; set; }
}

public sealed class TorrentJobStats
{
    public string JobId { get; set; } = string.Empty;
    public string InfoHash { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DownloadStatus State { get; set; }
    public double ProgressPercentage { get; set; }
    public long TotalBytes { get; set; }
    public long DownloadedBytes { get; set; }
    public long UploadedBytes { get; set; }
    public double DownloadSpeedBytesPerSec { get; set; }
    public double UploadSpeedBytesPerSec { get; set; }
    public int ConnectedSeeds { get; set; }
    public int TotalSeeds { get; set; }
    public int ConnectedPeers { get; set; }
    public int TotalPeers { get; set; }
    public double CurrentRatio => DownloadedBytes > 0
        ? (double)UploadedBytes / DownloadedBytes
        : (UploadedBytes > 0 ? 100.0 : 0.0);
    public int PieceLength { get; set; }
    public int PieceCount { get; set; }
    public bool HasMetadata { get; set; }
}

public sealed class TorrentFileInfo
{
    public int FileIndex { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public long DownloadedBytes { get; set; }
    public TorrentFilePriority Priority { get; set; } = TorrentFilePriority.Normal;
    public bool IsSelected { get; set; } = true;
    public double ProgressPercentage => FileSizeBytes > 0
        ? Math.Clamp((double)DownloadedBytes / FileSizeBytes * 100.0, 0.0, 100.0)
        : 0.0;
}

public sealed class TorrentPeerInfo
{
    public string Client { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public int Port { get; set; }
    public double DownloadSpeedBytesPerSec { get; set; }
    public double UploadSpeedBytesPerSec { get; set; }
    public string Flags { get; set; } = string.Empty;
    public bool IsEncrypted { get; set; }
    public bool IsUtp { get; set; }
}

public sealed class TorrentTrackerInfo
{
    public int Tier { get; set; }
    public string AnnounceUrl { get; set; } = string.Empty;
    public string Status { get; set; } = "NotContacted";
    public int Seeds { get; set; }
    public int Peers { get; set; }
    public DateTime? LastAnnounce { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class TorrentStateChangedEventArgs : EventArgs
{
    public string JobId { get; }
    public DownloadStatus OldState { get; }
    public DownloadStatus NewState { get; }

    public TorrentStateChangedEventArgs(string jobId, DownloadStatus oldState, DownloadStatus newState)
    {
        JobId = jobId;
        OldState = oldState;
        NewState = newState;
    }
}

public sealed class TorrentMetadataReceivedEventArgs : EventArgs
{
    public string JobId { get; }
    public string InfoHash { get; }
    public string Name { get; }
    public long TotalBytes { get; }
    public IReadOnlyList<TorrentFileInfo> Files { get; }

    public TorrentMetadataReceivedEventArgs(string jobId, string infoHash, string name, long totalBytes, IReadOnlyList<TorrentFileInfo> files)
    {
        JobId = jobId;
        InfoHash = infoHash;
        Name = name;
        TotalBytes = totalBytes;
        Files = files;
    }
}

public sealed class TorrentPieceHashedEventArgs : EventArgs
{
    public string JobId { get; }
    public int PieceIndex { get; }
    public int TotalPieces { get; }
    public bool HashPassed { get; }

    public TorrentPieceHashedEventArgs(string jobId, int pieceIndex, int totalPieces, bool hashPassed)
    {
        JobId = jobId;
        PieceIndex = pieceIndex;
        TotalPieces = totalPieces;
        HashPassed = hashPassed;
    }
}

public sealed class TorrentErrorEventArgs : EventArgs
{
    public string JobId { get; }
    public string ErrorMessage { get; }
    public Exception? Exception { get; }

    public TorrentErrorEventArgs(string jobId, string errorMessage, Exception? exception = null)
    {
        JobId = jobId;
        ErrorMessage = errorMessage;
        Exception = exception;
    }
}
