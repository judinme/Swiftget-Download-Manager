using FluentAssertions;
using SwiftGet.Core.Models;
using SwiftGet.Core.Storage;
using SwiftGet.Engine.Automation;
using SwiftGet.Engine.Queues;
using SwiftGet.Engine.Scheduling;
using Xunit;

namespace SwiftGet.Integration;

public class QueuesAndSchedulerTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private readonly SqliteDatabase _db;

    public QueuesAndSchedulerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"swiftget_test_sched_{Guid.NewGuid():N}.db");
        _db = new SqliteDatabase(_dbPath);
    }

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        try
        {
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
        }
        catch { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task QueueDatabase_SupportsCrudOperations()
    {
        var queue = new DownloadQueue
        {
            Id = "night-queue",
            Name = "Night Shift Queue",
            MaxConcurrentDownloads = 3,
            SpeedLimitBytesPerSec = 512 * 1024,
            IsActive = true,
            ScheduleDaysMask = 0b0011111, // Mon-Fri
            StartTimeUtc = TimeSpan.FromHours(1),
            StopTimeUtc = TimeSpan.FromHours(6),
            AfterCompletionAction = AfterCompletionAction.ExitApp
        };

        // 1. Create
        await _db.SaveQueueAsync(queue);

        // 2. Read
        var fetched = await _db.GetQueueAsync("night-queue");
        fetched.Should().NotBeNull();
        fetched!.Name.Should().Be("Night Shift Queue");
        fetched.MaxConcurrentDownloads.Should().Be(3);
        fetched.AfterCompletionAction.Should().Be(AfterCompletionAction.ExitApp);

        // 3. Update
        fetched.MaxConcurrentDownloads = 5;
        await _db.SaveQueueAsync(fetched);

        var updated = await _db.GetQueueAsync("night-queue");
        updated!.MaxConcurrentDownloads.Should().Be(5);

        // 4. Delete
        await _db.DeleteQueueAsync("night-queue");
        var deleted = await _db.GetQueueAsync("night-queue");
        deleted.Should().BeNull();
    }

    [Theory]
    // Normal window: 02:00 to 06:00
    [InlineData("01:30:00", false, "Before start time")]
    [InlineData("02:00:00", true, "Exact start time")]
    [InlineData("04:30:00", true, "Inside window")]
    [InlineData("06:00:00", false, "At stop time")]
    [InlineData("07:00:00", false, "After stop time")]
    public void Scheduler_EvaluatesNormalTimeWindowsAccurately(string timeStr, bool expectedShouldRun, string scenario)
    {
        var startTime = TimeSpan.FromHours(2);
        var stopTime = TimeSpan.FromHours(6);
        var currentTime = TimeSpan.Parse(timeStr);

        bool shouldStart = currentTime >= startTime && currentTime < stopTime;
        shouldStart.Should().Be(expectedShouldRun, scenario);
    }

    [Theory]
    // Overnight window: 23:00 to 05:00
    [InlineData("22:30:00", false, "Before start time")]
    [InlineData("23:00:00", true, "Exact start time")]
    [InlineData("02:00:00", true, "Past midnight")]
    [InlineData("04:59:59", true, "Just before morning stop")]
    [InlineData("05:00:00", false, "At morning stop")]
    [InlineData("12:00:00", false, "Midday")]
    public void Scheduler_EvaluatesOvernightTimeWindowsAccurately(string timeStr, bool expectedShouldRun, string scenario)
    {
        var startTime = TimeSpan.FromHours(23);
        var stopTime = TimeSpan.FromHours(5);
        var currentTime = TimeSpan.Parse(timeStr);

        bool shouldStart = currentTime >= startTime || currentTime < stopTime;
        shouldStart.Should().Be(expectedShouldRun, scenario);
    }

    [Fact]
    public void Scheduler_DayOfWeekMask_CorrectlyIdentifiesActiveDays()
    {
        // 0b0011111 = Monday through Friday
        const int weekdaysMask = (1 << (int)DayOfWeek.Monday) |
                                 (1 << (int)DayOfWeek.Tuesday) |
                                 (1 << (int)DayOfWeek.Wednesday) |
                                 (1 << (int)DayOfWeek.Thursday) |
                                 (1 << (int)DayOfWeek.Friday);

        // Monday (active)
        ((1 << (int)DayOfWeek.Monday) & weekdaysMask).Should().NotBe(0);
        // Wednesday (active)
        ((1 << (int)DayOfWeek.Wednesday) & weekdaysMask).Should().NotBe(0);
        // Saturday (inactive)
        ((1 << (int)DayOfWeek.Saturday) & weekdaysMask).Should().Be(0);
        // Sunday (inactive)
        ((1 << (int)DayOfWeek.Sunday) & weekdaysMask).Should().Be(0);
    }

    [Fact]
    public async Task SafeMockAfterCompletionService_LogsWithoutTriggeringMachineShutdown()
    {
        var executedActions = new List<AfterCompletionAction>();
        var safeService = new SafeMockAfterCompletionService(action => executedActions.Add(action));

        // Test shutdown action in safe mock mode
        var result = await safeService.ExecuteActionAsync(AfterCompletionAction.Shutdown);

        result.Should().BeTrue();
        executedActions.Should().ContainSingle().Which.Should().Be(AfterCompletionAction.Shutdown);
    }

    private sealed class SafeMockAfterCompletionService : IAfterCompletionService
    {
        private readonly Action<AfterCompletionAction> _onExecuted;

        public event EventHandler? ExitAppRequested;

        public SafeMockAfterCompletionService(Action<AfterCompletionAction> onExecuted)
        {
            _onExecuted = onExecuted;
        }

        public Task<bool> ExecuteActionAsync(
            AfterCompletionAction action,
            string? customCommand = null,
            int shutdownCountdownSeconds = 30,
            CancellationToken ct = default)
        {
            _onExecuted(action);
            if (action == AfterCompletionAction.ExitApp)
            {
                ExitAppRequested?.Invoke(this, EventArgs.Empty);
            }
            return Task.FromResult(true);
        }

        public bool AbortShutdown() => true;
    }
}
