namespace SwiftGet.Engine.Throttling;

public interface ITokenBucketLimiter
{
    long BytesPerSecond { get; set; }
    ValueTask ConsumeAsync(int byteCount, CancellationToken ct = default);
}
