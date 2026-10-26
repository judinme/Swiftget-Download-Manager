using FluentAssertions;
using SwiftGet.Core.Models;
using SwiftGet.Core.Storage;
using Xunit;

namespace SwiftGet.Engine.Tests;

public class SqliteDatabaseTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"swiftget_test_{Guid.NewGuid():N}.db");
    private SqliteDatabase? _db;

    public async Task InitializeAsync()
    {
        _db = new SqliteDatabase(_dbPath);
        await _db.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        if (_db != null)
        {
            await _db.DisposeAsync();
        }

        try
        {
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
            var walFile = _dbPath + "-wal";
            var shmFile = _dbPath + "-shm";
            if (File.Exists(walFile)) File.Delete(walFile);
            if (File.Exists(shmFile)) File.Delete(shmFile);
        }
        catch
        {
            // Best effort cleanup
        }
    }

    [Fact]
    public async Task SaveAndGetDownload_ShouldPersistAllFields()
    {
        var item = new DownloadItem
        {
            Url = "https://example.com/test.iso",
            FinalUrl = "https://cdn.example.com/test.iso",
            DestinationPath = "C:\\Downloads\\test.iso",
            FileName = "test.iso",
            TotalBytes = 104857600,
            DownloadedBytes = 52428800,
            Status = DownloadStatus.Downloading,
            ETag = "abc-123",
            SupportsRanges = true,
            ChecksumExpected = "deadbeef",
            ChecksumType = ChecksumType.Sha256
        };

        await _db!.SaveDownloadAsync(item);

        var loaded = await _db.GetDownloadAsync(item.Id);
        loaded.Should().NotBeNull();
        loaded!.Url.Should().Be(item.Url);
        loaded.FinalUrl.Should().Be(item.FinalUrl);
        loaded.TotalBytes.Should().Be(item.TotalBytes);
        loaded.DownloadedBytes.Should().Be(item.DownloadedBytes);
        loaded.Status.Should().Be(DownloadStatus.Downloading);
        loaded.ETag.Should().Be(item.ETag);
        loaded.SupportsRanges.Should().BeTrue();
    }

    [Fact]
    public async Task Segments_SaveAndCheckpoint_ShouldUpdateOffsetsCorrectly()
    {
        var downloadId = Guid.NewGuid().ToString("N");
        var parentDownload = new DownloadItem
        {
            Id = downloadId,
            Url = "https://example.com/file.bin",
            FinalUrl = "https://example.com/file.bin",
            DestinationPath = "C:\\file.bin",
            FileName = "file.bin"
        };
        await _db!.SaveDownloadAsync(parentDownload);

        var segments = new List<DownloadSegment>
        {
            new()
            {
                DownloadId = downloadId,
                SegmentIndex = 0,
                StartOffset = 0,
                EndOffset = 999,
                CurrentOffset = 0,
                Status = SegmentStatus.Pending
            },
            new()
            {
                DownloadId = downloadId,
                SegmentIndex = 1,
                StartOffset = 1000,
                EndOffset = 1999,
                CurrentOffset = 1000,
                Status = SegmentStatus.Pending
            }
        };

        await _db.SaveSegmentsAsync(downloadId, segments);
        var loaded = await _db.GetSegmentsAsync(downloadId);
        loaded.Should().HaveCount(2);

        // Advance progress
        segments[0].CurrentOffset = 500;
        segments[0].Status = SegmentStatus.Downloading;
        segments[1].CurrentOffset = 1500;

        await _db.CheckpointProgressAsync(downloadId, 1000, segments);

        var updated = await _db.GetSegmentsAsync(downloadId);
        updated[0].CurrentOffset.Should().Be(500);
        updated[0].Status.Should().Be(SegmentStatus.Downloading);
        updated[1].CurrentOffset.Should().Be(1500);
    }
}
