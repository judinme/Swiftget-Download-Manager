using System;
using System.Collections.Generic;

namespace SwiftGet.Engine.Server;

public sealed class InterceptDownloadRequest
{
    public string? DownloadId { get; set; }
    public string Url { get; set; } = string.Empty;
    public string? MagnetUri { get; set; }
    public string? FileBytesBase64 { get; set; }
    public string? FileName { get; set; }
    public string? Referrer { get; set; }
    public string? UserAgent { get; set; }
    public string? Cookies { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string QueueId { get; set; } = "default";
    public string? CategoryId { get; set; }
    public bool ShowDialog { get; set; } = true;
    public bool StartImmediately { get; set; } = true;
    public string? DestinationPath { get; set; }
    public int? WindowLeft { get; set; }
    public int? WindowTop { get; set; }
    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }
    public long TotalBytes { get; set; }
    public string? EventId { get; set; }
    public DateTime? CreatedAtUtc { get; set; }
    public int TabId { get; set; } = -1;
    public string? Initiator { get; set; }
    public bool UserGesture { get; set; } = true;
    public bool ExplicitUserAction { get; set; }
    public string? MimeType { get; set; }
    public string? Quality { get; set; }
}

public sealed class InterceptBatchRequest
{
    public List<InterceptDownloadRequest> Downloads { get; set; } = [];
}

public sealed class InterceptDownloadResponse
{
    public bool Success { get; set; }
    public string DownloadId { get; set; } = string.Empty;
    public string? InfoHash { get; set; }
    public bool IsTorrent { get; set; }
    public string Message { get; set; } = string.Empty;
}

public sealed class ServerHealthResponse
{
    public string Status { get; set; } = "ok";
    public string Version { get; set; } = "1.1.0";
    public bool TorrentSupported { get; set; } = true;
    public int ActiveDownloads { get; set; }
}
