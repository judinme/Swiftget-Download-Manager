using System;

namespace SwiftGet.Engine.Grabber;

/// <summary>
/// Represents a discovered file item found during site crawling.
/// </summary>
public sealed class GrabberFileItem
{
    public string Url { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public int Depth { get; set; }
    public string SourcePageUrl { get; set; } = string.Empty;
    public bool IsSelected { get; set; } = true;
    public long? FileSizeBytes { get; set; }
}

/// <summary>
/// Configuration and state model for a site grabber exploration project.
/// </summary>
public sealed class GrabberProject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New Grabber Project";
    public string StartUrl { get; set; } = string.Empty;
    public int MaxDepth { get; set; } = 2;
    public DomainScope DomainScope { get; set; } = DomainScope.SameDomain;
    public FileFilterPreset FilePreset { get; set; } = FileFilterPreset.Pictures;
    public string CustomExtensions { get; set; } = string.Empty;
    public string SaveDirectory { get; set; } = string.Empty;
    public string QueueId { get; set; } = "default";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
