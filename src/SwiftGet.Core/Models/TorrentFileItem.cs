using System;

namespace SwiftGet.Core.Models;

public class TorrentFileItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TorrentId { get; set; } = string.Empty;
    public int FileIndex { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public long DownloadedBytes { get; set; }
    public TorrentFilePriority Priority { get; set; } = TorrentFilePriority.Normal;
    public bool IsSelected { get; set; } = true;

    public double ProgressPercentage => FileSizeBytes > 0
        ? Math.Clamp((double)DownloadedBytes / FileSizeBytes * 100.0, 0.0, 100.0)
        : 0.0;
}
