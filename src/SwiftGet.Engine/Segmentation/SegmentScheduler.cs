using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SwiftGet.Core.Models;
using SwiftGet.Core.Storage;
using SwiftGet.Engine.IO;
using SwiftGet.Engine.Throttling;

using SwiftGet.Engine.Turbo;

namespace SwiftGet.Engine.Segmentation;

public class SegmentScheduler : IDownloadScheduler
{
    private readonly HttpClient _httpClient;
    private readonly DownloadItem _item;
    private readonly ISparseFileWriter _writer;
    private readonly ISwiftGetDatabase _db;
    private readonly int _maxConnections;
    private readonly long _minSplitSize;
    private readonly TimeSpan _checkpointInterval;
    private readonly ITokenBucketLimiter? _limiter;
    private readonly ILogger _logger;

    private readonly List<DownloadSegment> _segments = [];
    private readonly object _segmentsSync = new();
    private readonly SemaphoreSlim _segmentsLock = new(1, 1);
    private long _downloadedBytes;

    public Action<HttpResponseMessage, byte[]>? OnFirstChunkRead { get; set; }

    public int ActiveConnections
    {
        get
        {
            lock (_segmentsSync)
            {
                return _segments.Count(s => !s.IsComplete);
            }
        }
    }

    public IReadOnlyList<ConnectionSnapshot> GetConnectionSnapshots()
    {
        lock (_segmentsSync)
        {
            if (_segments.Count == 0)
            {
                return [new ConnectionSnapshot(1, "Ready", 0, Math.Max(0, _item.TotalBytes - 1), DownloadedBytes, 0)];
            }

            return _segments.Select(s => new ConnectionSnapshot(
                Index: s.SegmentIndex,
                Status: s.IsComplete ? "Complete" : (s.DownloadedBytes > 0 ? "Receiving" : "Ready"),
                StartOffset: s.StartOffset,
                EndOffset: s.EndOffset,
                DownloadedBytes: s.DownloadedBytes,
                SpeedBytesPerSec: 0)).ToList();
        }
    }

    public CapacityEstimate CurrentCapacity => new(
        EstimatedBtlBwBytesPerSec: 0,
        MinRtProp: TimeSpan.Zero,
        CurrentThroughputBytesPerSec: 0,
        Bottleneck: BottleneckType.Probing,
        AdapterSpeedBytesPerSec: 0,
        BottleneckDescription: "Legacy Segment Scheduler");

    public IReadOnlyList<DownloadSegment> Segments
    {
        get
        {
            lock (_segmentsSync)
            {
                return _segments.Select(s => s.Clone()).ToList();
            }
        }
    }

    public Task<IReadOnlyList<DownloadSegment>> GetSegmentsAsync(CancellationToken ct = default)
    {
        lock (_segmentsSync)
        {
            IReadOnlyList<DownloadSegment> list = _segments.Select(s => s.Clone()).ToList();
            return Task.FromResult(list);
        }
    }

    public long DownloadedBytes => Interlocked.Read(ref _downloadedBytes);

    public SegmentScheduler(
        HttpClient httpClient,
        DownloadItem item,
        ISparseFileWriter writer,
        ISwiftGetDatabase db,
        int maxConnections = 8,
        long minSplitSize = DynamicSplitter.DefaultMinSplitSize,
        ITokenBucketLimiter? limiter = null,
        TimeSpan? checkpointInterval = null,
        ILogger? logger = null)
    {
        _httpClient = httpClient;
        _item = item;
        _writer = writer;
        _db = db;
        _maxConnections = Math.Clamp(maxConnections, 1, 32);
        _minSplitSize = minSplitSize;
        _limiter = limiter;
        _checkpointInterval = checkpointInterval ?? TimeSpan.FromSeconds(3);
        _logger = logger ?? NullLogger.Instance;

        _downloadedBytes = item.DownloadedBytes;
    }

    public async Task ExecuteAsync(CancellationToken ct)
    {
        // 1. Initialize or load segments
        await _segmentsLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var existing = await _db.GetSegmentsAsync(_item.Id, ct).ConfigureAwait(false);
            lock (_segmentsSync)
            {
                if (existing.Count > 0)
                {
                    _segments.AddRange(existing);
                }
                else
                {
                    var initial = DynamicSplitter.CreateInitialSegments(_item.Id, _item.TotalBytes, _maxConnections);
                    _segments.AddRange(initial);
                }
            }

            if (existing.Count == 0)
            {
                List<DownloadSegment> toSave;
                lock (_segmentsSync)
                {
                    toSave = _segments.Select(s => s.Clone()).ToList();
                }
                await _db.SaveSegmentsAsync(_item.Id, toSave, ct).ConfigureAwait(false);
            }

            // Recalculate downloaded bytes from segments
            long sum = 0;
            lock (_segmentsSync)
            {
                foreach (var seg in _segments)
                {
                    sum += seg.DownloadedBytes;
                }
            }
            Interlocked.Exchange(ref _downloadedBytes, sum);
        }
        finally
        {
            _segmentsLock.Release();
        }

