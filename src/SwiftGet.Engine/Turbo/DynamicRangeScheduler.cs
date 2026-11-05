using System;
using System.Collections.Generic;
using System.Linq;
using SwiftGet.Core.Models;

namespace SwiftGet.Engine.Turbo;

/// <summary>
/// Status of a dynamic byte range.
/// </summary>
public enum RangeStatus
{
    Pending,
    Active,
    Completed,
    Hedged
}

/// <summary>
/// Represents an allocated byte range in the dynamic range scheduler.
/// </summary>
public sealed class DynamicRange
{
    public int Id { get; init; }
    public long StartOffset { get; init; }
    public long EndOffset { get; set; }
    public long CurrentOffset { get; set; }
    public RangeStatus Status { get; set; } = RangeStatus.Pending;
    public string? AssignedWorkerId { get; set; }
    public double EstimatedSpeedBytesPerSec { get; set; }

    public long TotalLength => EndOffset - StartOffset + 1;
    public long RemainingBytes => Math.Max(0, EndOffset - CurrentOffset + 1);
    public long DownloadedBytes => Math.Max(0, CurrentOffset - StartOffset);
    public bool IsComplete => CurrentOffset > EndOffset;
}

/// <summary>
/// Thread-safe work-stealing range scheduler with adaptive BDP sizing,
/// dynamic split stealing from slow workers, and end-game hedged requests.
/// </summary>
public sealed class DynamicRangeScheduler
{
    private readonly long _totalBytes;
    private readonly List<DynamicRange> _ranges = new();
    private readonly object _sync = new();
    private int _nextRangeId;

    public long TotalBytes => _totalBytes;
    public long CompletedBytes
    {
        get
        {
            lock (_sync)
            {
                return _ranges.Where(r => r.Status != RangeStatus.Hedged).Sum(r => r.DownloadedBytes);
            }
        }
    }

    public bool IsComplete
    {
        get
        {
            lock (_sync)
            {
                if (_totalBytes <= 0) return false;
                var primary = _ranges.Where(r => r.Status != RangeStatus.Hedged).ToList();
                return primary.Count > 0 && primary.All(r => r.IsComplete);
            }
        }
    }

    public bool IsEndGame
    {
        get
        {
            lock (_sync)
            {
                if (_totalBytes <= 0) return false;
                var remaining = _totalBytes - CompletedBytes;
                var threshold = Math.Max(1024 * 1024 * 2, (long)(_totalBytes * 0.05));
                return remaining <= threshold;
            }
        }
    }

    public DynamicRangeScheduler(long totalBytes, int targetChunks = 8, long minChunkSize = 512 * 1024)
    {
        _totalBytes = totalBytes;

        lock (_sync)
        {
            if (totalBytes <= 0)
            {
                _ranges.Add(new DynamicRange
                {
                    Id = _nextRangeId++,
                    StartOffset = 0,
                    EndOffset = 0,
                    CurrentOffset = 0,
                    Status = RangeStatus.Pending
                });
                return;
            }

            // Continuous segments: partition file across initial connections (default 8)
            int segmentsCount = Math.Clamp(targetChunks, 1, 32);
            long baseChunkSize = totalBytes / segmentsCount;
            long remainder = totalBytes % segmentsCount;

            long start = 0;
            for (int i = 0; i < segmentsCount; i++)
            {
                long chunkSize = baseChunkSize + (i < remainder ? 1 : 0);
                long end = Math.Min(totalBytes - 1, start + chunkSize - 1);

                _ranges.Add(new DynamicRange
                {
                    Id = _nextRangeId++,
                    StartOffset = start,
                    EndOffset = end,
                    CurrentOffset = start,
                    Status = RangeStatus.Pending
                });

                start = end + 1;
            }
        }
    }

    /// <summary>
    /// Restores range state from persisted segments when resuming a download.
    /// </summary>
    public void RestoreFromSegments(IReadOnlyList<DownloadSegment> segments)
    {
        if (segments == null || segments.Count == 0) return;

        lock (_sync)
        {
            _ranges.Clear();
            _nextRangeId = 0;

            foreach (var seg in segments.OrderBy(s => s.StartOffset))
            {
                var isComplete = seg.CurrentOffset > seg.EndOffset || seg.Status == SegmentStatus.Completed;
                _ranges.Add(new DynamicRange
                {
                    Id = seg.SegmentIndex >= 0 ? seg.SegmentIndex : _nextRangeId++,
                    StartOffset = seg.StartOffset,
                    EndOffset = seg.EndOffset,
                    CurrentOffset = Math.Max(seg.StartOffset, seg.CurrentOffset),
                    Status = isComplete ? RangeStatus.Completed : RangeStatus.Pending,
                    AssignedWorkerId = null
                });
                _nextRangeId = Math.Max(_nextRangeId, seg.SegmentIndex + 1);
            }
        }
    }

