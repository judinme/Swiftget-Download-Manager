using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SwiftGet.Core.Models;
using SwiftGet.Core.Storage;
using SwiftGet.Engine.IO;
using SwiftGet.Engine.Network;
using SwiftGet.Engine.Segmentation;
using SwiftGet.Engine.Throttling;

namespace SwiftGet.Engine.Turbo;

/// <summary>
/// High-performance self-tuning Turbo Download Scheduler.
/// Replaces static equal-segment partitioning with BBR-style link capacity estimation,
/// adaptive concurrency control, dynamic work-stealing, zero-stall disk write coalescing,
/// and end-game hedged request straggler elimination.
/// </summary>
public sealed class TurboDownloadScheduler : IDownloadScheduler
{
    private readonly HttpClient _httpClient;
    private readonly DownloadItem _item;
    private readonly ISparseFileWriter _sparseWriter;
    private readonly ISwiftGetDatabase _db;
    private readonly ICapacityEstimator _capacityEstimator;
    private readonly IConcurrencyController _concurrencyController;
    private readonly ITokenBucketLimiter? _limiter;
    private readonly ILogger _logger;

    private readonly DynamicRangeScheduler _rangeScheduler;
    private readonly CoalescingAsyncDiskWriter _diskWriter;
    private readonly ConcurrentDictionary<string, Task> _activeWorkerTasks = new();
    private readonly CancellationTokenSource _cts = new();

    private long _downloadedBytes;
    private int _workerIdCounter;

    public long DownloadedBytes => Interlocked.Read(ref _downloadedBytes);
    public int ActiveConnections => _activeWorkerTasks.Count;
    public CapacityEstimate CurrentCapacity => _capacityEstimator.CurrentEstimate;
    public DynamicRangeScheduler RangeScheduler => _rangeScheduler;

    public Action<HttpResponseMessage, byte[]>? OnFirstChunkRead { get; set; }

    public TurboDownloadScheduler(
        HttpClient httpClient,
        DownloadItem item,
        ISparseFileWriter sparseWriter,
        ISwiftGetDatabase db,
        ICapacityEstimator? capacityEstimator = null,
        IConcurrencyController? concurrencyController = null,
        ITokenBucketLimiter? limiter = null,
        ILogger? logger = null)
    {
        _httpClient = httpClient;
        _item = item;
        _sparseWriter = sparseWriter;
        _db = db;
        _limiter = limiter;
        _logger = logger ?? NullLogger.Instance;

        _capacityEstimator = capacityEstimator ?? new CapacityEstimator();
        _concurrencyController = concurrencyController ?? new AdaptiveConcurrencyController();
        _rangeScheduler = new DynamicRangeScheduler(item.TotalBytes, targetChunks: Math.Max(8, _concurrencyController.TargetConnections));
        _diskWriter = new CoalescingAsyncDiskWriter(_sparseWriter);

        _downloadedBytes = item.DownloadedBytes;
    }

    public async Task ExecuteAsync(CancellationToken ct)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var cancellationToken = linkedCts.Token;

