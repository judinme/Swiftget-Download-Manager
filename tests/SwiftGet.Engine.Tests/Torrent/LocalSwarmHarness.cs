using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent;
using MonoTorrent.Client;
using SwiftGet.Engine.Torrent;

namespace SwiftGet.Engine.Tests.Torrent;

public sealed class LocalSwarmHarness : IAsyncDisposable
{
    public string RootPath { get; }
    public string SeederPath { get; }
    public string LeecherPath { get; }
    public int SeederPort { get; private set; }

    public byte[] SingleTorrentBytes { get; private set; } = [];
    public string SingleMagnetUri { get; private set; } = string.Empty;
    public string SingleInfoHash { get; private set; } = string.Empty;
    public string SingleFileName { get; } = "single_payload.bin";
    public byte[] SinglePayloadExpectedHash { get; private set; } = [];

    public byte[] MultiTorrentBytes { get; private set; } = [];
    public string MultiMagnetUri { get; private set; } = string.Empty;
    public string MultiInfoHash { get; private set; } = string.Empty;

    private ClientEngine? _seederEngine;
    private TorrentManager? _singleSeederManager;
    private TorrentManager? _multiSeederManager;

    public LocalSwarmHarness()
    {
        RootPath = Path.Combine(Path.GetTempPath(), $"swiftget_swarm_{Guid.NewGuid():N}");
        SeederPath = Path.Combine(RootPath, "seeder");
        LeecherPath = Path.Combine(RootPath, "leecher");
    }

    public static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async Task StartAsync()
    {
        Directory.CreateDirectory(SeederPath);
        Directory.CreateDirectory(LeecherPath);

        SeederPort = GetAvailablePort();

        // 1. Create single-file payload
        var singleFilePath = Path.Combine(SeederPath, SingleFileName);
        var singlePayloadBytes = new byte[64 * 1024]; // 64 KB
        RandomNumberGenerator.Fill(singlePayloadBytes);
        await File.WriteAllBytesAsync(singleFilePath, singlePayloadBytes);
        SinglePayloadExpectedHash = SHA256.HashData(singlePayloadBytes);

        // 2. Create multi-file payload
        var multiDir = Path.Combine(SeederPath, "multi_bundle");
        Directory.CreateDirectory(multiDir);
        var multiFile1 = Path.Combine(multiDir, "file_a.txt");
        var multiFile2 = Path.Combine(multiDir, "file_b.bin");
        var multiPayload1 = new byte[32 * 1024];
        var multiPayload2 = new byte[48 * 1024];
        RandomNumberGenerator.Fill(multiPayload1);
        RandomNumberGenerator.Fill(multiPayload2);
        await File.WriteAllBytesAsync(multiFile1, multiPayload1);
        await File.WriteAllBytesAsync(multiFile2, multiPayload2);

        // 3. Create .torrent for single-file payload
        var creator = new TorrentCreator { PieceLength = 16 * 1024 };
        var singleDict = await creator.CreateAsync(new TorrentFileSource(singleFilePath));
        SingleTorrentBytes = singleDict.Encode();
        var singleTorrent = MonoTorrent.Torrent.Load(SingleTorrentBytes);
        SingleInfoHash = singleTorrent.InfoHashes.V1OrV2.ToHex();
        SingleMagnetUri = $"magnet:?xt=urn:btih:{SingleInfoHash}&dn={SingleFileName}";

        // 4. Create .torrent for multi-file payload
        var multiDict = await creator.CreateAsync(new TorrentFileSource(multiDir));
        MultiTorrentBytes = multiDict.Encode();
        var multiTorrent = MonoTorrent.Torrent.Load(MultiTorrentBytes);
        MultiInfoHash = multiTorrent.InfoHashes.V1OrV2.ToHex();
        MultiMagnetUri = $"magnet:?xt=urn:btih:{MultiInfoHash}&dn=multi_bundle";

        // 5. Start Seeder Engine
        var seederSettings = new EngineSettingsBuilder
        {
            CacheDirectory = Path.Combine(SeederPath, "cache"),
            ListenEndPoints = new Dictionary<string, IPEndPoint>
            {
                { "ipv4", new IPEndPoint(IPAddress.Loopback, SeederPort) }
            },
            AllowPortForwarding = false,
            AllowLocalPeerDiscovery = true,
            MaximumConnections = 100
        }.ToSettings();

        _seederEngine = new ClientEngine(seederSettings);
        await _seederEngine.StartAllAsync();

        _singleSeederManager = await _seederEngine.AddAsync(singleTorrent, SeederPath);
        await _singleSeederManager.HashCheckAsync(true);

        _multiSeederManager = await _seederEngine.AddAsync(multiTorrent, SeederPath);
        await _multiSeederManager.HashCheckAsync(true);

        // Wait until seeder is in Seeding state
        var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!timeoutCts.Token.IsCancellationRequested &&
               (_singleSeederManager.State != TorrentState.Seeding || _multiSeederManager.State != TorrentState.Seeding))
        {
            await Task.Delay(50, timeoutCts.Token);
        }
    }

    public async Task ConnectLeecherAsync(ITorrentEngine leecherEngine, string jobId)
    {
        var seederPeerUri = new Uri($"ipv4://127.0.0.1:{SeederPort}");
        await leecherEngine.AddPeerAsync(jobId, seederPeerUri);
    }

    public async ValueTask DisposeAsync()
    {
        if (_singleSeederManager != null && _seederEngine != null)
        {
            try { await _seederEngine.RemoveAsync(_singleSeederManager); } catch { /* ignore */ }
        }

        if (_multiSeederManager != null && _seederEngine != null)
        {
            try { await _seederEngine.RemoveAsync(_multiSeederManager); } catch { /* ignore */ }
        }

        if (_seederEngine != null)
        {
            try
            {
                await _seederEngine.StopAllAsync();
                _seederEngine.Dispose();
            }
            catch { /* ignore */ }
            _seederEngine = null;
        }

        if (Directory.Exists(RootPath))
        {
            try { Directory.Delete(RootPath, true); } catch { /* ignore */ }
        }
    }
}
