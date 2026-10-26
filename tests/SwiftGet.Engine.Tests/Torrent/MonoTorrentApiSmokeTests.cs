using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using MonoTorrent;
using MonoTorrent.Client;
using Xunit;

namespace SwiftGet.Engine.Tests.Torrent;

public class MonoTorrentApiSmokeTests
{
    [Fact]
    public async Task CanInstantiateClientEngine()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "SwiftGet_Torrent_Smoke_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var settings = new EngineSettingsBuilder
            {
                CacheDirectory = tempDir
            }.ToSettings();

            using var engine = new ClientEngine(settings);
            Assert.False(engine.IsRunning);

            await engine.StartAllAsync();
            await engine.StopAllAsync();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task CanAddMagnetAndInspectTorrentManager()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "SwiftGet_Torrent_Smoke_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var settings = new EngineSettingsBuilder
            {
                CacheDirectory = tempDir
            }.ToSettings();

            using var engine = new ClientEngine(settings);

            var magnet = MagnetLink.Parse("magnet:?xt=urn:btih:da39a3ee5e6b4b0d3255bfef95601890afd80709&dn=TestTorrent");
            var manager = await engine.AddAsync(magnet, tempDir);

            Assert.NotNull(manager);
            Assert.Equal("da39a3ee5e6b4b0d3255bfef95601890afd80709", manager.InfoHashes.V1OrV2.ToHex(), ignoreCase: true);
            Assert.Equal("TestTorrent", manager.Torrent?.Name ?? manager.MagnetLink.Name);

            if (manager.HasMetadata)
            {
                var fastResume = await manager.SaveFastResumeAsync();
                Assert.NotNull(fastResume);
            }

            // Test TorrentCreator
            var sampleFile = Path.Combine(tempDir, "sample.txt");
            await File.WriteAllTextAsync(sampleFile, "Hello BitTorrent Swarm!");
            var creator = new TorrentCreator { PieceLength = 16 * 1024 };
            var fileSource = new TorrentFileSource(sampleFile);
            var bencoded = await creator.CreateAsync(fileSource);
            var bytes = bencoded.Encode();
            var loadedTorrent = MonoTorrent.Torrent.Load(bytes);
            Assert.Equal("sample.txt", loadedTorrent.Name);
            Assert.Single(loadedTorrent.Files);

            await engine.RemoveAsync(manager);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }
}
