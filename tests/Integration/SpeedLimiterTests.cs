using System.Diagnostics;
using FluentAssertions;
using SwiftGet.Engine.Throttling;
using Xunit;

namespace SwiftGet.Integration;

public class SpeedLimiterTests
{
    [Fact]
    public async Task ConsumeAsync_WhenRateIsZero_ReturnsImmediatelyWithoutDelay()
    {
        using var limiter = new TokenBucketLimiter(bytesPerSecond: 0);

        var sw = Stopwatch.StartNew();
        await limiter.ConsumeAsync(1024 * 1024); // 1 MB
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(100);
    }

    [Fact]
    public async Task ConsumeAsync_WhenByteCountZeroOrNegative_ReturnsImmediately()
    {
        using var limiter = new TokenBucketLimiter(bytesPerSecond: 1024);

        var sw = Stopwatch.StartNew();
        await limiter.ConsumeAsync(0);
        await limiter.ConsumeAsync(-500);
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(50);
    }

    [Fact]
    public async Task ConsumeAsync_ExceedingBurstCapacity_DelaysAccurately()
    {
        // 50 KB/sec, burst budget 0.1s => max burst 5 KB
        const long rate = 50 * 1024;
        using var limiter = new TokenBucketLimiter(bytesPerSecond: rate, burstSeconds: 0.1);

        // First consume to exhaust available tokens
        await limiter.ConsumeAsync(5 * 1024);

        // Next consume 25 KB. At 50 KB/s, 25 KB should take ~500ms
        var sw = Stopwatch.StartNew();
        await limiter.ConsumeAsync(25 * 1024);
        sw.Stop();

        // Allow reasonable timing jitter on Windows (350ms - 800ms)
        sw.ElapsedMilliseconds.Should().BeInRange(350, 950);
    }

    [Fact]
    public void BytesPerSecond_CanBeUpdatedDynamicallyMidFlight()
    {
        using var limiter = new TokenBucketLimiter(bytesPerSecond: 10 * 1024);
        limiter.BytesPerSecond.Should().Be(10 * 1024);

        limiter.BytesPerSecond = 100 * 1024;
        limiter.BytesPerSecond.Should().Be(100 * 1024);

        // Setting negative rate clamps to 0 (unlimited)
        limiter.BytesPerSecond = -50;
        limiter.BytesPerSecond.Should().Be(0);
    }

    [Fact]
    public async Task ConsumeAsync_ConcurrentWorkers_ScheduleWithoutDeadlock()
    {
        // 100 KB/s, burst budget 0.2s => 20 KB burst
        using var limiter = new TokenBucketLimiter(bytesPerSecond: 100 * 1024, burstSeconds: 0.2);

        var tasks = Enumerable.Range(0, 5).Select(async _ =>
        {
            await limiter.ConsumeAsync(5 * 1024);
        });

        var allTask = Task.WhenAll(tasks);
        var completed = await Task.WhenAny(allTask, Task.Delay(3000));

        completed.Should().Be(allTask, "concurrent consumption should complete without deadlocking");
    }
}
