using System;
using System.Collections.Generic;
using System.Linq;

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
    public long EndOffset { get; init; }
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

    public DynamicRangeScheduler(long totalBytes, int targetChunks = 32, long minChunkSize = 512 * 1024)
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

            // Size chunks dynamically: 512 KB to 2 MB based on file size
            long chunkSize = Math.Clamp(totalBytes / Math.Max(1, targetChunks), minChunkSize, 2 * 1024 * 1024);
            long start = 0;

            while (start < totalBytes)
            {
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
    /// Acquires work for an idle worker: either next pending range or an end-game hedged request.
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

            // 2. End-Game Mode: Hedge remaining active straggler ranges
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
