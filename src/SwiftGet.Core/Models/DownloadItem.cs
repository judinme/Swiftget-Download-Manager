namespace SwiftGet.Core.Models;

public class DownloadItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Url { get; set; } = string.Empty;
    public string FinalUrl { get; set; } = string.Empty;
    public string DestinationPath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public bool IsFileNameUserLocked { get; set; }
    public long TotalBytes { get; set; } = -1;
    public long DownloadedBytes { get; set; }
    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;
    public JobKind JobKind { get; set; } = JobKind.Http;
    public string? InfoHash { get; set; }
    public string? ETag { get; set; }
    public string? LastModified { get; set; }
    public string? ContentType { get; set; }
    public bool SupportsRanges { get; set; } = true;
    public string? ChecksumExpected { get; set; }
    public string? ChecksumActual { get; set; }
    public ChecksumType ChecksumType { get; set; } = ChecksumType.None;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }
    public string? ErrorMessage { get; set; }
    public int RetryCount { get; set; }
    public long SpeedLimitBytesPerSec { get; set; }
    public string QueueId { get; set; } = "default";
    public string CategoryId { get; set; } = "all";
    public int Priority { get; set; }
    public string? Referrer { get; set; }
    public string? UserAgent { get; set; }
    public string? CustomHeadersJson { get; set; }
    public string? CookiesJson { get; set; }

    public List<DownloadSegment> Segments { get; set; } = [];

    public double ProgressPercentage => TotalBytes > 0
        ? Math.Clamp((double)DownloadedBytes / TotalBytes * 100.0, 0.0, 100.0)
        : 0.0;

    public bool CanResume => SupportsRanges && TotalBytes > 0 && !string.IsNullOrWhiteSpace(ETag ?? LastModified);
}
