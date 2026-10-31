using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SwiftGet.Core.Models;
using SwiftGet.Core.Storage;
using SwiftGet.Engine.Automation;
using SwiftGet.Engine.Coordinator;
using SwiftGet.Engine.Throttling;

namespace SwiftGet.Engine.Queues;

public class QueueManager : IQueueManager, IDisposable
{
    private readonly ISwiftGetDatabase _db;
    private readonly DownloadCoordinator _coordinator;
    private readonly IAfterCompletionService? _afterCompletionService;
    private readonly ILogger<QueueManager> _logger;

    private readonly ConcurrentDictionary<string, (CancellationTokenSource Cts, Task WorkerTask)> _runningQueues = new();
    private bool _disposed;

    public event EventHandler<QueueCompletedEventArgs>? QueueCompleted;
    public event EventHandler<DownloadItem>? DownloadStarted;
    public event EventHandler<DownloadItem>? DownloadCompleted;
    public event EventHandler<DownloadItem>? DownloadFailed;

    public QueueManager(
        ISwiftGetDatabase db,
        DownloadCoordinator coordinator,
        IAfterCompletionService? afterCompletionService = null,
        ILogger<QueueManager>? logger = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _afterCompletionService = afterCompletionService;
        _logger = logger ?? NullLogger<QueueManager>.Instance;
    }

    public bool IsQueueRunning(string queueId)
    {
        return _runningQueues.ContainsKey(queueId);
    }

    public IReadOnlyList<string> GetRunningQueueIds()
    {
        return _runningQueues.Keys.ToList().AsReadOnly();
    }

    public Task<IReadOnlyList<DownloadItem>> GetQueueItemsAsync(string queueId, CancellationToken ct = default)
    {
        return _db.GetDownloadsByQueueAsync(queueId, ct);
    }

    private readonly object _startLock = new();

    public async Task StartQueueAsync(string queueId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(queueId)) return;

        lock (_startLock)
        {
            if (_runningQueues.ContainsKey(queueId))
            {
                _logger.LogInformation("Queue {QueueId} is already running.", queueId);
                return;
            }
        }

        var queue = await _db.GetQueueAsync(queueId, ct).ConfigureAwait(false);
        if (queue == null)
        {
            _logger.LogWarning("Queue {QueueId} not found in database.", queueId);
            return;
        }

