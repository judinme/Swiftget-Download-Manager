namespace SwiftGet.Engine.IO;

public interface ISparseFileWriter : IAsyncDisposable
{
    long FileLength { get; }
    string FilePath { get; }
    Task PreallocateAsync(long totalBytes, CancellationToken ct = default);
    ValueTask WriteAtAsync(long offset, ReadOnlyMemory<byte> buffer, CancellationToken ct = default);
    Task FlushAsync(CancellationToken ct = default);
    Task CloseAsync();
}
