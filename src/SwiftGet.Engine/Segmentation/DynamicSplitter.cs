using SwiftGet.Core.Models;

namespace SwiftGet.Engine.Segmentation;

public static class DynamicSplitter
{
    public const long DefaultMinSplitSize = 1024 * 1024 * 2; // 2 MB minimum to justify new connection

    public static List<DownloadSegment> CreateInitialSegments(string downloadId, long totalBytes, int requestedConnections)
    {
        if (totalBytes <= 0 || requestedConnections <= 1)
        {
            return
            [
                new DownloadSegment
                {
                    DownloadId = downloadId,
                    SegmentIndex = 0,
                    StartOffset = 0,
                    EndOffset = Math.Max(0, totalBytes - 1),
                    CurrentOffset = 0,
                    Status = SegmentStatus.Pending
                }
            ];
        }

        // Clamp connections to not exceed total bytes
        var connectionCount = (int)Math.Min(requestedConnections, totalBytes);
        connectionCount = Math.Clamp(connectionCount, 1, 32);

        var segments = new List<DownloadSegment>(connectionCount);
        var baseChunkSize = totalBytes / connectionCount;
        var remainder = totalBytes % connectionCount;

        long currentStart = 0;
        for (var i = 0; i < connectionCount; i++)
        {
            var chunkSize = baseChunkSize + (i < remainder ? 1 : 0);
            var endOffset = currentStart + chunkSize - 1;

            segments.Add(new DownloadSegment
            {
                DownloadId = downloadId,
                SegmentIndex = i,
                StartOffset = currentStart,
                EndOffset = endOffset,
                CurrentOffset = currentStart,
                Status = SegmentStatus.Pending
            });

            currentStart = endOffset + 1;
        }

        return segments;
    }

    public static bool TrySplitLargestSegment(
        IReadOnlyList<DownloadSegment> activeSegments,
        long minSplitSize,
        int newSegmentIndex,
        out DownloadSegment? newSegment)
    {
        newSegment = null;

        // Find unfinished segment with the largest remaining work
        DownloadSegment? candidate = null;
        long maxRemaining = 0;

        foreach (var seg in activeSegments)
        {
            if (seg.Status == SegmentStatus.Completed) continue;
            var remaining = seg.EndOffset - seg.CurrentOffset + 1;
            if (remaining > maxRemaining)
            {
                maxRemaining = remaining;
                candidate = seg;
            }
        }

        if (candidate == null || maxRemaining < minSplitSize)
        {
            return false;
        }

        // Calculate midpoint
        var half = maxRemaining / 2;
        var splitPoint = candidate.CurrentOffset + half;

        // Guard against splitPoint overtaking current offset or leaving tiny fragments
        if (splitPoint <= candidate.CurrentOffset || splitPoint > candidate.EndOffset)
        {
            return false;
        }

        // Old segment covers [CurrentOffset, splitPoint - 1]
        var oldEnd = candidate.EndOffset;
        candidate.EndOffset = splitPoint - 1;
        candidate.UpdatedAtUtc = DateTime.UtcNow;

        // New segment covers [splitPoint, oldEnd]
        newSegment = new DownloadSegment
        {
            DownloadId = candidate.DownloadId,
            SegmentIndex = newSegmentIndex,
            StartOffset = splitPoint,
            EndOffset = oldEnd,
            CurrentOffset = splitPoint,
            Status = SegmentStatus.Pending,
            UpdatedAtUtc = DateTime.UtcNow
        };

        return true;
    }
}
