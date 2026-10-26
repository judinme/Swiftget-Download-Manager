using System.Globalization;
using FluentAssertions;
using FsCheck;
using FsCheck.Xunit;
using SwiftGet.Core.Models;
using SwiftGet.Engine.Segmentation;

namespace SwiftGet.Engine.PropertyTests;

public class DynamicSplitterPropertyTests
{
    [Property(MaxTest = 1000)]
    public bool InitialSegments_HaveNoGapsAndNoOverlaps(PositiveInt rawTotalBytes, PositiveInt rawConnections)
    {
        var totalBytes = (long)rawTotalBytes.Get;
        var connections = Math.Clamp(rawConnections.Get, 1, 32);

        var segments = DynamicSplitter.CreateInitialSegments("test-download", totalBytes, connections);

        var noGaps = true;
        var sumBytes = 0L;

        if (segments.Count == 0 || segments[0].StartOffset != 0)
        {
            return false;
        }

        for (var i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            if (seg.EndOffset < seg.StartOffset) return false;
            sumBytes += (seg.EndOffset - seg.StartOffset + 1);

            if (i > 0 && seg.StartOffset != segments[i - 1].EndOffset + 1)
            {
                noGaps = false;
            }
        }

        var correctEnd = segments[^1].EndOffset == totalBytes - 1;
        var correctSum = sumBytes == totalBytes;

        return noGaps && correctEnd && correctSum;
    }

    [Property(MaxTest = 500)]
    public bool DynamicSplitting_PreservesTotalByteCoverage(PositiveInt rawTotalBytes, PositiveInt rawConnections)
    {
        var totalBytes = (long)rawTotalBytes.Get;
        var connections = Math.Clamp(rawConnections.Get, 1, 32);

        var segments = DynamicSplitter.CreateInitialSegments("test-download", totalBytes, connections);

        // Try splitting
        if (DynamicSplitter.TrySplitLargestSegment(segments, minSplitSize: 2, newSegmentIndex: segments.Count, out var newSegment) && newSegment != null)
        {
            segments.Add(newSegment);

            // Re-sort and verify total coverage
            var sorted = segments.OrderBy(s => s.StartOffset).ToList();
            if (sorted[0].StartOffset != 0) return false;

            long totalSum = 0;
            for (var i = 0; i < sorted.Count; i++)
            {
                totalSum += (sorted[i].EndOffset - sorted[i].StartOffset + 1);
                if (i > 0 && sorted[i].StartOffset != sorted[i - 1].EndOffset + 1)
                {
                    return false;
                }
            }

            return totalSum == totalBytes && sorted[^1].EndOffset == totalBytes - 1;
        }

        return true;
    }
}
