using System.Linq;
using FluentAssertions;
using SwiftGet.Engine.Turbo;
using Xunit;

namespace SwiftGet.Turbo.Harness;

public class DynamicRangeSchedulerTests
{
    [Fact]
    public void InitialRanges_PartitionFileExactlyWithoutGapsOrOverlaps()
    {
        const long totalBytes = 100_000_000; // 100 MB
        var scheduler = new DynamicRangeScheduler(totalBytes, targetChunks: 50, minChunkSize: 2 * 1024 * 1024);

        var ranges = scheduler.GetSnapshot();
        ranges.Should().NotBeEmpty();

        ranges[0].StartOffset.Should().Be(0);
        ranges[^1].EndOffset.Should().Be(totalBytes - 1);

        // Verify continuity
        for (int i = 0; i < ranges.Count - 1; i++)
        {
            ranges[i + 1].StartOffset.Should().Be(ranges[i].EndOffset + 1);
        }

        ranges.Sum(r => r.RemainingBytes).Should().Be(totalBytes);
    }

    [Fact]
    public void AcquireWork_ConsistentlyDistributesDiscreteChunks()
    {
        const long totalBytes = 10_000_000; // 10 MB
        var scheduler = new DynamicRangeScheduler(totalBytes, targetChunks: 10, minChunkSize: 1024 * 1024);

        var r1 = scheduler.AcquireWork("w1");
        var r2 = scheduler.AcquireWork("w2");
        var r3 = scheduler.AcquireWork("w3");

        r1.Should().NotBeNull();
        r2.Should().NotBeNull();
        r3.Should().NotBeNull();

        // Ensure disjoint ranges
        (r1!.EndOffset < r2!.StartOffset).Should().BeTrue();
        (r2!.EndOffset < r3!.StartOffset).Should().BeTrue();
    }

    [Fact]
    public void EndGameMode_TriggersHedgedRequestForStraggler()
    {
        const long totalBytes = 2_000_000; // 2 MB
        var scheduler = new DynamicRangeScheduler(totalBytes, targetChunks: 2, minChunkSize: 1024 * 1024);

        var r1 = scheduler.AcquireWork("w1");
        var r2 = scheduler.AcquireWork("w2");

        // Worker 1 completes chunk 1
        scheduler.ReportProgress(r1!.Id, r1.TotalLength, 5_000_000);

        // Worker 2 downloaded halfway through chunk 2
        scheduler.ReportProgress(r2!.Id, r2.TotalLength / 2, 50_000);

        scheduler.IsEndGame.Should().BeTrue();

        // Worker 1 hedges the straggler chunk
        var hedged = scheduler.AcquireWork("w1");
        hedged.Should().NotBeNull();
        hedged!.Status.Should().Be(RangeStatus.Hedged);
        hedged.StartOffset.Should().Be(r2!.CurrentOffset);
    }
}
