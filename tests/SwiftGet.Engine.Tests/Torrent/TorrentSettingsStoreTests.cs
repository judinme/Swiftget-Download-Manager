using System;
using System.IO;
using FluentAssertions;
using SwiftGet.Core.Models;
using SwiftGet.Engine.Torrent;
using Xunit;

namespace SwiftGet.Engine.Tests.Torrent;

public sealed class TorrentSettingsStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;

    public TorrentSettingsStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "swiftget_settings_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _settingsPath = Path.Combine(_tempDir, "torrent_settings.json");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }
    }

    [Fact]
    public void Load_WhenFileDoesNotExist_ReturnsDefaultOptions()
    {
        var opts = TorrentSettingsStore.Load(_settingsPath);
        opts.Should().NotBeNull();
        opts.ListenPort.Should().Be(52138);
        opts.EnableUpnp.Should().BeTrue();
        opts.EnableDht.Should().BeTrue();
        opts.EnablePex.Should().BeTrue();
        opts.EnableLocalPeerDiscovery.Should().BeTrue();
        opts.EncryptionMode.Should().Be(TorrentEncryptionMode.PreferEncryption);
        opts.MaxConnections.Should().Be(500);
        opts.MaxConnectionsPerTorrent.Should().Be(200);
        opts.UsePartialFiles.Should().BeTrue();
    }

    [Fact]
    public void SaveAndLoad_PersistsAllOptionsAccurately()
    {
        var original = new TorrentEngineOptions
        {
            ListenPort = 59123,
            RandomizePortOnStartup = true,
            EnableUpnp = false,
            EnableDht = false,
            EnablePex = true,
            EnableLocalPeerDiscovery = false,
            EncryptionMode = TorrentEncryptionMode.RequireEncryption,
            MaxConnections = 250,
            MaxConnectionsPerTorrent = 75,
            GlobalMaxDownloadRateBytesPerSec = 5_000_000,
            GlobalMaxUploadRateBytesPerSec = 1_000_000,
            AltDownloadRateBytesPerSec = 256 * 1024,
            AltUploadRateBytesPerSec = 64 * 1024,
            EnableRateLimitSchedule = true,
            ScheduleStartHour = 9,
            ScheduleEndHour = 18,
            SaveDirectory = @"C:\CustomTorrents",
            UsePartialFiles = false,
            DefaultSeedRatio = 2.5,
            DefaultSeedTimeMins = 120,
            StopSeedingWhenLimitReached = true,
            DefaultTrackersText = "udp://tracker.custom.org:1337/announce",
            EnableIpFilter = true,
            IpFilterPath = @"C:\ipfilter.dat"
        };

        TorrentSettingsStore.Save(original, _settingsPath);
        File.Exists(_settingsPath).Should().BeTrue();

        var loaded = TorrentSettingsStore.Load(_settingsPath);
        loaded.ListenPort.Should().Be(59123);
        loaded.RandomizePortOnStartup.Should().BeTrue();
        loaded.EnableUpnp.Should().BeFalse();
        loaded.EnableDht.Should().BeFalse();
        loaded.EnablePex.Should().BeTrue();
        loaded.EnableLocalPeerDiscovery.Should().BeFalse();
        loaded.EncryptionMode.Should().Be(TorrentEncryptionMode.RequireEncryption);
        loaded.MaxConnections.Should().Be(250);
        loaded.MaxConnectionsPerTorrent.Should().Be(75);
        loaded.GlobalMaxDownloadRateBytesPerSec.Should().Be(5_000_000);
        loaded.GlobalMaxUploadRateBytesPerSec.Should().Be(1_000_000);
        loaded.AltDownloadRateBytesPerSec.Should().Be(256 * 1024);
        loaded.AltUploadRateBytesPerSec.Should().Be(64 * 1024);
        loaded.EnableRateLimitSchedule.Should().BeTrue();
        loaded.ScheduleStartHour.Should().Be(9);
        loaded.ScheduleEndHour.Should().Be(18);
        loaded.SaveDirectory.Should().Be(@"C:\CustomTorrents");
        loaded.UsePartialFiles.Should().BeFalse();
        loaded.DefaultSeedRatio.Should().Be(2.5);
        loaded.DefaultSeedTimeMins.Should().Be(120);
        loaded.StopSeedingWhenLimitReached.Should().BeTrue();
        loaded.DefaultTrackersText.Should().Be("udp://tracker.custom.org:1337/announce");
        loaded.EnableIpFilter.Should().BeTrue();
        loaded.IpFilterPath.Should().Be(@"C:\ipfilter.dat");
    }

    [Fact]
    public void Load_WhenFileIsCorrupted_RecoversGracefullyWithDefaults()
    {
        File.WriteAllText(_settingsPath, "{ this is not valid json! }}}");

        var opts = TorrentSettingsStore.Load(_settingsPath);
        opts.Should().NotBeNull();
        opts.ListenPort.Should().Be(52138);
    }
}
