using System.Buffers.Binary;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.Encoding;

namespace Plank.Tests.Writer;

internal sealed class PlainEncodingTests
{
    delegate void WriteExpected<T>(Span<byte> destination, T[] values);

    [Test]
    public void BooleansMatchParquetBitOrderAcrossVectorAndTailBoundaries()
    {
        int[] lengths = [0, 1, 7, 8, 15, 16, 31, 32, 63, 64, 65, 127, 128, 129, 4_096, 4_101];
        foreach (var length in lengths)
        {
            var values = new bool[length];
            for (var i = 0; i < values.Length; i++)
                values[i] = ((i * 17 + 3) % 11) < 5;

            var actual = Encode(new Column("value", ParquetPhysicalType.Boolean), values);
            var expected = PackBooleans(values);
            if (!actual.SequenceEqual(expected))
                throw new InvalidOperationException($"Plain boolean bytes differ at length {length}.");
        }
    }

    [Test]
    public void FixedWidthNumericValuesUseLittleEndianParquetBytes()
    {
        AssertEncoding(ParquetPhysicalType.Int32,
            new[] { int.MinValue, -1, 0, 0x01234567, int.MaxValue },
            WriteInt32Values);
        AssertEncoding(ParquetPhysicalType.Int32,
            new[] { 0u, 1u, 0x89ABCDEFu, uint.MaxValue },
            WriteUInt32Values);
        AssertEncoding(ParquetPhysicalType.Int32,
            new byte[] { 0, 1, 127, 128, byte.MaxValue },
            WriteByteAsInt32Values);
        AssertEncoding(ParquetPhysicalType.Int32,
            new ushort[] { 0, 1, 255, 256, ushort.MaxValue },
            WriteUInt16AsInt32Values);
        AssertEncoding(ParquetPhysicalType.Int64,
            new[] { long.MinValue, -1, 0, 0x0123456789ABCDEFL, long.MaxValue },
            WriteInt64Values);
        AssertEncoding(ParquetPhysicalType.Int64,
            new[] { 0UL, 1UL, 0x89ABCDEF01234567UL, ulong.MaxValue },
            WriteUInt64Values);
        AssertEncoding(ParquetPhysicalType.Float,
            new[] { float.NegativeInfinity, -0.0f, 1.25f, float.PositiveInfinity,
                BitConverter.Int32BitsToSingle(unchecked((int)0x7FC01234)) },
            WriteFloatValues);
        AssertEncoding(ParquetPhysicalType.Double,
            new[] { double.NegativeInfinity, -0.0, 1.25, double.PositiveInfinity,
                BitConverter.Int64BitsToDouble(unchecked((long)0x7FF8000000001234)) },
            WriteDoubleValues);
    }

    [Test]
    public void WidenedInt32ValuesMatchReferenceAcrossVectorBoundaries()
    {
        int[] lengths = [0, 1, 15, 16, 17, 31, 32, 33, 63, 64, 65, 127, 128, 129, 4_096];
        foreach (var length in lengths)
        {
            var byteValues = new byte[length];
            var ushortValues = new ushort[length];
            for (var i = 0; i < length; i++)
            {
                byteValues[i] = unchecked((byte)(i * 197 + 31));
                ushortValues[i] = unchecked((ushort)(i * 51_379 + 9_973));
            }

            AssertEncoding(ParquetPhysicalType.Int32, byteValues, WriteByteAsInt32Values);
            AssertEncoding(ParquetPhysicalType.Int32, ushortValues, WriteUInt16AsInt32Values);
        }
    }

    [Test]
    public void BinaryAndOptionalBinaryValuesMatchLengthPrefixedParquetBytes()
    {
        byte[][] required = [[], [0x11], [0x22, 0x33, 0x44], Enumerable.Range(0, 129).Select(static i => (byte)i).ToArray()];
        byte[][] optional = [null!, required[0], required[1], null!, required[2], required[3]];
        ReadOnlyMemory<byte>[] requiredMemory = required.Select(static value => (ReadOnlyMemory<byte>)value).ToArray();
        ReadOnlyMemory<byte>?[] optionalMemory = optional
            .Select(static value => value is null ? (ReadOnlyMemory<byte>?)null : value)
            .ToArray();

        var column = new Column("value", ParquetPhysicalType.ByteArray);
        AssertEqual(EncodeLengthPrefixed(required), Encode(column, required));
        AssertEqual(EncodeLengthPrefixed(required), Encode(column, requiredMemory));
        var expectedOptional = EncodeLengthPrefixed(optional.Where(static value => value is not null)!);
        AssertEqual(expectedOptional, EncodeOptionalByteArrays(column, optional));
        AssertEqual(expectedOptional, EncodeOptionalByteArrays(column, optional, expectedOptional.Length));
        AssertEqual(EncodeLengthPrefixed(optional.Where(static value => value is not null)!),
            EncodeOptionalMemory(column, optionalMemory));
    }

