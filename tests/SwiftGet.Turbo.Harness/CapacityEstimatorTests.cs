using System;
using FluentAssertions;
using SwiftGet.Engine.Turbo;
using Xunit;

namespace SwiftGet.Turbo.Harness;

public class CapacityEstimatorTests
{
    [Fact]
    public void WindowedMaxFilter_TracksPeakBandwidth()
    {
        var estimator = new CapacityEstimator(windowSize: 5);

        estimator.RecordSample(1_000_000, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(20), 2);
        estimator.CurrentEstimate.EstimatedBtlBwBytesPerSec.Should().Be(1_000_000);

        estimator.RecordSample(3_000_000, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(25), 4);
        estimator.CurrentEstimate.EstimatedBtlBwBytesPerSec.Should().Be(3_000_000);

        // Lower rate sample should not reduce the windowed max
        estimator.RecordSample(500_000, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(22), 4);
        estimator.CurrentEstimate.EstimatedBtlBwBytesPerSec.Should().Be(3_000_000);
    }

    [Fact]
    public void WindowedMinFilter_TracksPropagationRtt()
    {
        var estimator = new CapacityEstimator(windowSize: 5);

        estimator.RecordSample(1_000_000, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(45), 2);
        estimator.RecordSample(1_000_000, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(18), 3);
        estimator.RecordSample(1_000_000, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(80), 4);

        estimator.CurrentEstimate.MinRtProp.Should().Be(TimeSpan.FromMilliseconds(18));
    }

    [Fact]
    public void LinearThroughputScaling_ClassifiesPerConnectionLimited()
    {
        var estimator = new CapacityEstimator(windowSize: 5);

        // Adding connections from 2 to 4 to 8 roughly quadruples throughput
        estimator.RecordSample(2_000_000, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(20), 2);
        estimator.RecordSample(4_000_000, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(21), 4);
        estimator.RecordSample(8_000_000, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(22), 8);

        estimator.CurrentEstimate.Bottleneck.Should().Be(BottleneckType.PerConnectionLimited);
        estimator.CurrentEstimate.BottleneckDescription.Should().Contain("per-connection cap");
    }

    [Fact]
    public void PlateauWithBufferbloat_ClassifiesSharedPathLimited()
    {
        var estimator = new CapacityEstimator(windowSize: 5);

        // RTT climbs from 20ms to 40ms while throughput stays flat at 10 MB/s
        estimator.RecordSample(10_000_000, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(20), 4);
        estimator.RecordSample(10_100_000, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(30), 6);
        estimator.RecordSample(10_050_000, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(42), 8);

        estimator.CurrentEstimate.Bottleneck.Should().Be(BottleneckType.SharedPathLimited);
        estimator.CurrentEstimate.BottleneckDescription.Should().Contain("internet path capacity");
    }

    [Fact]
    public void DiskBackpressure_ClassifiesDiskBound()
    {
        var estimator = new CapacityEstimator(windowSize: 5);

        estimator.RecordSample(5_000_000, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(20), 4, diskBackpressureCount: 5);

        estimator.CurrentEstimate.Bottleneck.Should().Be(BottleneckType.DiskBound);
        estimator.CurrentEstimate.BottleneckDescription.Should().Contain("disk write speed");
    }

    [Fact]
    public void AdapterSaturation_ClassifiesNetworkAdapterBound()
    {
        // 100 Mbps Fast Ethernet = 12.5 MB/s
        const long fastEthernetBytesPerSec = 100_000_000 / 8;
        var estimator = new CapacityEstimator(windowSize: 5, adapterSpeedBytesPerSec: fastEthernetBytesPerSec);

        // Running at 12.0 MB/s (96% of link speed)
        estimator.RecordSample(12_000_000, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(5), 4);

        estimator.CurrentEstimate.Bottleneck.Should().Be(BottleneckType.NetworkAdapterBound);
        estimator.CurrentEstimate.BottleneckDescription.Should().Contain("local network adapter speed");
    }
}
