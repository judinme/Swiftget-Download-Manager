using System;

namespace SwiftGet.Core.Models;

public class TorrentItem
{
    public string Id { get; set; } = string.Empty;
    public string InfoHash { get; set; } = string.Empty;
    public string? MagnetUri { get; set; }
    public string Name { get; set; } = string.Empty;
    public long TotalBytes { get; set; }
    public int PieceLength { get; set; }
    public int PieceCount { get; set; }
    public bool IsPrivate { get; set; }
    public double SeedRatioLimit { get; set; } = 1.0;
    public int SeedTimeLimitMins { get; set; }
    public long UploadedBytes { get; set; }
    public long DownloadedBytes { get; set; }
    public byte[]? FastResumeBlob { get; set; }
    public string? Comment { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }

    public double CurrentRatio => DownloadedBytes > 0
        ? (double)UploadedBytes / DownloadedBytes
        : (UploadedBytes > 0 ? 100.0 : 0.0);
}
