using System;
using FluentAssertions;
using SwiftGet.Engine.Turbo;
using Xunit;

namespace SwiftGet.Turbo.Harness;

public class AdaptiveConcurrencyControllerTests
{
    [Fact]
    public void SlowStart_IncreasesConnectionsWhenGainExceedsEightPercent()
    {
        var controller = new AdaptiveConcurrencyController(initialConnections: 2, maxConnections: 16);

        // Step 1: establish baseline and step up
        var est1 = new CapacityEstimate(5_000_000, TimeSpan.FromMilliseconds(20), 5_000_000, BottleneckType.Probing, 0, "");
        controller.Evaluate(est1, TimeSpan.FromMilliseconds(700));
        controller.TargetConnections.Should().Be(4);

        // Step 2: Rate grew from 5 MB/s to 8 MB/s (+60% > 8%)
        var est2 = new CapacityEstimate(8_000_000, TimeSpan.FromMilliseconds(20), 8_000_000, BottleneckType.Probing, 0, "");
        controller.Evaluate(est2, TimeSpan.FromMilliseconds(700));
        controller.TargetConnections.Should().Be(6);
    }

    [Fact]
    public void PlateauDetection_TransitionsToSteadyState()
    {
        var controller = new AdaptiveConcurrencyController(initialConnections: 4, maxConnections: 16);

        var est1 = new CapacityEstimate(10_000_000, TimeSpan.FromMilliseconds(20), 10_000_000, BottleneckType.Probing, 0, "");
        controller.Evaluate(est1, TimeSpan.FromMilliseconds(700));

        // Negligible gain: 10.0 MB/s to 10.1 MB/s (+1% < 8%)
        var est2 = new CapacityEstimate(10_100_000, TimeSpan.FromMilliseconds(20), 10_100_000, BottleneckType.Probing, 0, "");
        controller.Evaluate(est2, TimeSpan.FromMilliseconds(700));

        controller.CurrentPhase.Should().Be(ControllerPhase.SteadyState);
    }

    [Fact]
    public void ReportThrottled_PerformsAimdMultiplicativeDecreaseAndBackoff()
    {
        var controller = new AdaptiveConcurrencyController(initialConnections: 12, minConnections: 2, maxConnections: 32);

        controller.ReportThrottled(TimeSpan.FromSeconds(2));

        controller.CurrentPhase.Should().Be(ControllerPhase.Backoff);
        controller.TargetConnections.Should().Be(6); // 12 halved to 6
    }
}
