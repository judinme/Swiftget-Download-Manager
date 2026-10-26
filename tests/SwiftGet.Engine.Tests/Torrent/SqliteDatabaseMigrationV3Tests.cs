using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using SwiftGet.Core.Models;
using SwiftGet.Core.Storage;
using Xunit;

namespace SwiftGet.Engine.Tests.Torrent;

public class SqliteDatabaseMigrationV3Tests : IAsyncLifetime
{
    private readonly string _dbPath;
    private SqliteDatabase _db = null!;

    public SqliteDatabaseMigrationV3Tests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"swiftget_test_v3_{Guid.NewGuid():N}.db");
    }

    public async Task InitializeAsync()
    {
        _db = new SqliteDatabase(_dbPath);
        await _db.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        if (File.Exists(_dbPath))
        {
            try { File.Delete(_dbPath); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task MigrationV3_PreservesExistingDownloads_AndAddsJobKindAndInfoHash()
    {
        // Save standard HTTP download
        var httpItem = new DownloadItem
        {
            Id = "http_dl_001",
            Url = "https://example.com/test.iso",
            FinalUrl = "https://example.com/test.iso",
            FileName = "test.iso",
            DestinationPath = Path.Combine(Path.GetTempPath(), "test.iso"),
            TotalBytes = 1048576,
            DownloadedBytes = 524288,
            Status = DownloadStatus.Downloading,
            JobKind = JobKind.Http,
            QueueId = "default",
            CategoryId = "general"
        };

        await _db.SaveDownloadAsync(httpItem);

        var retrieved = await _db.GetDownloadAsync("http_dl_001");
        retrieved.Should().NotBeNull();
        retrieved!.JobKind.Should().Be(JobKind.Http);
        retrieved.InfoHash.Should().BeNull();
        retrieved.FileName.Should().Be("test.iso");
        retrieved.DownloadedBytes.Should().Be(524288);
    }

    [Fact]
    public async Task TorrentStorage_SaveAndRetrieve_RoundTripsAllFields()
    {
        var torrentId = Guid.NewGuid().ToString("N");
        var infoHash = "4a5b6c7d8e9f0123456789abcdef0123456789ab";

        // 1. Create parent download item with JobKind.Torrent
        var dlItem = new DownloadItem
        {
            Id = torrentId,
            Url = $"magnet:?xt=urn:btih:{infoHash}&dn=LinuxDistro",
            FileName = "LinuxDistro",
            DestinationPath = Path.Combine(Path.GetTempPath(), "Torrents", "LinuxDistro"),
            TotalBytes = 2147483648,
            DownloadedBytes = 1073741824,
            Status = DownloadStatus.Downloading,
            JobKind = JobKind.Torrent,
            InfoHash = infoHash,
            QueueId = "default",
            CategoryId = "torrents"
        };
        await _db.SaveDownloadAsync(dlItem);

        // 2. Create TorrentItem
        var resumeBytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03 };
        var torrent = new TorrentItem
        {
            Id = torrentId,
            InfoHash = infoHash,
            MagnetUri = dlItem.Url,
            Name = "LinuxDistro",
            TotalBytes = 2147483648,
            PieceLength = 1048576,
            PieceCount = 2048,
            IsPrivate = false,
            SeedRatioLimit = 2.0,
            SeedTimeLimitMins = 60,
            UploadedBytes = 536870912,
            DownloadedBytes = 1073741824,
            FastResumeBlob = resumeBytes,
            Comment = "Official Linux Release",
            CreatedBy = "SwiftGet Test Packager"
        };
        await _db.SaveTorrentAsync(torrent);

        // 3. Retrieve by ID
        var retrieved = await _db.GetTorrentAsync(torrentId);
        retrieved.Should().NotBeNull();
        retrieved!.Id.Should().Be(torrentId);
        retrieved.InfoHash.Should().Be(infoHash);
        retrieved.Name.Should().Be("LinuxDistro");
        retrieved.TotalBytes.Should().Be(2147483648);
        retrieved.PieceLength.Should().Be(1048576);
        retrieved.PieceCount.Should().Be(2048);
        retrieved.IsPrivate.Should().BeFalse();
        retrieved.SeedRatioLimit.Should().Be(2.0);
        retrieved.SeedTimeLimitMins.Should().Be(60);
        retrieved.UploadedBytes.Should().Be(536870912);
        retrieved.DownloadedBytes.Should().Be(1073741824);
        retrieved.CurrentRatio.Should().BeApproximately(0.5, 0.001);
        retrieved.FastResumeBlob.Should().Equal(resumeBytes);

        // 4. Retrieve by InfoHash (case-insensitive)
        var retrievedByHash = await _db.GetTorrentByInfoHashAsync(infoHash.ToUpperInvariant());
        retrievedByHash.Should().NotBeNull();
        retrievedByHash!.Id.Should().Be(torrentId);
    }

    [Fact]
    public async Task TorrentFiles_SaveAndSelectivePriority_WorksCorrectly()
    {
        var torrentId = Guid.NewGuid().ToString("N");
        var dlItem = new DownloadItem
        {
            Id = torrentId,
            Url = "magnet:?xt=urn:btih:1111111111111111111111111111111111111111",
            FileName = "MultiFileBundle",
            DestinationPath = Path.Combine(Path.GetTempPath(), "MultiFileBundle"),
            JobKind = JobKind.Torrent,
            QueueId = "default",
            CategoryId = "torrents"
        };
        await _db.SaveDownloadAsync(dlItem);

        var torrent = new TorrentItem
        {
            Id = torrentId,
            InfoHash = "1111111111111111111111111111111111111111",
            Name = "MultiFileBundle",
            TotalBytes = 3000
        };
        await _db.SaveTorrentAsync(torrent);

        var file1Id = Guid.NewGuid().ToString("N");
        var file2Id = Guid.NewGuid().ToString("N");
        var file3Id = Guid.NewGuid().ToString("N");

        var files = new List<TorrentFileItem>
        {
            new() { Id = file1Id, TorrentId = torrentId, FileIndex = 0, RelativePath = "file1.txt", FileSizeBytes = 1000, DownloadedBytes = 1000, Priority = TorrentFilePriority.Normal, IsSelected = true },
            new() { Id = file2Id, TorrentId = torrentId, FileIndex = 1, RelativePath = "file2.mkv", FileSizeBytes = 1500, DownloadedBytes = 750, Priority = TorrentFilePriority.High, IsSelected = true },
            new() { Id = file3Id, TorrentId = torrentId, FileIndex = 2, RelativePath = "file3.nfo", FileSizeBytes = 500, DownloadedBytes = 0, Priority = TorrentFilePriority.DoNotDownload, IsSelected = false }
        };

        await _db.SaveTorrentFilesAsync(torrentId, files);

        var loadedFiles = await _db.GetTorrentFilesAsync(torrentId);
        loadedFiles.Should().HaveCount(3);
        loadedFiles[0].RelativePath.Should().Be("file1.txt");
        loadedFiles[0].ProgressPercentage.Should().Be(100.0);
        loadedFiles[1].Priority.Should().Be(TorrentFilePriority.High);
        loadedFiles[1].ProgressPercentage.Should().Be(50.0);
        loadedFiles[2].Priority.Should().Be(TorrentFilePriority.DoNotDownload);
        loadedFiles[2].IsSelected.Should().BeFalse();

        // Update single file priority
        await _db.UpdateTorrentFilePriorityAsync(file3Id, TorrentFilePriority.Highest, isSelected: true);
        var updatedFiles = await _db.GetTorrentFilesAsync(torrentId);
        updatedFiles[2].Priority.Should().Be(TorrentFilePriority.Highest);
        updatedFiles[2].IsSelected.Should().BeTrue();
    }

    [Fact]
    public async Task TorrentTrackers_SaveAndRetrieve_PersistsCorrectTiers()
    {
        var torrentId = Guid.NewGuid().ToString("N");
        var dlItem = new DownloadItem
        {
            Id = torrentId,
            Url = "magnet:?xt=urn:btih:2222222222222222222222222222222222222222",
            FileName = "TrackerTest",
            DestinationPath = Path.Combine(Path.GetTempPath(), "TrackerTest"),
            JobKind = JobKind.Torrent,
            QueueId = "default",
            CategoryId = "torrents"
        };
        await _db.SaveDownloadAsync(dlItem);

        var torrent = new TorrentItem
        {
            Id = torrentId,
            InfoHash = "2222222222222222222222222222222222222222",
            Name = "TrackerTest",
            TotalBytes = 1000
        };
        await _db.SaveTorrentAsync(torrent);

        var trackers = new List<TorrentTrackerItem>
        {
            new() { Id = Guid.NewGuid().ToString("N"), TorrentId = torrentId, Tier = 0, AnnounceUrl = "https://tracker1.example.com/announce", Status = "Working", Seeders = 15, Leechers = 3 },
            new() { Id = Guid.NewGuid().ToString("N"), TorrentId = torrentId, Tier = 1, AnnounceUrl = "udp://tracker2.example.com:6969/announce", Status = "NotContacted", Seeders = 0, Leechers = 0 }
        };

        await _db.SaveTorrentTrackersAsync(torrentId, trackers);

        var loadedTrackers = await _db.GetTorrentTrackersAsync(torrentId);
        loadedTrackers.Should().HaveCount(2);
        loadedTrackers[0].AnnounceUrl.Should().Be("https://tracker1.example.com/announce");
        loadedTrackers[0].Seeders.Should().Be(15);
        loadedTrackers[1].AnnounceUrl.Should().Be("udp://tracker2.example.com:6969/announce");
    }

    [Fact]
    public async Task DeleteDownload_CascadesToTorrentFilesAndTrackers()
    {
        var torrentId = Guid.NewGuid().ToString("N");
        var dlItem = new DownloadItem
        {
            Id = torrentId,
            Url = "magnet:?xt=urn:btih:3333333333333333333333333333333333333333",
            FileName = "CascadeTest",
            DestinationPath = Path.Combine(Path.GetTempPath(), "CascadeTest"),
            JobKind = JobKind.Torrent,
            QueueId = "default",
            CategoryId = "torrents"
        };
        await _db.SaveDownloadAsync(dlItem);

        var torrent = new TorrentItem
        {
            Id = torrentId,
            InfoHash = "3333333333333333333333333333333333333333",
            Name = "CascadeTest",
            TotalBytes = 500
        };
        await _db.SaveTorrentAsync(torrent);

        await _db.SaveTorrentFilesAsync(torrentId, new List<TorrentFileItem>
        {
            new() { Id = Guid.NewGuid().ToString("N"), TorrentId = torrentId, FileIndex = 0, RelativePath = "item.bin", FileSizeBytes = 500 }
        });

        await _db.SaveTorrentTrackersAsync(torrentId, new List<TorrentTrackerItem>
        {
            new() { Id = Guid.NewGuid().ToString("N"), TorrentId = torrentId, Tier = 0, AnnounceUrl = "https://tracker.cascade.com" }
        });

        // Delete parent download
        await _db.DeleteDownloadAsync(torrentId);

        // Verify cascade deletion
        var loadedTorrent = await _db.GetTorrentAsync(torrentId);
        loadedTorrent.Should().BeNull();

        var loadedFiles = await _db.GetTorrentFilesAsync(torrentId);
        loadedFiles.Should().BeEmpty();

        var loadedTrackers = await _db.GetTorrentTrackersAsync(torrentId);
        loadedTrackers.Should().BeEmpty();
    }
}
