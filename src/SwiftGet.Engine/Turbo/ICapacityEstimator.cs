using System;

namespace SwiftGet.Engine.Turbo;

/// <summary>
/// Continuously estimates the bottleneck bandwidth (BtlBw) and minimum round-trip time (RTprop)
/// and identifies path bottlenecks in the style of BBR.
/// </summary>
public interface ICapacityEstimator
{
    /// <summary>
    /// Current snapshot of capacity metrics and bottleneck diagnostics.
    /// </summary>
    CapacityEstimate CurrentEstimate { get; }

    /// <summary>
    /// Records a new measurement sample from the download pipeline.
    /// </summary>
    void RecordSample(
        long bytesDelivered,
        TimeSpan interval,
        TimeSpan observedRtt,
        int activeConnections,
        int diskBackpressureCount = 0,
        bool serverThrottled = false);

    /// <summary>
    /// Resets sliding window filters.
    /// </summary>
    void Reset();
}