        // Restore existing segments if resuming a paused download
        try
        {
            var existingSegments = await _db.GetSegmentsAsync(_item.Id, cancellationToken).ConfigureAwait(false);
            if (existingSegments != null && existingSegments.Count > 0)
            {
                _rangeScheduler.RestoreFromSegments(existingSegments);
                Interlocked.Exchange(ref _downloadedBytes, _rangeScheduler.CompletedBytes);
            }
            else if (_item.DownloadedBytes > 0)
            {
                Interlocked.Exchange(ref _downloadedBytes, _item.DownloadedBytes);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to restore segments from database for {Id}", _item.Id);
        }

        // Start background telemetry & controller loop
        var controllerTask = Task.Run(() => RunControllerLoopAsync(cancellationToken), CancellationToken.None);

        try
        {
            // Initial concurrency seeding
            AdjustWorkerPool(cancellationToken);

            // Wait until file is fully downloaded
            while (!_rangeScheduler.IsComplete && !cancellationToken.IsCancellationRequested)
            {
                AdjustWorkerPool(cancellationToken);
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        finally
        {
            _cts.Cancel();

            // Wait for all active worker tasks to complete cleanly
            try
            {
                await Task.WhenAll(_activeWorkerTasks.Values).ConfigureAwait(false);
            }
            catch { }

            try { await controllerTask.ConfigureAwait(false); } catch { }

            // Flush disk pipeline and persist final checkpoint
            await _diskWriter.DisposeAsync().ConfigureAwait(false);
            Interlocked.Exchange(ref _downloadedBytes, _rangeScheduler.CompletedBytes);
            await CheckpointAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private void AdjustWorkerPool(CancellationToken ct)
    {
        var target = _item.SupportsRanges ? _concurrencyController.TargetConnections : 1;
        while (_activeWorkerTasks.Count < target && !_rangeScheduler.IsComplete && !ct.IsCancellationRequested)
        {
            var workerId = $"turbo-w{Interlocked.Increment(ref _workerIdCounter)}";
            var workerTask = Task.Run(() => RunWorkerLoopAsync(workerId, ct), CancellationToken.None);
            _activeWorkerTasks[workerId] = workerTask;
        }
    }

    private async Task RunWorkerLoopAsync(string workerId, CancellationToken ct)
    {
        const int bufferSize = 256 * 1024;
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);

        try
        {
            while (!ct.IsCancellationRequested && !_rangeScheduler.IsComplete)
            {
                var targetCount = _item.SupportsRanges ? _concurrencyController.TargetConnections : 1;
                if (_activeWorkerTasks.Count > targetCount && !_rangeScheduler.IsEndGame)
                {
                    // Scale down excess workers in steady state
                    break;
                }

                var range = _rangeScheduler.AcquireWork(workerId);
                if (range == null)
                {
                    // No work currently available, back off slightly
                    await Task.Delay(25, ct).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    await DownloadRangeAsync(range, workerId, buffer, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Read timeout / stall watchdog triggered on this range
                    _logger.LogWarning("Worker {WorkerId} stalled (30s read timeout) on range {RangeId} [{CurrentOffset}-{EndOffset}]. Releasing range for retry.",
                        workerId, range.Id, range.CurrentOffset, range.EndOffset);
                    _rangeScheduler.ReleaseWork(range);
                    await Task.Delay(500, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "Worker {WorkerId} failed downloading range {RangeId}. Releasing range for retry.", workerId, range.Id);
                    _rangeScheduler.ReleaseWork(range);
                    await Task.Delay(250, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Worker {WorkerId} faulted", workerId);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            _activeWorkerTasks.TryRemove(workerId, out _);
        }
    }

    private async Task DownloadRangeAsync(DynamicRange range, string workerId, byte[] buffer, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, _item.FinalUrl)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        req.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");

        if (_item.SupportsRanges && range.EndOffset >= range.CurrentOffset)
        {
            req.Headers.Range = new RangeHeaderValue(range.CurrentOffset, range.EndOffset);
            if (!string.IsNullOrWhiteSpace(_item.ETag))
            {
                req.Headers.TryAddWithoutValidation("If-Range", _item.ETag);
            }
        }

        if (!string.IsNullOrWhiteSpace(_item.UserAgent))
        {
            req.Headers.TryAddWithoutValidation("User-Agent", _item.UserAgent);
        }

        var rttSw = Stopwatch.StartNew();
        using var response = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        rttSw.Stop();

        if (response.StatusCode == HttpStatusCode.TooManyRequests || response.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            TimeSpan? retryAfter = null;
            if (response.Headers.RetryAfter?.Delta != null) retryAfter = response.Headers.RetryAfter.Delta;
            _concurrencyController.ReportThrottled(retryAfter);
            _capacityEstimator.RecordSample(0, TimeSpan.FromMilliseconds(100), rttSw.Elapsed, _activeWorkerTasks.Count, serverThrottled: true);
            _rangeScheduler.ReleaseWork(range);
            return;
        }

        if (response.StatusCode != HttpStatusCode.PartialContent && response.StatusCode != HttpStatusCode.OK)
        {
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var speedSw = Stopwatch.StartNew();
        long bytesInThisRange = 0;

        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        while (range.CurrentOffset <= range.EndOffset && !ct.IsCancellationRequested)
        {
            var maxToRead = (int)Math.Min(buffer.Length, range.EndOffset - range.CurrentOffset + 1);
            if (maxToRead <= 0) break;

            // 30-second stall watchdog on stream read
            readCts.CancelAfter(TimeSpan.FromSeconds(30));
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, maxToRead), readCts.Token).ConfigureAwait(false);
            if (bytesRead <= 0) break;

            // Apply limiter if configured
            if (_limiter != null)
            {
                await _limiter.ConsumeAsync(bytesRead, ct).ConfigureAwait(false);
            }

            // Rent chunk buffer for write pipeline
            var chunkBuffer = ArrayPool<byte>.Shared.Rent(bytesRead);
            Array.Copy(buffer, 0, chunkBuffer, 0, bytesRead);

            var chunk = new SegmentChunk(range.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), range.CurrentOffset, bytesRead, chunkBuffer);
            await _diskWriter.Writer.WriteAsync(chunk, ct).ConfigureAwait(false);

            Interlocked.Add(ref _downloadedBytes, bytesRead);
            bytesInThisRange += bytesRead;

            var elapsedSec = speedSw.Elapsed.TotalSeconds;
            var instantSpeed = elapsedSec > 0 ? (bytesInThisRange / elapsedSec) : 0;
            _rangeScheduler.ReportProgress(range.Id, bytesRead, instantSpeed);
        }

        if (!range.IsComplete && !ct.IsCancellationRequested)
        {
            _rangeScheduler.ReleaseWork(range);
        }
    }

    private async Task RunControllerLoopAsync(CancellationToken ct)
    {
        var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
        var lastBytes = _downloadedBytes;
        var sw = Stopwatch.StartNew();

        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var currentBytes = DownloadedBytes;
                var delta = Math.Max(0, currentBytes - lastBytes);
                lastBytes = currentBytes;

                var elapsed = sw.Elapsed;
                sw.Restart();

                var activeConns = _activeWorkerTasks.Count;
                var backpressure = _diskWriter.BackpressureEvents;

                _capacityEstimator.RecordSample(
                    bytesDelivered: delta,
                    interval: elapsed,
                    observedRtt: _capacityEstimator.CurrentEstimate.MinRtProp,
                    activeConnections: activeConns,
                    diskBackpressureCount: backpressure);

                _concurrencyController.Evaluate(_capacityEstimator.CurrentEstimate, elapsed);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task CheckpointAsync(CancellationToken ct)
    {
        try
        {
            var snapshot = _rangeScheduler.GetSnapshot();
            var segments = snapshot.Select(r => new DownloadSegment
            {
                DownloadId = _item.Id,
                SegmentIndex = r.Id,
                StartOffset = r.StartOffset,
                EndOffset = r.EndOffset,
                CurrentOffset = r.CurrentOffset,
                Status = r.IsComplete ? SegmentStatus.Completed : SegmentStatus.Downloading
            }).ToList();

            await _db.CheckpointProgressAsync(_item.Id, DownloadedBytes, segments, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to checkpoint Turbo download progress");
        }
    }

    public IReadOnlyList<ConnectionSnapshot> GetConnectionSnapshots()
    {
        var ranges = _rangeScheduler.GetSnapshot();
        if (ranges.Count == 0)
        {
            return [new ConnectionSnapshot(1, "Ready", 0, Math.Max(0, _item.TotalBytes - 1), DownloadedBytes, 0)];
        }

        var activeRanges = ranges.Where(r => r.Status == RangeStatus.Active || r.Status == RangeStatus.Hedged).ToList();
        if (activeRanges.Count > 0)
        {
            var list = new List<ConnectionSnapshot>();
            for (int i = 0; i < activeRanges.Count; i++)
            {
                var r = activeRanges[i];
                var status = r.Status == RangeStatus.Hedged ? "Hedged" : "Receiving";
                list.Add(new ConnectionSnapshot(
                    Index: i + 1,
                    Status: status,
                    StartOffset: r.StartOffset,
                    EndOffset: r.EndOffset,
                    DownloadedBytes: r.DownloadedBytes,
                    SpeedBytesPerSec: r.EstimatedSpeedBytesPerSec));
            }
            return list;
        }

        // If no ranges are currently active (e.g. initial or complete)
        var recent = ranges.Take(Math.Min(16, ranges.Count)).ToList();
        return recent.Select((r, i) => new ConnectionSnapshot(
            Index: i + 1,
            Status: r.IsComplete ? "Complete" : (r.Status == RangeStatus.Pending ? "Pending" : "Ready"),
            StartOffset: r.StartOffset,
            EndOffset: r.EndOffset,
            DownloadedBytes: r.DownloadedBytes,
            SpeedBytesPerSec: r.EstimatedSpeedBytesPerSec)).ToList();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}