    /// <summary>
    /// Acquires work for an idle worker:
    /// 1. Next pending unassigned segment.
    /// 2. Dynamic split / work-stealing from the largest running active segment.
    /// 3. End-Game Mode: Hedge remaining straggler segments.
    /// </summary>
    public DynamicRange? AcquireWork(string workerId, long minSplitSize = 512 * 1024)
    {
        lock (_sync)
        {
            // 1. Try to pick up a pending unassigned range
            var pending = _ranges.FirstOrDefault(r => r.Status == RangeStatus.Pending);
            if (pending != null)
            {
                pending.Status = RangeStatus.Active;
                pending.AssignedWorkerId = workerId;
                return pending;
            }

            // 2. Dynamic Work Stealing: Split the upper half of the largest running active segment
            if (!IsEndGame)
            {
                DynamicRange? largest = null;
                long maxRemaining = 0;

                foreach (var r in _ranges)
                {
                    if (r.Status == RangeStatus.Active && !r.IsComplete && r.AssignedWorkerId != workerId)
                    {
                        var rem = r.RemainingBytes;
                        if (rem > maxRemaining)
                        {
                            maxRemaining = rem;
                            largest = r;
                        }
                    }
                }

                if (largest != null && maxRemaining >= (minSplitSize * 2))
                {
                    long half = maxRemaining / 2;
                    long splitStart = largest.CurrentOffset + half;
                    long splitEnd = largest.EndOffset;

                    // Shrink original segment to splitStart - 1
                    largest.EndOffset = splitStart - 1;

                    var stolen = new DynamicRange
                    {
                        Id = _nextRangeId++,
                        StartOffset = splitStart,
                        EndOffset = splitEnd,
                        CurrentOffset = splitStart,
                        Status = RangeStatus.Active,
                        AssignedWorkerId = workerId
                    };

                    _ranges.Add(stolen);
                    return stolen;
                }
            }

            // 3. End-Game Mode: Hedge remaining active straggler ranges
            if (IsEndGame)
            {
                var lagging = _ranges.FirstOrDefault(r => r.Status == RangeStatus.Active && !r.IsComplete && r.AssignedWorkerId != workerId);
                if (lagging != null && lagging.RemainingBytes > 32 * 1024)
                {
                    var hedged = new DynamicRange
                    {
                        Id = _nextRangeId++,
                        StartOffset = lagging.CurrentOffset,
                        EndOffset = lagging.EndOffset,
                        CurrentOffset = lagging.CurrentOffset,
                        Status = RangeStatus.Hedged,
                        AssignedWorkerId = workerId
                    };

                    _ranges.Add(hedged);
                    return hedged;
                }
            }

            return null;
        }
    }

    public void ReportProgress(int rangeId, long bytesRead, double speedBytesPerSec)
    {
        lock (_sync)
        {
            var range = _ranges.FirstOrDefault(r => r.Id == rangeId);
            if (range != null)
            {
                range.CurrentOffset += bytesRead;
                range.EstimatedSpeedBytesPerSec = speedBytesPerSec;

                if (range.IsComplete)
                {
                    range.Status = RangeStatus.Completed;
                }
            }
        }
    }

    /// <summary>
    /// Releases an in-progress range back to Pending status so another worker or retry can acquire it.
    /// If the range is already complete, it is marked Completed instead.
    /// </summary>
    public void ReleaseWork(DynamicRange? range)
    {
        if (range == null) return;

        lock (_sync)
        {
            var target = _ranges.FirstOrDefault(r => r.Id == range.Id);
            if (target != null)
            {
                if (target.IsComplete)
                {
                    target.Status = RangeStatus.Completed;
                    target.AssignedWorkerId = null;
                }
                else
                {
                    target.Status = RangeStatus.Pending;
                    target.AssignedWorkerId = null;
                    target.EstimatedSpeedBytesPerSec = 0;
                }
            }
        }
    }

    public IReadOnlyList<DynamicRange> GetSnapshot()
    {
        lock (_sync)
        {
            return _ranges.Select(r => new DynamicRange
            {
                Id = r.Id,
                StartOffset = r.StartOffset,
                EndOffset = r.EndOffset,
                CurrentOffset = r.CurrentOffset,
                Status = r.Status,
                AssignedWorkerId = r.AssignedWorkerId,
                EstimatedSpeedBytesPerSec = r.EstimatedSpeedBytesPerSec
            }).ToList();
        }
    }
}
