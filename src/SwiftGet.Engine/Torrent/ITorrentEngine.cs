using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SwiftGet.Core.Models;

namespace SwiftGet.Engine.Torrent;

public interface ITorrentEngine : IAsyncDisposable
{
    bool IsRunning { get; }
    TorrentEngineOptions Options { get; }

    Task InitializeAsync(TorrentEngineOptions options, CancellationToken ct = default);
    Task<string> AddMagnetAsync(string magnetUri, TorrentAddOptions options, CancellationToken ct = default);
    Task<string> AddTorrentFileAsync(byte[] torrentBytes, TorrentAddOptions options, CancellationToken ct = default);
    Task StartAsync(string jobId, CancellationToken ct = default);
    Task PauseAsync(string jobId, CancellationToken ct = default);
    Task StopAsync(string jobId, CancellationToken ct = default);
    Task ForceRecheckAsync(string jobId, CancellationToken ct = default);
    Task RemoveAsync(string jobId, bool deleteFiles, CancellationToken ct = default);
    Task SetFilePriorityAsync(string jobId, int fileIndex, TorrentFilePriority priority, CancellationToken ct = default);
    Task SetTorrentRateLimitsAsync(string jobId, long maxDownloadBytesPerSec, long maxUploadBytesPerSec, CancellationToken ct = default);
    Task SetGlobalRateLimitsAsync(long maxDownloadBytesPerSec, long maxUploadBytesPerSec, CancellationToken ct = default);
    Task UpdateOptionsAsync(TorrentEngineOptions options, CancellationToken ct = default);
    Task AddPeerAsync(string jobId, Uri peerUri, CancellationToken ct = default);
    Task BanPeerAsync(string jobId, string ipAddress, CancellationToken ct = default);
    Task MoveStorageAsync(string jobId, string newDirectory, CancellationToken ct = default);
    Task SetSequentialDownloadAsync(string jobId, bool enable, CancellationToken ct = default);
    Task ForceAnnounceAsync(string jobId, CancellationToken ct = default);
    Task AddTrackerAsync(string jobId, Uri trackerUri, CancellationToken ct = default);
    Task RemoveTrackerAsync(string jobId, Uri trackerUri, CancellationToken ct = default);
    Task<bool> WaitForMetadataAsync(string jobId, TimeSpan timeout, CancellationToken ct = default);
    Task<byte[]> CreateTorrentAsync(string path, string[]? announceUrls = null, string? comment = null, string? createdBy = null, int pieceSize = 0, bool isPrivate = false, CancellationToken ct = default);
    bool HasJob(string jobId);

    TorrentJobStats? GetJobStats(string jobId);
    IReadOnlyList<TorrentFileInfo> GetFiles(string jobId);
    IReadOnlyList<TorrentPeerInfo> GetPeers(string jobId);
    Task<IReadOnlyList<TorrentPeerInfo>> GetPeersAsync(string jobId, CancellationToken ct = default);
    IReadOnlyList<TorrentTrackerInfo> GetTrackers(string jobId);
    ReadOnlyMemory<byte> GetPieceBitField(string jobId);
    PieceState[] GetPieceStates(string jobId);
    byte[]? GetFastResumeData(string jobId);

    IReadOnlyList<TorrentLogEntry> GetRecentLogs(string? jobId = null, int maxCount = 200);
    Task ExportLogsAsync(string destinationFilePath, string? jobId = null, CancellationToken ct = default);

    event EventHandler<TorrentStateChangedEventArgs>? StateChanged;
    event EventHandler<TorrentMetadataReceivedEventArgs>? MetadataReceived;
    event EventHandler<TorrentPieceHashedEventArgs>? PieceHashed;
    event EventHandler<TorrentErrorEventArgs>? ErrorOccurred;
    event EventHandler<TorrentLogEventArgs>? LogReceived;
}
