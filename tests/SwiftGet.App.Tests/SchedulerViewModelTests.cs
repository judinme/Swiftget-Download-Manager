using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SwiftGet.App.ViewModels;
using SwiftGet.Core.Models;
using SwiftGet.Core.Storage;
using SwiftGet.Engine.Queues;
using SwiftGet.Engine.Scheduling;
using Xunit;

namespace SwiftGet.App.Tests;

public sealed class SchedulerViewModelTests : IAsyncDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly SqliteDatabase _db;
    private readonly MockQueueManager _queueManager;
    private readonly MockScheduler _scheduler;

    public SchedulerViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "swiftget_sched_vm_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test.db");
        _db = new SqliteDatabase(_dbPath);
        _queueManager = new MockQueueManager();
        _scheduler = new MockScheduler();
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync().ConfigureAwait(false);
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task InitializeAsync_ShouldLoadDefaultQueues()
    {
        var vm = new SchedulerViewModel(_db, _queueManager, _scheduler);
        await vm.InitializeAsync();

        vm.Queues.Should().NotBeEmpty();
        vm.SelectedQueue.Should().NotBeNull();
        vm.SelectedQueue!.Id.Should().Be("default");
        vm.IsScheduleEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task SaveSettingsAsync_ShouldPersistQueueConfiguration()
    {
        var vm = new SchedulerViewModel(_db, _queueManager, _scheduler);
        await vm.InitializeAsync();

        vm.IsScheduleEnabled = true;
        vm.StartTime = TimeSpan.FromHours(2);
        vm.HasStopTime = true;
        vm.StopTime = TimeSpan.FromHours(5);
        vm.DaySunday = false;
        vm.MaxConcurrentDownloads = 3;
        vm.SpeedLimitKbps = 512;
        vm.ExitAppOnCompletion = true;

        await vm.SaveSettingsAsync();

        var reloaded = await _db.GetQueueAsync("default");
        reloaded.Should().NotBeNull();
        reloaded!.IsActive.Should().BeTrue();
        reloaded.StartTimeUtc.Should().Be(TimeSpan.FromHours(2));
        reloaded.StopTimeUtc.Should().Be(TimeSpan.FromHours(5));
        (reloaded.ScheduleDaysMask & 64).Should().Be(0); // Sunday disabled
        reloaded.MaxConcurrentDownloads.Should().Be(3);
        reloaded.SpeedLimitBytesPerSec.Should().Be(512 * 1024);
        reloaded.AfterCompletionAction.Should().Be(AfterCompletionAction.ExitApp);
    }

    [Fact]
    public async Task ReorderingQueueItems_MoveUpAndDown_ShouldSwapOrderAndPersist()
    {
        await _db.InitializeAsync();
        var item1 = new DownloadItem
        {
            Id = "item_1",
            Url = "https://example.com/file1.zip",
            FileName = "file1.zip",
            DestinationPath = "C:\\Downloads\\file1.zip",
            QueueId = "default",
            Priority = 0
        };
        var item2 = new DownloadItem
        {
            Id = "item_2",
            Url = "https://example.com/file2.zip",
            FileName = "file2.zip",
            DestinationPath = "C:\\Downloads\\file2.zip",
            QueueId = "default",
            Priority = 1
        };

        await _db.SaveDownloadAsync(item1);
        await _db.SaveDownloadAsync(item2);

        var vm = new SchedulerViewModel(_db, _queueManager, _scheduler);
        await vm.InitializeAsync();
        await vm.LoadQueueItemsAsync("default");

        vm.QueueItems.Should().HaveCount(2);

        // Select item2 and move up
        vm.SelectedQueueItem = vm.QueueItems[1];
        await vm.MoveUpAsync();

        vm.QueueItems[0].Id.Should().Be("item_2");
        vm.QueueItems[1].Id.Should().Be("item_1");

        // Move down again
        await vm.MoveDownAsync();
        vm.QueueItems[0].Id.Should().Be("item_1");
        vm.QueueItems[1].Id.Should().Be("item_2");
    }

    [Fact]
    public async Task StartAndStopQueueCommands_ShouldInvokeQueueManager()
    {
        var vm = new SchedulerViewModel(_db, _queueManager, _scheduler);
        await vm.InitializeAsync();

        await vm.StartQueueNowAsync();
        _queueManager.StartedQueues.Should().Contain("default");

        await vm.StopQueueNowAsync();
        _queueManager.StoppedQueues.Should().Contain("default");
    }

    private sealed class MockQueueManager : IQueueManager
    {
        public HashSet<string> StartedQueues { get; } = [];
        public HashSet<string> StoppedQueues { get; } = [];

        public event EventHandler<QueueCompletedEventArgs>? QueueCompleted { add { } remove { } }
        public event EventHandler<DownloadItem>? DownloadStarted { add { } remove { } }
        public event EventHandler<DownloadItem>? DownloadCompleted { add { } remove { } }
        public event EventHandler<DownloadItem>? DownloadFailed { add { } remove { } }

        public Task StartQueueAsync(string queueId, CancellationToken ct = default)
        {
            StartedQueues.Add(queueId);
            return Task.CompletedTask;
        }

        public Task StopQueueAsync(string queueId, CancellationToken ct = default)
        {
            StoppedQueues.Add(queueId);
            return Task.CompletedTask;
        }

        public bool IsQueueRunning(string queueId) => StartedQueues.Contains(queueId);
        public IReadOnlyList<string> GetRunningQueueIds() => [.. StartedQueues];
        public Task<IReadOnlyList<DownloadItem>> GetQueueItemsAsync(string queueId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DownloadItem>>([]);
        public Task MoveItemAsync(string queueId, string downloadId, int direction, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveFromQueueAsync(string downloadId, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MockScheduler : IScheduler
    {
        public bool IsRunning => false;
        public void Start() { }
        public void Stop() { }
        public Task EvaluateSchedulesAsync(DateTime currentLocalTime, CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() { }
    }
}