    [Test]
    public void OptionalSingleByteArrayFastPathMatchesLengthPrefixedParquetBytes()
    {
        byte[][] values = [null!, [0x11], null!, [0x22], [0x33], null!, [0x44]];
        var expected = EncodeLengthPrefixed(values.Where(static value => value is not null)!);
        var writer = new BufferWriter(DefaultParquetBufferPool.Shared, 128 * 1024, 128 * 1024);
        try
        {
            PlainEncoding.WriteOptionalSingleByteArrayPayloads(values, 4, ref writer);
            AssertEqual(expected, CopyWritten(ref writer));
        }
        finally
        {
            writer.Dispose();
        }
    }

    [Test]
    public void OptionalSingleByteMemoryFastPathMatchesLengthPrefixedParquetBytes()
    {
        byte[][] source = [null!, [0x11], null!, [0x22], [0x33], null!, [0x44]];
        ReadOnlyMemory<byte>?[] values = source
            .Select(static value => value is null ? (ReadOnlyMemory<byte>?)null : value)
            .ToArray();
        var expected = EncodeLengthPrefixed(source.Where(static value => value is not null)!);
        var writer = new BufferWriter(DefaultParquetBufferPool.Shared, 128 * 1024, 128 * 1024);
        try
        {
            PlainEncoding.WriteOptionalSingleByteMemoryPayloads(values, 4, ref writer);
            AssertEqual(expected, CopyWritten(ref writer));
        }
        finally
        {
            writer.Dispose();
        }
    }

    [Test]
    public void FixedLengthInt96AndGuidValuesMatchCanonicalParquetBytes()
    {
        var first = Enumerable.Range(0, 16).Select(static i => (byte)i).ToArray();
        var second = Enumerable.Range(16, 16).Select(static i => (byte)i).ToArray();
        byte[][] fixedValues = [first, second];
        byte[][] optionalFixedValues = [null!, first, null!, second];
        var fixedColumn = new Column("value", ParquetPhysicalType.FixedLenByteArray,
            new ColumnOptions(typeLength: 16));

        AssertEqual(first.Concat(second), Encode(fixedColumn, fixedValues));
        AssertEqual(first.Concat(second), EncodeOptionalByteArrays(fixedColumn, optionalFixedValues));

        var int96First = first[..12];
        var int96Second = second[..12];
        var int96Column = new Column("value", ParquetPhysicalType.Int96);
        AssertEqual(int96First.Concat(int96Second), Encode(int96Column, new[] { int96First, int96Second }));
        AssertEqual(int96First.Concat(int96Second),
            EncodeOptionalByteArrays(int96Column, new byte[][] { null!, int96First, int96Second, null! }));

        Guid[] guids =
        [
            Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
            Guid.Parse("ffeeddcc-bbaa-9988-7766-554433221100")
        ];
        var expectedGuids = Convert.FromHexString(
            "00112233445566778899AABBCCDDEEFF" +
            "FFEEDDCCBBAA99887766554433221100");
        AssertEqual(expectedGuids, Encode(fixedColumn, guids));
    }

    [Test]
    public void EmptyValuesDoNotRequireAnInitializedWriter()
    {
        var writer = default(BufferWriter);
        PlainEncoding.WriteValues(new Column("value", ParquetPhysicalType.Boolean), ReadOnlySpan<bool>.Empty,
            ref writer);
        PlainEncoding.WriteValues(new Column("value", ParquetPhysicalType.Int32), ReadOnlySpan<int>.Empty,
            ref writer);
        PlainEncoding.WriteValues(new Column("value", ParquetPhysicalType.ByteArray), ReadOnlySpan<byte[]>.Empty,
            ref writer);
        PlainEncoding.WriteValues(new Column("value", ParquetPhysicalType.FixedLenByteArray,
            new ColumnOptions(typeLength: 16)), ReadOnlySpan<byte[]>.Empty, ref writer);

        if (writer.WrittenLength != 0)
            throw new InvalidOperationException("Empty plain values unexpectedly wrote output.");
    }

