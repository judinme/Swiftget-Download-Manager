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

    [Fact]
    public void RestoreFromSegments_RestoresExistingOffsetsAndCalculatesCompletedBytes()
    {
        const long totalBytes = 10_000_000;
        var scheduler = new DynamicRangeScheduler(totalBytes, targetChunks: 2);

        var segments = new List<SwiftGet.Core.Models.DownloadSegment>
        {
            new()
            {
                SegmentIndex = 0,
                StartOffset = 0,
                EndOffset = 4_999_999,
                CurrentOffset = 5_000_000,
                Status = SwiftGet.Core.Models.SegmentStatus.Completed
            },
            new()
            {
                SegmentIndex = 1,
                StartOffset = 5_000_000,
                EndOffset = 9_999_999,
                CurrentOffset = 7_500_000,
                Status = SwiftGet.Core.Models.SegmentStatus.Downloading
            }
        };

        scheduler.RestoreFromSegments(segments);

        scheduler.CompletedBytes.Should().Be(5_000_000 + 2_500_000); // 7.5 MB
        scheduler.IsComplete.Should().BeFalse();

        // Worker should pick up only the unfinished range starting at 7,500,000
        var work = scheduler.AcquireWork("w1");
        work.Should().NotBeNull();
        work!.StartOffset.Should().Be(5_000_000);
        work.CurrentOffset.Should().Be(7_500_000);
        work.EndOffset.Should().Be(9_999_999);
    }

    [Fact]
    public void ReleaseWork_WhenWorkerFaultsOrStalls_ReturnsRangeToPendingForAnotherWorker()
    {
        const long totalBytes = 10_000_000;
        var scheduler = new DynamicRangeScheduler(totalBytes, targetChunks: 2);

        // Worker 1 acquires the first chunk
        var r1 = scheduler.AcquireWork("worker-1");
        r1.Should().NotBeNull();
        r1!.Status.Should().Be(RangeStatus.Active);
        r1.AssignedWorkerId.Should().Be("worker-1");

        // Worker 1 reports some progress then stalls or throws an exception
        scheduler.ReportProgress(r1.Id, 1_000_000, 500_000);
        r1.CurrentOffset.Should().Be(1_000_000);
        r1.IsComplete.Should().BeFalse();

        // Release the work back to scheduler
        scheduler.ReleaseWork(r1);

        var snapshot = scheduler.GetSnapshot();
        var released = snapshot.First(r => r.Id == r1.Id);
        released.Status.Should().Be(RangeStatus.Pending);
        released.AssignedWorkerId.Should().BeNull();
        released.CurrentOffset.Should().Be(1_000_000);

        // Worker 2 can now acquire the released work and resume from the current offset
        var reacquired = scheduler.AcquireWork("worker-2");
        reacquired.Should().NotBeNull();
        reacquired!.Id.Should().Be(r1.Id);
        reacquired.CurrentOffset.Should().Be(1_000_000);
        reacquired.AssignedWorkerId.Should().Be("worker-2");
        reacquired.Status.Should().Be(RangeStatus.Active);
    }

    [Fact]
    public void ReleaseWork_WhenRangeIsAlreadyComplete_RetainsCompletedStatus()
    {
        const long totalBytes = 2_000_000;
        var scheduler = new DynamicRangeScheduler(totalBytes, targetChunks: 1);

        var r = scheduler.AcquireWork("w1");
        r.Should().NotBeNull();

        // Complete the chunk
        scheduler.ReportProgress(r!.Id, r.TotalLength, 1_000_000);
        r.IsComplete.Should().BeTrue();

        // Release work should not revert completed state
        scheduler.ReleaseWork(r);

        var snapshot = scheduler.GetSnapshot();
        snapshot[0].Status.Should().Be(RangeStatus.Completed);
        scheduler.IsComplete.Should().BeTrue();
    }
}
