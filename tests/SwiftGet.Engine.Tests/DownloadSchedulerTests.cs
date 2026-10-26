using FluentAssertions;
using SwiftGet.Core.Models;
using SwiftGet.Core.Storage;
using SwiftGet.Engine.Queues;
using SwiftGet.Engine.Scheduling;
using Xunit;

namespace SwiftGet.Engine.Tests;

public class DownloadSchedulerTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"sched_test_{Guid.NewGuid():N}.db");
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
        catch { }
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

        public bool IsQueueRunning(string queueId) => StartedQueues.Contains(queueId) && !StoppedQueues.Contains(queueId);

        public IReadOnlyList<string> GetRunningQueueIds() => StartedQueues.Except(StoppedQueues).ToList();

        public Task<IReadOnlyList<DownloadItem>> GetQueueItemsAsync(string queueId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DownloadItem>>([]);

        public Task MoveItemAsync(string queueId, string downloadId, int direction, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task RemoveFromQueueAsync(string downloadId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task EvaluateSchedulesAsync_MatchingTimeAndDay_ShouldStartQueue()
    {
        var qId = "sched-start-" + Guid.NewGuid().ToString("N");
        var queue = new DownloadQueue
        {
            Id = qId,
            Name = "Morning Queue",
            IsActive = true,
            ScheduleDaysMask = 1, // Monday only
            StartTimeUtc = new TimeSpan(8, 0, 0),
            StopTimeUtc = new TimeSpan(12, 0, 0)
        };
        await _db!.SaveQueueAsync(queue);

        var mockQm = new MockQueueManager();
        using var scheduler = new DownloadScheduler(_db, mockQm);

        // Monday at 09:30:00 -> should start
        var mondayNineThirty = new DateTime(2026, 10, 26, 9, 30, 0); // Oct 26, 2026 is a Monday
        mondayNineThirty.DayOfWeek.Should().Be(DayOfWeek.Monday);

        await scheduler.EvaluateSchedulesAsync(mondayNineThirty);

        mockQm.StartedQueues.Should().Contain(qId);
        mockQm.StoppedQueues.Should().NotContain(qId);
    }

    [Fact]
    public async Task EvaluateSchedulesAsync_InactiveDay_ShouldNotStartQueue()
    {
        var qId = "sched-inact-" + Guid.NewGuid().ToString("N");
        var queue = new DownloadQueue
        {
            Id = qId,
            Name = "Weekday Queue",
            IsActive = true,
            ScheduleDaysMask = 31, // Monday to Friday
            StartTimeUtc = new TimeSpan(8, 0, 0),
            StopTimeUtc = new TimeSpan(12, 0, 0)
        };
        await _db!.SaveQueueAsync(queue);

        var mockQm = new MockQueueManager();
        using var scheduler = new DownloadScheduler(_db, mockQm);

        // Sunday at 09:30:00 -> should NOT start
        var sundayNineThirty = new DateTime(2026, 10, 25, 9, 30, 0); // Oct 25, 2026 is a Sunday
        sundayNineThirty.DayOfWeek.Should().Be(DayOfWeek.Sunday);

        await scheduler.EvaluateSchedulesAsync(sundayNineThirty);

        mockQm.StartedQueues.Should().NotContain(qId);
    }

    [Fact]
    public async Task EvaluateSchedulesAsync_PastStopTime_ShouldStopRunningQueue()
    {
        var qId = "sched-stop-" + Guid.NewGuid().ToString("N");
        var queue = new DownloadQueue
        {
            Id = qId,
            Name = "Time Bound Queue",
            IsActive = true,
            ScheduleDaysMask = 127, // All days
            StartTimeUtc = new TimeSpan(8, 0, 0),
            StopTimeUtc = new TimeSpan(11, 0, 0)
        };
        await _db!.SaveQueueAsync(queue);

        var mockQm = new MockQueueManager();
        // Queue is already running
        await mockQm.StartQueueAsync(qId);

        using var scheduler = new DownloadScheduler(_db, mockQm);

        // Current time is 11:15:00 -> past stop time
        var pastStopTime = new DateTime(2026, 10, 26, 11, 15, 0);
        await scheduler.EvaluateSchedulesAsync(pastStopTime);

        mockQm.StoppedQueues.Should().Contain(qId);
    }
}
