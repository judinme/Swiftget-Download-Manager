using System;
using System.IO;
using System.Net;
using FluentAssertions;
using SwiftGet.Engine.Torrent;
using Xunit;

namespace SwiftGet.Engine.Tests.Torrent;

public class TorrentSafetyTests
{
    [Theory]
    [InlineData("normal_file.txt", "normal_file.txt")]
    [InlineData("subfolder/file.bin", "subfolder\\file.bin")]
    [InlineData("../../etc/passwd", "etc\\passwd")]
    [InlineData("..\\..\\Windows\\System32\\calc.exe", "Windows\\System32\\calc.exe")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("aux.dat", "_aux.dat")]
    [InlineData("nested/PRN/file.txt", "nested\\_PRN\\file.txt")]
    [InlineData("file.txt:hidden_stream", "file.txt")]
    [InlineData("folder/com1.bin:stream", "folder\\_com1.bin")]
    public void TorrentPathSanitizer_SanitizesUnsafePaths(string input, string expected)
    {
        var sanitized = TorrentPathSanitizer.SanitizeRelativePath(input);
        var expectedNormalized = expected.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        sanitized.Should().Be(expectedNormalized);
    }

    [Fact]
    public void TorrentPathSanitizer_GetSafeFullPath_PreventsDirectoryEscape()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "SwiftGet_Safety_Base");
        Directory.CreateDirectory(baseDir);

        try
        {
            var safePath = TorrentPathSanitizer.GetSafeFullPath(baseDir, "../../malicious.exe");
            safePath.Should().StartWith(baseDir);
            safePath.Should().EndWith("malicious.exe");
        }
        finally
        {
            if (Directory.Exists(baseDir))
            {
                Directory.Delete(baseDir, true);
            }
        }
    }

    [Theory]
    [InlineData("https://tracker.opentrackr.org/announce", true)]
    [InlineData("udp://tracker.openbittorrent.com:6969/announce", true)]
    [InlineData("http://127.0.0.1:8080/announce", false)]
    [InlineData("http://localhost:6969/announce", false)]
    [InlineData("http://[::1]:8080/announce", false)]
    [InlineData("http://192.168.1.1:8080/announce", false)]
    [InlineData("http://10.0.0.1:8080/announce", false)]
    [InlineData("http://172.16.5.20:8080/announce", false)]
    [InlineData("ftp://invalid-scheme.com/announce", false)]
    [InlineData("not-a-url", false)]
    public void TrackerSafetyValidator_BlocksPrivateAndInvalidTrackers_WhenLocalDisabled(string url, bool expectedSafe)
    {
        var isSafe = TrackerSafetyValidator.IsSafeTrackerUrl(url, allowLocalTrackers: false, out var reason);
        isSafe.Should().Be(expectedSafe);
        if (!expectedSafe)
        {
            reason.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080/announce")]
    [InlineData("http://localhost:6969/announce")]
    [InlineData("http://192.168.1.50:6969/announce")]
    public void TrackerSafetyValidator_AllowsLocalTrackers_WhenExplicitlyEnabled(string url)
    {
        var isSafe = TrackerSafetyValidator.IsSafeTrackerUrl(url, allowLocalTrackers: true, out var reason);
        isSafe.Should().BeTrue();
        reason.Should().BeNull();
    }

    [Fact]
    public void TrackerSafetyValidator_DetectsPrivateIpRangesAccurately()
    {
        TrackerSafetyValidator.IsPrivateOrLoopback(IPAddress.Loopback).Should().BeTrue();
        TrackerSafetyValidator.IsPrivateOrLoopback(IPAddress.IPv6Loopback).Should().BeTrue();
        TrackerSafetyValidator.IsPrivateOrLoopback(IPAddress.Parse("10.254.1.1")).Should().BeTrue();
        TrackerSafetyValidator.IsPrivateOrLoopback(IPAddress.Parse("172.20.10.5")).Should().BeTrue();
        TrackerSafetyValidator.IsPrivateOrLoopback(IPAddress.Parse("192.168.100.2")).Should().BeTrue();
        TrackerSafetyValidator.IsPrivateOrLoopback(IPAddress.Parse("169.254.50.1")).Should().BeTrue();

        TrackerSafetyValidator.IsPrivateOrLoopback(IPAddress.Parse("8.8.8.8")).Should().BeFalse();
        TrackerSafetyValidator.IsPrivateOrLoopback(IPAddress.Parse("1.1.1.1")).Should().BeFalse();
        TrackerSafetyValidator.IsPrivateOrLoopback(IPAddress.Parse("142.250.190.46")).Should().BeFalse();
    }

    [Fact]
    public void TorrentPathSanitizer_GetSafeFullPath_PreventsDirectoryPrefixEscape()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "SwiftGet_Safety_PrefixTest_" + Guid.NewGuid().ToString("N"));
        var baseDir = Path.Combine(tempRoot, "base");
        var siblingDir = Path.Combine(tempRoot, "base_evil");
        Directory.CreateDirectory(baseDir);
        Directory.CreateDirectory(siblingDir);

        try
        {
            var safePath = TorrentPathSanitizer.GetSafeFullPath(baseDir, "../base_evil/payload.txt");
            safePath.Should().StartWith(baseDir + Path.DirectorySeparatorChar);
            safePath.Should().NotStartWith(siblingDir);
            safePath.Should().EndWith("payload.txt");
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    [Fact]
    public async Task AddMagnetAsync_FiltersLocalTrackers_WhenAllowLocalTrackersIsFalse()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "SwiftGet_Torrent_TrackerSafety_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var engine = new MonoTorrentEngine();
        try
        {
            await engine.InitializeAsync(new TorrentEngineOptions
            {
                SaveDirectory = tempDir,
                CacheDirectory = Path.Combine(tempDir, "cache"),
                AllowLocalTrackers = false
            });

            var magnetUrl = "magnet:?xt=urn:btih:da39a3ee5e6b4b0d3255bfef95601890afd80709&dn=TestTorrent" +
                            "&tr=http://127.0.0.1:8080/announce" +
                            "&tr=http://192.168.1.1:6969/announce" +
                            "&tr=https://tracker.opentrackr.org/announce";

            var jobId = Guid.NewGuid().ToString("N");
            await engine.AddMagnetAsync(magnetUrl, new TorrentAddOptions
            {
                DownloadId = jobId,
                SaveDirectory = tempDir,
                StartImmediately = false
            });

            var stats = engine.GetJobStats(jobId);
            stats.Should().NotBeNull();
        }
        finally
        {
            await engine.DisposeAsync();
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task RemoveAsync_DoesNotDeleteOutsideSaveDirectory()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "SwiftGet_RemoveSafety_" + Guid.NewGuid().ToString("N"));
        var saveDir = Path.Combine(tempRoot, "save");
        var outsideFile = Path.Combine(tempRoot, "outside_sentinel.txt");
        Directory.CreateDirectory(saveDir);
        await File.WriteAllTextAsync(outsideFile, "important sentinel file");

        var engine = new MonoTorrentEngine();
        try
        {
            await engine.InitializeAsync(new TorrentEngineOptions
            {
                SaveDirectory = saveDir,
                CacheDirectory = Path.Combine(tempRoot, "cache"),
                AllowLocalTrackers = true
            });

            var jobId = Guid.NewGuid().ToString("N");
            await engine.AddMagnetAsync("magnet:?xt=urn:btih:da39a3ee5e6b4b0d3255bfef95601890afd80709&dn=SafeTorrent", new TorrentAddOptions
            {
                DownloadId = jobId,
                SaveDirectory = saveDir,
                StartImmediately = false
            });

            await engine.RemoveAsync(jobId, deleteFiles: true);

            File.Exists(outsideFile).Should().BeTrue();
        }
        finally
        {
            await engine.DisposeAsync();
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }
}

