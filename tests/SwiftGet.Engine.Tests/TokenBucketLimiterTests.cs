using System.Diagnostics;
using FluentAssertions;
using SwiftGet.Engine.Throttling;
using Xunit;

namespace SwiftGet.Engine.Tests;

public class TokenBucketLimiterTests
{
    [Fact]
    public async Task ConsumeAsync_RateZero_ReturnsImmediately()
    {
        using var limiter = new TokenBucketLimiter(0);
        var sw = Stopwatch.StartNew();

        await limiter.ConsumeAsync(1_000_000);

        sw.ElapsedMilliseconds.Should().BeLessThan(50);
    }

    [Fact]
    public async Task ConsumeAsync_WithinBurstCapacity_ConsumesWithoutDelay()
    {
        // 100 KB/sec with 1.0s burst budget = 100 KB burst capacity
        using var limiter = new TokenBucketLimiter(100_000, burstSeconds: 1.0);

        // Allow tokens to accumulate for 500ms -> ~50 KB available
        await Task.Delay(500);

        var sw = Stopwatch.StartNew();
        await limiter.ConsumeAsync(40_000);
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(50);
    }

    [Fact]
    public async Task ConsumeAsync_ExceedingRate_ThrottlesAccurately()
    {
        // 50 KB/sec limit
        using var limiter = new TokenBucketLimiter(50_000, burstSeconds: 0.1);

        var sw = Stopwatch.StartNew();
        // Consume 50 KB -> should take approx 900-1100 ms
        await limiter.ConsumeAsync(50_000);
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeInRange(750, 1500);
    }

    [Fact]
    public async Task ConsumeAsync_ManySmallChunks_MaintainsThroughputWithoutStall()
    {
        // 200 KB/s rate limit, consume 20 chunks of 5 KB = 100 KB total (~500ms expected)
        using var limiter = new TokenBucketLimiter(200_000, burstSeconds: 0.5);

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 20; i++)
        {
            await limiter.ConsumeAsync(5_000);
        }
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeInRange(200, 1000);
    }

    [Fact]
    public async Task ConsumeAsync_ConcurrentConsumers_ShareBandwidthWithoutDeadlock()
    {
        // 100 KB/s rate shared across 4 concurrent tasks consuming 25 KB each (total 100 KB ~ 1s)
        using var limiter = new TokenBucketLimiter(100_000, burstSeconds: 0.2);

        var sw = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, 4)
            .Select(_ => Task.Run(async () => await limiter.ConsumeAsync(25_000)))
            .ToArray();

        await Task.WhenAll(tasks);
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeInRange(600, 1500);
    }
}