        // 2. Setup bounded channel for disk writes with backpressure
        var channelCapacity = Math.Max(32, _maxConnections * 16);
        var channel = Channel.CreateBounded<SegmentChunk>(new BoundedChannelOptions(channelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = false,
            SingleReader = true
        });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // 3. Start writer consumer task to drain all produced chunks to disk
        var consumerTask = Task.Run(async () =>
        {
            await foreach (var chunk in channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                try
                {
                    if (chunk.Buffer != null && chunk.Length > 0)
                    {
                        await _writer.WriteAtAsync(chunk.Offset, chunk.Buffer.AsMemory(0, chunk.Length), CancellationToken.None).ConfigureAwait(false);
                        Interlocked.Add(ref _downloadedBytes, chunk.Length);
                    }
                }
                finally
                {
                    chunk.Return();
                }
            }
        }, CancellationToken.None);

        // 4. Start periodic checkpointing task
        var checkpointTask = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(_checkpointInterval);
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    await timer.WaitForNextTickAsync(cts.Token).ConfigureAwait(false);
                    await CheckpointAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Checkpoint failed for download {DownloadId}", _item.Id);
                }
            }
        }, CancellationToken.None);

        // 5. Worker execution & dynamic splitting loop
        var runningWorkers = new Dictionary<Task, SegmentWorker>();

        try
        {
            lock (_segmentsSync)
            {
                foreach (var seg in _segments.Where(s => !s.IsComplete))
                {
                    if (runningWorkers.Count >= _maxConnections) break;
                    var worker = new SegmentWorker(_httpClient, seg, _item, channel.Writer, _limiter, null, _logger)
                    {
                        OnFirstChunkRead = OnFirstChunkRead
                    };
                    var task = worker.RunAsync(cts.Token);
                    runningWorkers[task] = worker;
                }
            }

            while (runningWorkers.Count > 0)
            {
                var completedTask = await Task.WhenAny(runningWorkers.Keys).ConfigureAwait(false);
                runningWorkers.Remove(completedTask);

                // Propagate exception if worker failed
                await completedTask.ConfigureAwait(false);

                // Worker finished its segment, check if dynamic re-segmentation is possible
                if (runningWorkers.Count < _maxConnections)
                {
                    DownloadSegment? newSegment = null;
                    List<DownloadSegment>? segmentsSnapshot = null;

                    lock (_segmentsSync)
                    {
                        var nextIndex = _segments.Count;
                        if (DynamicSplitter.TrySplitLargestSegment(_segments, _minSplitSize, nextIndex, out newSegment) && newSegment != null)
                        {
                            _segments.Add(newSegment);
                            segmentsSnapshot = _segments.Select(s => s.Clone()).ToList();
                        }
                    }

                    if (newSegment != null && segmentsSnapshot != null)
                    {
                        await _db.SaveSegmentsAsync(_item.Id, segmentsSnapshot, CancellationToken.None).ConfigureAwait(false);

                        var newWorker = new SegmentWorker(_httpClient, newSegment, _item, channel.Writer, _limiter, null, _logger)
                        {
                            OnFirstChunkRead = OnFirstChunkRead
                        };
                        var newTask = newWorker.RunAsync(cts.Token);
                        runningWorkers[newTask] = newWorker;
                    }
                }
            }
        }
        catch
        {
            cts.Cancel();
            throw;
        }
        finally
        {
            // Close writer channel and wait for consumer to drain
            channel.Writer.Complete();
            await consumerTask.ConfigureAwait(false);

            cts.Cancel();
            try
            {
                await checkpointTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on cancellation
            }

            // Final disk flush and DB checkpoint
            await _writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            await CheckpointAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task CheckpointAsync(CancellationToken ct)
    {
        IReadOnlyList<DownloadSegment> snapshot;
        long bytes;

        lock (_segmentsSync)
        {
            snapshot = _segments.Select(s => s.Clone()).ToList();
            bytes = Interlocked.Read(ref _downloadedBytes);
        }

        await _db.CheckpointProgressAsync(_item.Id, bytes, snapshot, ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _segmentsLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
