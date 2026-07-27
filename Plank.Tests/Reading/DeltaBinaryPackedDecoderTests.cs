using Plank.Reading;
using Plank.Reading.Logical;
using Plank.Reading.Logical.Internal;
using Plank.Writing;
using Plank.Writing.Encoding;

namespace Plank.Tests.Reading;

internal sealed class DeltaBinaryPackedDecoderTests
{
    [Test]
    public void ReadInt32HandlesDeltasWiderThanInt32()
    {
        var values = new[] { int.MinValue, int.MaxValue, int.MinValue, 0 };
        var writer = new BufferWriter(DefaultParquetBufferPool.Shared, 1024, 1024);
        try
        {
            DeltaBinaryPackedEncoding.WriteInt32(values, ref writer);

            var payload = new byte[writer.WrittenLength];
            writer.CopyTo(payload);

            var decoded = DeltaBinaryPackedDecoder.ReadInt32(payload);

            if (!decoded.SequenceEqual(values))
                throw new InvalidOperationException("Delta binary packed Int32 values did not round-trip.");
        }
        finally
        {
            writer.Dispose();
        }
    }

    [Test]
    public void ReadConsumedByteCountIncludesPaddedMiniBlockBytes()
    {
        var values = new[] { 4, 11, 3, 18, 2 };
        var writer = new BufferWriter(DefaultParquetBufferPool.Shared, 1024, 1024);
        try
        {
            DeltaBinaryPackedEncoding.WriteInt32(values, ref writer);

            var payload = new byte[writer.WrittenLength + 3];
            writer.CopyTo(payload);
            payload[^3] = 0xAA;
            payload[^2] = 0xBB;
            payload[^1] = 0xCC;

            var decoded = DeltaBinaryPackedDecoder.ReadInt32(payload);
            var (_, consumed) = DeltaBinaryPackedDecoder.ReadUInt32WithConsumedBytes(payload);

            if (!decoded.SequenceEqual(values))
                throw new InvalidOperationException("Delta binary packed values did not round-trip.");
            if (consumed != writer.WrittenLength)
                throw new InvalidOperationException(
                    $"Expected consumed byte count {writer.WrittenLength}, got {consumed}.");
        }
        finally
        {
            writer.Dispose();
        }
    }

    [Test]
    public void PackedMiniBlocksMatchReferenceAcrossBitWidths()
    {
        int[] widths = [0, 1, 7, 8, 9, 31, 32, 33, 63, 64];
        foreach (var width in widths)
        {
            var values = CreatePackedValues(width);
            var writer = new BufferWriter(DefaultParquetBufferPool.Shared, 1024, 1024);
            try
            {
                DeltaBinaryPackedEncoding.WritePackedUnsignedValues(values, width, ref writer);

                var actual = new byte[writer.WrittenLength];
                writer.CopyTo(actual);
                var expected = PackReference(values, width);
                if (!actual.SequenceEqual(expected))
                    throw new InvalidOperationException(
                        $"Packed bytes for bit width {width} do not match the reference implementation.");
            }
            finally
            {
                writer.Dispose();
            }
        }
    }

    [Test]
    public void ReadInt32RoundTripsPartialBlocksAndSignedExtremes()
    {
        int[] counts = [0, 1, 2, 7, 8, 9, 31, 32, 33, 127, 128, 129, 257];
        foreach (var count in counts)
        {
            var values = new int[count];
            for (var i = 0; i < values.Length; i++)
                values[i] = (i % 4) switch
                {
                    0 => int.MinValue,
                    1 => int.MaxValue,
                    2 => i,
                    _ => -i
                };

            var writer = new BufferWriter(DefaultParquetBufferPool.Shared, 4096, 4096);
            try
            {
                DeltaBinaryPackedEncoding.WriteInt32(values, ref writer);

                var payload = new byte[writer.WrittenLength];
                writer.CopyTo(payload);
                var decoded = DeltaBinaryPackedDecoder.ReadInt32(payload);
                if (!decoded.SequenceEqual(values))
                    throw new InvalidOperationException(
                        $"Delta binary packed values did not round-trip for count {count}.");
            }
            finally
            {
                writer.Dispose();
            }
        }
    }

    static long[] CreatePackedValues(int bitWidth)
    {
        var values = new long[32];
        var mask = bitWidth switch
        {
            0 => 0UL,
            64 => ulong.MaxValue,
            _ => (1UL << bitWidth) - 1
        };
        for (var i = 0; i < values.Length; i++)
        {
            var mixed = unchecked((ulong)i * 0x9E3779B97F4A7C15UL);
            values[i] = unchecked((long)(mixed & mask));
        }

        return values;
    }

    static byte[] PackReference(ReadOnlySpan<long> values, int bitWidth)
    {
        var destination = new byte[checked((values.Length * bitWidth + 7) >> 3)];
        var mask = bitWidth switch
        {
            0 => 0UL,
            64 => ulong.MaxValue,
            _ => (1UL << bitWidth) - 1
        };
        var bitOffset = 0;
        for (var i = 0; i < values.Length; i++)
        {
            var value = unchecked((ulong)values[i]) & mask;
            for (var bit = 0; bit < bitWidth; bit++)
                if (((value >> bit) & 1) != 0)
                    destination[(bitOffset + bit) >> 3] |= (byte)(1 << ((bitOffset + bit) & 7));
            bitOffset += bitWidth;
        }

        return destination;
    }
}
