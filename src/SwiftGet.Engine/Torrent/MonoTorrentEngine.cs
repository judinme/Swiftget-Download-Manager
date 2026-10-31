using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.Connections;
using MonoTorrent.Dht;
using MonoTorrent.Trackers;
using SwiftGet.Core.Models;
using SwiftGet.Engine.IO;

namespace SwiftGet.Engine.Torrent;

public sealed class MonoTorrentEngine : ITorrentEngine
{
    private readonly ILogger<MonoTorrentEngine> _logger;
    private readonly ConcurrentDictionary<string, TorrentEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _lastSeenComplete = new(StringComparer.OrdinalIgnoreCase);
    private ClientEngine? _engine;
    private DhtEngine? _dhtEngine;
    private TorrentEngineOptions _options = new();
    private bool _isDisposed;
    private readonly object _stateLock = new();
    private readonly MonoTorrent.Client.BanList _banList = new();
    private readonly HashSet<string> _bannedIpStrings = new(StringComparer.OrdinalIgnoreCase);

    public static readonly string[] DefaultPublicTrackers =
    [
        "udp://tracker.opentrackr.org:1337/announce",
        "udp://open.tracker.cl:1337/announce",
        "udp://tracker.torrent.eu.org:451/announce",
        "udp://explodie.org:6969/announce",
        "udp://tracker.openbittorrent.com:6969/announce",
        "udp://tracker.dler.org:6969/announce",
        "udp://open.stealth.si:80/announce",
        "udp://exodus.desync.com:6969/announce",
        "udp://tracker.tiny-vps.com:6969/announce",
        "udp://tracker.theoks.net:6969/announce",
        "http://tracker.opentrackr.org:1337/announce",
        "udp://9.rarbg.to:2920/announce",
        "udp://p4p.arenabg.com:1337/announce"
    ];

    private static readonly char[] TrackerSeparators = ['\r', '\n'];

    public IReadOnlyList<string> GetEffectiveTrackers()
    {
        if (!string.IsNullOrWhiteSpace(_options?.DefaultTrackersText))
        {
            var lines = _options.DefaultTrackersText.Split(TrackerSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var valid = new List<string>();
            foreach (var line in lines)
            {
                if (TrackerSafetyValidator.IsSafeTrackerUrl(line, _options?.AllowLocalTrackers ?? false, out _))
                {
                    valid.Add(line);
                }
            }
            if (valid.Count > 0) return valid;
        }

        return DefaultPublicTrackers;
    }

    private static List<EncryptionType> MapEncryptionTypes(TorrentEncryptionMode mode)
    {
        return mode switch
        {
            TorrentEncryptionMode.RequireEncryption => [EncryptionType.RC4Header, EncryptionType.RC4Full],
            TorrentEncryptionMode.AllowPlaintext => [EncryptionType.PlainText, EncryptionType.RC4Header, EncryptionType.RC4Full],
            _ => [EncryptionType.RC4Header, EncryptionType.RC4Full, EncryptionType.PlainText]
        };
    }

    public static readonly (string Host, int Port)[] DefaultDhtBootstrapRouters =
    [
        ("router.bittorrent.com", 6881),
        ("router.utorrent.com", 6881),
        ("dht.transmissionbt.com", 6881),
        ("dht.aelitis.com", 6881),
        ("dht.libtorrent.org", 25401)
    ];

    public bool IsRunning => _engine != null && !_engine.Disposed;
    public TorrentEngineOptions Options => _options;

    private readonly TorrentLogBuffer _logBuffer = new(1000);
    private readonly CancellationTokenSource _watchdogCts = new();
    private Task? _watchdogTask;
    public event EventHandler<TorrentStateChangedEventArgs>? StateChanged;
    public event EventHandler<TorrentMetadataReceivedEventArgs>? MetadataReceived;
    public event EventHandler<TorrentPieceHashedEventArgs>? PieceHashed;
    public event EventHandler<TorrentErrorEventArgs>? ErrorOccurred;
    public event EventHandler<TorrentLogEventArgs>? LogReceived;

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
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwiftGet", "torrent_cache")
            : options.CacheDirectory;
        Directory.CreateDirectory(cacheDir);

        int maxConn = options.MaxConnections >= 200 ? options.MaxConnections : 500;
        var settingsBuilder = new EngineSettingsBuilder
        {
            CacheDirectory = cacheDir,
            AutoSaveLoadDhtCache = true,
            AutoSaveLoadFastResume = true,
            AutoSaveLoadMagnetLinkMetadata = true,
            MaximumConnections = maxConn,
            MaximumHalfOpenConnections = 60,
            DiskCacheBytes = 64 * 1024 * 1024,
            StaleRequestTimeout = TimeSpan.FromSeconds(20),
            MaximumOpenFiles = 100,
            MaximumDownloadRate = options.GlobalMaxDownloadRateBytesPerSec > 0 ? (int)options.GlobalMaxDownloadRateBytesPerSec : 0,
            MaximumUploadRate = options.GlobalMaxUploadRateBytesPerSec > 0 ? (int)options.GlobalMaxUploadRateBytesPerSec : 0,
            AllowedEncryption = MapEncryptionTypes(options.EncryptionMode),
            AllowLocalPeerDiscovery = options.EnableLocalPeerDiscovery,
            AllowPortForwarding = options.EnableUpnp,
            UsePartialFiles = options.UsePartialFiles
        };

        var listenPort = options.ListenPort > 0 ? options.ListenPort : 52138;
        if (options.RandomizePortOnStartup)
        {
            listenPort = new Random().Next(49152, 65535);
        }
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

        try
        {
            MonoTorrent.Logging.LoggerFactory.Register(name => new MonoTorrentLoggerBridge(name, this));
        }
        catch { }

        var factories = Factories.Default.WithDhtCreator(() =>
        {
            var dht = new DhtEngine();
            _dhtEngine = dht;
            dht.PeersFound += (s, e) =>
            {
                var hashHex = e.InfoHash.ToHex();
                var shortHash = hashHex[..Math.Min(8, hashHex.Length)];
                var matched = _entries.Values.FirstOrDefault(ent => ent.Manager.InfoHashes.Contains(e.InfoHash));
                LogTorrent("INF", "DHT", $"Discovered {e.Peers.Count} peers for infohash {shortHash}...", matched?.JobId);
            };
            dht.StateChanged += (s, e) =>
            {
                LogTorrent("INF", "DHT", $"DHT engine state changed to {dht.State}.");
            };
            return dht;
        });

        try
        {
            _engine = new ClientEngine(settingsBuilder.ToSettings(), factories);
            HookEngineEvents(_engine);
            await _engine.StartAllAsync().ConfigureAwait(false);
            _ = BootstrapDhtRoutersAsync();
            LogTorrent("INF", "ENGINE", $"MonoTorrent engine initialized and listening on port {listenPort} (DHT: {options.EnableDht}, Encryption: {options.EncryptionMode}, UPnP: {options.EnableUpnp}).");
            _logger.LogInformation("MonoTorrent engine initialized and started on port {Port}.", listenPort);
        }
        catch (Exception ex)
        {
            // Port collision or network bind failure fallback: dynamically assign port 0
            _logger.LogWarning(ex, "Port {Port} failed; falling back to dynamic port 0.", listenPort);
            settingsBuilder.ListenEndPoints = new Dictionary<string, IPEndPoint>
            {
                { "ipv4", new IPEndPoint(bindIp, 0) }
            };
            if (options.EnableDht)
            {
                settingsBuilder.DhtEndPoint = new IPEndPoint(bindIp, 0);
            }
            _engine = new ClientEngine(settingsBuilder.ToSettings(), factories);
            HookEngineEvents(_engine);
            await _engine.StartAllAsync().ConfigureAwait(false);
            _ = BootstrapDhtRoutersAsync();
            LogTorrent("WRN", "ENGINE", $"Port {listenPort} failed; engine started on dynamic port fallback.");
            _logger.LogInformation("MonoTorrent engine initialized and started on dynamic port.");
        }
    }

