using System;

namespace SwiftGet.Turbo.Harness;

/// <summary>
/// Configuration for the simulated network path and server behavior.
/// </summary>
public sealed class NetworkShaperOptions
{
    /// <summary>
    /// Per-connection rate limit in bytes per second (0 = unlimited).
    /// </summary>
    public long PerConnectionRateLimitBytesPerSec { get; set; }

    /// <summary>
    /// Aggregate per-IP or link rate limit in bytes per second (0 = unlimited).
    /// </summary>
    public long PerIpRateLimitBytesPerSec { get; set; }

    /// <summary>
    /// Simulated one-way latency in milliseconds (RTT = 2 * LatencyMs).
    /// </summary>
    public int LatencyMs { get; set; }

    /// <summary>
    /// Random delay jitter in milliseconds added to chunks.
    /// </summary>
    public int JitterMs { get; set; }

    /// <summary>
    /// Simulated packet drop or connection reset ratio (0.0 to 1.0).
    /// </summary>
    public double PacketLossRatio { get; set; }

    /// <summary>
    /// Whether the server honors HTTP byte ranges (206 Partial Content).
    /// </summary>
    public bool SupportsRanges { get; set; } = true;

    /// <summary>
    /// When true, simulates a lagging straggler connection during the final 5% of data.
    /// </summary>
    public bool SimulateStragglerOnLastRange { get; set; }

    /// <summary>
    /// Threshold connection count that triggers HTTP 429 Too Many Requests (0 = disabled).
    /// </summary>
    public int MaxConcurrentConnectionsBefore429 { get; set; }

    /// <summary>
    /// Retry-After duration in seconds sent with HTTP 429.
    /// </summary>
    public int RetryAfterSeconds { get; set; } = 2;

    /// <summary>
    /// Enable HTTP/2 and HTTP/3 support on Kestrel endpoints.
    /// </summary>
    public bool EnableHttp2 { get; set; } = true;
    public bool EnableHttp3 { get; set; }

    /// <summary>
    /// Content length of the test payload in bytes.
    /// </summary>
    public long ContentLengthBytes { get; set; } = 64 * 1024 * 1024; // 64 MB default
}