    static byte[] Encode<T>(Column column, ReadOnlySpan<T> values)
        where T : notnull
    {
        var writer = new BufferWriter(DefaultParquetBufferPool.Shared, 128 * 1024, 128 * 1024);
        try
        {
            PlainEncoding.WriteValues(column, values, ref writer);
            return CopyWritten(ref writer);
        }
        finally
        {
            writer.Dispose();
        }
    }

    static byte[] EncodeOptionalByteArrays(Column column, ReadOnlySpan<byte[]> values, int knownPayloadBytes = -1)
    {
        var writer = new BufferWriter(DefaultParquetBufferPool.Shared, 128 * 1024, 128 * 1024);
        try
        {
            PlainEncoding.WriteOptionalValues<byte[], OptionalByteArrayRow>(column, values, knownPayloadBytes,
                ref writer);
            return CopyWritten(ref writer);
        }
        finally
        {
            writer.Dispose();
        }
    }

    static byte[] EncodeOptionalMemory(Column column, ReadOnlySpan<ReadOnlyMemory<byte>?> values)
    {
        var writer = new BufferWriter(DefaultParquetBufferPool.Shared, 128 * 1024, 128 * 1024);
        try
        {
            PlainEncoding.WriteOptionalValues<ReadOnlyMemory<byte>?, OptionalMemoryRow>(column, values, -1, ref writer);
            return CopyWritten(ref writer);
        }
        finally
        {
            writer.Dispose();
        }
    }

    static byte[] CopyWritten(ref BufferWriter writer)
    {
        var result = new byte[writer.WrittenLength];
        writer.CopyTo(result);
        return result;
    }

    static byte[] PackBooleans(ReadOnlySpan<bool> values)
    {
        var result = new byte[(values.Length + 7) / 8];
        for (var i = 0; i < values.Length; i++)
            if (values[i])
                result[i >> 3] |= (byte)(1 << (i & 7));
        return result;
    }

    static byte[] EncodeLengthPrefixed(IEnumerable<byte[]> values)
    {
        using var stream = new MemoryStream();
        Span<byte> lengthBytes = stackalloc byte[sizeof(int)];
        foreach (var value in values)
        {
            BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, value.Length);
            stream.Write(lengthBytes);
            stream.Write(value);
        }
        return stream.ToArray();
    }

    static void AssertEncoding<T>(ParquetPhysicalType physicalType, T[] values,
        WriteExpected<T> writeExpected)
        where T : notnull
    {
        var actual = Encode(new Column("value", physicalType), values);
        var expected = new byte[actual.Length];
        writeExpected(expected, values);
        AssertEqual(expected, actual);
    }

    static void WriteInt32Values(Span<byte> destination, int[] values)
    {
        for (var i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteInt32LittleEndian(destination[(i * sizeof(int))..], values[i]);
    }

    static void WriteUInt32Values(Span<byte> destination, uint[] values)
    {
        for (var i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(destination[(i * sizeof(uint))..], values[i]);
    }

    static void WriteByteAsInt32Values(Span<byte> destination, byte[] values)
    {
        for (var i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteInt32LittleEndian(destination[(i * sizeof(int))..], values[i]);
    }

    static void WriteUInt16AsInt32Values(Span<byte> destination, ushort[] values)
    {
        for (var i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteInt32LittleEndian(destination[(i * sizeof(int))..], values[i]);
    }

    static void WriteInt64Values(Span<byte> destination, long[] values)
    {
        for (var i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteInt64LittleEndian(destination[(i * sizeof(long))..], values[i]);
    }

    static void WriteUInt64Values(Span<byte> destination, ulong[] values)
    {
        for (var i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteUInt64LittleEndian(destination[(i * sizeof(ulong))..], values[i]);
    }

    static void WriteFloatValues(Span<byte> destination, float[] values)
    {
        for (var i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteInt32LittleEndian(destination[(i * sizeof(float))..],
                BitConverter.SingleToInt32Bits(values[i]));
    }

    static void WriteDoubleValues(Span<byte> destination, double[] values)
    {
        for (var i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteInt64LittleEndian(destination[(i * sizeof(double))..],
                BitConverter.DoubleToInt64Bits(values[i]));
    }

    static void AssertEqual(IEnumerable<byte> expected, byte[] actual)
    {
        var expectedArray = expected as byte[] ?? expected.ToArray();
        if (!actual.SequenceEqual(expectedArray))
            throw new InvalidOperationException(
                $"Expected {Convert.ToHexString(expectedArray)}, got {Convert.ToHexString(actual)}.");
    }
}
