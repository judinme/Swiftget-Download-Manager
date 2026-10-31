using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SwiftGet.Core.Models;
using SwiftGet.Engine.Torrent;

namespace SwiftGet.App.Tests;

public sealed class MockTorrentEngine : ITorrentEngine
{
    public bool IsRunning { get; set; } = true;
    public TorrentEngineOptions Options { get; set; } = new();

    public Dictionary<string, TorrentJobStats> JobStats { get; } = [];
    public Dictionary<string, List<TorrentFileInfo>> Files { get; } = [];
    public Dictionary<string, List<TorrentPeerInfo>> Peers { get; } = [];
    public Dictionary<string, List<TorrentTrackerInfo>> Trackers { get; } = [];
    public Dictionary<string, byte[]> PieceBitFields { get; } = [];

    public List<string> StartedJobs { get; } = [];
    public List<string> StoppedJobs { get; } = [];
    public List<string> RecheckedJobs { get; } = [];
    public List<(string JobId, int FileIndex, TorrentFilePriority Priority)> FilePriorityChanges { get; } = [];
    public List<(string JobId, long Down, long Up)> TorrentRateLimits { get; } = [];
    public (long Down, long Up)? LastGlobalRateLimit { get; private set; }
    public TorrentEngineOptions? LastUpdatedOptions { get; private set; }
    public List<(string JobId, string IpAddress)> BannedPeers { get; } = [];

#pragma warning disable CS0067
    public event EventHandler<TorrentStateChangedEventArgs>? StateChanged;
    public event EventHandler<TorrentMetadataReceivedEventArgs>? MetadataReceived;
    public event EventHandler<TorrentPieceHashedEventArgs>? PieceHashed;
    public event EventHandler<TorrentErrorEventArgs>? ErrorOccurred;
#pragma warning restore CS0067

    public Task InitializeAsync(TorrentEngineOptions options, CancellationToken ct = default)
    {
        Options = options;
        return Task.CompletedTask;
    }

    public Task<string> AddMagnetAsync(string magnetUri, TorrentAddOptions options, CancellationToken ct = default)
    {
        return Task.FromResult(options.DownloadId);
    }

    public Task<string> AddTorrentFileAsync(byte[] torrentBytes, TorrentAddOptions options, CancellationToken ct = default)
    {
        return Task.FromResult(options.DownloadId);
    }

    public Task StartAsync(string jobId, CancellationToken ct = default)
    {
        StartedJobs.Add(jobId);
        return Task.CompletedTask;
    }

