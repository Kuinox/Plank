using System.Buffers;

namespace Plank;

// Presents an owned ParquetBuffer as Memory<byte> to synchronous serializers.
internal sealed class PooledByteMemory : MemoryManager<byte>
{
    ParquetBuffer _buffer;

    internal Memory<byte> SetBuffer(ParquetBuffer buffer)
    {
        _buffer = buffer;
        return Memory;
    }

    internal void Reset() => _buffer.Dispose();

    public override Span<byte> GetSpan() => _buffer.Span;
    public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
    public override void Unpin() { }
    protected override void Dispose(bool disposing) => Reset();
}
