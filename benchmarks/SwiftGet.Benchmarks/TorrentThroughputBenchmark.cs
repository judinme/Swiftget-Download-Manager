using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using BenchmarkDotNet.Attributes;
using MonoTorrent;
using MonoTorrent.Client;
using SwiftGet.Core.Models;
using SwiftGet.Engine.Torrent;

namespace SwiftGet.Benchmarks;

[MemoryDiagnoser]
public class TorrentThroughputBenchmark
{
    private string _tempDir = null!;
    private string _seederPath = null!;
    private string _leecherPath = null!;
    private ClientEngine _seederEngine = null!;
    private TorrentManager _seederManager = null!;
    private byte[] _torrentBytes = null!;
    private int _seederPort;
    private const int PayloadSizeBytes = 16 * 1024 * 1024; // 16 MB
    private const int PieceSizeBytes = 64 * 1024; // 64 KB

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [GlobalSetup]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"bench_torrent_{Guid.NewGuid():N}");
        _seederPath = Path.Combine(_tempDir, "seeder");
        _leecherPath = Path.Combine(_tempDir, "leecher");

        Directory.CreateDirectory(_seederPath);
        Directory.CreateDirectory(_leecherPath);

        // 1. Generate 16 MB payload
        var payloadPath = Path.Combine(_seederPath, "payload16mb.bin");
        var payloadBytes = new byte[PayloadSizeBytes];
        Random.Shared.NextBytes(payloadBytes);
        File.WriteAllBytes(payloadPath, payloadBytes);

        // 2. Create .torrent
        var creator = new TorrentCreator { PieceLength = PieceSizeBytes };
        var dict = creator.CreateAsync(new TorrentFileSource(payloadPath)).GetAwaiter().GetResult();
        _torrentBytes = dict.Encode();
        var torrent = MonoTorrent.Torrent.Load(_torrentBytes);

        // 3. Start Seeder
        _seederPort = GetAvailablePort();
        var seederSettings = new EngineSettingsBuilder
        {
            CacheDirectory = Path.Combine(_seederPath, "cache"),
            ListenEndPoints = new Dictionary<string, IPEndPoint>
            {
                { "ipv4", new IPEndPoint(IPAddress.Loopback, _seederPort) }
            },
            AllowPortForwarding = false,
            AllowLocalPeerDiscovery = true,
            MaximumConnections = 100
        }.ToSettings();

        _seederEngine = new ClientEngine(seederSettings);
        _seederEngine.StartAllAsync().GetAwaiter().GetResult();

        _seederManager = _seederEngine.AddAsync(torrent, _seederPath).GetAwaiter().GetResult();
        _seederManager.HashCheckAsync(true).GetAwaiter().GetResult();

        var timeout = Stopwatch.StartNew();
        while (timeout.ElapsedMilliseconds < 10000 && _seederManager.State != TorrentState.Seeding)
        {
            Thread.Sleep(50);
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try
        {
            if (_seederManager != null && _seederEngine != null)
            {
                _seederEngine.RemoveAsync(_seederManager).GetAwaiter().GetResult();
            }
            if (_seederEngine != null)
            {
                _seederEngine.StopAllAsync().GetAwaiter().GetResult();
                _seederEngine.Dispose();
            }
        }
        catch { }

        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch { }
    }

    [Benchmark]
    public async Task<long> Download16Mb_LoopbackSwarm()
    {
        var runDir = Path.Combine(_leecherPath, $"run_{Guid.NewGuid():N}");
        Directory.CreateDirectory(runDir);

        var leecherPort = GetAvailablePort();
        var leecherEngine = new MonoTorrentEngine();
        await leecherEngine.InitializeAsync(new TorrentEngineOptions
        {
            ListenPort = leecherPort,
            SaveDirectory = runDir,
            CacheDirectory = Path.Combine(runDir, "cache"),
            AllowLocalTrackers = true,
            EnableLocalPeerDiscovery = true,
            MaxConnections = 50
        });

        try
        {
            var jobId = Guid.NewGuid().ToString("N");
            await leecherEngine.AddTorrentFileAsync(_torrentBytes, new TorrentAddOptions
            {
                DownloadId = jobId,
                SaveDirectory = runDir,
                StartImmediately = true
            });

            var seederUri = new Uri($"ipv4://127.0.0.1:{_seederPort}");
            await leecherEngine.AddPeerAsync(jobId, seederUri);

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 15000)
            {
                var stats = leecherEngine.GetJobStats(jobId);
                if (stats != null && (stats.ProgressPercentage >= 100.0 || stats.State == DownloadStatus.Seeding || stats.State == DownloadStatus.Completed))
                {
                    break;
                }
                await Task.Delay(50);
            }

            var destFile = Path.Combine(runDir, "payload16mb.bin");
            var length = File.Exists(destFile) ? new FileInfo(destFile).Length : 0L;
            return length;
        }
        finally
        {
            await leecherEngine.DisposeAsync();
            try { Directory.Delete(runDir, true); } catch { }
        }
    }

    public (long Bytes, double ElapsedSeconds, double MbPerSec, double Mbps) RunSmoke()
    {
        Setup();
        try
        {
            var sw = Stopwatch.StartNew();
            var bytes = Download16Mb_LoopbackSwarm().GetAwaiter().GetResult();
            sw.Stop();

            var elapsedSeconds = sw.Elapsed.TotalSeconds;
            var mb = bytes / (1024.0 * 1024.0);
            var mbPerSec = elapsedSeconds > 0 ? mb / elapsedSeconds : 0;
            var mbps = mbPerSec * 8;

            return (bytes, elapsedSeconds, mbPerSec, mbps);
        }
        finally
        {
            Cleanup();
        }
    }
}