    public Task PauseAsync(string jobId, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public Task StopAsync(string jobId, CancellationToken ct = default)
    {
        StoppedJobs.Add(jobId);
        return Task.CompletedTask;
    }

    public Task ForceRecheckAsync(string jobId, CancellationToken ct = default)
    {
        RecheckedJobs.Add(jobId);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string jobId, bool deleteFiles, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public Task SetFilePriorityAsync(string jobId, int fileIndex, TorrentFilePriority priority, CancellationToken ct = default)
    {
        FilePriorityChanges.Add((jobId, fileIndex, priority));
        if (Files.TryGetValue(jobId, out var files) && fileIndex < files.Count)
        {
            files[fileIndex].Priority = priority;
        }
        return Task.CompletedTask;
    }

    public Task SetTorrentRateLimitsAsync(string jobId, long maxDownloadBytesPerSec, long maxUploadBytesPerSec, CancellationToken ct = default)
    {
        TorrentRateLimits.Add((jobId, maxDownloadBytesPerSec, maxUploadBytesPerSec));
        return Task.CompletedTask;
    }

    public Task SetGlobalRateLimitsAsync(long maxDownloadBytesPerSec, long maxUploadBytesPerSec, CancellationToken ct = default)
    {
        LastGlobalRateLimit = (maxDownloadBytesPerSec, maxUploadBytesPerSec);
        return Task.CompletedTask;
    }

    public Task UpdateOptionsAsync(TorrentEngineOptions options, CancellationToken ct = default)
    {
        Options = options;
        LastUpdatedOptions = options;
        return Task.CompletedTask;
    }

    public Task BanPeerAsync(string jobId, string ipAddress, CancellationToken ct = default)
    {
        BannedPeers.Add((jobId, ipAddress));
        return Task.CompletedTask;
    }

    public Task AddPeerAsync(string jobId, Uri peerUri, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public Task MoveStorageAsync(string jobId, string newDirectory, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetSequentialDownloadAsync(string jobId, bool enable, CancellationToken ct = default) => Task.CompletedTask;
    public Task ForceAnnounceAsync(string jobId, CancellationToken ct = default) => Task.CompletedTask;
    public Task AddTrackerAsync(string jobId, Uri trackerUri, CancellationToken ct = default) => Task.CompletedTask;
    public Task RemoveTrackerAsync(string jobId, Uri trackerUri, CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> WaitForMetadataAsync(string jobId, TimeSpan timeout, CancellationToken ct = default) => Task.FromResult(true);
    public Task<byte[]> CreateTorrentAsync(string path, string[]? announceUrls = null, string? comment = null, string? createdBy = null, int pieceSize = 0, bool isPrivate = false, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public bool HasJob(string jobId) => JobStats.ContainsKey(jobId);

    public TorrentJobStats? GetJobStats(string jobId)
    {
        return JobStats.TryGetValue(jobId, out var s) ? s : null;
    }

    public IReadOnlyList<TorrentFileInfo> GetFiles(string jobId)
    {
        return Files.TryGetValue(jobId, out var list) ? list : Array.Empty<TorrentFileInfo>();
    }

    public IReadOnlyList<TorrentPeerInfo> GetPeers(string jobId)
    {
        return Peers.TryGetValue(jobId, out var list) ? list : Array.Empty<TorrentPeerInfo>();
    }

    public IReadOnlyList<TorrentTrackerInfo> GetTrackers(string jobId)
    {
        return Trackers.TryGetValue(jobId, out var list) ? list : Array.Empty<TorrentTrackerInfo>();
    }

    public ReadOnlyMemory<byte> GetPieceBitField(string jobId)
    {
        return PieceBitFields.TryGetValue(jobId, out var bytes) ? bytes : ReadOnlyMemory<byte>.Empty;
    }

    public Task<IReadOnlyList<TorrentPeerInfo>> GetPeersAsync(string jobId, CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<TorrentPeerInfo>>(GetPeers(jobId));
    }

    public PieceState[] GetPieceStates(string jobId)
    {
        if (PieceBitFields.TryGetValue(jobId, out var bytes) && bytes != null && bytes.Length > 0)
        {
            var states = new PieceState[bytes.Length * 8];
            for (int i = 0; i < states.Length; i++)
            {
                int byteIdx = i / 8;
                int bitIdx = 7 - (i % 8);
                bool has = (bytes[byteIdx] & (1 << bitIdx)) != 0;
                states[i] = has ? PieceState.Downloaded : PieceState.Missing;
            }
            return states;
        }
        return Array.Empty<PieceState>();
    }

    public byte[]? GetFastResumeData(string jobId) => null;

    public List<TorrentLogEntry> Logs { get; } = new();

    public IReadOnlyList<TorrentLogEntry> GetRecentLogs(string? jobId = null, int maxCount = 200)
    {
        IEnumerable<TorrentLogEntry> q = Logs;
        if (!string.IsNullOrWhiteSpace(jobId))
        {
            q = q.Where(e => string.IsNullOrWhiteSpace(e.JobId) || string.Equals(e.JobId, jobId, StringComparison.OrdinalIgnoreCase));
        }
        return q.TakeLast(maxCount).ToList();
    }

    public Task ExportLogsAsync(string destinationFilePath, string? jobId = null, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public event EventHandler<TorrentLogEventArgs>? LogReceived;

    public void AddLog(string level, string category, string message, string? jobId = null)
    {
        var entry = new TorrentLogEntry
        {
            Level = level,
            Category = category,
            Message = message,
            JobId = jobId
        };
        Logs.Add(entry);
        LogReceived?.Invoke(this, new TorrentLogEventArgs(entry));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class MockTorrentAssociationManager : ITorrentAssociationManager
{
    public bool IsSupported { get; set; } = true;
    public bool MagnetRegistered { get; set; } = true;
    public bool TorrentFileRegistered { get; set; } = true;
    public bool RegisterCalled { get; private set; }
    public bool UnregisterCalled { get; private set; }
    public bool OpenDefaultAppsCalled { get; private set; }

    public bool IsMagnetRegistered(string? executablePath = null) => MagnetRegistered;
    public bool IsTorrentFileRegistered() => TorrentFileRegistered;

    public bool RegisterAssociations(string? executablePath = null)
    {
        RegisterCalled = true;
        MagnetRegistered = true;
        TorrentFileRegistered = true;
        return true;
    }

    public bool UnregisterAssociations()
    {
        UnregisterCalled = true;
        MagnetRegistered = false;
        TorrentFileRegistered = false;
        return true;
    }

    public bool OpenDefaultAppsSettings()
    {
        OpenDefaultAppsCalled = true;
        return true;
    }
}
