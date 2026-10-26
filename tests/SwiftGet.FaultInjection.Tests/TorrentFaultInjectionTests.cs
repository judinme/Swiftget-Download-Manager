using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using MonoTorrent;
using MonoTorrent.Client;
using SwiftGet.Core.Models;
using SwiftGet.Engine.Torrent;
using Xunit;

namespace SwiftGet.FaultInjection.Tests;

public class TorrentFaultInjectionTests : IAsyncLifetime
{
    private string _tempDir = null!;
    private readonly List<IAsyncDisposable> _disposables = new();

    public Task InitializeAsync()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"swiftget_fault_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
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
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
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
    public async Task FailingTrackers_Http500AndTimeout_DoesNotBlockTorrentOrCrashEngine()
    {
        // 1. Host an HTTP tracker that returns HTTP 500
        var trackerPort = GetAvailablePort();
        using var trackerHost = new HostBuilder()
            .ConfigureWebHost(wh =>
            {
                wh.UseKestrel(k => k.Listen(IPAddress.Loopback, trackerPort));
                wh.Configure(app =>
                {
                    app.Run(ctx =>
                    {
                        ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
                        return ctx.Response.WriteAsync("Internal Tracker Error (Fault Injection)");
                    });
                });
            })
            .Start();

        // 2. Setup seeder
        var seederDir = Path.Combine(_tempDir, "seeder");
        Directory.CreateDirectory(seederDir);
        var payloadFile = Path.Combine(seederDir, "tracker_fault_payload.bin");
        var payloadBytes = new byte[64 * 1024];
        RandomNumberGenerator.Fill(payloadBytes);
        await File.WriteAllBytesAsync(payloadFile, payloadBytes);
        var expectedHash = SHA256.HashData(payloadBytes);

        var deadPort = GetAvailablePort();
        var creator = new TorrentCreator { PieceLength = 16 * 1024 };
        // Add failing HTTP 500 tracker, dead connection tracker, and unreachable UDP tracker
        creator.Announces.Add(new List<string>
        {
            $"http://127.0.0.1:{trackerPort}/announce",
            $"http://127.0.0.1:{deadPort}/announce",
            "udp://127.0.0.1:59998/announce"
        });

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
            AllowLocalPeerDiscovery = true
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

        // 3. Start Leecher
        var leecherDir = Path.Combine(_tempDir, "leecher");
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
        await leecher.AddTorrentFileAsync(torrentBytes, new TorrentAddOptions
        {
            DownloadId = jobId,
            SaveDirectory = leecherDir,
            StartImmediately = true
        });

        // Direct connect to local seeder peer
        await leecher.AddPeerAsync(jobId, new Uri($"ipv4://127.0.0.1:{seederPort}"));

        // Wait for download completion
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

        try { await seederEngine.RemoveAsync(seederManager); } catch { }
        try { await seederEngine.StopAllAsync(); seederEngine.Dispose(); } catch { }
        try { await trackerHost.StopAsync(); trackerHost.Dispose(); } catch { }

        completed.Should().BeTrue("Torrent must finish successfully despite tracker HTTP 500 and dead tracker endpoints.");

        var downloadedFile = Path.Combine(leecherDir, "tracker_fault_payload.bin");
        File.Exists(downloadedFile).Should().BeTrue();
        var downloadedBytes = await File.ReadAllBytesAsync(downloadedFile);
        var actualHash = SHA256.HashData(downloadedBytes);
        actualHash.Should().Equal(expectedHash);
    }

