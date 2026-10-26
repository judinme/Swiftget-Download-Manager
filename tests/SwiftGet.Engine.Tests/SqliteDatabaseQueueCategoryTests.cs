using FluentAssertions;
using SwiftGet.Core.Models;
using SwiftGet.Core.Storage;
using Xunit;

namespace SwiftGet.Engine.Tests;

public class SqliteDatabaseQueueCategoryTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"swiftget_qc_test_{Guid.NewGuid():N}.db");
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
    public async Task InitializeAsync_ShouldSeedDefaultCategoriesAndQueues()
    {
        var queues = await _db!.GetAllQueuesAsync();
        queues.Should().NotBeEmpty();
        queues.Should().Contain(q => q.Id == "default" && q.Name == "Main Download Queue");
        queues.Should().Contain(q => q.Id == "sync" && q.Name == "Synchronization Queue");

        var categories = await _db.GetAllCategoriesAsync();
        categories.Should().HaveCountGreaterOrEqualTo(6);
        categories.Should().Contain(c => c.Id == "compressed" && c.Name == "Compressed");
        categories.Should().Contain(c => c.Id == "documents" && c.Name == "Documents");
        categories.Should().Contain(c => c.Id == "general" && c.IsDefault);
    }

    [Fact]
    public async Task SaveAndGetQueue_ShouldPersistAllProperties()
    {
        var customQueue = new DownloadQueue
        {
            Id = "night-queue",
            Name = "Night Owl Queue",
            MaxConcurrentDownloads = 2,
            SpeedLimitBytesPerSec = 5 * 1024 * 1024,
            IsActive = true,
            ScheduleDaysMask = 31, // Mon-Fri
            StartTimeUtc = new TimeSpan(2, 0, 0),
            StopTimeUtc = new TimeSpan(6, 30, 0),
            AfterCompletionAction = AfterCompletionAction.Shutdown,
            CustomActionCommand = "shutdown.exe /s"
        };

        await _db!.SaveQueueAsync(customQueue);

        var loaded = await _db.GetQueueAsync("night-queue");
        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be("Night Owl Queue");
        loaded.MaxConcurrentDownloads.Should().Be(2);
        loaded.SpeedLimitBytesPerSec.Should().Be(5 * 1024 * 1024);
        loaded.IsActive.Should().BeTrue();
        loaded.ScheduleDaysMask.Should().Be(31);
        loaded.StartTimeUtc.Should().Be(new TimeSpan(2, 0, 0));
        loaded.StopTimeUtc.Should().Be(new TimeSpan(6, 30, 0));
        loaded.AfterCompletionAction.Should().Be(AfterCompletionAction.Shutdown);
        loaded.CustomActionCommand.Should().Be("shutdown.exe /s");

        await _db.DeleteQueueAsync("night-queue");
        var deleted = await _db.GetQueueAsync("night-queue");
        deleted.Should().BeNull();
    }

    [Fact]
    public async Task SaveAndGetCategory_ShouldPersistAndManageRules()
    {
        var rule = new CategoryRule
        {
            Id = "torrents",
            Name = "Torrents",
            TargetDirectory = "C:\\Downloads\\Torrents",
            FileExtensions = "torrent,magnet",
            IconKey = "Link",
            IsDefault = false
        };

        await _db!.SaveCategoryAsync(rule);

        var loaded = await _db.GetCategoryAsync("torrents");
        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be("Torrents");
        loaded.FileExtensions.Should().Be("torrent,magnet");

        await _db.DeleteCategoryAsync("torrents");
        var deleted = await _db.GetCategoryAsync("torrents");
        deleted.Should().BeNull();
    }

    [Fact]
    public async Task GetDownloadsByQueue_ShouldReturnItemsInPriorityOrder()
    {
        var qId = "test-q-" + Guid.NewGuid().ToString("N");
        var item1 = new DownloadItem
        {
            Id = "item-1",
            Url = "http://example.com/file1.zip",
            DestinationPath = "C:\\file1.zip",
            QueueId = qId,
            Priority = 2,
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5)
        };
        var item2 = new DownloadItem
        {
            Id = "item-2",
            Url = "http://example.com/file2.zip",
            DestinationPath = "C:\\file2.zip",
            QueueId = qId,
            Priority = 1,
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-10)
        };

        await _db!.SaveDownloadAsync(item1);
        await _db.SaveDownloadAsync(item2);

        var queueItems = await _db.GetDownloadsByQueueAsync(qId);
        queueItems.Should().HaveCount(2);
        queueItems[0].Id.Should().Be("item-2"); // Priority 1 first
        queueItems[1].Id.Should().Be("item-1"); // Priority 2 second

        // Reprioritize item 1 to priority 0
        await _db.UpdateDownloadPriorityAsync("item-1", 0);
        var updatedItems = await _db.GetDownloadsByQueueAsync(qId);
        updatedItems[0].Id.Should().Be("item-1"); // Now priority 0 first
        updatedItems[1].Id.Should().Be("item-2");
    }
}
