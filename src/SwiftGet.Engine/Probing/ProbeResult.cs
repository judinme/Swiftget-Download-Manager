namespace SwiftGet.Engine.Probing;

public record ProbeResult
{
    public required string OriginalUrl { get; init; }
    public required string FinalUrl { get; init; }
    public long TotalBytes { get; init; } = -1;
    public bool SupportsRanges { get; init; }
    public string? ETag { get; init; }
    public string? LastModified { get; init; }
    public string? ContentType { get; init; }
    public string? ContentDisposition { get; init; }
    public string? SuggestedFileName { get; init; }
    public byte[]? InitialBytes { get; init; }
    public bool ExtensionRecovered { get; init; }
    public bool IsDangerous { get; init; }
    public int StatusCode { get; init; }
    public bool IsSuccess { get; init; }
    public string? ErrorMessage { get; init; }
}
