using System.Buffers;

namespace Plank2.Writing;

public struct BufferWriter : IBufferWriter<byte>
{
    public void Advance(int count)
        => TODO;

    public Memory<byte> GetMemory(int sizeHint = 0)
        => TODO;

    public Span<byte> GetSpan(int sizeHint = 0)
        => TODO;
}
