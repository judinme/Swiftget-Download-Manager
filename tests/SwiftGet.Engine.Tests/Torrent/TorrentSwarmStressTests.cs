using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using FluentAssertions;
using MonoTorrent;
using MonoTorrent.Client;
using SwiftGet.Core.Models;
using SwiftGet.Engine.Torrent;
using Xunit;

namespace SwiftGet.Engine.Tests.Torrent;

public class TorrentSwarmStressTests : IAsyncLifetime
{
    private string _tempRoot = null!;
    private readonly List<IAsyncDisposable> _disposables = new();

    public Task InitializeAsync()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"swiftget_stress_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var item in _disposables)
        {
            try { await item.DisposeAsync(); } catch { }
        }
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch { }
    }

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class SeederNode : IAsyncDisposable
    {
        public int Port { get; }
        public string DataDir { get; }
        public byte[] TorrentBytes { get; }
        public byte[] ExpectedHash { get; }
        public string FileName { get; }

        private readonly ClientEngine _engine;
        private readonly TorrentManager _manager;

        private SeederNode(int port, string dataDir, byte[] torrentBytes, byte[] expectedHash, string fileName, ClientEngine engine, TorrentManager manager)
        {
            Port = port;
            DataDir = dataDir;
            TorrentBytes = torrentBytes;
            ExpectedHash = expectedHash;
            FileName = fileName;
            _engine = engine;
            _manager = manager;
        }

        public static async Task<SeederNode> CreateAsync(string rootPath, string name, int payloadSize = 64 * 1024, int pieceSize = 16 * 1024)
        {
            var nodeDir = Path.Combine(rootPath, name);
            Directory.CreateDirectory(nodeDir);

            var fileName = $"{name}_payload.bin";
            var filePath = Path.Combine(nodeDir, fileName);
            var payload = new byte[payloadSize];
            RandomNumberGenerator.Fill(payload);
            await File.WriteAllBytesAsync(filePath, payload);
            var expectedHash = SHA256.HashData(payload);

            var creator = new TorrentCreator { PieceLength = pieceSize };
            var dict = await creator.CreateAsync(new TorrentFileSource(filePath));
            var torrentBytes = dict.Encode();
            var torrent = MonoTorrent.Torrent.Load(torrentBytes);

            var port = GetAvailablePort();
            var settings = new EngineSettingsBuilder
            {
                CacheDirectory = Path.Combine(nodeDir, "cache"),
                ListenEndPoints = new Dictionary<string, IPEndPoint>
                {
                    { "ipv4", new IPEndPoint(IPAddress.Loopback, port) }
                },
                AllowPortForwarding = false,
                AllowLocalPeerDiscovery = true,
                MaximumConnections = 100
            }.ToSettings();

            var engine = new ClientEngine(settings);
            await engine.StartAllAsync();

            var manager = await engine.AddAsync(torrent, nodeDir);
            await manager.HashCheckAsync(true);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!cts.Token.IsCancellationRequested && manager.State != TorrentState.Seeding)
            {
                await Task.Delay(50, cts.Token);
            }

            return new SeederNode(port, nodeDir, torrentBytes, expectedHash, fileName, engine, manager);
        }

        public async Task StopAsync()
        {
            try { await _engine.RemoveAsync(_manager); } catch { }
            try { await _engine.StopAllAsync(); } catch { }
        }

        public async ValueTask DisposeAsync()
        {
            try { await _engine.RemoveAsync(_manager); } catch { }
            try
            {
                await _engine.StopAllAsync();
                _engine.Dispose();
            }
            catch { }
        }
    }

    [Fact]
    public async Task ConcurrentTorrents_DownloadSimultaneously_AllSucceed()
    {
        const int concurrentCount = 4;
        var seeders = new List<SeederNode>();
        for (int i = 0; i < concurrentCount; i++)
        {
            var seeder = await SeederNode.CreateAsync(_tempRoot, $"concurrent_{i}", payloadSize: 48 * 1024);
            seeders.Add(seeder);
            _disposables.Add(seeder);
        }

        var leecherDir = Path.Combine(_tempRoot, "leecher_concurrent");
        Directory.CreateDirectory(leecherDir);

        var leecher = new MonoTorrentEngine();
        _disposables.Add(leecher);
        await leecher.InitializeAsync(new TorrentEngineOptions
        {
            ListenPort = GetAvailablePort(),
            SaveDirectory = leecherDir,
            CacheDirectory = Path.Combine(leecherDir, "cache"),
            AllowLocalTrackers = true,
            EnableLocalPeerDiscovery = true,
            MaxConnections = 100
        });

        var jobIds = new List<string>();
        for (int i = 0; i < concurrentCount; i++)
        {
            var jobId = Guid.NewGuid().ToString("N");
            jobIds.Add(jobId);
            await leecher.AddTorrentFileAsync(seeders[i].TorrentBytes, new TorrentAddOptions
            {
                DownloadId = jobId,
                SaveDirectory = leecherDir,
                StartImmediately = true
            });
            await leecher.AddPeerAsync(jobId, new Uri($"ipv4://127.0.0.1:{seeders[i].Port}"));
        }

        // Wait for all concurrent downloads to reach completion
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var completedSet = new HashSet<string>();

        while (!cts.Token.IsCancellationRequested && completedSet.Count < concurrentCount)
        {
            foreach (var jobId in jobIds)
            {
                if (completedSet.Contains(jobId)) continue;
                var stats = leecher.GetJobStats(jobId);
                if (stats != null && (stats.ProgressPercentage >= 100.0 || stats.State == DownloadStatus.Seeding || stats.State == DownloadStatus.Completed))
                {
                    completedSet.Add(jobId);
                }
            }
            if (completedSet.Count < concurrentCount)
            {
                await Task.Delay(100, cts.Token);
            }
        }

        completedSet.Count.Should().Be(concurrentCount, "All concurrent torrent downloads must complete successfully.");

        // Verify byte hashes for all downloads
        for (int i = 0; i < concurrentCount; i++)
        {
            var downloadedFile = Path.Combine(leecherDir, seeders[i].FileName);
            File.Exists(downloadedFile).Should().BeTrue();

            byte[] downloadedBytes = [];
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    using var stream = new FileStream(downloadedFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    downloadedBytes = new byte[stream.Length];
                    await stream.ReadExactlyAsync(downloadedBytes);
                    break;
                }
                catch (IOException)
                {
                    await Task.Delay(100);
                }
            }

            var hash = SHA256.HashData(downloadedBytes);
            hash.Should().Equal(seeders[i].ExpectedHash, $"Payload for job {i} must match seeder hash.");
        }
    }

    [Fact]
    public async Task PeerChurn_RapidConnectDisconnect_CompletesWithoutHanging()
    {
        var seeder = await SeederNode.CreateAsync(_tempRoot, "churn_seeder", payloadSize: 128 * 1024, pieceSize: 16 * 1024);
        _disposables.Add(seeder);

        var leecherDir = Path.Combine(_tempRoot, "leecher_churn");
        Directory.CreateDirectory(leecherDir);

        var leecherPort = GetAvailablePort();
        var leecher = new MonoTorrentEngine();
        _disposables.Add(leecher);
        await leecher.InitializeAsync(new TorrentEngineOptions
        {
            ListenPort = leecherPort,
            SaveDirectory = leecherDir,
            CacheDirectory = Path.Combine(leecherDir, "cache"),
            AllowLocalTrackers = true,
            EnableLocalPeerDiscovery = true,
            MaxConnections = 100
        });

        var jobId = Guid.NewGuid().ToString("N");
        await leecher.AddTorrentFileAsync(seeder.TorrentBytes, new TorrentAddOptions
        {
            DownloadId = jobId,
            SaveDirectory = leecherDir,
            StartImmediately = true
        });

        await leecher.AddPeerAsync(jobId, new Uri($"ipv4://127.0.0.1:{seeder.Port}"));

        // Background peer churn task: rapidly opens and closes TCP connections to leecher port
        using var churnCts = new CancellationTokenSource();
        var churnTask = Task.Run(async () =>
        {
            while (!churnCts.Token.IsCancellationRequested)
            {
                try
                {
                    using var client = new TcpClient();
                    var connectTask = client.ConnectAsync(IPAddress.Loopback, leecherPort);
                    if (await Task.WhenAny(connectTask, Task.Delay(50, churnCts.Token)) == connectTask)
                    {
                        // Abrupt disconnect
                        client.Client.LingerState = new LingerOption(true, 0);
                        client.Close();
                    }
                }
                catch { }
                await Task.Delay(10, churnCts.Token);
            }
        });

        // Wait for download completion
        using var downloadCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var completed = false;
        while (!downloadCts.Token.IsCancellationRequested)
        {
            var stats = leecher.GetJobStats(jobId);
            if (stats != null && (stats.ProgressPercentage >= 100.0 || stats.State == DownloadStatus.Seeding || stats.State == DownloadStatus.Completed))
            {
                completed = true;
                break;
            }
            await Task.Delay(100, downloadCts.Token);
        }

        churnCts.Cancel();
        try { await churnTask; } catch { }

        completed.Should().BeTrue("Torrent must complete even under rapid external peer connection/disconnection churn.");

        var downloadedFile = Path.Combine(leecherDir, seeder.FileName);
        File.Exists(downloadedFile).Should().BeTrue();
    }

    [Fact]
    public async Task SeederDropout_RecoversAndCompletesOnReconnect()
    {
        var seederNode = await SeederNode.CreateAsync(_tempRoot, "dropout_seeder", payloadSize: 96 * 1024, pieceSize: 16 * 1024);
        _disposables.Add(seederNode);

        var leecherDir = Path.Combine(_tempRoot, "leecher_dropout");
        Directory.CreateDirectory(leecherDir);

        var leecher = new MonoTorrentEngine();
        _disposables.Add(leecher);
        await leecher.InitializeAsync(new TorrentEngineOptions
        {
            ListenPort = GetAvailablePort(),
            SaveDirectory = leecherDir,
            CacheDirectory = Path.Combine(leecherDir, "cache"),
            AllowLocalTrackers = true,
            EnableLocalPeerDiscovery = true,
            MaxConnections = 50
        });

        var jobId = Guid.NewGuid().ToString("N");
        await leecher.AddTorrentFileAsync(seederNode.TorrentBytes, new TorrentAddOptions
        {
            DownloadId = jobId,
            SaveDirectory = leecherDir,
            StartImmediately = true
        });

        // Connect seeder
        await leecher.AddPeerAsync(jobId, new Uri($"ipv4://127.0.0.1:{seederNode.Port}"));

        // Wait for some progress or brief transfer
        await Task.Delay(200);

        // Disconnect / stop the seeder
        await seederNode.StopAsync();

        // Verify leecher stays responsive
        var statsAfterDrop = leecher.GetJobStats(jobId);
        statsAfterDrop.Should().NotBeNull();
        statsAfterDrop!.State.Should().NotBe(DownloadStatus.Failed);

        // Spin up a replacement seeder on a new port with the exact same payload & torrent
        var replacementSeeder = await SeederNode.CreateAsync(_tempRoot, "dropout_seeder_replacement", payloadSize: 96 * 1024, pieceSize: 16 * 1024);
        _disposables.Add(replacementSeeder);

        // Note: For replacement to seed the same torrent, we recreate with same torrent bytes:
        var replacementTorrent = MonoTorrent.Torrent.Load(seederNode.TorrentBytes);
        var replacementPort = GetAvailablePort();
        var replacementSettings = new EngineSettingsBuilder
        {
            CacheDirectory = Path.Combine(replacementSeeder.DataDir, "cache_rep"),
            ListenEndPoints = new Dictionary<string, IPEndPoint>
            {
                { "ipv4", new IPEndPoint(IPAddress.Loopback, replacementPort) }
            },
            AllowPortForwarding = false,
            AllowLocalPeerDiscovery = true
        }.ToSettings();

        var repEngine = new ClientEngine(replacementSettings);
        await repEngine.StartAllAsync();
        // Copy the source file to replacement dir
        var repFilePath = Path.Combine(replacementSeeder.DataDir, seederNode.FileName);
        File.Copy(Path.Combine(seederNode.DataDir, seederNode.FileName), repFilePath, true);

        var repManager = await repEngine.AddAsync(replacementTorrent, replacementSeeder.DataDir);
        await repManager.HashCheckAsync(true);

        using var repWaitCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!repWaitCts.Token.IsCancellationRequested && repManager.State != TorrentState.Seeding)
        {
            await Task.Delay(50, repWaitCts.Token);
        }

        // Connect the replacement seeder to the existing leecher
        await leecher.AddPeerAsync(jobId, new Uri($"ipv4://127.0.0.1:{replacementPort}"));

        // Wait for completion
        using var finishCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var completed = false;
        while (!finishCts.Token.IsCancellationRequested)
        {
            var stats = leecher.GetJobStats(jobId);
            if (stats != null && (stats.ProgressPercentage >= 100.0 || stats.State == DownloadStatus.Seeding || stats.State == DownloadStatus.Completed))
            {
                completed = true;
                break;
            }
            await Task.Delay(100, finishCts.Token);
        }

        try { await repEngine.RemoveAsync(repManager); } catch { }
        try { await repEngine.StopAllAsync(); repEngine.Dispose(); } catch { }

        completed.Should().BeTrue("Leecher must resume and complete after seeder reconnection.");
    }
}