    [Fact]
    public async Task CorruptedPieceHashFailure_DetectedAndRepaired()
    {
        var seederDir = Path.Combine(_tempDir, "recheck_seeder");
        Directory.CreateDirectory(seederDir);

        var fileName = "recheck_payload.bin";
        var payloadPath = Path.Combine(seederDir, fileName);
        var payloadBytes = new byte[64 * 1024]; // 64 KB = 4 pieces of 16 KB
        RandomNumberGenerator.Fill(payloadBytes);
        await File.WriteAllBytesAsync(payloadPath, payloadBytes);
        var originalHash = SHA256.HashData(payloadBytes);

        var creator = new TorrentCreator { PieceLength = 16 * 1024 };
        var dict = await creator.CreateAsync(new TorrentFileSource(payloadPath));
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
            AllowLocalPeerDiscovery = true
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

        // Start Leecher
        var leecherDir = Path.Combine(_tempDir, "recheck_leecher");
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
        await leecher.AddTorrentFileAsync(torrentBytes, new TorrentAddOptions
        {
            DownloadId = jobId,
            SaveDirectory = leecherDir,
            StartImmediately = true
        });

        await leecher.AddPeerAsync(jobId, new Uri($"ipv4://127.0.0.1:{seederPort}"));

        // Wait for initial completion
        using var initCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!initCts.Token.IsCancellationRequested)
        {
            var stats = leecher.GetJobStats(jobId);
            if (stats != null && (stats.ProgressPercentage >= 100.0 || stats.State == DownloadStatus.Seeding || stats.State == DownloadStatus.Completed))
            {
                break;
            }
            await Task.Delay(50, initCts.Token);
        }

        var downloadedPath = Path.Combine(leecherDir, fileName);
        File.Exists(downloadedPath).Should().BeTrue();

        // 4. Corrupt piece 2 (offset 20,000)
        byte corruptedByte;
        using (var stream = new FileStream(downloadedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            stream.Seek(20000, SeekOrigin.Begin);
            var currentByte = (byte)stream.ReadByte();
            corruptedByte = (byte)(currentByte ^ 0xFF);
            stream.Seek(20000, SeekOrigin.Begin);
            stream.WriteByte(corruptedByte);
            await stream.FlushAsync();
        }

        // Trigger ForceRecheck
        await leecher.ForceRecheckAsync(jobId);

        // Wait for recheck and repair
        using var repairCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var repaired = false;
        while (!repairCts.Token.IsCancellationRequested)
        {
            var stats = leecher.GetJobStats(jobId);
            if (stats != null && (stats.ProgressPercentage >= 100.0 || stats.State == DownloadStatus.Seeding || stats.State == DownloadStatus.Completed))
            {
                repaired = true;
                break;
            }
            await Task.Delay(50, repairCts.Token);
        }

        try { await seederEngine.RemoveAsync(seederManager); } catch { }
        try { await seederEngine.StopAllAsync(); seederEngine.Dispose(); } catch { }

        repaired.Should().BeTrue("Leecher must detect corrupted piece, re-request from seeder, and restore 100% integrity.");

        // Verify SHA-256 matches original hash
        byte[] repairedBytes = [];
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var stream = new FileStream(downloadedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                repairedBytes = new byte[stream.Length];
                await stream.ReadExactlyAsync(repairedBytes);
                break;
            }
            catch (IOException)
            {
                await Task.Delay(100);
            }
        }

        var finalHash = SHA256.HashData(repairedBytes);
        finalHash.Should().Equal(originalHash, "Repaired file bytes must match expected original hash.");
    }

    [Theory]
    [InlineData("../../Windows/System32/evil.dll")]
    [InlineData("..\\..\\..\\malware.exe")]
    [InlineData("/etc/shadow")]
    [InlineData("sub/../../escape.bin")]
    [InlineData("folder\\sub\\..\\..\\..\\root.txt")]
    public void MaliciousTorrentPathTraversal_BlockedFromOverwritingSystemFiles(string maliciousRelativePath)
    {
        var targetBaseDir = Path.Combine(_tempDir, "safe_downloads");
        Directory.CreateDirectory(targetBaseDir);

        var sanitizedPath = TorrentPathSanitizer.GetSafeFullPath(targetBaseDir, maliciousRelativePath);
        var targetBaseFull = Path.GetFullPath(targetBaseDir);

        sanitizedPath.Should().StartWith(targetBaseFull, "Sanitized path must remain strictly rooted within the target folder.");
        Path.GetFullPath(sanitizedPath).Should().StartWith(targetBaseFull);
    }
}
