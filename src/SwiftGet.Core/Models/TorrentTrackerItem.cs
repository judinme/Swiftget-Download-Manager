using System;

namespace SwiftGet.Core.Models;

public class TorrentTrackerItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TorrentId { get; set; } = string.Empty;
    public int Tier { get; set; }
    public string AnnounceUrl { get; set; } = string.Empty;
    public string Status { get; set; } = "NotContacted";
    public int Seeders { get; set; }
    public int Leechers { get; set; }
    public DateTime? LastAnnounceUtc { get; set; }
    public string? ErrorMessage { get; set; }
}
