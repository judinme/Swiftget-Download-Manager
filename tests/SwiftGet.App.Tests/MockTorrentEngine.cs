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

    public Task AddPeerAsync(string jobId, Uri peerUri, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

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

    public byte[]? GetFastResumeData(string jobId) => null;

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
