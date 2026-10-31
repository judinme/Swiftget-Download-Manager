using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SwiftGet.Core.Models;
using SwiftGet.Engine.Torrent;
using Xunit;

namespace SwiftGet.Engine.Tests.Torrent;

public class TorrentEngineIntegrationTests : IAsyncLifetime
{
    private LocalSwarmHarness _harness = null!;
    private MonoTorrentEngine _leecherEngine = null!;

    public async Task InitializeAsync()
    {
        _harness = new LocalSwarmHarness();
        await _harness.StartAsync();

        _leecherEngine = new MonoTorrentEngine();
        await _leecherEngine.InitializeAsync(new TorrentEngineOptions
        {
            ListenPort = LocalSwarmHarness.GetAvailablePort(),
            SaveDirectory = _harness.LeecherPath,
            CacheDirectory = Path.Combine(_harness.LeecherPath, "cache"),
            AllowLocalTrackers = true,
            EnableLocalPeerDiscovery = true,
            MaxConnections = 50
        });
    }

    public async Task DisposeAsync()
    {
        await _leecherEngine.DisposeAsync();
        await _harness.DisposeAsync();
    }

    [Fact]
    public async Task DownloadTorrentFile_CompletesFromLocalSwarm_HashesMatch()
    {
        var jobId = Guid.NewGuid().ToString("N");
        var addOptions = new TorrentAddOptions
        {
            DownloadId = jobId,
            SaveDirectory = _harness.LeecherPath,
            StartImmediately = true
        };

        var returnedJobId = await _leecherEngine.AddTorrentFileAsync(_harness.SingleTorrentBytes, addOptions);
        returnedJobId.Should().Be(jobId);

        await _harness.ConnectLeecherAsync(_leecherEngine, jobId);

        // Wait for completion (up to 15 seconds)
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var completed = false;

        while (!cts.Token.IsCancellationRequested)
        {
            var stats = _leecherEngine.GetJobStats(jobId);
            if (stats != null && (stats.ProgressPercentage >= 100.0 || stats.State == DownloadStatus.Seeding || stats.State == DownloadStatus.Completed))
            {
                completed = true;
                break;
            }
            await Task.Delay(100, cts.Token);
        }

        completed.Should().BeTrue("Torrent download should reach 100% completion from local swarm.");

        // Verify payload file on disk
        var downloadedFilePath = Path.Combine(_harness.LeecherPath, _harness.SingleFileName);
        File.Exists(downloadedFilePath).Should().BeTrue();

        var downloadedBytes = await ReadAllBytesWithRetryAsync(downloadedFilePath);
        var actualHash = SHA256.HashData(downloadedBytes);
        actualHash.Should().Equal(_harness.SinglePayloadExpectedHash, "Downloaded payload hash must match seeder hash exactly.");
    }

    [Fact]
    public async Task DownloadMagnet_ResolvesMetadataAndCompletes_HashesMatch()
    {
        var jobId = Guid.NewGuid().ToString("N");
        var metadataTcs = new TaskCompletionSource<bool>();

        _leecherEngine.MetadataReceived += (s, e) =>
        {
            if (e.JobId == jobId)
            {
                metadataTcs.TrySetResult(true);
            }
        };

        var addOptions = new TorrentAddOptions
        {
            DownloadId = jobId,
            SaveDirectory = _harness.LeecherPath,
            StartImmediately = true
        };

        await _leecherEngine.AddMagnetAsync(_harness.SingleMagnetUri, addOptions);
        await _harness.ConnectLeecherAsync(_leecherEngine, jobId);

        // Wait for metadata exchange (BEP 9) and download completion
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var completed = false;

        while (!cts.Token.IsCancellationRequested)
        {
            var stats = _leecherEngine.GetJobStats(jobId);
            if (stats != null && (stats.ProgressPercentage >= 100.0 || stats.State == DownloadStatus.Seeding || stats.State == DownloadStatus.Completed))
            {
                completed = true;
                break;
            }
            await Task.Delay(100, cts.Token);
        }

        completed.Should().BeTrue("Magnet transfer must resolve metadata and complete 100%.");

        var downloadedFilePath = Path.Combine(_harness.LeecherPath, _harness.SingleFileName);
        File.Exists(downloadedFilePath).Should().BeTrue();

        var downloadedBytes = await ReadAllBytesWithRetryAsync(downloadedFilePath);
        var actualHash = SHA256.HashData(downloadedBytes);
        actualHash.Should().Equal(_harness.SinglePayloadExpectedHash);
    }

