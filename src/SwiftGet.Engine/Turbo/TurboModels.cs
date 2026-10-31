using System;

namespace SwiftGet.Engine.Turbo;

/// <summary>
/// Identifies the primary physical or protocol constraint limiting download throughput.
/// </summary>
public enum BottleneckType
{
    /// <summary>Controller is probing path characteristics.</summary>
    Probing = 0,

    /// <summary>Server enforces a per-connection rate limit; adding connections increases throughput.</summary>
    PerConnectionLimited = 1,

    /// <summary>The internet connection or shared network path is saturated (throughput plateaus, RTT climbs).</summary>
    SharedPathLimited = 2,

    /// <summary>The local physical network adapter (e.g. 100 Mbps Fast Ethernet) is capping throughput.</summary>
    NetworkAdapterBound = 3,

    /// <summary>Local disk write speed is slower than network ingest.</summary>
    DiskBound = 4,

    /// <summary>CPU saturation or decryption/decompression is limiting throughput.</summary>
    CpuBound = 5,

    /// <summary>Server responded with HTTP 429 Too Many Requests or 503; backoff required.</summary>
    ServerThrottled = 6
}

/// <summary>
/// A discreet delivery sample capturing bytes received over an interval.
/// </summary>
public readonly record struct DeliverySample(
    DateTime TimestampUtc,
    long BytesDelivered,
    TimeSpan Interval,
    TimeSpan ObservedRtt,
    int ActiveConnections);

/// <summary>
/// Snapshot of the current capacity estimate and detected bottleneck.
/// </summary>
public sealed record CapacityEstimate(
    long EstimatedBtlBwBytesPerSec,
    TimeSpan MinRtProp,
    long CurrentThroughputBytesPerSec,
    BottleneckType Bottleneck,
    long AdapterSpeedBytesPerSec,
    string BottleneckDescription)
{
    public double EstimatedBtlBwMBps => Math.Round(EstimatedBtlBwBytesPerSec / (1024.0 * 1024.0), 2);
    public double CurrentThroughputMBps => Math.Round(CurrentThroughputBytesPerSec / (1024.0 * 1024.0), 2);
    public double AdapterSpeedMBps => Math.Round(AdapterSpeedBytesPerSec / (1024.0 * 1024.0), 2);
    public double UtilizationPercent => EstimatedBtlBwBytesPerSec > 0
        ? Math.Min(100.0, Math.Round(((double)CurrentThroughputBytesPerSec / EstimatedBtlBwBytesPerSec) * 100.0, 1))
        : 100.0;
}

/// <summary>
/// Metadata for an active network interface.
/// </summary>
public sealed record NetworkAdapterInfo(
    string Id,
    string Name,
    string Description,
    string InterfaceType,
    long SpeedBitsPerSec,
    string? GatewayMacAddress);
