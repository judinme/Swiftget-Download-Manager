using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MonoTorrent.Client;
using MonoTorrent.Connections;
using SwiftGet.Core.Models;
using SwiftGet.Engine.IO;

namespace SwiftGet.Engine.Torrent;

public sealed class MonoTorrentEngine : ITorrentEngine
{
    private readonly ILogger<MonoTorrentEngine> _logger;
    private readonly ConcurrentDictionary<string, TorrentEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private ClientEngine? _engine;
    private TorrentEngineOptions _options = new();
    private bool _isDisposed;
    private readonly object _stateLock = new();

    public bool IsRunning => _engine != null && !_engine.Disposed;
    public TorrentEngineOptions Options => _options;

    public event EventHandler<TorrentStateChangedEventArgs>? StateChanged;
    public event EventHandler<TorrentMetadataReceivedEventArgs>? MetadataReceived;
    public event EventHandler<TorrentPieceHashedEventArgs>? PieceHashed;
    public event EventHandler<TorrentErrorEventArgs>? ErrorOccurred;

    public MonoTorrentEngine(ILogger<MonoTorrentEngine>? logger = null)
    {
        _logger = logger ?? NullLogger<MonoTorrentEngine>.Instance;
    }

    public async Task InitializeAsync(TorrentEngineOptions options, CancellationToken ct = default)
    {
        lock (_stateLock)
        {
            if (_engine != null)
            {
                throw new InvalidOperationException("Torrent engine has already been initialized.");
            }
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        var cacheDir = string.IsNullOrWhiteSpace(options.CacheDirectory)
            ? Path.Combine(Path.GetTempPath(), "SwiftGet_Torrents_Cache")
            : options.CacheDirectory;
        Directory.CreateDirectory(cacheDir);

        var settingsBuilder = new EngineSettingsBuilder
        {
            CacheDirectory = cacheDir,
            MaximumConnections = options.MaxConnections > 0 ? options.MaxConnections : 150,
            MaximumDownloadRate = options.GlobalMaxDownloadRateBytesPerSec > 0 ? (int)options.GlobalMaxDownloadRateBytesPerSec : 0,
            MaximumUploadRate = options.GlobalMaxUploadRateBytesPerSec > 0 ? (int)options.GlobalMaxUploadRateBytesPerSec : 0,
            AllowedEncryption = options.PreferEncryption
                ? [EncryptionType.PlainText, EncryptionType.RC4Header, EncryptionType.RC4Full]
                : [EncryptionType.PlainText],
            AllowLocalPeerDiscovery = options.EnableLocalPeerDiscovery,
            AllowPortForwarding = options.EnableUpnp
        };

        var listenPort = options.ListenPort > 0 ? options.ListenPort : 0;
        var bindIp = options.BoundInterfaceAddress ?? IPAddress.Any;

        settingsBuilder.ListenEndPoints = new Dictionary<string, IPEndPoint>
        {
            { "ipv4", new IPEndPoint(bindIp, listenPort) }
        };

        if (options.EnableDht)
        {
            settingsBuilder.DhtEndPoint = new IPEndPoint(bindIp, listenPort);
        }

        if (options.BoundInterfaceAddress != null)
        {
            NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        }

        _engine = new ClientEngine(settingsBuilder.ToSettings());
        await _engine.StartAllAsync().ConfigureAwait(false);
        _logger.LogInformation("MonoTorrent engine initialized and started on port {Port}.", listenPort);
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        // VPN Kill-Switch check: if bound IP is no longer present on any active UP network interface, stop all active torrents
        if (_options.BoundInterfaceAddress == null || _engine == null) return;

        try
        {
            var isBoundIpAlive = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up)
                .SelectMany(ni => ni.GetIPProperties().UnicastAddresses)
                .Any(ua => ua.Address.Equals(_options.BoundInterfaceAddress));

            if (!isBoundIpAlive)
            {
                _logger.LogWarning("VPN Kill-Switch Triggered: Bound interface {Address} is no longer active. Pausing all torrents.", _options.BoundInterfaceAddress);
                foreach (var entry in _entries.Values)
                {
                    _ = entry.Manager.PauseAsync();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while checking bound interface status during NetworkAddressChanged event.");
        }
    }

    public async Task<string> AddMagnetAsync(string magnetUri, TorrentAddOptions options, CancellationToken ct = default)
    {
        EnsureEngineReady();

        if (string.IsNullOrWhiteSpace(magnetUri))
        {
            throw new ArgumentException("Magnet URI cannot be empty.", nameof(magnetUri));
        }

        var magnet = MonoTorrent.MagnetLink.Parse(magnetUri);

        // Tracker safety validation on announce URLs inside magnet link
        if (magnet.AnnounceUrls != null)
        {
            var safeAnnounces = new List<string>();
            foreach (var announce in magnet.AnnounceUrls)
            {
                if (TrackerSafetyValidator.IsSafeTrackerUrl(announce, _options.AllowLocalTrackers, out var reason))
                {
                    safeAnnounces.Add(announce);
                }
                else
                {
                    _logger.LogWarning("Blocked unsafe tracker URL '{Url}' in magnet: {Reason}", announce, reason);
                }
            }
            magnet = new MonoTorrent.MagnetLink(magnet.InfoHashes, magnet.Name, safeAnnounces, magnet.Webseeds, magnet.Size);
            if (magnet.AnnounceUrls.Count == 0)
            {
                _logger.LogWarning("All tracker URLs in magnet were unsafe and filtered out. Download will rely on DHT and PEX.");
            }
        }

        var saveDir = string.IsNullOrWhiteSpace(options.SaveDirectory) ? _options.SaveDirectory : options.SaveDirectory;
        Directory.CreateDirectory(saveDir);

        var torrentSettings = new TorrentSettingsBuilder
        {
            MaximumConnections = 50,
            MaximumDownloadRate = 0,
            MaximumUploadRate = 0
        }.ToSettings();

        var manager = await _engine!.AddAsync(magnet, saveDir, torrentSettings).ConfigureAwait(false);

        var entry = new TorrentEntry(options.DownloadId, manager, options, saveDir);
        _entries[options.DownloadId] = entry;

        WireManagerEvents(entry);

        if (options.StartImmediately)
        {
            await manager.StartAsync().ConfigureAwait(false);
        }

        return options.DownloadId;
    }

    public async Task<string> AddTorrentFileAsync(byte[] torrentBytes, TorrentAddOptions options, CancellationToken ct = default)
    {
        EnsureEngineReady();

        if (torrentBytes == null || torrentBytes.Length == 0)
        {
            throw new ArgumentException("Torrent bytes cannot be empty.", nameof(torrentBytes));
        }

        if (torrentBytes.Length > 10 * 1024 * 1024)
        {
            throw new InvalidOperationException("Torrent metadata file size exceeds safety limit of 10 MB.");
        }

        var torrent = MonoTorrent.Torrent.Load(torrentBytes);

        if (torrent.PieceCount > 1_000_000)
        {
            throw new InvalidOperationException($"Torrent piece count {torrent.PieceCount} exceeds maximum safety limit of 1,000,000 pieces.");
        }

        if (torrent.Size > 100L * 1024 * 1024 * 1024)
        {
            throw new InvalidOperationException($"Torrent total size {torrent.Size} bytes exceeds maximum safety limit of 100 GiB.");
        }

        if (torrent.Files.Count > 10000)
        {
            throw new InvalidOperationException($"Torrent file contains {torrent.Files.Count} files, exceeding maximum safety limit of 10,000 files.");
        }

        // Validate and sanitize every file relative path
        foreach (var file in torrent.Files)
        {
            _ = TorrentPathSanitizer.SanitizeRelativePath(file.Path);
        }

        var saveDir = string.IsNullOrWhiteSpace(options.SaveDirectory) ? _options.SaveDirectory : options.SaveDirectory;
        Directory.CreateDirectory(saveDir);

        var torrentSettingsBuilder = new TorrentSettingsBuilder
        {
            MaximumConnections = 50,
            MaximumDownloadRate = 0,
            MaximumUploadRate = 0
        };

        // BEP 27: Private torrent rule
        if (torrent.IsPrivate)
        {
            _logger.LogInformation("Torrent '{Name}' is marked private (BEP 27). DHT, PEX, and LSD disabled for this transfer.", torrent.Name);
        }

        var manager = await _engine!.AddAsync(torrent, saveDir, torrentSettingsBuilder.ToSettings()).ConfigureAwait(false);

        // Tracker safety validation on announce URLs inside .torrent
        if (manager.TrackerManager != null)
        {
            var unsafeTrackers = new List<MonoTorrent.Trackers.ITracker>();
            foreach (var tier in manager.TrackerManager.Tiers)
            {
                foreach (var tracker in tier.Trackers)
                {
                    if (!TrackerSafetyValidator.IsSafeTrackerUrl(tracker.Uri.OriginalString, _options.AllowLocalTrackers, out var reason))
                    {
                        _logger.LogWarning("Blocked unsafe tracker URL '{Url}' in .torrent: {Reason}", tracker.Uri, reason);
                        unsafeTrackers.Add(tracker);
                    }
                }
            }

            foreach (var tracker in unsafeTrackers)
            {
                await manager.TrackerManager.RemoveTrackerAsync(tracker).ConfigureAwait(false);
            }

            if (manager.TrackerManager.Tiers.All(t => t.Trackers.Count == 0))
            {
                _logger.LogWarning("All tracker URLs in .torrent were unsafe and filtered out. Download will rely on DHT and PEX.");
            }
        }

        // Apply selective file priorities if specified
        if (options.SelectedFileIndices != null && options.SelectedFileIndices.Count > 0)
        {
            var selectedSet = new HashSet<int>(options.SelectedFileIndices);
            for (int i = 0; i < manager.Files.Count; i++)
            {
                if (!selectedSet.Contains(i))
                {
                    await manager.SetFilePriorityAsync(manager.Files[i], MonoTorrent.Priority.DoNotDownload).ConfigureAwait(false);
                }
                else if (options.FilePriorities != null && options.FilePriorities.TryGetValue(i, out var prio))
                {
                    await manager.SetFilePriorityAsync(manager.Files[i], MapPriority(prio)).ConfigureAwait(false);
                }
                else
                {
                    await manager.SetFilePriorityAsync(manager.Files[i], MonoTorrent.Priority.Normal).ConfigureAwait(false);
                }
            }
        }

        var entry = new TorrentEntry(options.DownloadId, manager, options, saveDir);
        _entries[options.DownloadId] = entry;

        WireManagerEvents(entry);

        if (options.StartImmediately)
        {
            await manager.StartAsync().ConfigureAwait(false);
        }

        return options.DownloadId;
    }

    private void WireManagerEvents(TorrentEntry entry)
    {
        var manager = entry.Manager;
        var jobId = entry.JobId;

        manager.TorrentStateChanged += (sender, args) =>
        {
            var oldStatus = MapTorrentState(args.OldState, manager.Complete);
            var newStatus = MapTorrentState(args.NewState, manager.Complete);

            if (oldStatus != newStatus)
            {
                StateChanged?.Invoke(this, new TorrentStateChangedEventArgs(jobId, oldStatus, newStatus));
            }

            if (args.NewState == TorrentState.Error)
            {
                var errMessage = manager.Error?.Exception?.Message ?? "Torrent encountered an unhandled error state.";
                ErrorOccurred?.Invoke(this, new TorrentErrorEventArgs(jobId, errMessage, manager.Error?.Exception));
            }

            // Check if metadata just completed loading for magnet
            if (args.OldState == TorrentState.Metadata && manager.HasMetadata)
            {
                DispatchMetadataReceived(jobId, manager);
            }
        };

        manager.PieceHashed += (sender, args) =>
        {
            int pieceCount = manager.Torrent?.PieceCount ?? (manager.Bitfield != null ? manager.Bitfield.Length : 0);
            PieceHashed?.Invoke(this, new TorrentPieceHashedEventArgs(jobId, args.PieceIndex, pieceCount, args.HashPassed));
        };
    }

    private void DispatchMetadataReceived(string jobId, TorrentManager manager)
    {
        var files = manager.Files.Select((f, idx) => new TorrentFileInfo
        {
            FileIndex = idx,
            RelativePath = f.Path,
            FileSizeBytes = f.Length,
            DownloadedBytes = f.BitField.AllTrue ? f.Length : (long)(f.Length * (f.BitField.PercentComplete / 100.0)),
            Priority = MapPriority(f.Priority),
            IsSelected = f.Priority != MonoTorrent.Priority.DoNotDownload
        }).ToList();

        var name = manager.Torrent?.Name ?? manager.MagnetLink.Name ?? "Torrent";
        var totalBytes = manager.Torrent?.Size ?? 0;
        var infoHash = manager.InfoHashes.V1OrV2.ToHex();

        MetadataReceived?.Invoke(this, new TorrentMetadataReceivedEventArgs(jobId, infoHash, name, totalBytes, files));
    }

    public async Task StartAsync(string jobId, CancellationToken ct = default)
    {
        var entry = GetEntryOrThrow(jobId);
        await entry.Manager.StartAsync().ConfigureAwait(false);
    }

    public async Task PauseAsync(string jobId, CancellationToken ct = default)
    {
        var entry = GetEntryOrThrow(jobId);
        await entry.Manager.PauseAsync().ConfigureAwait(false);
    }

    public async Task StopAsync(string jobId, CancellationToken ct = default)
    {
        var entry = GetEntryOrThrow(jobId);
        await entry.Manager.StopAsync().ConfigureAwait(false);
    }

    public async Task ForceRecheckAsync(string jobId, CancellationToken ct = default)
    {
        var entry = GetEntryOrThrow(jobId);
        var wasRunning = entry.Manager.State != TorrentState.Stopped;
        if (wasRunning)
        {
            await entry.Manager.StopAsync().ConfigureAwait(false);
        }
        await entry.Manager.HashCheckAsync(autoStart: wasRunning).ConfigureAwait(false);
    }

    public async Task RemoveAsync(string jobId, bool deleteFiles, CancellationToken ct = default)
    {
        if (!_entries.TryRemove(jobId, out var entry))
        {
            return;
        }

        if (_engine != null)
        {
            if (entry.Manager.State != TorrentState.Stopped)
            {
                await entry.Manager.StopAsync().ConfigureAwait(false);
            }
            await _engine.RemoveAsync(entry.Manager).ConfigureAwait(false);
        }

        if (deleteFiles && Directory.Exists(entry.SaveDirectory))
        {
            try
            {
                if (entry.Manager.Torrent != null && entry.Manager.Torrent.Files.Count == 1)
                {
                    try
                    {
                        var sanitizedRelative = TorrentPathSanitizer.SanitizeRelativePath(entry.Manager.Torrent.Files[0].Path);
                        var singleFilePath = TorrentPathSanitizer.GetSafeFullPath(entry.SaveDirectory, sanitizedRelative);
                        if (File.Exists(singleFilePath)) File.Delete(singleFilePath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Blocked unsafe single-file deletion on torrent remove for job {JobId}", jobId);
                    }
                }
                else if (entry.Manager.Torrent != null)
                {
                    try
                    {
                        var sanitizedName = FileNameSanitizer.Sanitize(entry.Manager.Torrent.Name, "torrent_files");
                        var payloadDir = TorrentPathSanitizer.GetSafeFullPath(entry.SaveDirectory, sanitizedName);
                        if (Directory.Exists(payloadDir)) Directory.Delete(payloadDir, true);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Blocked unsafe directory deletion on torrent remove for job {JobId}", jobId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to completely delete files for torrent job {JobId}", jobId);
            }
        }
    }

    public async Task SetFilePriorityAsync(string jobId, int fileIndex, TorrentFilePriority priority, CancellationToken ct = default)
    {
        var entry = GetEntryOrThrow(jobId);
        if (fileIndex >= 0 && fileIndex < entry.Manager.Files.Count)
        {
            await entry.Manager.SetFilePriorityAsync(entry.Manager.Files[fileIndex], MapPriority(priority)).ConfigureAwait(false);
        }
    }

    public async Task SetTorrentRateLimitsAsync(string jobId, long maxDownloadBytesPerSec, long maxUploadBytesPerSec, CancellationToken ct = default)
    {
        var entry = GetEntryOrThrow(jobId);
        var settings = new TorrentSettingsBuilder(entry.Manager.Settings)
        {
            MaximumDownloadRate = (int)maxDownloadBytesPerSec,
            MaximumUploadRate = (int)maxUploadBytesPerSec
        }.ToSettings();

        await entry.Manager.UpdateSettingsAsync(settings).ConfigureAwait(false);
    }

    public async Task SetGlobalRateLimitsAsync(long maxDownloadBytesPerSec, long maxUploadBytesPerSec, CancellationToken ct = default)
    {
        EnsureEngineReady();

        _options.GlobalMaxDownloadRateBytesPerSec = maxDownloadBytesPerSec;
        _options.GlobalMaxUploadRateBytesPerSec = maxUploadBytesPerSec;

        var settings = new EngineSettingsBuilder(_engine!.Settings)
        {
            MaximumDownloadRate = (int)maxDownloadBytesPerSec,
            MaximumUploadRate = (int)maxUploadBytesPerSec
        }.ToSettings();

        await _engine.UpdateSettingsAsync(settings).ConfigureAwait(false);
    }

    public async Task AddPeerAsync(string jobId, Uri peerUri, CancellationToken ct = default)
    {
        var entry = GetEntryOrThrow(jobId);
        await entry.Manager.AddPeerAsync(new MonoTorrent.PeerInfo(peerUri)).ConfigureAwait(false);
    }

    public TorrentJobStats? GetJobStats(string jobId)
    {
        if (!_entries.TryGetValue(jobId, out var entry)) return null;
        var m = entry.Manager;

        return new TorrentJobStats
        {
            JobId = jobId,
            InfoHash = m.InfoHashes.V1OrV2.ToHex(),
            Name = m.Torrent?.Name ?? m.MagnetLink.Name ?? "Torrent",
            State = MapTorrentState(m.State, m.Complete),
            ProgressPercentage = m.Progress,
            TotalBytes = m.Torrent?.Size ?? 0,
            DownloadedBytes = m.Monitor.DataBytesReceived,
            UploadedBytes = m.Monitor.DataBytesSent,
            DownloadSpeedBytesPerSec = m.Monitor.DownloadRate,
            UploadSpeedBytesPerSec = m.Monitor.UploadRate,
            ConnectedSeeds = 0,
            TotalSeeds = m.Peers.Seeds,
            ConnectedPeers = m.OpenConnections,
            TotalPeers = m.Peers.Available,
            PieceLength = m.Torrent?.PieceLength ?? 0,
            PieceCount = m.Torrent?.PieceCount ?? (m.Bitfield != null ? m.Bitfield.Length : 0),
            HasMetadata = m.HasMetadata
        };
    }

    public IReadOnlyList<TorrentFileInfo> GetFiles(string jobId)
    {
        if (!_entries.TryGetValue(jobId, out var entry)) return [];
        var m = entry.Manager;

        return m.Files.Select((f, idx) => new TorrentFileInfo
        {
            FileIndex = idx,
            RelativePath = TorrentPathSanitizer.SanitizeRelativePath(f.Path),
            FileSizeBytes = f.Length,
            DownloadedBytes = f.BitField.AllTrue ? f.Length : (long)(f.Length * (f.BitField.PercentComplete / 100.0)),
            Priority = MapPriority(f.Priority),
            IsSelected = f.Priority != MonoTorrent.Priority.DoNotDownload
        }).ToList();
    }

    public IReadOnlyList<TorrentPeerInfo> GetPeers(string jobId)
    {
        if (!_entries.TryGetValue(jobId, out var entry)) return [];
        var m = entry.Manager;

        var peers = m.GetPeersAsync().GetAwaiter().GetResult();
        return peers.Select(p => new TorrentPeerInfo
        {
            Client = string.IsNullOrWhiteSpace(p.ClientApp.ToString()) ? "Unknown Peer" : p.ClientApp.ToString(),
            IpAddress = p.Uri.Host,
            Port = p.Uri.Port,
            DownloadSpeedBytesPerSec = p.Monitor.DownloadRate,
            UploadSpeedBytesPerSec = p.Monitor.UploadRate,
            Flags = (p.IsSeeder ? "S" : "L") + (p.EncryptionType != EncryptionType.PlainText ? "E" : ""),
            IsEncrypted = p.EncryptionType != EncryptionType.PlainText,
            IsUtp = string.Equals(p.Uri.Scheme, "utp", StringComparison.OrdinalIgnoreCase)
        }).ToList();
    }

    public IReadOnlyList<TorrentTrackerInfo> GetTrackers(string jobId)
    {
        if (!_entries.TryGetValue(jobId, out var entry)) return [];
        var m = entry.Manager;

        var list = new List<TorrentTrackerInfo>();
        if (m.TrackerManager == null) return list;

        int tierIndex = 0;
        foreach (var tier in m.TrackerManager.Tiers)
        {
            foreach (var tracker in tier.Trackers)
            {
                list.Add(new TorrentTrackerInfo
                {
                    Tier = tierIndex,
                    AnnounceUrl = tracker.Uri.ToString(),
                    Status = tracker.Status.ToString(),
                    Seeds = 0,
                    Peers = 0,
                    LastAnnounce = DateTime.UtcNow,
                    ErrorMessage = tracker.WarningMessage
                });
            }
            tierIndex++;
        }
        return list;
    }

    public ReadOnlyMemory<byte> GetPieceBitField(string jobId)
    {
        if (!_entries.TryGetValue(jobId, out var entry)) return ReadOnlyMemory<byte>.Empty;
        var bf = entry.Manager.Bitfield;
        if (bf == null || bf.Length == 0) return ReadOnlyMemory<byte>.Empty;

        var bytes = new byte[(bf.Length + 7) / 8];
        for (int i = 0; i < bf.Length; i++)
        {
            if (bf[i])
            {
                bytes[i / 8] |= (byte)(1 << (7 - (i % 8)));
            }
        }
        return bytes;
    }

    public byte[]? GetFastResumeData(string jobId)
    {
        if (!_entries.TryGetValue(jobId, out var entry)) return null;
        if (!entry.Manager.HasMetadata) return null;

        try
        {
            var fastResume = entry.Manager.SaveFastResumeAsync().GetAwaiter().GetResult();
            return fastResume?.Encode();
        }
        catch
        {
            return null;
        }
    }

    private static DownloadStatus MapTorrentState(TorrentState state, bool isComplete)
    {
        return state switch
        {
            TorrentState.Hashing => DownloadStatus.Checking,
            TorrentState.Metadata => DownloadStatus.FetchingMetadata,
            TorrentState.Downloading => DownloadStatus.Downloading,
            TorrentState.Seeding => DownloadStatus.Seeding,
            TorrentState.Paused => DownloadStatus.Paused,
            TorrentState.Stopped => isComplete ? DownloadStatus.Completed : DownloadStatus.Paused,
            TorrentState.Error => DownloadStatus.Failed,
            TorrentState.Starting => DownloadStatus.Downloading,
            _ => DownloadStatus.Downloading
        };
    }

    private static MonoTorrent.Priority MapPriority(TorrentFilePriority priority) => priority switch
    {
        TorrentFilePriority.DoNotDownload => MonoTorrent.Priority.DoNotDownload,
        TorrentFilePriority.Lowest => MonoTorrent.Priority.Lowest,
        TorrentFilePriority.Low => MonoTorrent.Priority.Low,
        TorrentFilePriority.Normal => MonoTorrent.Priority.Normal,
        TorrentFilePriority.High => MonoTorrent.Priority.High,
        TorrentFilePriority.Highest => MonoTorrent.Priority.Highest,
        _ => MonoTorrent.Priority.Normal
    };

    private static TorrentFilePriority MapPriority(MonoTorrent.Priority priority) => priority switch
    {
        MonoTorrent.Priority.DoNotDownload => TorrentFilePriority.DoNotDownload,
        MonoTorrent.Priority.Lowest => TorrentFilePriority.Lowest,
        MonoTorrent.Priority.Low => TorrentFilePriority.Low,
        MonoTorrent.Priority.Normal => TorrentFilePriority.Normal,
        MonoTorrent.Priority.High => TorrentFilePriority.High,
        MonoTorrent.Priority.Highest => TorrentFilePriority.Highest,
        MonoTorrent.Priority.Immediate => TorrentFilePriority.Highest,
        _ => TorrentFilePriority.Normal
    };

    private void EnsureEngineReady()
    {
        if (_engine == null || _engine.Disposed)
        {
            throw new InvalidOperationException("Torrent engine is not initialized or running. Call InitializeAsync first.");
        }
    }

    private TorrentEntry GetEntryOrThrow(string jobId)
    {
        if (!_entries.TryGetValue(jobId, out var entry))
        {
            throw new KeyNotFoundException($"No active torrent found with job ID '{jobId}'.");
        }
        return entry;
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;

        if (_engine != null)
        {
            try
            {
                await _engine.StopAllAsync().ConfigureAwait(false);
                _engine.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping MonoTorrent engine during disposal.");
            }
            _engine = null;
        }

        _entries.Clear();
    }

    private sealed class TorrentEntry
    {
        public string JobId { get; }
        public TorrentManager Manager { get; }
        public TorrentAddOptions Options { get; }
        public string SaveDirectory { get; }

        public TorrentEntry(string jobId, TorrentManager manager, TorrentAddOptions options, string saveDirectory)
        {
            JobId = jobId;
            Manager = manager;
            Options = options;
            SaveDirectory = saveDirectory;
        }
    }
}
