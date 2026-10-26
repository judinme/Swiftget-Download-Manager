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
    Task AddPeerAsync(string jobId, Uri peerUri, CancellationToken ct = default);

    TorrentJobStats? GetJobStats(string jobId);
    IReadOnlyList<TorrentFileInfo> GetFiles(string jobId);
    IReadOnlyList<TorrentPeerInfo> GetPeers(string jobId);
    IReadOnlyList<TorrentTrackerInfo> GetTrackers(string jobId);
    ReadOnlyMemory<byte> GetPieceBitField(string jobId);
    byte[]? GetFastResumeData(string jobId);

    event EventHandler<TorrentStateChangedEventArgs>? StateChanged;
    event EventHandler<TorrentMetadataReceivedEventArgs>? MetadataReceived;
    event EventHandler<TorrentPieceHashedEventArgs>? PieceHashed;
    event EventHandler<TorrentErrorEventArgs>? ErrorOccurred;
}
