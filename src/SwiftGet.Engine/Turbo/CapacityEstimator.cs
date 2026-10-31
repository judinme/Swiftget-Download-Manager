using System;
using System.Collections.Generic;
using System.Linq;

namespace SwiftGet.Engine.Turbo;

/// <summary>
/// BBR-style link capacity estimator that tracks bottleneck bandwidth (BtlBw)
/// and propagation RTT (RTprop), distinguishing per-connection limits from shared bottlenecks.
/// </summary>
public sealed class CapacityEstimator : ICapacityEstimator
{
    private readonly int _windowSize;
    private readonly long _adapterSpeedBytesPerSec;
    private readonly LinkedList<long> _bandwidthWindow = new();
    private readonly LinkedList<TimeSpan> _rttWindow = new();
    private readonly LinkedList<(int Connections, long RateBytesPerSec, TimeSpan Rtt)> _concurrencyHistory = new();
    private readonly object _sync = new();

    private long _maxBtlBwBytesPerSec;
    private TimeSpan _minRtProp = TimeSpan.FromSeconds(10);
    private long _lastRateBytesPerSec;
    private BottleneckType _lastBottleneck = BottleneckType.Probing;
    private string _lastBottleneckDescription = "Analyzing network path characteristics...";

    public CapacityEstimate CurrentEstimate
    {
        get
        {
            lock (_sync)
            {
                return new CapacityEstimate(
                    EstimatedBtlBwBytesPerSec: Math.Max(_maxBtlBwBytesPerSec, _lastRateBytesPerSec),
                    MinRtProp: _minRtProp == TimeSpan.FromSeconds(10) ? TimeSpan.Zero : _minRtProp,
                    CurrentThroughputBytesPerSec: _lastRateBytesPerSec,
                    Bottleneck: _lastBottleneck,
                    AdapterSpeedBytesPerSec: _adapterSpeedBytesPerSec,
                    BottleneckDescription: _lastBottleneckDescription);
            }
        }
    }

    public CapacityEstimator(int windowSize = 10, long? adapterSpeedBytesPerSec = null)
    {
        _windowSize = Math.Max(3, windowSize);
        _adapterSpeedBytesPerSec = adapterSpeedBytesPerSec ?? NetworkAdapterDetector.GetPrimaryAdapterSpeedBytesPerSec();
    }

    public void RecordSample(
        long bytesDelivered,
        TimeSpan interval,
        TimeSpan observedRtt,
        int activeConnections,
        int diskBackpressureCount = 0,
        bool serverThrottled = false)
    {
        if (interval.TotalSeconds <= 0) return;

        var currentRate = (long)(bytesDelivered / interval.TotalSeconds);

        lock (_sync)
        {
            _lastRateBytesPerSec = currentRate;

            // 1. Sliding Window Max Filter for Bandwidth (BtlBw)
            _bandwidthWindow.AddLast(currentRate);
            if (_bandwidthWindow.Count > _windowSize) _bandwidthWindow.RemoveFirst();
            _maxBtlBwBytesPerSec = _bandwidthWindow.Max();

            // 2. Sliding Window Min Filter for RTT (RTprop)
            if (observedRtt > TimeSpan.Zero)
            {
                _rttWindow.AddLast(observedRtt);
                if (_rttWindow.Count > _windowSize) _rttWindow.RemoveFirst();
                _minRtProp = _rttWindow.Min();
            }

            // 3. Concurrency History for Gradient Tracking
            _concurrencyHistory.AddLast((activeConnections, currentRate, observedRtt));
            if (_concurrencyHistory.Count > _windowSize) _concurrencyHistory.RemoveFirst();

            // 4. Bottleneck Classification
            ClassifyBottleneck(activeConnections, currentRate, observedRtt, diskBackpressureCount, serverThrottled);
        }
    }

    private void ClassifyBottleneck(
        int activeConnections,
        long currentRate,
        TimeSpan observedRtt,
        int diskBackpressureCount,
        bool serverThrottled)
    {
        if (serverThrottled)
        {
            _lastBottleneck = BottleneckType.ServerThrottled;
            _lastBottleneckDescription = "Server throttled connection (HTTP 429/503). Backing off concurrency.";
            return;
        }

        if (diskBackpressureCount > 2)
        {
            _lastBottleneck = BottleneckType.DiskBound;
            _lastBottleneckDescription = "Limited by disk write speed. Network readers paused to prevent memory buildup.";
            return;
        }

        // Check if physical network adapter is saturated (within 8% of link PHY rate)
        if (_adapterSpeedBytesPerSec > 0 && currentRate >= (_adapterSpeedBytesPerSec * 0.92))
        {
            _lastBottleneck = BottleneckType.NetworkAdapterBound;
            var adapterMb = Math.Round(_adapterSpeedBytesPerSec / (1024.0 * 1024.0), 1);
            _lastBottleneckDescription = $"Limited by local network adapter speed ({adapterMb} MB/s). Line capacity may be higher.";
            return;
        }

        // Compare historical concurrency points to distinguish per-connection caps from shared link bottlenecks
        if (_concurrencyHistory.Count >= 3)
        {
            var first = _concurrencyHistory.First!.Value;
            var latest = _concurrencyHistory.Last!.Value;

            if (latest.Connections > first.Connections && first.RateBytesPerSec > 0)
            {
                double connDeltaRatio = (double)(latest.Connections - first.Connections) / first.Connections;
                double rateDeltaRatio = (double)(latest.RateBytesPerSec - first.RateBytesPerSec) / first.RateBytesPerSec;

                if (rateDeltaRatio >= (connDeltaRatio * 0.65))
                {
                    // Throughput scales almost linearly with connection count -> per-connection cap
                    _lastBottleneck = BottleneckType.PerConnectionLimited;
                    var perConnRateMb = Math.Round((double)currentRate / Math.Max(1, activeConnections) / (1024.0 * 1024.0), 2);
                    _lastBottleneckDescription = $"Limited by server per-connection cap (~{perConnRateMb} MB/s). Using {activeConnections} connections to scale bandwidth.";
                    return;
                }

                if (rateDeltaRatio < 0.08 && observedRtt > (_minRtProp * 1.30))
                {
                    // Rate plateaued and RTT jumped -> bufferbloat on shared bottleneck
                    _lastBottleneck = BottleneckType.SharedPathLimited;
                    var pathRateMb = Math.Round(_maxBtlBwBytesPerSec / (1024.0 * 1024.0), 1);
                    _lastBottleneckDescription = $"Limited by internet path capacity (~{pathRateMb} MB/s). Concurrency optimal.";
                    return;
                }
            }
        }

        if (_lastBottleneck == BottleneckType.Probing && _maxBtlBwBytesPerSec > 0)
        {
            var pathRateMb = Math.Round(_maxBtlBwBytesPerSec / (1024.0 * 1024.0), 1);
            _lastBottleneckDescription = $"Operating at {pathRateMb} MB/s across {activeConnections} active connection(s).";
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _bandwidthWindow.Clear();
            _rttWindow.Clear();
            _concurrencyHistory.Clear();
            _maxBtlBwBytesPerSec = 0;
            _minRtProp = TimeSpan.FromSeconds(10);
            _lastRateBytesPerSec = 0;
            _lastBottleneck = BottleneckType.Probing;
            _lastBottleneckDescription = "Analyzing network path characteristics...";
        }
    }
}
