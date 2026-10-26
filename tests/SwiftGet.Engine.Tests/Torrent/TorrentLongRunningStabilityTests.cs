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

public class TorrentLongRunningStabilityTests : IAsyncLifetime
{
    private string _tempRoot = null!;
    private readonly List<IAsyncDisposable> _disposables = new();

    public Task InitializeAsync()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"swiftget_stability_{Guid.NewGuid():N}");
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

    [Fact]
    public async Task ExtendedSeeding_MemoryRemainsBounded()
    {
        var seederDir = Path.Combine(_tempRoot, "seeder_stability");
        Directory.CreateDirectory(seederDir);

        var payloadFile = Path.Combine(seederDir, "stability_payload.bin");
        var payloadBytes = new byte[64 * 1024]; // 64 KB
        RandomNumberGenerator.Fill(payloadBytes);
        await File.WriteAllBytesAsync(payloadFile, payloadBytes);

        var creator = new TorrentCreator { PieceLength = 16 * 1024 };
        var dict = await creator.CreateAsync(new TorrentFileSource(payloadFile));
        var torrentBytes = dict.Encode();
        var torrent = MonoTorrent.Torrent.Load(torrentBytes);

        var seederPort = GetAvailablePort();
        var seederSettings = new EngineSettingsBuilder
        {
            CacheDirectory = Path.Combine(seederDir, "cache"),
            ListenEndPoints = new Dictionary<string, IPEndPoint>
            {
                { "ipv4", new IPEndPoint(IPAddress.Loopback, seederPort) }
            },
            AllowPortForwarding = false,
            AllowLocalPeerDiscovery = true,
            MaximumConnections = 100
        }.ToSettings();

        var seederEngine = new ClientEngine(seederSettings);
        await seederEngine.StartAllAsync();
        var seederManager = await seederEngine.AddAsync(torrent, seederDir);
        await seederManager.HashCheckAsync(true);

        using var waitSeeder = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!waitSeeder.Token.IsCancellationRequested && seederManager.State != TorrentState.Seeding)
        {
            await Task.Delay(50, waitSeeder.Token);
        }

        // Run baseline GC to stabilize heap
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long initialMemory = GC.GetTotalMemory(true);

        const int iterations = 5;
        for (int i = 0; i < iterations; i++)
        {
            var leecherDir = Path.Combine(_tempRoot, $"leecher_run_{i}");
            Directory.CreateDirectory(leecherDir);

            var leecher = new MonoTorrentEngine();
            await leecher.InitializeAsync(new TorrentEngineOptions
            {
                ListenPort = GetAvailablePort(),
                SaveDirectory = leecherDir,
                CacheDirectory = Path.Combine(leecherDir, "cache"),
                AllowLocalTrackers = true,
                EnableLocalPeerDiscovery = true,
                MaxConnections = 20
            });

            var jobId = Guid.NewGuid().ToString("N");
            await leecher.AddTorrentFileAsync(torrentBytes, new TorrentAddOptions
            {
                DownloadId = jobId,
                SaveDirectory = leecherDir,
                StartImmediately = true
            });

            await leecher.AddPeerAsync(jobId, new Uri($"ipv4://127.0.0.1:{seederPort}"));

            using var downloadCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!downloadCts.Token.IsCancellationRequested)
            {
                var stats = leecher.GetJobStats(jobId);
                if (stats != null && (stats.ProgressPercentage >= 100.0 || stats.State == DownloadStatus.Seeding || stats.State == DownloadStatus.Completed))
                {
                    break;
                }
                await Task.Delay(50, downloadCts.Token);
            }

            await leecher.DisposeAsync();
            try { Directory.Delete(leecherDir, true); } catch { }
        }

        // Final GC collection
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long finalMemory = GC.GetTotalMemory(true);

        try { await seederEngine.RemoveAsync(seederManager); } catch { }
        try { await seederEngine.StopAllAsync(); seederEngine.Dispose(); } catch { }

        long memoryDelta = Math.Max(0, finalMemory - initialMemory);
        // Memory delta over 5 complete loopback swarm transfers should remain well bounded (< 35 MB)
        memoryDelta.Should().BeLessThan(35 * 1024 * 1024, $"Memory delta ({memoryDelta / 1024 / 1024} MB) should remain bounded without runaway leaks.");
    }

    [Fact]
    public async Task EngineLifecycle_RepeatedStartStop_LeavesNoOrphanedResources()
    {
        var testPort = GetAvailablePort();

        // 5 consecutive initialize -> start -> add -> stop -> dispose cycles on the exact same port
        for (int i = 0; i < 5; i++)
        {
            var cycleDir = Path.Combine(_tempRoot, $"lifecycle_cycle_{i}");
            Directory.CreateDirectory(cycleDir);

            var engine = new MonoTorrentEngine();
            await engine.InitializeAsync(new TorrentEngineOptions
            {
                ListenPort = testPort,
                SaveDirectory = cycleDir,
                CacheDirectory = Path.Combine(cycleDir, "cache"),
                AllowLocalTrackers = true,
                EnableLocalPeerDiscovery = false,
                MaxConnections = 20
            });

            engine.IsRunning.Should().BeTrue($"Cycle {i}: Engine must be running after initialization.");
            await engine.DisposeAsync();
            engine.IsRunning.Should().BeFalse($"Cycle {i}: Engine must be stopped after disposal.");

            // After dispose, port must be immediately released so another socket can bind to it
            Action bindAction = () =>
            {
                using var testListener = new TcpListener(IPAddress.Loopback, testPort);
                testListener.Start();
                testListener.Stop();
            };
            bindAction.Should().NotThrow($"Cycle {i}: Listening port {testPort} must be released immediately upon engine disposal.");
        }
    }
}
