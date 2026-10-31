using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using SwiftGet.Engine.IO;
using SwiftGet.Engine.Segmentation;

namespace SwiftGet.Engine.Turbo;

/// <summary>
/// High-throughput zero-stall asynchronous disk pipeline.
/// Coalesces adjacent 64 KB blocks into 1 to 4 MB sequential writes, uses pooled memory,
/// and reports disk queue depth backpressure.
/// </summary>
public sealed class CoalescingAsyncDiskWriter : IAsyncDisposable
{
    private const int DefaultCoalesceThresholdBytes = 1024 * 1024 * 2; // 2 MB
    private readonly ISparseFileWriter _sparseWriter;
    private readonly int _channelCapacity;
    private readonly Channel<SegmentChunk> _channel;
    private readonly Task _drainTask;
    private readonly CancellationTokenSource _cts = new();
    private int _backpressureCounter;
    private long _totalBytesWritten;
    private double _lastDiskWriteLatencyMs;

    public int QueueDepth => _channel.Reader.Count;
    public int BackpressureEvents => Volatile.Read(ref _backpressureCounter);
    public double LastDiskWriteLatencyMs => Volatile.Read(ref _lastDiskWriteLatencyMs);
    public long TotalBytesWritten => Volatile.Read(ref _totalBytesWritten);
    public ChannelWriter<SegmentChunk> Writer => _channel.Writer;

    public CoalescingAsyncDiskWriter(ISparseFileWriter sparseWriter, int channelCapacity = 256)
    {
        _sparseWriter = sparseWriter;
        _channelCapacity = channelCapacity;
        _channel = Channel.CreateBounded<SegmentChunk>(new BoundedChannelOptions(channelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = false,
            SingleReader = true
        });

        _drainTask = Task.Run(DrainLoopAsync);
    }

    private async Task DrainLoopAsync()
    {
        var reader = _channel.Reader;
        var pendingOffset = -1L;
        var pendingBuffer = ArrayPool<byte>.Shared.Rent(DefaultCoalesceThresholdBytes);
        var pendingLength = 0;

        try
        {
            await foreach (var chunk in reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                // Monitor queue depth for disk backpressure signaling
                if (reader.Count >= (_channelCapacity * 0.8))
                {
                    Interlocked.Increment(ref _backpressureCounter);
                }

                if (chunk.Buffer == null || chunk.Length <= 0)
                {
                    chunk.Return();
                    continue;
                }

                // Check if chunk is contiguous with pending buffer
                if (pendingLength > 0 && chunk.Offset == (pendingOffset + pendingLength) &&
                    (pendingLength + chunk.Length) <= pendingBuffer.Length)
                {
                    // Coalesce chunk
                    Array.Copy(chunk.Buffer, 0, pendingBuffer, pendingLength, chunk.Length);
                    pendingLength += chunk.Length;
                    chunk.Return();
                }
                else
                {
                    // Flush pending coalesced block
                    if (pendingLength > 0)
                    {
                        await FlushPendingBlockAsync(pendingOffset, pendingBuffer, pendingLength).ConfigureAwait(false);
                        pendingLength = 0;
                    }

                    if (chunk.Length >= DefaultCoalesceThresholdBytes)
                    {
                        // Write directly if chunk is already huge
                        await FlushPendingBlockAsync(chunk.Offset, chunk.Buffer, chunk.Length).ConfigureAwait(false);
                        chunk.Return();
                        pendingOffset = -1;
                    }
                    else
                    {
                        pendingOffset = chunk.Offset;
                        Array.Copy(chunk.Buffer, 0, pendingBuffer, 0, chunk.Length);
                        pendingLength = chunk.Length;
                        chunk.Return();
                    }
                }
            }

            // Flush remainder
            if (pendingLength > 0)
            {
                await FlushPendingBlockAsync(pendingOffset, pendingBuffer, pendingLength).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            ArrayPool<byte>.Shared.Return(pendingBuffer);
        }
    }

    private async Task FlushPendingBlockAsync(long offset, byte[] buffer, int length)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await _sparseWriter.WriteAtAsync(offset, buffer.AsMemory(0, length), CancellationToken.None).ConfigureAwait(false);
        sw.Stop();

        Volatile.Write(ref _lastDiskWriteLatencyMs, sw.Elapsed.TotalMilliseconds);
        Interlocked.Add(ref _totalBytesWritten, length);
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.Complete();
        try
        {
            await _drainTask.ConfigureAwait(false);
        }
        catch { }

        _cts.Cancel();
        _cts.Dispose();
        await _sparseWriter.FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
