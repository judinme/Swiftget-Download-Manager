using SwiftGet.Core.Models;

namespace SwiftGet.Engine.Queues;

public class QueueCompletedEventArgs : EventArgs
{
    public string QueueId { get; }
    public string QueueName { get; }
    public int CompletedCount { get; }
    public int FailedCount { get; }
    public AfterCompletionAction Action { get; }
    public string? CustomActionCommand { get; }

    public QueueCompletedEventArgs(
        string queueId,
        string queueName,
        int completedCount,
        int failedCount,
        AfterCompletionAction action,
        string? customActionCommand)
    {
        QueueId = queueId;
        QueueName = queueName;
        CompletedCount = completedCount;
        FailedCount = failedCount;
        Action = action;
        CustomActionCommand = customActionCommand;
    }
}

public interface IQueueManager : IAsyncDisposable
{
    event EventHandler<QueueCompletedEventArgs>? QueueCompleted;
    event EventHandler<DownloadItem>? DownloadStarted;
    event EventHandler<DownloadItem>? DownloadCompleted;
    event EventHandler<DownloadItem>? DownloadFailed;

    Task StartQueueAsync(string queueId, CancellationToken ct = default);
    Task StopQueueAsync(string queueId, CancellationToken ct = default);
    bool IsQueueRunning(string queueId);
    IReadOnlyList<string> GetRunningQueueIds();
    Task<IReadOnlyList<DownloadItem>> GetQueueItemsAsync(string queueId, CancellationToken ct = default);
    Task MoveItemAsync(string queueId, string downloadId, int direction, CancellationToken ct = default);
    Task RemoveFromQueueAsync(string downloadId, CancellationToken ct = default);
}
