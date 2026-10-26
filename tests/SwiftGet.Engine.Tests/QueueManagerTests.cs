using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using SwiftGet.Core.Models;
using SwiftGet.Core.Storage;
using SwiftGet.Engine.Coordinator;
using SwiftGet.Engine.Queues;
using Xunit;

namespace SwiftGet.Engine.Tests;

public class QueueManagerTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"qm_test_{Guid.NewGuid():N}");
    private string _dbPath = null!;
    private SqliteDatabase? _db;
    private IHost? _testServerHost;
    private HttpClient? _client;
    private DownloadCoordinator? _coordinator;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test.db");
        _db = new SqliteDatabase(_dbPath);
        await _db.InitializeAsync();

        var payload = new byte[1024 * 64];
        Random.Shared.NextBytes(payload);

        _testServerHost = new HostBuilder()
            .ConfigureWebHost(wh =>
            {
                wh.UseTestServer();
                wh.Configure(app =>
                {
                    app.Run(async ctx =>
                    {
                        var res = ctx.Response;
                        res.Headers.AcceptRanges = "bytes";
                        res.Headers.ETag = "\"test-etag\"";
                        res.ContentLength = payload.Length;

                        if (HttpMethods.IsHead(ctx.Request.Method))
                        {
                            res.StatusCode = StatusCodes.Status200OK;
                            return;
                        }

                        var rangeHeader = ctx.Request.Headers.Range.ToString();
                        if (rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
                        {
                            var parts = rangeHeader["bytes=".Length..].Split('-');
                            var start = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
                            var end = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1])
                                ? int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)
                                : payload.Length - 1;
                            var len = end - start + 1;
                            res.StatusCode = StatusCodes.Status206PartialContent;
                            res.Headers["Content-Range"] = $"bytes {start}-{end}/{payload.Length}";
                            res.ContentLength = len;
                            await res.Body.WriteAsync(payload.AsMemory(start, len));
                            return;
                        }

                        res.StatusCode = StatusCodes.Status200OK;
                        await res.Body.WriteAsync(payload);
                    });
                });
            })
            .Start();

        _client = _testServerHost.GetTestClient();
        _coordinator = new DownloadCoordinator(_client, _db);
    }

    public async Task DisposeAsync()
    {
        if (_db != null)
        {
            await _db.DisposeAsync();
        }
        _client?.Dispose();
        if (_testServerHost != null)
        {
            await _testServerHost.StopAsync();
            _testServerHost.Dispose();
        }

        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }
    }

    [Fact]
    public async Task ProcessQueueAsync_ShouldExecuteAllItemsAndFireCompletionEvents()
    {
        var qId = "q-test-" + Guid.NewGuid().ToString("N");
        var queue = new DownloadQueue
        {
            Id = qId,
            Name = "Batch Queue",
            MaxConcurrentDownloads = 2,
            IsActive = true
        };
        await _db!.SaveQueueAsync(queue);

        var item1 = new DownloadItem
        {
            Id = "item-1-" + Guid.NewGuid().ToString("N"),
            Url = "http://localhost/test1.bin",
            DestinationPath = Path.Combine(_tempDir, "test1.bin"),
            FileName = "test1.bin",
            QueueId = qId,
            Priority = 0
        };

        var item2 = new DownloadItem
        {
            Id = "item-2-" + Guid.NewGuid().ToString("N"),
            Url = "http://localhost/test2.bin",
            DestinationPath = Path.Combine(_tempDir, "test2.bin"),
            FileName = "test2.bin",
            QueueId = qId,
            Priority = 1
        };

        await _db.SaveDownloadAsync(item1);
        await _db.SaveDownloadAsync(item2);

        using var queueManager = new QueueManager(_db, _coordinator!);

        var startedCount = 0;
        var completedCount = 0;
        var failedCount = 0;
        var queueCompletedFired = false;
        var tcs = new TaskCompletionSource<bool>();

        string? failureError = null;
        queueManager.DownloadStarted += (s, item) => Interlocked.Increment(ref startedCount);
        queueManager.DownloadCompleted += (s, item) => Interlocked.Increment(ref completedCount);
        queueManager.DownloadFailed += (s, item) =>
        {
            failureError = item.ErrorMessage;
            Interlocked.Increment(ref failedCount);
        };
        queueManager.QueueCompleted += (s, e) =>
        {
            queueCompletedFired = true;
            tcs.TrySetResult(true);
        };

        await queueManager.StartQueueAsync(qId);

        // Wait for queue to finish
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(5000));
        completed.Should().Be(tcs.Task, $"Queue should complete within 5 seconds. Failure: {failureError}");

        startedCount.Should().Be(2);
        completedCount.Should().Be(2);
        queueCompletedFired.Should().BeTrue();

        var updatedItem1 = await _db.GetDownloadAsync(item1.Id);
        updatedItem1!.Status.Should().Be(DownloadStatus.Completed);

        var updatedItem2 = await _db.GetDownloadAsync(item2.Id);
        updatedItem2!.Status.Should().Be(DownloadStatus.Completed);
    }

    [Fact]
    public async Task MoveItemAsync_ShouldSwapPrioritiesCorrectly()
    {
        var qId = "q-order-" + Guid.NewGuid().ToString("N");
        var queue = new DownloadQueue { Id = qId, Name = "Order Queue" };
        await _db!.SaveQueueAsync(queue);

        var item1 = new DownloadItem { Id = "d1", Url = "http://localhost/1", QueueId = qId, Priority = 0 };
        var item2 = new DownloadItem { Id = "d2", Url = "http://localhost/2", QueueId = qId, Priority = 10 };
        await _db.SaveDownloadAsync(item1);
        await _db.SaveDownloadAsync(item2);

        using var queueManager = new QueueManager(_db, _coordinator!);

        // Move item2 up (-1)
        await queueManager.MoveItemAsync(qId, "d2", -1);

        var items = await _db.GetDownloadsByQueueAsync(qId);
        items[0].Id.Should().Be("d2");
        items[1].Id.Should().Be("d1");
    }

    [Fact]
    public async Task StartQueueAsync_ConcurrentCalls_StartsSingleWorker()
    {
        var qId = "q-concurrent-" + Guid.NewGuid().ToString("N");
        var queue = new DownloadQueue { Id = qId, Name = "Concurrent Queue" };
        await _db!.SaveQueueAsync(queue);

        using var queueManager = new QueueManager(_db, _coordinator!);

        // Start 5 concurrent tasks trying to start the same queue
        var tasks = Enumerable.Range(0, 5).Select(_ => queueManager.StartQueueAsync(qId)).ToArray();
        await Task.WhenAll(tasks);

        queueManager.IsQueueRunning(qId).Should().BeTrue();
        queueManager.GetRunningQueueIds().Count(id => id == qId).Should().Be(1);

        await queueManager.StopQueueAsync(qId);
        queueManager.IsQueueRunning(qId).Should().BeFalse();
    }

    [Fact]
    public async Task StopQueueAsync_CancelsRunningQueue()
    {
        var qId = "q-cancel-" + Guid.NewGuid().ToString("N");
        var queue = new DownloadQueue { Id = qId, Name = "Cancel Queue" };
        await _db!.SaveQueueAsync(queue);

        using var queueManager = new QueueManager(_db, _coordinator!);

        await queueManager.StartQueueAsync(qId);
        queueManager.IsQueueRunning(qId).Should().BeTrue();

        await queueManager.StopQueueAsync(qId);
        queueManager.IsQueueRunning(qId).Should().BeFalse();
    }
}
