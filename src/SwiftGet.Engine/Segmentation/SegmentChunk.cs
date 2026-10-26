using System.Buffers;

namespace SwiftGet.Engine.Segmentation;

public readonly struct SegmentChunk
{
    public string SegmentId { get; }
    public long Offset { get; }
    public int Length { get; }
    public byte[]? Buffer { get; }

    public SegmentChunk(string segmentId, long offset, int length, byte[] buffer)
    {
        SegmentId = segmentId;
        Offset = offset;
        Length = length;
        Buffer = buffer;
    }

    public void Return()
    {
        if (Buffer != null)
        {
            ArrayPool<byte>.Shared.Return(Buffer);
        }
    }
}
