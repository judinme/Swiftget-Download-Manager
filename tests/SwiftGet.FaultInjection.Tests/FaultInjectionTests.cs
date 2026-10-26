using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using SwiftGet.Core.Models;
using SwiftGet.Core.Storage;
using SwiftGet.Engine.Coordinator;
using SwiftGet.Engine.Network;
using SwiftGet.Engine.Throttling;
using Xunit;

namespace SwiftGet.FaultInjection.Tests;

public class FaultInjectionTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"fault_test_{Guid.NewGuid():N}");
    private SqliteDatabase? _db;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        _db = new SqliteDatabase(Path.Combine(_tempDir, "fault.db"));
        await _db.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        if (_db != null) await _db.DisposeAsync();
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
    }

    [Fact]
    public async Task RateLimitBurst_WithRetryAfter_ShouldSucceedAfterBackoff()
    {
        var payload = new byte[64 * 1024]; // 64 KB
        Random.Shared.NextBytes(payload);
        var expectedHash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        var attempts = 0;

        using var host = new HostBuilder()
            .ConfigureWebHost(wh =>
            {
                wh.UseTestServer();
                wh.Configure(app =>
                {
                    app.Run(async ctx =>
                    {
                        var req = ctx.Request;
                        var res = ctx.Response;

                        res.Headers.AcceptRanges = "bytes";
                        res.Headers.ETag = "\"burst-etag\"";

                        if (HttpMethods.IsHead(req.Method))
                        {
                            res.ContentLength = payload.Length;
                            res.StatusCode = StatusCodes.Status200OK;
                            return;
                        }

                        // Inject 429 for the first 2 GET requests
                        if (Interlocked.Increment(ref attempts) <= 2)
                        {
                            res.StatusCode = StatusCodes.Status429TooManyRequests;
                            res.Headers.RetryAfter = "1";
                            await res.WriteAsync("Rate limited. Try again in 1s.");
                            return;
                        }

                        // Subsequent requests succeed with range support
                        await WritePayloadWithRangeSupportAsync(ctx, payload);
                    });
                });
            })
            .Start();

        var client = host.GetTestClient();
        var destination = Path.Combine(_tempDir, "ratelimit_output.bin");

        var item = new DownloadItem
        {
            Url = "http://localhost/ratelimit.bin",
            DestinationPath = destination,
            FileName = "ratelimit_output.bin",
            ChecksumExpected = expectedHash,
            ChecksumType = ChecksumType.Sha256
        };

        var coordinator = new DownloadCoordinator(client, _db!);
        await coordinator.StartAsync(item, maxConnections: 1);

        item.Status.Should().Be(DownloadStatus.Completed);
        attempts.Should().BeGreaterThan(2, "Rate limit bursts must have triggered retries");

        var actualHash = await DownloadCoordinator.ComputeChecksumAsync(destination, ChecksumType.Sha256, CancellationToken.None);
        actualHash.Should().Be(expectedHash);
    }

    [Fact]
    public async Task SpeedLimiter_ShouldAccuratelyThrottleThroughput()
    {
        var payload = new byte[100 * 1024]; // 100 KB
        Random.Shared.NextBytes(payload);

        using var host = new HostBuilder()
            .ConfigureWebHost(wh =>
            {
                wh.UseTestServer();
                wh.Configure(app =>
                {
                    app.Run(async ctx =>
                    {
                        await WritePayloadWithRangeSupportAsync(ctx, payload);
                    });
                });
            })
            .Start();

        var client = host.GetTestClient();
        var destination = Path.Combine(_tempDir, "throttled_output.bin");

        var item = new DownloadItem
        {
            Url = "http://localhost/throttled.bin",
            DestinationPath = destination,
            FileName = "throttled_output.bin",
            SpeedLimitBytesPerSec = 50 * 1024 // 50 KB/sec limit
        };

        var coordinator = new DownloadCoordinator(client, _db!);
        var sw = Stopwatch.StartNew();

        await coordinator.StartAsync(item, maxConnections: 1);
        sw.Stop();

        item.Status.Should().Be(DownloadStatus.Completed);
        // At 50 KB/sec, downloading 100 KB with 0 initial burst takes ~2.0 seconds
        sw.Elapsed.TotalSeconds.Should().BeGreaterThanOrEqualTo(1.7, "Bandwidth limiter must enforce target transfer rate");
    }

    [Fact]
    public async Task MirrorFailover_ShouldSwitchToHealthyMirror()
    {
        var payload = new byte[32 * 1024];
        Random.Shared.NextBytes(payload);
        var expectedHash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        using var host = new HostBuilder()
            .ConfigureWebHost(wh =>
            {
                wh.UseTestServer();
                wh.Configure(app =>
                {
                    app.Run(async ctx =>
                    {
                        var path = ctx.Request.Path.Value;
                        if (path == "/primary.bin")
                        {
                            // Primary URL completely unavailable
                            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
                            return;
                        }

                        if (path == "/mirror.bin")
                        {
                            await WritePayloadWithRangeSupportAsync(ctx, payload);
                        }
                    });
                });
            })
            .Start();

        var client = host.GetTestClient();
        var destination = Path.Combine(_tempDir, "mirror_output.bin");

        var item = new DownloadItem
        {
            Url = "http://localhost/primary.bin",
            DestinationPath = destination,
            FileName = "mirror_output.bin",
            ChecksumExpected = expectedHash,
            ChecksumType = ChecksumType.Sha256
        };

        var mirrorHandler = new MirrorFailoverHandler(item.Url, ["http://localhost/mirror.bin"]);
        var coordinator = new DownloadCoordinator(client, _db!);

        await coordinator.StartAsync(item, mirrorHandler: mirrorHandler);

        item.Status.Should().Be(DownloadStatus.Completed);
        item.FinalUrl.Should().Be("http://localhost/mirror.bin");

        var actualHash = await DownloadCoordinator.ComputeChecksumAsync(destination, ChecksumType.Sha256, CancellationToken.None);
        actualHash.Should().Be(expectedHash);
    }

    [Fact]
    public async Task ExpiredLink_ShouldTriggerLinkRefreshHookAndResume()
    {
        var payload = new byte[32 * 1024];
        Random.Shared.NextBytes(payload);
        var expectedHash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        using var host = new HostBuilder()
            .ConfigureWebHost(wh =>
            {
                wh.UseTestServer();
                wh.Configure(app =>
                {
                    app.Run(async ctx =>
                    {
                        var path = ctx.Request.Path.Value;
                        if (path == "/expired.bin")
                        {
                            // Link expired
                            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                            await ctx.Response.WriteAsync("Signature expired.");
                            return;
                        }

                        if (path == "/refreshed.bin")
                        {
                            await WritePayloadWithRangeSupportAsync(ctx, payload);
                        }
                    });
                });
            })
            .Start();

        var client = host.GetTestClient();
        var destination = Path.Combine(_tempDir, "refreshed_output.bin");

        var item = new DownloadItem
        {
            Url = "http://localhost/expired.bin",
            DestinationPath = destination,
            FileName = "refreshed_output.bin",
            ChecksumExpected = expectedHash,
            ChecksumType = ChecksumType.Sha256
        };

        var refreshHook = new MockLinkRefreshHook("http://localhost/refreshed.bin");
        var coordinator = new DownloadCoordinator(client, _db!);

        await coordinator.StartAsync(item, linkRefreshHook: refreshHook);

        item.Status.Should().Be(DownloadStatus.Completed);
        item.FinalUrl.Should().Be("http://localhost/refreshed.bin");
        refreshHook.RefreshInvoked.Should().BeTrue();

        var actualHash = await DownloadCoordinator.ComputeChecksumAsync(destination, ChecksumType.Sha256, CancellationToken.None);
        actualHash.Should().Be(expectedHash);
    }

    private static async Task WritePayloadWithRangeSupportAsync(HttpContext ctx, byte[] payload)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        res.Headers.AcceptRanges = "bytes";
        res.Headers.ETag = "\"test-etag-v1\"";

        if (HttpMethods.IsHead(req.Method))
        {
            res.ContentLength = payload.Length;
            res.StatusCode = StatusCodes.Status200OK;
            return;
        }

        var rangeHeader = req.Headers.Range.ToString();
        if (rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            var parts = rangeHeader["bytes=".Length..].Split('-');
            var start = int.Parse(parts[0], CultureInfo.InvariantCulture);
            var end = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1])
                ? int.Parse(parts[1], CultureInfo.InvariantCulture)
                : payload.Length - 1;
            var len = end - start + 1;

            res.StatusCode = StatusCodes.Status206PartialContent;
            res.Headers["Content-Range"] = $"bytes {start}-{end}/{payload.Length}";
            res.ContentLength = len;
            await res.Body.WriteAsync(payload.AsMemory(start, len));
            return;
        }

        res.StatusCode = StatusCodes.Status200OK;
        res.ContentLength = payload.Length;
        await res.Body.WriteAsync(payload);
    }

    private sealed class MockLinkRefreshHook : ILinkRefreshHook
    {
        private readonly string _freshUrl;
        public bool RefreshInvoked { get; private set; }

        public MockLinkRefreshHook(string freshUrl) => _freshUrl = freshUrl;

        public Task<string?> RefreshUrlAsync(string downloadId, string currentUrl, CancellationToken ct = default)
        {
            RefreshInvoked = true;
            return Task.FromResult<string?>(_freshUrl);
        }
    }
}
