namespace SwiftGet.Core.Models;

public class DownloadSegment
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DownloadId { get; set; } = string.Empty;
    public int SegmentIndex { get; set; }
    public long StartOffset { get; set; }
    public long EndOffset { get; set; }
    public long CurrentOffset { get; set; }
    public SegmentStatus Status { get; set; } = SegmentStatus.Pending;
    public int? ConnectionId { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? Checksum { get; set; }

    public long TotalBytes => EndOffset >= StartOffset ? EndOffset - StartOffset + 1 : 0;
    public long DownloadedBytes => Math.Clamp(CurrentOffset - StartOffset, 0, TotalBytes);
    public long RemainingBytes => Math.Max(0, EndOffset - CurrentOffset + 1);
    public bool IsComplete => CurrentOffset > EndOffset;

    public DownloadSegment Clone() => new()
    {
        Id = Id,
        DownloadId = DownloadId,
        SegmentIndex = SegmentIndex,
        StartOffset = StartOffset,
        EndOffset = EndOffset,
        CurrentOffset = CurrentOffset,
        Status = Status,
        ConnectionId = ConnectionId,
        UpdatedAtUtc = UpdatedAtUtc,
        Checksum = Checksum
    };
}