        lock (_startLock)
        {
            if (_runningQueues.ContainsKey(queueId))
            {
                _logger.LogInformation("Queue {QueueId} is already running.", queueId);
                return;
            }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var task = Task.Run(() => ProcessQueueAsync(queue, cts.Token), cts.Token);

            _runningQueues[queueId] = (cts, task);
            _logger.LogInformation("Started queue: {QueueName} ({QueueId}) with max concurrency {MaxConns}", queue.Name, queue.Id, queue.MaxConcurrentDownloads);
        }
    }

    public async Task StopQueueAsync(string queueId, CancellationToken ct = default)
    {
        if (_runningQueues.TryRemove(queueId, out var entry))
        {
            _logger.LogInformation("Stopping queue {QueueId}...", queueId);
            entry.Cts.Cancel();
            try
            {
                await entry.WorkerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when cancelled
            }
            finally
            {
                entry.Cts.Dispose();
            }
        }
    }

    private async Task ProcessQueueAsync(DownloadQueue queue, CancellationToken ct)
    {
        int completedCount = 0;
        int failedCount = 0;

        using var semaphore = new SemaphoreSlim(Math.Max(1, queue.MaxConcurrentDownloads));
        using var queueLimiter = queue.SpeedLimitBytesPerSec > 0
            ? new TokenBucketLimiter(queue.SpeedLimitBytesPerSec)
            : null;

        var activeTasks = new List<Task>();

        try
        {
            var items = await _db.GetDownloadsByQueueAsync(queue.Id, ct).ConfigureAwait(false);
            var eligibleItems = items
                .Where(i => i.JobKind == JobKind.Http && i.Status != DownloadStatus.Completed && i.Status != DownloadStatus.Failed)
                .OrderBy(i => i.Priority)
                .ThenBy(i => i.CreatedAtUtc)
                .ToList();

            foreach (var item in eligibleItems)
            {
                ct.ThrowIfCancellationRequested();

                await semaphore.WaitAsync(ct).ConfigureAwait(false);

                var task = Task.Run(async () =>
                {
                    try
                    {
                        DownloadStarted?.Invoke(this, item);
                        await _coordinator.StartAsync(
                            item,
                            maxConnections: 8,
                            limiter: queueLimiter,
                            ct: ct).ConfigureAwait(false);

                        Interlocked.Increment(ref completedCount);
                        DownloadCompleted?.Invoke(this, item);
                    }
                    catch (OperationCanceledException)
                    {
                        item.Status = DownloadStatus.Paused;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Download {Id} failed in queue {QueueId}", item.Id, queue.Id);
                        Interlocked.Increment(ref failedCount);
                        DownloadFailed?.Invoke(this, item);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, ct);

                activeTasks.Add(task);
            }

            await Task.WhenAll(activeTasks).ConfigureAwait(false);

            if (!ct.IsCancellationRequested)
            {
                _logger.LogInformation("Queue {QueueName} completed. Finished: {Completed}, Failed: {Failed}", queue.Name, completedCount, failedCount);
                var args = new QueueCompletedEventArgs(queue.Id, queue.Name, completedCount, failedCount, queue.AfterCompletionAction, queue.CustomActionCommand);
                QueueCompleted?.Invoke(this, args);

                if (_afterCompletionService != null && queue.AfterCompletionAction != AfterCompletionAction.None)
                {
                    await _afterCompletionService.ExecuteActionAsync(
                        queue.AfterCompletionAction,
                        queue.CustomActionCommand,
                        ct: ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Queue {QueueName} execution cancelled.", queue.Name);
        }
        finally
        {
            _runningQueues.TryRemove(queue.Id, out _);
        }
    }

    public async Task MoveItemAsync(string queueId, string downloadId, int direction, CancellationToken ct = default)
    {
        var items = await _db.GetDownloadsByQueueAsync(queueId, ct).ConfigureAwait(false);
        var list = items.OrderBy(i => i.Priority).ThenBy(i => i.CreatedAtUtc).ToList();

        var index = list.FindIndex(i => i.Id == downloadId);
        if (index < 0) return;

        var targetIndex = index + direction;
        if (targetIndex < 0 || targetIndex >= list.Count) return;

        // Swap priorities
        var currentItem = list[index];
        var targetItem = list[targetIndex];

        var tempPriority = currentItem.Priority;
        if (tempPriority == targetItem.Priority)
        {
            // Normalize all priorities to distinct values if they matched
            for (int i = 0; i < list.Count; i++)
            {
                list[i].Priority = i * 10;
                await _db.UpdateDownloadPriorityAsync(list[i].Id, list[i].Priority, ct).ConfigureAwait(false);
            }
            tempPriority = currentItem.Priority;
        }

        currentItem.Priority = targetItem.Priority;
        targetItem.Priority = tempPriority;

        await _db.UpdateDownloadPriorityAsync(currentItem.Id, currentItem.Priority, ct).ConfigureAwait(false);
        await _db.UpdateDownloadPriorityAsync(targetItem.Id, targetItem.Priority, ct).ConfigureAwait(false);
    }

    public async Task RemoveFromQueueAsync(string downloadId, CancellationToken ct = default)
    {
        var item = await _db.GetDownloadAsync(downloadId, ct).ConfigureAwait(false);
        if (item != null)
        {
            item.QueueId = string.Empty;
            await _db.SaveDownloadAsync(item, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            foreach (var kvp in _runningQueues)
            {
                kvp.Value.Cts.Cancel();
                try
                {
                    await kvp.Value.WorkerTask.ConfigureAwait(false);
                }
                catch { }
                kvp.Value.Cts.Dispose();
            }
            _runningQueues.Clear();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }
}