    private static async Task<byte[]> ReadAllBytesWithRetryAsync(string path)
    {
        for (int i = 0; i < 25; i++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var ms = new MemoryStream();
                await fs.CopyToAsync(ms);
                return ms.ToArray();
            }
            catch (IOException)
            {
                await Task.Delay(100);
            }
        }
        using var finalFs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var finalMs = new MemoryStream();
        await finalFs.CopyToAsync(finalMs);
        return finalMs.ToArray();
    }

    [Fact]
    public async Task KillAndResume_RestoresStateWithoutDataCorruption()
    {
        var jobId = Guid.NewGuid().ToString("N");
        var addOptions = new TorrentAddOptions
        {
            DownloadId = jobId,
            SaveDirectory = _harness.LeecherPath,
            StartImmediately = true
        };

        await _leecherEngine.AddTorrentFileAsync(_harness.SingleTorrentBytes, addOptions);
        await _harness.ConnectLeecherAsync(_leecherEngine, jobId);

        // Wait until at least 1 piece is hashed
        var pieceHashedTcs = new TaskCompletionSource<bool>();
        _leecherEngine.PieceHashed += (s, e) =>
        {
            if (e.JobId == jobId && e.HashPassed)
            {
                pieceHashedTcs.TrySetResult(true);
            }
        };

        await Task.WhenAny(pieceHashedTcs.Task, Task.Delay(3000));

        // Simulate crash: pause and dispose first engine
        await _leecherEngine.PauseAsync(jobId);
        var fastResumeBlob = _leecherEngine.GetFastResumeData(jobId);
        await _leecherEngine.DisposeAsync();

        // Re-instantiate a second engine instance to resume
        _leecherEngine = new MonoTorrentEngine();
        await _leecherEngine.InitializeAsync(new TorrentEngineOptions
        {
            ListenPort = LocalSwarmHarness.GetAvailablePort(),
            SaveDirectory = _harness.LeecherPath,
            CacheDirectory = Path.Combine(_harness.LeecherPath, "cache2"),
            AllowLocalTrackers = true
        });

        // Resume torrent
        await _leecherEngine.AddTorrentFileAsync(_harness.SingleTorrentBytes, new TorrentAddOptions
        {
            DownloadId = jobId,
            SaveDirectory = _harness.LeecherPath,
            StartImmediately = true
        });

        await _harness.ConnectLeecherAsync(_leecherEngine, jobId);

        // Wait for resume to finish to 100%
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var completed = false;

        while (!cts.Token.IsCancellationRequested)
        {
            var stats = _leecherEngine.GetJobStats(jobId);
            if (stats != null && (stats.ProgressPercentage >= 100.0 || stats.State == DownloadStatus.Seeding || stats.State == DownloadStatus.Completed))
            {
                completed = true;
                break;
            }
            await Task.Delay(100, cts.Token);
        }

        completed.Should().BeTrue("Resumed transfer must complete cleanly.");

        var downloadedFilePath = Path.Combine(_harness.LeecherPath, _harness.SingleFileName);
        var downloadedBytes = await ReadAllBytesWithRetryAsync(downloadedFilePath);
        SHA256.HashData(downloadedBytes).Should().Equal(_harness.SinglePayloadExpectedHash, "File must not be corrupted after restart/resume.");
    }

    [Fact]
    public async Task SelectiveFileDownload_DownloadsOnlySelectedFiles()
    {
        var jobId = Guid.NewGuid().ToString("N");
        var addOptions = new TorrentAddOptions
        {
            DownloadId = jobId,
            SaveDirectory = _harness.LeecherPath,
            StartImmediately = true,
            SelectedFileIndices = [0] // Only select file_a.txt (index 0)
        };

        await _leecherEngine.AddTorrentFileAsync(_harness.MultiTorrentBytes, addOptions);
        await _harness.ConnectLeecherAsync(_leecherEngine, jobId);

        // Wait for download to finish
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!cts.Token.IsCancellationRequested)
        {
            var files = _leecherEngine.GetFiles(jobId);
            if (files.Count > 0 && files[0].ProgressPercentage >= 100.0)
            {
                break;
            }
            await Task.Delay(100, cts.Token);
        }

        var filesStatus = _leecherEngine.GetFiles(jobId);
        filesStatus.Should().HaveCount(2);
        filesStatus[0].Priority.Should().Be(TorrentFilePriority.Normal);
        filesStatus[0].ProgressPercentage.Should().Be(100.0);
        filesStatus[1].Priority.Should().Be(TorrentFilePriority.DoNotDownload);
        filesStatus[1].ProgressPercentage.Should().Be(0.0);
    }

    [Fact]
    public async Task RateLimiting_ConfiguresTorrentAndGlobalLimitsSuccessfully()
    {
        var jobId = Guid.NewGuid().ToString("N");
        var addOptions = new TorrentAddOptions
        {
            DownloadId = jobId,
            SaveDirectory = _harness.LeecherPath,
            StartImmediately = false
        };

        await _leecherEngine.AddTorrentFileAsync(_harness.SingleTorrentBytes, addOptions);

        // Set per-torrent limits
        await _leecherEngine.SetTorrentRateLimitsAsync(jobId, maxDownloadBytesPerSec: 1024 * 1024, maxUploadBytesPerSec: 512 * 1024);

        // Set global limits
        await _leecherEngine.SetGlobalRateLimitsAsync(maxDownloadBytesPerSec: 5 * 1024 * 1024, maxUploadBytesPerSec: 2 * 1024 * 1024);

        var stats = _leecherEngine.GetJobStats(jobId);
        stats.Should().NotBeNull();
        stats!.JobId.Should().Be(jobId);
    }

    [Fact]
    public async Task CreateTorrentAsync_GeneratesValidTorrentBytes_ParsableAndMatchesSource()
    {
        var tempFile = Path.Combine(_harness.LeecherPath, "sample_for_creation.bin");
        var testData = new byte[65536];
        Random.Shared.NextBytes(testData);
        await File.WriteAllBytesAsync(tempFile, testData);

        var torrentBytes = await _leecherEngine.CreateTorrentAsync(
            tempFile,
            announceUrls: ["udp://tracker.opentrackr.org:1337/announce"],
            comment: "SwiftGet Unit Test",
            createdBy: "SwiftGet Test Suite");

        torrentBytes.Should().NotBeNull();
        torrentBytes.Length.Should().BeGreaterThan(50);

        var parsed = MonoTorrent.Torrent.Load(torrentBytes);
        parsed.Name.Should().Be(Path.GetFileName(tempFile));
        parsed.Size.Should().Be(testData.Length);
        parsed.Comment.Should().Be("SwiftGet Unit Test");
        parsed.CreatedBy.Should().Be("SwiftGet Test Suite");
    }

    [Fact]
    public async Task SetSequentialDownload_And_MoveStorage_ExecuteCleanly()
    {
        var jobId = Guid.NewGuid().ToString("N");
        var addOptions = new TorrentAddOptions
        {
            DownloadId = jobId,
            SaveDirectory = _harness.LeecherPath,
            StartImmediately = false,
            SequentialDownload = true
        };

        await _leecherEngine.AddTorrentFileAsync(_harness.SingleTorrentBytes, addOptions);
        _leecherEngine.HasJob(jobId).Should().BeTrue();

        // Toggle sequential download
        await _leecherEngine.SetSequentialDownloadAsync(jobId, false);
        await _leecherEngine.SetSequentialDownloadAsync(jobId, true);

        // Move storage
        var newSubdir = Path.Combine(_harness.LeecherPath, "relocated");
        await _leecherEngine.MoveStorageAsync(jobId, newSubdir);
        _leecherEngine.HasJob(jobId).Should().BeTrue();
    }
}