    private async Task BootstrapDhtRoutersAsync()
    {
        if (_dhtEngine == null) return;
        await Task.Delay(1000).ConfigureAwait(false);

        var random = new Random();
        var compactNodes = new List<ReadOnlyMemory<byte>>();

        foreach (var (host, port) in DefaultDhtBootstrapRouters)
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
                foreach (var ip in addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork))
                {
                    byte[] buffer = new byte[26];
                    random.NextBytes(buffer.AsSpan(0, 20));
                    ip.GetAddressBytes().CopyTo(buffer.AsSpan(20, 4));
                    BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(24, 2), (ushort)port);
                    compactNodes.Add(buffer);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not resolve DHT bootstrap router {Host}:{Port}", host, port);
            }
        }

        if (compactNodes.Count > 0 && _dhtEngine != null)
        {
            try
            {
                _dhtEngine.Add(compactNodes);
                _logger.LogInformation("Injected {Count} bootstrap nodes into DHT engine.", compactNodes.Count);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to inject DHT bootstrap nodes.");
            }
        }
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
        var safeAnnounces = new List<string>();
        if (magnet.AnnounceUrls != null)
        {
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
        }

        // Always ensure reliable public trackers are appended so the magnet can swarm even if original trackers are dead
        if (!_options.AllowLocalTrackers)
        {
            foreach (var tr in GetEffectiveTrackers())
            {
                if (!safeAnnounces.Contains(tr, StringComparer.OrdinalIgnoreCase))
                {
                    safeAnnounces.Add(tr);
                }
            }
        }

        // For hybrid v1/v2 torrents, standard public swarms use v1 (SHA-1) pieces.
        // If a hybrid torrent is registered with both v1 and v2 hashes, MonoTorrent enters
        // PieceHashesMode requesting Merkle hash layers that typical v1 peers do not implement,
        // causing downloads to stall at 25 B/s. Pinning to v1 when available connects directly
        // to the active data swarm where pieces download immediately at full line speed.
        var effectiveInfoHashes = magnet.InfoHashes.V1 != null
            ? MonoTorrent.InfoHashes.FromV1(magnet.InfoHashes.V1)
            : magnet.InfoHashes;

        magnet = new MonoTorrent.MagnetLink(effectiveInfoHashes, magnet.Name, safeAnnounces, magnet.Webseeds, magnet.Size);
        if (magnet.AnnounceUrls == null || magnet.AnnounceUrls.Count == 0)
        {
            _logger.LogWarning("All tracker URLs in magnet were unsafe and filtered out. Download will rely on DHT and PEX.");
        }

        var saveDir = string.IsNullOrWhiteSpace(options.SaveDirectory) ? _options.SaveDirectory : options.SaveDirectory;
        Directory.CreateDirectory(saveDir);

        int maxConnPerTorrent = _options.MaxConnectionsPerTorrent >= 100 ? _options.MaxConnectionsPerTorrent : 200;
        var torrentSettings = new TorrentSettingsBuilder
        {
            MaximumConnections = maxConnPerTorrent,
            UploadSlots = 20,
            MaximumDownloadRate = 0,
            MaximumUploadRate = 0
        }.ToSettings();

        TorrentManager manager;
        var existingManager = _engine!.Torrents.FirstOrDefault(t => t.InfoHashes == effectiveInfoHashes || (effectiveInfoHashes.V1 != null && t.InfoHashes.V1 == effectiveInfoHashes.V1));
        if (existingManager != null)
        {
            manager = existingManager;
            if (!string.Equals(manager.SavePath, saveDir, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    bool wasRunning = manager.State != TorrentState.Stopped;
                    if (wasRunning) await manager.StopAsync().ConfigureAwait(false);
                    await manager.MoveFilesAsync(saveDir, true).ConfigureAwait(false);
                    if (wasRunning) await manager.StartAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not move existing manager files to {SaveDir}", saveDir);
                }
            }

            var staleKeys = _entries.Where(kv => kv.Value.Manager == existingManager).Select(kv => kv.Key).ToList();
            foreach (var key in staleKeys)
            {
                _entries.TryRemove(key, out _);
            }
        }
        else
        {
            manager = await _engine!.AddAsync(magnet, saveDir, torrentSettings).ConfigureAwait(false);
        }

        LogTorrent("INF", "ENGINE", $"Registered magnet for '{options.DownloadId}' (InfoHash: {magnet.InfoHashes.V1OrV2.ToHex()}, Trackers: {safeAnnounces.Count})", options.DownloadId);

        // Add public fallback trackers as distinct tiers so each announces in parallel
        if (manager.TrackerManager != null)
        {
            foreach (var tr in GetEffectiveTrackers())
            {
                if (Uri.TryCreate(tr, UriKind.Absolute, out var trackerUri))
                {
                    bool exists = manager.TrackerManager.Tiers.Any(t => t.Trackers.Any(tk => tk.Uri == trackerUri));
                    if (!exists)
                    {
                        try
                        {
                            await manager.TrackerManager.AddTrackerAsync(trackerUri).ConfigureAwait(false);
                        }
                        catch { }
                    }
                }
            }

            _ = manager.TrackerManager.AnnounceAsync(CancellationToken.None);
        }

        if (options.SequentialDownload)
        {
            try
            {
                await manager.ChangePickerAsync(Factories.Default.CreateStreamingPieceRequester()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not set streaming piece requester for {JobId}", options.DownloadId);
            }
        }

        var entry = new TorrentEntry(options.DownloadId, manager, options, saveDir);
        _entries[options.DownloadId] = entry;

        WireManagerEvents(entry);

        if (manager.HasMetadata)
        {
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

            entry.MetadataDispatched = true;
            DispatchMetadataReceived(options.DownloadId, manager);
        }
        else
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                    await manager.WaitForMetadataAsync(linked.Token).ConfigureAwait(false);
                    if (manager.HasMetadata && !entry.MetadataDispatched)
                    {
                        _logger.LogInformation("Metadata successfully resolved for magnet {JobId}", options.DownloadId);
                        entry.MetadataDispatched = true;

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

                        DispatchMetadataReceived(options.DownloadId, manager);

                        LogTorrent("INF", "METADATA", $"Metadata successfully resolved for magnet '{options.DownloadId}'. Size: {manager.Torrent?.Size ?? 0:N0} bytes, Files: {manager.Files.Count}.", options.DownloadId);

                        if (options.StartImmediately && manager.State != TorrentState.Downloading && manager.State != TorrentState.Starting && manager.State != TorrentState.Hashing)
                        {
                            try
                            {
                                await manager.StartAsync().ConfigureAwait(false);
                                LogTorrent("INF", "ENGINE", $"Started download for '{options.DownloadId}' following metadata resolution.", options.DownloadId);
                            }
                            catch (Exception startEx)
                            {
                                LogTorrent("ERR", "ENGINE", $"Failed to start torrent manager for {options.DownloadId}: {startEx.Message}", options.DownloadId);
                                _logger.LogWarning(startEx, "Failed to start torrent after metadata arrival for {JobId}", options.DownloadId);
                            }
                        }

                        if (manager.TrackerManager != null)
                        {
                            _ = manager.TrackerManager.AnnounceAsync(CancellationToken.None);
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogTorrent("WRN", "METADATA", $"Metadata probe task ended for '{options.DownloadId}': {ex.Message}", options.DownloadId);
                    _logger.LogDebug(ex, "WaitForMetadata background task exited for {JobId}", options.DownloadId);
                }
            }, ct);
        }

        if (options.StartImmediately &&
            manager.State is TorrentState.Stopped or TorrentState.Paused or TorrentState.Error)
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

        int maxConnPerTorrent = _options.MaxConnectionsPerTorrent >= 100 ? _options.MaxConnectionsPerTorrent : 200;
        var torrentSettingsBuilder = new TorrentSettingsBuilder
        {
            MaximumConnections = maxConnPerTorrent,
            UploadSlots = 20,
            MaximumDownloadRate = 0,
            MaximumUploadRate = 0
        };

        // BEP 27: Private torrent rule
        if (torrent.IsPrivate)
        {
            _logger.LogInformation("Torrent '{Name}' is marked private (BEP 27). DHT, PEX, and LSD disabled for this transfer.", torrent.Name);
        }

        TorrentManager manager;
        var existingManager = _engine!.Torrents.FirstOrDefault(t => t.InfoHashes == torrent.InfoHashes);
        if (existingManager != null)
        {
            manager = existingManager;
            if (!string.Equals(manager.SavePath, saveDir, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    bool wasRunning = manager.State != TorrentState.Stopped;
                    if (wasRunning) await manager.StopAsync().ConfigureAwait(false);
                    await manager.MoveFilesAsync(saveDir, true).ConfigureAwait(false);
                    if (wasRunning) await manager.StartAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not move existing manager files to {SaveDir}", saveDir);
                }
            }

            var staleKeys = _entries.Where(kv => kv.Value.Manager == existingManager).Select(kv => kv.Key).ToList();
            foreach (var key in staleKeys)
            {
                _entries.TryRemove(key, out _);
            }
        }
        else
        {
            manager = await _engine!.AddAsync(torrent, saveDir, torrentSettingsBuilder.ToSettings()).ConfigureAwait(false);
        }

        LogTorrent("INF", "ENGINE", $"Registered .torrent file for '{options.DownloadId}' (Name: {torrent.Name}, Size: {torrent.Size:N0} bytes, Files: {torrent.Files.Count})", options.DownloadId);

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

        // Multi-tier default public trackers if not private
        if (manager.TrackerManager != null && !torrent.IsPrivate)
        {
            foreach (var tr in GetEffectiveTrackers())
            {
                if (Uri.TryCreate(tr, UriKind.Absolute, out var trackerUri))
                {
                    bool exists = manager.TrackerManager.Tiers.Any(t => t.Trackers.Any(tk => tk.Uri == trackerUri));
                    if (!exists)
                    {
                        try
                        {
                            await manager.TrackerManager.AddTrackerAsync(trackerUri).ConfigureAwait(false);
                        }
                        catch { }
                    }
                }
            }

            _ = manager.TrackerManager.AnnounceAsync(CancellationToken.None);
        }

        if (options.SequentialDownload)
        {
            try
            {
                await manager.ChangePickerAsync(Factories.Default.CreateStreamingPieceRequester()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not set streaming piece requester for {JobId}", options.DownloadId);
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
            LogTorrent("INF", "STATE", $"State changed from {args.OldState} to {args.NewState}", jobId);

            var oldStatus = MapTorrentState(args.OldState, manager.Complete);
            var newStatus = MapTorrentState(args.NewState, manager.Complete);

            if (oldStatus != newStatus)
            {
                StateChanged?.Invoke(this, new TorrentStateChangedEventArgs(jobId, oldStatus, newStatus));
            }

            if (args.NewState == TorrentState.Error)
            {
                var errMessage = manager.Error?.Exception?.Message ?? "Torrent encountered an unhandled error state.";
                LogTorrent("ERR", "STATE", $"Error: {errMessage}", jobId);
                ErrorOccurred?.Invoke(this, new TorrentErrorEventArgs(jobId, errMessage, manager.Error?.Exception));
            }

            // Check if metadata just completed loading for magnet
            if (manager.HasMetadata && !entry.MetadataDispatched)
            {
                entry.MetadataDispatched = true;
                DispatchMetadataReceived(jobId, manager);
            }
        };

        manager.PieceHashed += (sender, args) =>
        {
            int pieceCount = manager.Torrent?.PieceCount ?? (manager.Bitfield != null ? manager.Bitfield.Length : 0);
            if (!args.HashPassed)
            {
                LogTorrent("WRN", "HASH", $"Piece #{args.PieceIndex} FAILED hash check! Re-requesting piece.", jobId);
            }
            else if (args.PieceIndex % 50 == 0 || args.PieceIndex == pieceCount - 1)
            {
                LogTorrent("DBG", "HASH", $"Piece #{args.PieceIndex}/{pieceCount} passed hash check ({manager.Bitfield?.PercentComplete ?? 0:F1}%).", jobId);
            }
            PieceHashed?.Invoke(this, new TorrentPieceHashedEventArgs(jobId, args.PieceIndex, pieceCount, args.HashPassed));
        };

        manager.PeerConnected += async (sender, args) =>
        {
            var enc = args.Peer.EncryptionType.ToString();
            var client = string.IsNullOrWhiteSpace(args.Peer.ClientApp.ToString()) ? "Unknown" : args.Peer.ClientApp.ToString();
            LogTorrent("INF", "PEER", $"Connected to {args.Peer.Uri} [{client}] (Direction: {args.Direction}, Enc: {enc})", jobId);
            try
            {
                var peers = await manager.GetPeersAsync().ConfigureAwait(false);
                entry.LatestPeers = peers.ToList();
            }
            catch { }
        };

        manager.PeerDisconnected += async (sender, args) =>
        {
            LogTorrent("INF", "PEER", $"Disconnected from {args.Peer.Uri}", jobId);
            try
            {
                var peers = await manager.GetPeersAsync().ConfigureAwait(false);
                entry.LatestPeers = peers.ToList();
            }
            catch { }
        };

        if (manager.TrackerManager != null)
        {
            manager.TrackerManager.AnnounceComplete += (sender, args) =>
            {
                if (args.Successful)
                {
                    int peerCount = args.Peers?.Values.Sum(p => p.Count) ?? 0;
                    LogTorrent("INF", "TRACKER", $"Announce succeeded for {args.Tracker.Uri}. Returned {peerCount} peers.", jobId);
                }
                else
                {
                    var reason = !string.IsNullOrWhiteSpace(args.Tracker.FailureMessage)
                        ? args.Tracker.FailureMessage
                        : (!string.IsNullOrWhiteSpace(args.Tracker.WarningMessage) ? args.Tracker.WarningMessage : args.Tracker.Status.ToString());
                    LogTorrent("WRN", "TRACKER", $"Announce failed for {args.Tracker.Uri}: {reason}", jobId);
                }
            };

            manager.TrackerManager.ScrapeComplete += (sender, args) =>
            {
                if (args.Successful)
                {
                    LogTorrent("DBG", "TRACKER", $"Scrape succeeded for {args.Tracker.Uri}", jobId);
                }
                else if (!string.IsNullOrWhiteSpace(args.Tracker.FailureMessage))
                {
                    LogTorrent("DBG", "TRACKER", $"Scrape failed for {args.Tracker.Uri}: {args.Tracker.FailureMessage}", jobId);
                }
            };
        }
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
        var entry = FindEntry(jobId);
        if (entry == null)
        {
            _logger.LogWarning("Cannot start torrent: no active job found for '{JobId}'.", jobId);
            return;
        }
        await entry.Manager.StartAsync().ConfigureAwait(false);
        if (entry.Manager.TrackerManager != null)
        {
            _ = entry.Manager.TrackerManager.AnnounceAsync(CancellationToken.None);
        }
        if (entry.Manager.CanUseDht)
        {
            _ = entry.Manager.DhtAnnounceAsync();
        }
    }

    public async Task PauseAsync(string jobId, CancellationToken ct = default)
    {
        var entry = FindEntry(jobId);
        if (entry == null)
        {
            _logger.LogWarning("Cannot pause torrent: no active job found for '{JobId}'.", jobId);
            return;
        }
        await entry.Manager.PauseAsync().ConfigureAwait(false);
    }

    public async Task StopAsync(string jobId, CancellationToken ct = default)
    {
        var entry = FindEntry(jobId);
        if (entry == null)
        {
            _logger.LogWarning("Cannot stop torrent: no active job found for '{JobId}'.", jobId);
            return;
        }
        await entry.Manager.StopAsync().ConfigureAwait(false);
    }

    public async Task ForceRecheckAsync(string jobId, CancellationToken ct = default)
    {
        var entry = FindEntry(jobId);
        if (entry == null)
        {
            _logger.LogWarning("Cannot force recheck: no active job found for '{JobId}'.", jobId);
            return;
        }
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

            var removeMode = deleteFiles
                ? RemoveMode.CacheDataOnly | RemoveMode.DownloadedDataOnly
                : RemoveMode.KeepAllData;

            await _engine.RemoveAsync(entry.Manager, removeMode).ConfigureAwait(false);
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

    public async Task UpdateOptionsAsync(TorrentEngineOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (_stateLock)
        {
            _options = options;
        }

        if (_engine == null) return;

        var bindIp = options.BoundInterfaceAddress ?? IPAddress.Any;
        var listenPort = options.ListenPort > 0 ? options.ListenPort : 52138;
        var cacheDir = string.IsNullOrWhiteSpace(options.CacheDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwiftGet", "torrent_cache")
            : options.CacheDirectory;

        int maxConn = options.MaxConnections >= 200 ? options.MaxConnections : 500;
        var settingsBuilder = new EngineSettingsBuilder
        {
            CacheDirectory = cacheDir,
            AutoSaveLoadDhtCache = true,
            AutoSaveLoadFastResume = true,
            AutoSaveLoadMagnetLinkMetadata = true,
            MaximumConnections = maxConn,
            MaximumHalfOpenConnections = 60,
            DiskCacheBytes = 64 * 1024 * 1024,
            StaleRequestTimeout = TimeSpan.FromSeconds(20),
            MaximumOpenFiles = 100,
            MaximumDownloadRate = options.GlobalMaxDownloadRateBytesPerSec > 0 ? (int)options.GlobalMaxDownloadRateBytesPerSec : 0,
            MaximumUploadRate = options.GlobalMaxUploadRateBytesPerSec > 0 ? (int)options.GlobalMaxUploadRateBytesPerSec : 0,
            AllowedEncryption = MapEncryptionTypes(options.EncryptionMode),
            AllowLocalPeerDiscovery = options.EnableLocalPeerDiscovery,
            AllowPortForwarding = options.EnableUpnp,
            UsePartialFiles = options.UsePartialFiles
        };

        settingsBuilder.ListenEndPoints = new Dictionary<string, IPEndPoint>
        {
            { "ipv4", new IPEndPoint(bindIp, listenPort) }
        };

        if (options.EnableDht)
        {
            settingsBuilder.DhtEndPoint = new IPEndPoint(bindIp, listenPort);
        }
        else
        {
            settingsBuilder.DhtEndPoint = null;
        }

        try
        {
            await _engine.UpdateSettingsAsync(settingsBuilder.ToSettings()).ConfigureAwait(false);
            _logger.LogInformation("MonoTorrent engine settings updated live (Port={Port}, Encryption={Encryption}, Conns={Conns}).",
                listenPort, options.EncryptionMode, options.MaxConnections);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply live engine settings update.");
        }

        // Apply per-torrent limits
        int maxPerTorrent = options.MaxConnectionsPerTorrent >= 100 ? options.MaxConnectionsPerTorrent : 200;
        lock (_stateLock)
        {
            foreach (var entry in _entries.Values)
            {
                try
                {
                    var tSettings = new TorrentSettingsBuilder(entry.Manager.Settings)
                    {
                        MaximumConnections = maxPerTorrent,
                        UploadSlots = 20
                    }.ToSettings();
                    _ = entry.Manager.UpdateSettingsAsync(tSettings);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to update per-torrent settings for {JobId}", entry.JobId);
                }
            }
        }

        if (options.EnableIpFilter && !string.IsNullOrWhiteSpace(options.IpFilterPath))
        {
            await LoadIpFilterAsync(options.IpFilterPath).ConfigureAwait(false);
        }
    }

    public Task<int> LoadIpFilterAsync(string filterPath)
    {
        if (string.IsNullOrWhiteSpace(filterPath) || !File.Exists(filterPath)) return Task.FromResult(0);
        try
        {
            var parser = new MonoTorrent.Client.BanListParser();
            using var stream = File.OpenRead(filterPath);
            var ranges = parser.Parse(stream).ToList();
            _banList.AddRange(ranges);
            _logger.LogInformation("Loaded {Count} IP filter ranges from {Path}.", ranges.Count, filterPath);
            return Task.FromResult(ranges.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load IP filter file from {Path}.", filterPath);
            return Task.FromResult(0);
        }
    }

    public Task BanPeerAsync(string jobId, string ipAddress, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ipAddress)) return Task.CompletedTask;
        if (IPAddress.TryParse(ipAddress, out var ip))
        {
            _banList.Add(ip);
            lock (_stateLock)
            {
                _bannedIpStrings.Add(ipAddress);
            }
            _logger.LogInformation("Peer IP {Ip} added to ban list.", ipAddress);
        }
        return Task.CompletedTask;
    }

    public async Task AddPeerAsync(string jobId, Uri peerUri, CancellationToken ct = default)
    {
        if (peerUri != null && IPAddress.TryParse(peerUri.Host, out var ip))
        {
            if (_banList.IsBanned(ip) || _bannedIpStrings.Contains(peerUri.Host))
            {
                _logger.LogWarning("Blocked connection to banned peer {PeerUri}", peerUri);
                return;
            }
        }

        var entry = GetEntryOrThrow(jobId);
        await entry.Manager.AddPeerAsync(new MonoTorrent.PeerInfo(peerUri!)).ConfigureAwait(false);
    }

    public TorrentJobStats? GetJobStats(string jobId)
    {
        var entry = FindEntry(jobId);
        if (entry == null) return null;
        var m = entry.Manager;

        int connectedSeeds = 0;
        int connectedPeers = m.OpenConnections;
        double availability = 0.0;

        var peerList = entry.LatestPeers;
        if (peerList != null && peerList.Count > 0)
        {
            connectedSeeds = peerList.Count(p => p.IsSeeder);
            if (connectedPeers == 0)
            {
                connectedPeers = peerList.Count;
            }

            int pieceCount = m.Torrent?.PieceCount ?? (m.Bitfield?.Length ?? 0);
            if (pieceCount > 0)
            {
                int[] copies = new int[pieceCount];
                foreach (var p in peerList)
                {
                    if (p.IsSeeder)
                    {
                        for (int i = 0; i < pieceCount; i++) copies[i]++;
                    }
                    else if (p.BitField != null)
                    {
                        for (int i = 0; i < Math.Min(pieceCount, p.BitField.Length); i++)
                        {
                            if (p.BitField[i]) copies[i]++;
                        }
                    }
                }

                int minCopies = int.MaxValue;
                for (int i = 0; i < pieceCount; i++)
                {
                    if (copies[i] < minCopies) minCopies = copies[i];
                }
                if (minCopies == int.MaxValue) minCopies = 0;

                int piecesWithMore = 0;
                for (int i = 0; i < pieceCount; i++)
                {
                    if (copies[i] > minCopies) piecesWithMore++;
                }

                availability = minCopies + ((double)piecesWithMore / pieceCount);
            }
        }
        else if (m.Peers != null)
        {
            connectedSeeds = m.Peers.Seeds;
            connectedPeers = m.OpenConnections;
            if (connectedSeeds > 0) availability = connectedSeeds;
        }

        if (availability >= 1.0 || m.Complete)
        {
            _lastSeenComplete[jobId] = DateTime.UtcNow;
        }

        _lastSeenComplete.TryGetValue(jobId, out var lastSeen);

        if (m.HasMetadata && !entry.MetadataDispatched)
        {
            entry.MetadataDispatched = true;
            DispatchMetadataReceived(jobId, m);
        }

        long totalBytes = m.Torrent?.Size ?? 0;
        long downloadedBytes;
        if (m.Complete && totalBytes > 0)
        {
            downloadedBytes = totalBytes;
        }
        else if (m.Bitfield != null && totalBytes > 0)
        {
            downloadedBytes = (long)(totalBytes * (m.Progress / 100.0));
            if (m.Monitor.DataBytesReceived > downloadedBytes)
            {
                downloadedBytes = Math.Min(totalBytes, m.Monitor.DataBytesReceived);
            }
        }
        else
        {
            downloadedBytes = m.Monitor.DataBytesReceived;
        }

        return new TorrentJobStats
        {
            JobId = jobId,
            InfoHash = m.InfoHashes.V1OrV2.ToHex(),
            Name = m.Torrent?.Name ?? m.MagnetLink.Name ?? "Torrent",
            State = MapTorrentState(m.State, m.Complete),
            ProgressPercentage = m.Progress,
            TotalBytes = totalBytes,
            DownloadedBytes = downloadedBytes,
            UploadedBytes = m.Monitor.DataBytesSent,
            DownloadSpeedBytesPerSec = m.Monitor.DownloadRate,
            UploadSpeedBytesPerSec = m.Monitor.UploadRate,
            ConnectedSeeds = connectedSeeds,
            TotalSeeds = m.Peers?.Seeds ?? 0,
            ConnectedPeers = connectedPeers,
            TotalPeers = m.Peers?.Available ?? 0,
            PieceLength = m.Torrent?.PieceLength ?? 0,
            PieceCount = m.Torrent?.PieceCount ?? (m.Bitfield != null ? m.Bitfield.Length : 0),
            HasMetadata = m.HasMetadata,
            Availability = Math.Round(availability, 2),
            LastSeenComplete = lastSeen
        };
    }

    public IReadOnlyList<TorrentFileInfo> GetFiles(string jobId)
    {
        var entry = FindEntry(jobId);
        if (entry == null) return [];
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
        var entry = FindEntry(jobId);
        if (entry == null) return [];
        var peers = entry.LatestPeers;
        if ((peers == null || peers.Count == 0) && entry.Manager.OpenConnections > 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var p = await entry.Manager.GetPeersAsync().ConfigureAwait(false);
                    entry.LatestPeers = p.ToList();
                }
                catch { }
            });
        }
        if (peers == null || peers.Count == 0) return Array.Empty<TorrentPeerInfo>();

        return peers
            .Where(p => p.Uri == null || !_bannedIpStrings.Contains(p.Uri.Host))
            .Select(p => new TorrentPeerInfo
        {
            Client = string.IsNullOrWhiteSpace(p.ClientApp.ToString()) ? "Unknown Peer" : p.ClientApp.ToString(),
            IpAddress = p.Uri?.Host ?? "Unknown",
            Port = p.Uri?.Port ?? 0,
            DownloadSpeedBytesPerSec = p.Monitor?.DownloadRate ?? 0,
            UploadSpeedBytesPerSec = p.Monitor?.UploadRate ?? 0,
            Flags = (p.IsSeeder ? "S" : "L") + (p.EncryptionType != EncryptionType.PlainText ? "E" : ""),
            IsEncrypted = p.EncryptionType != EncryptionType.PlainText,
            IsUtp = string.Equals(p.Uri?.Scheme, "utp", StringComparison.OrdinalIgnoreCase),
            ProgressPercentage = p.BitField?.PercentComplete ?? 0
        }).ToList();
    }

    public async Task<IReadOnlyList<TorrentPeerInfo>> GetPeersAsync(string jobId, CancellationToken ct = default)
    {
        var entry = FindEntry(jobId);
        if (entry == null) return [];
        var m = entry.Manager;

        var peers = await m.GetPeersAsync().ConfigureAwait(false);
        entry.LatestPeers = peers.ToList();
        return peers
            .Where(p => p.Uri == null || !_bannedIpStrings.Contains(p.Uri.Host))
            .Select(p => new TorrentPeerInfo
        {
            Client = string.IsNullOrWhiteSpace(p.ClientApp.ToString()) ? "Unknown Peer" : p.ClientApp.ToString(),
            IpAddress = p.Uri?.Host ?? "Unknown",
            Port = p.Uri?.Port ?? 0,
            DownloadSpeedBytesPerSec = p.Monitor?.DownloadRate ?? 0,
            UploadSpeedBytesPerSec = p.Monitor?.UploadRate ?? 0,
            Flags = (p.IsSeeder ? "S" : "L") + (p.EncryptionType != EncryptionType.PlainText ? "E" : ""),
            IsEncrypted = p.EncryptionType != EncryptionType.PlainText,
            IsUtp = string.Equals(p.Uri?.Scheme, "utp", StringComparison.OrdinalIgnoreCase),
            ProgressPercentage = p.BitField?.PercentComplete ?? 0
        }).ToList();
    }

    public IReadOnlyList<TorrentTrackerInfo> GetTrackers(string jobId)
    {
        var entry = FindEntry(jobId);
        if (entry == null) return [];
        var m = entry.Manager;

        var list = new List<TorrentTrackerInfo>();
        if (m.TrackerManager == null) return list;

        int tierIndex = 0;
        foreach (var tier in m.TrackerManager.Tiers)
        {
            foreach (var tracker in tier.Trackers)
            {
                string statusText;
                if (!string.IsNullOrWhiteSpace(tracker.FailureMessage))
                {
                    statusText = tracker.FailureMessage;
                }
                else if (!string.IsNullOrWhiteSpace(tracker.WarningMessage))
                {
                    statusText = tracker.WarningMessage;
                }
                else
                {
                    statusText = tracker.Status switch
                    {
                        MonoTorrent.Trackers.TrackerState.Ok => "Working",
                        MonoTorrent.Trackers.TrackerState.Connecting => "Announcing",
                        MonoTorrent.Trackers.TrackerState.Offline => "skipping tracker announce (unreachable)",
                        _ => tracker.Status.ToString()
                    };
                }

                DateTime? lastAnnounce = null;
                if (tracker.TimeSinceLastAnnounce > TimeSpan.Zero && tracker.TimeSinceLastAnnounce < TimeSpan.FromDays(365))
                {
                    lastAnnounce = DateTime.UtcNow - tracker.TimeSinceLastAnnounce;
                }

                DateTime? nextAnnounce = null;
                if (tracker.UpdateInterval > TimeSpan.Zero && tracker.UpdateInterval < TimeSpan.FromDays(365))
                {
                    if (tracker.TimeSinceLastAnnounce > TimeSpan.Zero && tracker.TimeSinceLastAnnounce < tracker.UpdateInterval)
                    {
                        nextAnnounce = DateTime.UtcNow + (tracker.UpdateInterval - tracker.TimeSinceLastAnnounce);
                    }
                    else
                    {
                        nextAnnounce = DateTime.UtcNow + tracker.UpdateInterval;
                    }
                }

                list.Add(new TorrentTrackerInfo
                {
                    Tier = tierIndex,
                    AnnounceUrl = tracker.Uri.ToString(),
                    Status = statusText,
                    Seeds = 0,
                    Peers = 0,
                    LastAnnounce = lastAnnounce,
                    NextAnnounce = nextAnnounce,
                    ErrorMessage = !string.IsNullOrWhiteSpace(tracker.FailureMessage) ? tracker.FailureMessage : tracker.WarningMessage
                });
            }
            tierIndex++;
        }
        return list;
    }

    public PieceState[] GetPieceStates(string jobId)
    {
        var entry = FindEntry(jobId);
        if (entry == null) return Array.Empty<PieceState>();
        var m = entry.Manager;
        var pieceCount = m.Torrent?.PieceCount ?? (m.Bitfield?.Length ?? 0);
        if (pieceCount <= 0) return Array.Empty<PieceState>();

        var result = new PieceState[pieceCount];
        var bf = m.Bitfield;
        bool isHashing = m.State == TorrentState.Hashing;

        for (int i = 0; i < pieceCount; i++)
        {
            if (bf != null && i < bf.Length && bf[i])
            {
                result[i] = PieceState.Downloaded;
            }
            else if (isHashing)
            {
                result[i] = PieceState.Verifying;
            }
            else
            {
                result[i] = PieceState.Missing;
            }
        }

        return result;
    }

    public ReadOnlyMemory<byte> GetPieceBitField(string jobId)
    {
        var entry = FindEntry(jobId);
        if (entry == null) return ReadOnlyMemory<byte>.Empty;
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
        var entry = FindEntry(jobId);
        if (entry == null) return null;
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

    public bool HasJob(string jobId) => FindEntry(jobId) != null;

    public async Task MoveStorageAsync(string jobId, string newDirectory, CancellationToken ct = default)
    {
        var entry = GetEntryOrThrow(jobId);
        Directory.CreateDirectory(newDirectory);
        bool wasRunning = entry.Manager.State != TorrentState.Stopped;
        if (wasRunning) await entry.Manager.StopAsync().ConfigureAwait(false);
        await entry.Manager.MoveFilesAsync(newDirectory, true).ConfigureAwait(false);
        if (wasRunning) await entry.Manager.StartAsync().ConfigureAwait(false);
        entry.SaveDirectory = newDirectory;
    }

    public async Task SetSequentialDownloadAsync(string jobId, bool enable, CancellationToken ct = default)
    {
        var entry = GetEntryOrThrow(jobId);
        if (enable)
        {
            await entry.Manager.ChangePickerAsync(Factories.Default.CreateStreamingPieceRequester()).ConfigureAwait(false);
        }
        else
        {
            await entry.Manager.ChangePickerAsync(Factories.Default.CreatePieceRequester()).ConfigureAwait(false);
        }
    }

    public async Task ForceAnnounceAsync(string jobId, CancellationToken ct = default)
    {
        var entry = GetEntryOrThrow(jobId);
        if (entry.Manager.TrackerManager != null)
        {
            await entry.Manager.TrackerManager.AnnounceAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task AddTrackerAsync(string jobId, Uri trackerUri, CancellationToken ct = default)
    {
        var entry = GetEntryOrThrow(jobId);
        if (entry.Manager.TrackerManager != null)
        {
            await entry.Manager.TrackerManager.AddTrackerAsync(trackerUri).ConfigureAwait(false);
        }
    }

    public async Task RemoveTrackerAsync(string jobId, Uri trackerUri, CancellationToken ct = default)
    {
        var entry = GetEntryOrThrow(jobId);
        if (entry.Manager.TrackerManager != null)
        {
            var tracker = entry.Manager.TrackerManager.Tiers
                .SelectMany(t => t.Trackers)
                .FirstOrDefault(tk => tk.Uri == trackerUri);
            if (tracker != null)
            {
                await entry.Manager.TrackerManager.RemoveTrackerAsync(tracker).ConfigureAwait(false);
            }
        }
    }

    public async Task<bool> WaitForMetadataAsync(string jobId, TimeSpan timeout, CancellationToken ct = default)
    {
        var entry = FindEntry(jobId);
        if (entry == null) return false;
        var manager = entry.Manager;
        if (manager.HasMetadata) return true;

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await manager.WaitForMetadataAsync(linkedCts.Token).ConfigureAwait(false);
            if (manager.HasMetadata && !entry.MetadataDispatched)
            {
                entry.MetadataDispatched = true;
                DispatchMetadataReceived(jobId, manager);
            }
            return manager.HasMetadata;
        }
        catch (OperationCanceledException)
        {
            return manager.HasMetadata;
        }
    }

    public async Task<byte[]> CreateTorrentAsync(string path, string[]? announceUrls = null, string? comment = null, string? createdBy = null, int pieceSize = 0, bool isPrivate = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path)))
        {
            throw new ArgumentException("Specified path does not exist for torrent creation.", nameof(path));
        }

        var creator = new TorrentCreator();
        creator.Private = isPrivate;
        if (pieceSize > 0)
        {
            creator.PieceLength = pieceSize;
        }
        if (!string.IsNullOrWhiteSpace(comment))
        {
            creator.Comment = comment;
        }
        if (!string.IsNullOrWhiteSpace(createdBy))
        {
            creator.CreatedBy = createdBy;
        }
        else
        {
            creator.CreatedBy = "SwiftGet 1.0";
        }

        if (announceUrls != null && announceUrls.Length > 0)
        {
            foreach (var url in announceUrls)
            {
                if (!string.IsNullOrWhiteSpace(url))
                {
                    creator.Announces.Add([url.Trim()]);
                }
            }
        }
        else
        {
            foreach (var tr in GetEffectiveTrackers())
            {
                creator.Announces.Add([tr]);
            }
        }

        var fileSource = new TorrentFileSource(path);
        var bencoded = await creator.CreateAsync(fileSource, ct).ConfigureAwait(false);
        return bencoded.Encode();
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

    private TorrentEntry? FindEntry(string jobIdOrInfoHash)
    {
        if (string.IsNullOrWhiteSpace(jobIdOrInfoHash)) return null;
        if (_entries.TryGetValue(jobIdOrInfoHash, out var entry)) return entry;
        return _entries.Values.FirstOrDefault(e =>
            string.Equals(e.JobId, jobIdOrInfoHash, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(e.Manager.InfoHashes.V1OrV2.ToHex(), jobIdOrInfoHash, StringComparison.OrdinalIgnoreCase));
    }

    private TorrentEntry GetEntryOrThrow(string jobId)
    {
        var entry = FindEntry(jobId);
        if (entry == null)
        {
            throw new KeyNotFoundException($"No active torrent found with job ID '{jobId}'.");
        }
        return entry;
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        try
        {
            _watchdogCts.Cancel();
            if (_watchdogTask != null)
            {
                await _watchdogTask.ConfigureAwait(false);
            }
        }
        catch { }
        finally
        {
            _watchdogCts.Dispose();
        }

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

    private void HookEngineEvents(ClientEngine engine)
    {
        engine.ConnectionManager.BanPeer += (s, e) =>
        {
            if (e.Peer.ConnectionUri != null && IPAddress.TryParse(e.Peer.ConnectionUri.Host, out var ip))
            {
                if (_banList.IsBanned(ip))
                {
                    e.BanPeer = true;
                    LogTorrent("WRN", "BAN", $"Banned peer connection attempt from {ip} dropped.");
                }
            }
        };

        if (_watchdogTask == null || _watchdogTask.IsCompleted)
        {
            _watchdogTask = RunSwarmWatchdogAsync(_watchdogCts.Token);
        }
    }

    private async Task RunSwarmWatchdogAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) break;
                if (_engine == null || _engine.Disposed) continue;

                foreach (var kvp in _entries)
                {
                    var entry = kvp.Value;
                    var manager = entry.Manager;

                    if (manager.State != TorrentState.Downloading)
                    {
                        entry.ConsecutiveStallTicks = 0;
                        continue;
                    }

                    long downloadRate = manager.Monitor.DownloadRate;
                    int openConnections = manager.OpenConnections;

                    // 1. Proactive Peer Replenishment:
                    // If download is slow (< 100 KB/s) or connected peers < 25, actively poll DHT & Trackers
                    if (downloadRate < 100_000 || openConnections < 25)
                    {
                        LogTorrent("DBG", "SWARM", $"Watchdog: Download rate {downloadRate / 1024.0:F1} KB/s (Peers: {openConnections}). Requesting fresh peers via DHT & Trackers...", entry.JobId);

                        if (manager.CanUseDht)
                        {
                            try { _ = manager.DhtAnnounceAsync(); } catch { }
                        }

                        if (manager.TrackerManager != null)
                        {
                            try { _ = manager.TrackerManager.AnnounceAsync(CancellationToken.None); } catch { }
                        }
                    }

                    // 2. Anti-Stall Recovery:
                    // If the torrent is actively downloading but stalled at < 1 KB/s for 40+ seconds:
                    if (downloadRate < 1024)
                    {
                        entry.ConsecutiveStallTicks++;
                        if (entry.ConsecutiveStallTicks >= 4) // 4 * 10s = 40 seconds of near-zero speed
                        {
                            entry.ConsecutiveStallTicks = 0;
                            LogTorrent("WRN", "SWARM", $"Watchdog: Torrent '{entry.JobId}' stalled at < 1 KB/s for 40s. Recycling socket pool to unblock choked connections...", entry.JobId);
                            try
                            {
                                await manager.PauseAsync().ConfigureAwait(false);
                                await Task.Delay(500, ct).ConfigureAwait(false);
                                await manager.StartAsync().ConfigureAwait(false);
                                if (manager.CanUseDht) _ = manager.DhtAnnounceAsync();
                                if (manager.TrackerManager != null) _ = manager.TrackerManager.AnnounceAsync(CancellationToken.None);
                            }
                            catch (Exception ex)
                            {
                                LogTorrent("ERR", "SWARM", $"Watchdog recovery failed for '{entry.JobId}': {ex.Message}", entry.JobId);
                            }
                        }
                    }
                    else
                    {
                        entry.ConsecutiveStallTicks = 0;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error in swarm watchdog loop");
            }
        }
    }

    public void LogTorrent(string level, string category, string message, string? jobId = null)
    {
        var entry = new TorrentLogEntry
        {
            TimestampUtc = DateTime.UtcNow,
            Level = level,
            Category = category,
            Message = message,
            JobId = jobId
        };

        _logBuffer.Add(entry);

        switch (level)
        {
            case "ERR":
                _logger.LogError("[{Category}] {Message} (Job: {JobId})", category, message, jobId ?? "Global");
                break;
            case "WRN":
                _logger.LogWarning("[{Category}] {Message} (Job: {JobId})", category, message, jobId ?? "Global");
                break;
            case "DBG":
                _logger.LogDebug("[{Category}] {Message} (Job: {JobId})", category, message, jobId ?? "Global");
                break;
            default:
                _logger.LogInformation("[{Category}] {Message} (Job: {JobId})", category, message, jobId ?? "Global");
                break;
        }

        try
        {
            LogReceived?.Invoke(this, new TorrentLogEventArgs(entry));
        }
        catch { }
    }

    public IReadOnlyList<TorrentLogEntry> GetRecentLogs(string? jobId = null, int maxCount = 200)
    {
        return _logBuffer.GetRecent(jobId, maxCount);
    }

    public Task ExportLogsAsync(string destinationFilePath, string? jobId = null, CancellationToken ct = default)
    {
        return _logBuffer.ExportToFileAsync(destinationFilePath, jobId, ct);
    }

    private sealed class MonoTorrentLoggerBridge : MonoTorrent.Logging.ILogger
    {
        private readonly MonoTorrentEngine _engine;
        private readonly string _shortCategory;

        public MonoTorrentLoggerBridge(string name, MonoTorrentEngine engine)
        {
            _engine = engine;
            int lastDot = name.LastIndexOf('.');
            _shortCategory = lastDot >= 0 && lastDot < name.Length - 1 ? name[(lastDot + 1)..] : name;
        }

        public void Info(string message) => _engine.LogTorrent("INF", _shortCategory, message);
        public void Debug(string message) => _engine.LogTorrent("DBG", _shortCategory, message);
        public void Error(string message) => _engine.LogTorrent("ERR", _shortCategory, message);
    }

    private sealed class TorrentEntry
    {
        public string JobId { get; }
        public TorrentManager Manager { get; }
        public TorrentAddOptions Options { get; }
        public string SaveDirectory { get; set; }
        public bool MetadataDispatched { get; set; }
        public int ConsecutiveStallTicks { get; set; }
        public IReadOnlyList<MonoTorrent.Client.PeerId> LatestPeers { get; set; } = Array.Empty<MonoTorrent.Client.PeerId>();

        public TorrentEntry(string jobId, TorrentManager manager, TorrentAddOptions options, string saveDirectory)
        {
            JobId = jobId;
            Manager = manager;
            Options = options;
            SaveDirectory = saveDirectory;
        }
    }
}
