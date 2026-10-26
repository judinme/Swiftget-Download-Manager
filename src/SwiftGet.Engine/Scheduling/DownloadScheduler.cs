using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SwiftGet.Core.Storage;
using SwiftGet.Engine.Queues;

namespace SwiftGet.Engine.Scheduling;

public class DownloadScheduler : IScheduler
{
    private readonly ISwiftGetDatabase _db;
    private readonly IQueueManager _queueManager;
    private readonly ILogger<DownloadScheduler> _logger;
    private readonly TimeSpan _checkInterval;

    private PeriodicTimer? _timer;
    private CancellationTokenSource? _cts;
    private Task? _timerTask;
    private bool _disposed;

    public bool IsRunning => _timerTask != null && !_timerTask.IsCompleted;

    public DownloadScheduler(
        ISwiftGetDatabase db,
        IQueueManager queueManager,
        TimeSpan? checkInterval = null,
        ILogger<DownloadScheduler>? logger = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _queueManager = queueManager ?? throw new ArgumentNullException(nameof(queueManager));
        _checkInterval = checkInterval ?? TimeSpan.FromSeconds(1);
        _logger = logger ?? NullLogger<DownloadScheduler>.Instance;
    }

    public void Start()
    {
        if (IsRunning) return;

        _logger.LogInformation("Download scheduler started with check interval {Interval}s", _checkInterval.TotalSeconds);
    }

    public void Stop()
    {
        if (_cts != null)
        {
            _cts.Cancel();
            try
            {
                _timerTask?.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
            _cts.Dispose();
            _cts = null;
        }

        _timer?.Dispose();
        _timer = null;
        _timerTask = null;
        _logger.LogInformation("Download scheduler stopped.");
    }

    public void NotifySystemResumed()
    {
        _logger.LogInformation("System resumed. Re-evaluating download schedules.");
        _ = Task.Run(async () =>
        {
            try
            {
                var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.Local);
                await EvaluateSchedulesAsync(localNow, _cts?.Token ?? CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error re-evaluating schedules on system resume.");
            }
        });
    }

    private async Task RunTimerLoopAsync(CancellationToken ct)
    {
        var lastTickUtc = DateTime.UtcNow;
        while (!ct.IsCancellationRequested && _timer != null && await _timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try
            {
                var nowUtc = DateTime.UtcNow;
                if (nowUtc - lastTickUtc > TimeSpan.FromSeconds(5 * Math.Max(1, _checkInterval.TotalSeconds)))
                {
                    _logger.LogInformation("Timer resume jump detected ({Elapsed}s elapsed). Re-evaluating schedules.", (nowUtc - lastTickUtc).TotalSeconds);
                }
                lastTickUtc = nowUtc;

                var localNow = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, TimeZoneInfo.Local);
                await EvaluateSchedulesAsync(localNow, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error evaluating download queue schedules.");
            }
        }
    }

    public async Task EvaluateSchedulesAsync(DateTime currentLocalTime, CancellationToken ct = default)
    {
        var queues = await _db.GetAllQueuesAsync(ct).ConfigureAwait(false);
        var dayBit = GetDayOfWeekMaskBit(currentLocalTime.DayOfWeek);
        var currentTimeOfDay = currentLocalTime.TimeOfDay;

        foreach (var queue in queues)
        {
            if (!queue.IsActive) continue;

            // Check day mask
            if ((queue.ScheduleDaysMask & dayBit) == 0)
            {
                // Today is not a scheduled day for this queue
                continue;
            }

            var isRunning = _queueManager.IsQueueRunning(queue.Id);

            // 1. Check Stop Time first (if queue is currently running)
            if (isRunning && queue.StopTimeUtc.HasValue)
            {
                var stopTime = queue.StopTimeUtc.Value;
                // If current time passed stop time
                if (currentTimeOfDay >= stopTime)
                {
                    _logger.LogInformation("Scheduled stop triggered for queue {QueueName} at {Time}", queue.Name, currentTimeOfDay);
                    await _queueManager.StopQueueAsync(queue.Id, ct).ConfigureAwait(false);
                    continue;
                }
            }

            // 2. Check Start Time
            if (!isRunning && queue.StartTimeUtc.HasValue)
            {
                var startTime = queue.StartTimeUtc.Value;
                bool shouldStart = false;

                if (queue.StopTimeUtc.HasValue)
                {
                    var stopTime = queue.StopTimeUtc.Value;
                    if (startTime <= stopTime)
                    {
                        // Normal window: e.g. 02:00 to 06:00
                        shouldStart = currentTimeOfDay >= startTime && currentTimeOfDay < stopTime;
                    }
                    else
                    {
                        // Overnight window: e.g. 23:00 to 05:00
                        shouldStart = currentTimeOfDay >= startTime || currentTimeOfDay < stopTime;
                    }
                }
                else
                {
                    // Start at/after startTime
                    shouldStart = currentTimeOfDay >= startTime;
                }

                if (shouldStart)
                {
                    _logger.LogInformation("Scheduled start triggered for queue {QueueName} at {Time}", queue.Name, currentTimeOfDay);
                    await _queueManager.StartQueueAsync(queue.Id, ct).ConfigureAwait(false);
                }
            }
        }
    }

    public static int GetDayOfWeekMaskBit(DayOfWeek dayOfWeek)
    {
        return dayOfWeek switch
        {
            DayOfWeek.Monday => 1,
            DayOfWeek.Tuesday => 2,
            DayOfWeek.Wednesday => 4,
            DayOfWeek.Thursday => 8,
            DayOfWeek.Friday => 16,
            DayOfWeek.Saturday => 32,
            DayOfWeek.Sunday => 64,
            _ => 0
        };
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            Stop();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
