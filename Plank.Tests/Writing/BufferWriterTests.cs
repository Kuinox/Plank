using Plank.Writing;

namespace Plank.Tests.Writing;

internal sealed class BufferWriterTests
{
    [Test]
    public void PrependPreservesMultiSegmentContent()
    {
        var writer = new BufferWriter(DefaultParquetBufferPool.Shared, 4, 4);
        try
        {
            var values = new byte[32];
            for (var i = 0; i < values.Length; i++)
                values[i] = checked((byte)(i + 1));
            writer.Write(values.AsSpan(0, 16));
            writer.Write(values.AsSpan(16));

            writer.Prepend([9, 10, 11, 12]);

            var actual = new byte[writer.WrittenLength];
            writer.CopyTo(actual);
            if (!actual.AsSpan(0, 4).SequenceEqual([9, 10, 11, 12])
                || !actual.AsSpan(4).SequenceEqual(values))
                throw new InvalidOperationException("Prepended multi-segment content did not preserve byte order.");
        }
        finally
        {
            writer.Dispose();
        }
    }

    [Test]
    public void PrependReusesTheLeadingSegmentAfterReset()
    {
        var writer = new BufferWriter(DefaultParquetBufferPool.Shared, 4, 4);
        try
        {
            WriteAndPrepend(ref writer, [1, 2, 3, 4]);
            writer.Reset();
            WriteAndPrepend(ref writer, [5, 6, 7, 8]);

            var actual = new byte[writer.WrittenLength];
            writer.CopyTo(actual);
            if (!actual.AsSpan().SequenceEqual([4, 0, 0, 0, 5, 6, 7, 8]))
                throw new InvalidOperationException("Reset buffer did not reuse its leading prefix segment.");
        }
        finally
        {
            writer.Dispose();
        }
    }

    static void WriteAndPrepend(ref BufferWriter writer, ReadOnlySpan<byte> values)
    {
        writer.Write(values);
        Span<byte> prefix = stackalloc byte[sizeof(uint)];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(prefix, checked((uint)values.Length));
        writer.Prepend(prefix);
    }
}
