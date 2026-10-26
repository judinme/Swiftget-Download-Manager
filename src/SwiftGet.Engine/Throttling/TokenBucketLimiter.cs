using System.Diagnostics;

namespace SwiftGet.Engine.Throttling;

/// <summary>
/// Thread-safe token bucket bandwidth rate limiter with monotonic Stopwatch timing,
/// configurable burst budget, and non-blocking asynchronous reservation scheduling.
/// </summary>
public sealed class TokenBucketLimiter : ITokenBucketLimiter, IDisposable
{
    private long _bytesPerSecond;
    private double _availableTokens;
    private long _lastTimestamp;
    private readonly double _burstSeconds;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _isDisposed;

    public long BytesPerSecond
    {
        get => Interlocked.Read(ref _bytesPerSecond);
        set => Interlocked.Exchange(ref _bytesPerSecond, Math.Max(0, value));
    }

    /// <summary>
    /// Configured burst budget in seconds. Maximum token capacity is (BytesPerSecond * BurstSeconds).
    /// </summary>
    public double BurstSeconds => _burstSeconds;

    public TokenBucketLimiter(long bytesPerSecond = 0, double burstSeconds = 1.0)
    {
        _bytesPerSecond = Math.Max(0, bytesPerSecond);
        _burstSeconds = Math.Max(0.1, burstSeconds);
        _availableTokens = 0;
        _lastTimestamp = Stopwatch.GetTimestamp();
    }

    public async ValueTask ConsumeAsync(int byteCount, CancellationToken ct = default)
    {
        if (byteCount <= 0) return;

        var rate = Interlocked.Read(ref _bytesPerSecond);
        if (rate <= 0) return; // Unlimited

        var delayMs = 0;

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = Stopwatch.GetTimestamp();
            var maxBurst = (double)rate * _burstSeconds;

            // Compute elapsed time since last replenishment
            var elapsedTicks = now - _lastTimestamp;
            if (elapsedTicks > 0)
            {
                var elapsedSeconds = elapsedTicks / (double)Stopwatch.Frequency;
                _availableTokens = Math.Min(maxBurst, _availableTokens + (elapsedSeconds * rate));
                _lastTimestamp = now;
            }

            if (_availableTokens >= byteCount)
            {
                _availableTokens -= byteCount;
                return;
            }

            // Deficit must be compensated via scheduled delay
            var needed = byteCount - _availableTokens;
            var waitSeconds = needed / (double)rate;
            _availableTokens = 0;

            var waitTicks = (long)Math.Ceiling(waitSeconds * Stopwatch.Frequency);
            var scheduledCompletion = Math.Max(_lastTimestamp, now) + waitTicks;
            _lastTimestamp = scheduledCompletion;

            var totalWaitSeconds = (scheduledCompletion - now) / (double)Stopwatch.Frequency;
            delayMs = Math.Max(1, (int)Math.Ceiling(totalWaitSeconds * 1000.0));
        }
        finally
        {
            _lock.Release();
        }

        // Perform the delay OUTSIDE the lock to allow concurrent workers to schedule without blocking
        if (delayMs > 0)
        {
            await Task.Delay(delayMs, ct).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _lock.Dispose();
    }
}
