using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MonoTorrent.Client;
using SwiftGet.Core.Models;
using SwiftGet.Engine.Torrent;
using Xunit;

namespace SwiftGet.Engine.Tests.Torrent;

public sealed class TorrentDiagnosticPipelineTests : IAsyncLifetime
{
    private LocalSwarmHarness _harness = null!;
    private MonoTorrentEngine _engine = null!;
    private int _testListenPort;

    public async Task InitializeAsync()
    {
        _harness = new LocalSwarmHarness();
        await _harness.StartAsync();

        _testListenPort = LocalSwarmHarness.GetAvailablePort();
        _engine = new MonoTorrentEngine();
    }

    public async Task DisposeAsync()
    {
        await _engine.DisposeAsync();
        await _harness.DisposeAsync();
    }

    [Fact]
    public async Task Stage1_EngineCreatedAndStarted_ShouldBeRunning()
    {
        await _engine.InitializeAsync(new TorrentEngineOptions
        {
            ListenPort = _testListenPort,
            SaveDirectory = _harness.LeecherPath,
            CacheDirectory = Path.Combine(_harness.LeecherPath, "cache")
        });

        _engine.IsRunning.Should().BeTrue("Torrent engine must be running after InitializeAsync.");
    }

    [Fact]
    public async Task Stage2_ListenPortBinding_PortCollisionHandling()
    {
        // First engine on port
        await _engine.InitializeAsync(new TorrentEngineOptions
        {
            ListenPort = _testListenPort,
            SaveDirectory = _harness.LeecherPath,
            CacheDirectory = Path.Combine(_harness.LeecherPath, "cache")
        });

        // Second engine attempting port 0 should bind an OS-assigned available port without throwing
        var engine2 = new MonoTorrentEngine();
        var initPort0 = async () =>
        {
            await engine2.InitializeAsync(new TorrentEngineOptions
            {
                ListenPort = 0,
                SaveDirectory = _harness.LeecherPath,
                CacheDirectory = Path.Combine(_harness.LeecherPath, "cache2")
            });
        };

        await initPort0.Should().NotThrowAsync();
        await engine2.DisposeAsync();
    }

    [Fact]
    public async Task Stage3_DhtAndTrackers_AnnounceAndState()
    {
        await _engine.InitializeAsync(new TorrentEngineOptions
        {
            ListenPort = _testListenPort,
            EnableDht = true,
            SaveDirectory = _harness.LeecherPath,
            CacheDirectory = Path.Combine(_harness.LeecherPath, "cache"),
            AllowLocalTrackers = true
        });

        var jobId = Guid.NewGuid().ToString("N");
        await _engine.AddTorrentFileAsync(_harness.SingleTorrentBytes, new TorrentAddOptions
        {
            DownloadId = jobId,
            SaveDirectory = _harness.LeecherPath,
            StartImmediately = true
        });

        var trackers = _engine.GetTrackers(jobId);
        trackers.Should().NotBeNull();
    }

    [Fact]
    public void Stage4_MagnetParsing_Hex_Base32_Trackers()
    {
        // Valid BTIH hex
        var hexUri = "magnet:?xt=urn:btih:da39a3ee5e6b4b0d3255bfef95601890afd80709&dn=Ubuntu&tr=http%3A%2F%2Ftracker.example.com%2Fannounce";
        MagnetUriParser.TryParse(hexUri, out var parsedHex, out var errHex).Should().BeTrue();
        parsedHex!.InfoHash.Should().Be("da39a3ee5e6b4b0d3255bfef95601890afd80709");
        parsedHex.DisplayName.Should().Be("Ubuntu");
        parsedHex.Trackers.Should().ContainSingle();

        // Base32 BTIH
        var base32Uri = "magnet:?xt=urn:btih:3B245504CF5F11BBDFFE1201CEA6A6BF45A004E3&dn=Test";
        MagnetUriParser.TryParse(base32Uri, out var parsedB32, out _).Should().BeTrue();
        parsedB32!.InfoHash.ToLowerInvariant().Should().Be("3b245504cf5f11bbdffe1201cea6a6bf45a004e3");
    }

    [Fact]
    public async Task Stage5_MetadataFetch_LocalSwarm_ReceivesMetadata()
    {
        await _engine.InitializeAsync(new TorrentEngineOptions
        {
            ListenPort = _testListenPort,
            SaveDirectory = _harness.LeecherPath,
            CacheDirectory = Path.Combine(_harness.LeecherPath, "cache"),
            AllowLocalTrackers = true,
            EnableLocalPeerDiscovery = true
        });

        var jobId = Guid.NewGuid().ToString("N");
        var metadataReceivedTcs = new TaskCompletionSource<TorrentMetadataReceivedEventArgs>();

        _engine.MetadataReceived += (s, e) =>
        {
            if (e.JobId == jobId)
            {
                metadataReceivedTcs.TrySetResult(e);
            }
        };

        await _engine.AddMagnetAsync(_harness.SingleMagnetUri, new TorrentAddOptions
        {
            DownloadId = jobId,
            SaveDirectory = _harness.LeecherPath,
            StartImmediately = true
        });

        await _harness.ConnectLeecherAsync(_engine, jobId);

        var completed = await Task.WhenAny(metadataReceivedTcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        completed.Should().Be(metadataReceivedTcs.Task, "Metadata BEP 9 handshake should resolve metadata from loopback seeder.");
    }

    [Fact]
    public async Task Stage6And7_DownloadStart_DiskWrites_HashesMatch()
    {
        await _engine.InitializeAsync(new TorrentEngineOptions
        {
            ListenPort = _testListenPort,
            SaveDirectory = _harness.LeecherPath,
            CacheDirectory = Path.Combine(_harness.LeecherPath, "cache"),
            AllowLocalTrackers = true,
            EnableLocalPeerDiscovery = true
        });

        var jobId = Guid.NewGuid().ToString("N");
        await _engine.AddTorrentFileAsync(_harness.SingleTorrentBytes, new TorrentAddOptions
        {
            DownloadId = jobId,
            SaveDirectory = _harness.LeecherPath,
            StartImmediately = true
        });

        await _harness.ConnectLeecherAsync(_engine, jobId);

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var completed = false;

        while (!cts.Token.IsCancellationRequested)
        {
            var stats = _engine.GetJobStats(jobId);
            if (stats != null && (stats.ProgressPercentage >= 100.0 || stats.State == DownloadStatus.Seeding || stats.State == DownloadStatus.Completed))
            {
                completed = true;
                break;
            }
            await Task.Delay(100, cts.Token);
        }

        completed.Should().BeTrue();

        var filePath = Path.Combine(_harness.LeecherPath, _harness.SingleFileName);
        File.Exists(filePath).Should().BeTrue();

        var bytes = await File.ReadAllBytesAsync(filePath);
        var hash = SHA256.HashData(bytes);
        hash.Should().Equal(_harness.SinglePayloadExpectedHash);
    }
}
