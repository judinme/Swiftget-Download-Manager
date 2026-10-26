using SwiftGet.Core.Models;

namespace SwiftGet.Core.Storage;

public interface ISwiftGetDatabase : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken ct = default);
    Task SaveDownloadAsync(DownloadItem item, CancellationToken ct = default);
    Task<DownloadItem?> GetDownloadAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<DownloadItem>> GetAllDownloadsAsync(CancellationToken ct = default);
    Task UpdateDownloadStatusAsync(string id, DownloadStatus status, string? errorMessage = null, CancellationToken ct = default);
    Task SaveSegmentsAsync(string downloadId, IReadOnlyList<DownloadSegment> segments, CancellationToken ct = default);
    Task<IReadOnlyList<DownloadSegment>> GetSegmentsAsync(string downloadId, CancellationToken ct = default);
    Task CheckpointProgressAsync(string downloadId, long downloadedBytes, IReadOnlyList<DownloadSegment> segments, CancellationToken ct = default);
    Task DeleteDownloadAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<DownloadItem>> GetDownloadsByQueueAsync(string queueId, CancellationToken ct = default);
    Task UpdateDownloadPriorityAsync(string id, int priority, CancellationToken ct = default);

    // Queue Management
    Task SaveQueueAsync(DownloadQueue queue, CancellationToken ct = default);
    Task<DownloadQueue?> GetQueueAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<DownloadQueue>> GetAllQueuesAsync(CancellationToken ct = default);
    Task DeleteQueueAsync(string id, CancellationToken ct = default);

    // Category Management
    Task SaveCategoryAsync(CategoryRule category, CancellationToken ct = default);
    Task<CategoryRule?> GetCategoryAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<CategoryRule>> GetAllCategoriesAsync(CancellationToken ct = default);
    Task DeleteCategoryAsync(string id, CancellationToken ct = default);

    // Torrent Management
    Task SaveTorrentAsync(TorrentItem torrent, CancellationToken ct = default);
    Task<TorrentItem?> GetTorrentAsync(string id, CancellationToken ct = default);
    Task<TorrentItem?> GetTorrentByInfoHashAsync(string infoHash, CancellationToken ct = default);
    Task<IReadOnlyList<TorrentItem>> GetAllTorrentsAsync(CancellationToken ct = default);
    Task SaveTorrentFilesAsync(string torrentId, IReadOnlyList<TorrentFileItem> files, CancellationToken ct = default);
    Task<IReadOnlyList<TorrentFileItem>> GetTorrentFilesAsync(string torrentId, CancellationToken ct = default);
    Task UpdateTorrentFilePriorityAsync(string fileId, TorrentFilePriority priority, bool isSelected, CancellationToken ct = default);
    Task SaveTorrentTrackersAsync(string torrentId, IReadOnlyList<TorrentTrackerItem> trackers, CancellationToken ct = default);
    Task<IReadOnlyList<TorrentTrackerItem>> GetTorrentTrackersAsync(string torrentId, CancellationToken ct = default);
    Task UpdateTorrentFastResumeAsync(string torrentId, byte[] fastResumeBlob, CancellationToken ct = default);
    Task UpdateTorrentStatsAsync(string torrentId, long downloadedBytes, long uploadedBytes, CancellationToken ct = default);
    Task DeleteTorrentAsync(string id, CancellationToken ct = default);
}
