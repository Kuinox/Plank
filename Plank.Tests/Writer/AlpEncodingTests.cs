using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Plank.Reading;
using Plank.Reading.Logical.Internal;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.Encoding;

namespace Plank.Tests.Writer;

internal sealed class AlpEncodingTests
{
    static readonly Column DoubleColumn = new("value", ParquetPhysicalType.Double);

    [Test]
    public void PageLayoutUsesSpecHeaderAndRelativeVectorOffsets()
    {
        var values = CreateDoubleValues(2_057);
        var encoded = Encode(DoubleColumn, values);

        if (encoded[0] != 0 || encoded[1] != 0 || encoded[2] != 10)
            throw new InvalidOperationException("ALP page modes or vector size do not match the specification.");
        if (BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(3, 4)) != values.Length)
            throw new InvalidOperationException("ALP page element count is incorrect.");

        const int vectorCount = 3;
        var previous = 0U;
        for (var i = 0; i < vectorCount; i++)
        {
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(7 + i * sizeof(uint), sizeof(uint)));
            if (i == 0 && offset != vectorCount * sizeof(uint))
                throw new InvalidOperationException($"Expected first ALP offset 12, got {offset}.");
            if (i != 0 && offset <= previous)
                throw new InvalidOperationException("ALP vector offsets are not strictly increasing.");
            previous = offset;
        }

        var decoded = new double[values.Length];
        if (!AlpDecoder.TryDecode(encoded, DoubleColumn, checked((uint)values.Length), decoded))
            throw new InvalidOperationException("ALP decoder declined a DOUBLE page.");
        AssertBitsEqual(values, decoded);
    }

    [Test]
    public void PublishedWorkedVectorDecodesBitExactly()
    {
        byte[] payload =
        [
            // Page header and one relative vector offset.
            0x00, 0x00, 0x0A, 0x04, 0x00, 0x00, 0x00,
            0x04, 0x00, 0x00, 0x00,
            // e=4, f=3, one exception, frame of reference 3335, bit width 15.
            0x04, 0x03, 0x01, 0x00,
            0x07, 0x0D, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0F,
            // [11665, 11665, 21665, 0], packed least-significant bit first.
            0x91, 0xAD, 0xC8, 0x56, 0x28, 0x15, 0x00, 0x00,
            // Exception at position 1 with a non-canonical NaN payload.
            0x01, 0x00,
            0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0xF8, 0x7F
        ];
        double[] expected = [1500.0, BitConverter.Int64BitsToDouble(0x7FF8000000000001), 2500.0, 333.5];
        var actual = new double[expected.Length];

        if (!AlpDecoder.TryDecode(payload, DoubleColumn, 4, actual))
            throw new InvalidOperationException("ALP decoder declined the specification's DOUBLE vector.");
        AssertBitsEqual(expected, actual);
    }

    [Test]
    public void MalformedPageAndVectorMetadataIsRejected()
    {
        var valid = WorkedVectorPayload();
        var malformed = new List<byte[]>
        {
            Mutate(valid, 0, 1),
            Mutate(valid, 2, 2),
            Mutate(valid, 3, 5),
            Mutate(valid, 7, 5),
            Mutate(valid, 23, 65),
            Mutate(valid, 31, 0x80),
            Mutate(valid, 32, 4),
            valid[..^1]
        };

        foreach (var payload in malformed)
            Assert.Throws<CorruptParquetException>(() =>
                AlpDecoder.TryDecode(payload, DoubleColumn, 4, new double[4]));
    }

    [Test]
    [Arguments(ParquetDataPageVersion.V1)]
    [Arguments(ParquetDataPageVersion.V2)]
    public void RequiredAndOptionalFloatsAndDoublesRoundTrip(ParquetDataPageVersion dataPageVersion)
    {
        var floats = CreateFloatValues(2_057);
        var doubles = CreateDoubleValues(2_057);
        AssertBitsEqual(floats, RoundTrip(ParquetPhysicalType.Float, floats, dataPageVersion));
        AssertBitsEqual(doubles, RoundTrip(ParquetPhysicalType.Double, doubles, dataPageVersion));

        var nullableFloats = new float?[floats.Length];
        var nullableDoubles = new double?[doubles.Length];
        for (var i = 0; i < floats.Length; i++)
        {
            nullableFloats[i] = i % 11 == 0 ? null : floats[i];
            nullableDoubles[i] = i % 13 == 0 ? null : doubles[i];
        }
        AssertNullableBitsEqual(nullableFloats,
            RoundTrip(ParquetPhysicalType.Float, nullableFloats, dataPageVersion));
        AssertNullableBitsEqual(nullableDoubles,
            RoundTrip(ParquetPhysicalType.Double, nullableDoubles, dataPageVersion));
    }

    [Test]
    public void EncodingCompatibilityRestrictsAlpToFloatingPointPhysicalTypes()
    {
        _ = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("f", ParquetPhysicalType.Float,
                new ColumnOptions(encodings: [EncodingKind.Alp])),
            ColumnDefinition.RequiredLeaf("d", ParquetPhysicalType.Double,
                new ColumnOptions(encodings: [EncodingKind.Alp]))
        ]);

        Assert.Throws<NotSupportedException>(() => new ParquetSchema([
            ColumnDefinition.RequiredLeaf("i", ParquetPhysicalType.Int64,
                new ColumnOptions(encodings: [EncodingKind.Alp]))
        ]));
    }

    static T[] RoundTrip<T>(ParquetPhysicalType physicalType, T[] values,
        ParquetDataPageVersion dataPageVersion)
    {
        var repetition = Nullable.GetUnderlyingType(typeof(T)) is null
            ? ParquetRepetition.Required
            : ParquetRepetition.Optional;
        var schema = new ParquetSchema([
            ColumnDefinition.Leaf("value", physicalType,
                new ColumnOptions(repetition, ImmutableArray.Create(EncodingKind.Alp)))
        ]);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None,
            DataPageVersion = dataPageVersion
        });
        var serialized = writer.CreateSerializedColumn<T>(schema.LeafColumns[0]);
        serialized.Serialize(values);
        writer.StartRowGroup().Write(serialized);
        writer.CloseFile();

        using var reader = schema.CreateReader(new MemoryReadSource(stream.ToArray()));
        var actual = new List<T>(values.Length);
        foreach (var buffer in reader.RowGroups[0].Column<T>(0))
            actual.AddRange(buffer.Values);
        return actual.ToArray();
    }

    static byte[] Encode<T>(Column column, T[] values)
        where T : notnull
    {
        const uint bufferSize = 64 * 1024;
        var factory = new BufferWriterFactory(DefaultParquetBufferPool.Shared,
            bufferSize, bufferSize, bufferSize, bufferSize);
        var writer = factory.CreatePageBufferWriter();
        try
        {
            AlpEncoding.WriteValues(column, values, factory, ref writer);
            var result = new byte[writer.WrittenLength];
            writer.CopyTo(result);
            return result;
        }
        finally
        {
            writer.Dispose();
        }
    }

    static float[] CreateFloatValues(int count)
    {
        var values = new float[count];
        for (var i = 0; i < values.Length; i++)
            values[i] = (i % 401 - 200) / 10f;
        values[0] = 0f;
        values[1] = BitConverter.Int32BitsToSingle(unchecked((int)0x80000000));
        values[2] = BitConverter.Int32BitsToSingle(unchecked((int)0x7FC00001));
        values[3] = float.PositiveInfinity;
        values[4] = float.NegativeInfinity;
        values[5] = BitConverter.Int32BitsToSingle(1);
        values[6] = float.MaxValue;
        return values;
    }

    static double[] CreateDoubleValues(int count)
    {
        var values = new double[count];
        for (var i = 0; i < values.Length; i++)
            values[i] = (i % 2_001 - 1_000) / 100.0;
        values[0] = 0.0;
        values[1] = BitConverter.Int64BitsToDouble(unchecked((long)0x8000000000000000));
        values[2] = BitConverter.Int64BitsToDouble(unchecked((long)0x7FF8000000000001));
        values[3] = double.PositiveInfinity;
        values[4] = double.NegativeInfinity;
        values[5] = BitConverter.Int64BitsToDouble(1);
        values[6] = double.MaxValue;
        return values;
    }

    static byte[] WorkedVectorPayload()
        =>
        [
            0x00, 0x00, 0x0A, 0x04, 0x00, 0x00, 0x00,
            0x04, 0x00, 0x00, 0x00,
            0x04, 0x03, 0x01, 0x00,
            0x07, 0x0D, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0F,
            0x91, 0xAD, 0xC8, 0x56, 0x28, 0x15, 0x00, 0x00,
            0x01, 0x00,
            0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0xF8, 0x7F
        ];

    static byte[] Mutate(byte[] source, int offset, byte value)
    {
        var clone = (byte[])source.Clone();
        clone[offset] = value;
        return clone;
    }

    static void AssertBitsEqual<T>(ReadOnlySpan<T> expected, ReadOnlySpan<T> actual)
        where T : struct
    {
        var expectedBytes = MemoryMarshal.AsBytes(expected);
        var actualBytes = MemoryMarshal.AsBytes(actual);
        if (!expectedBytes.SequenceEqual(actualBytes))
            throw new InvalidOperationException(
                $"ALP round-trip bits differ. Expected {Convert.ToHexString(expectedBytes)}, " +
                $"got {Convert.ToHexString(actualBytes)}.");
    }

    static void AssertNullableBitsEqual(ReadOnlySpan<float?> expected, ReadOnlySpan<float?> actual)
    {
        if (expected.Length != actual.Length)
            throw new InvalidOperationException("Nullable FLOAT lengths differ.");
        for (var i = 0; i < expected.Length; i++)
            if (expected[i].HasValue != actual[i].HasValue ||
                expected[i].HasValue && BitConverter.SingleToInt32Bits(expected[i]!.Value) !=
                BitConverter.SingleToInt32Bits(actual[i]!.Value))
                throw new InvalidOperationException($"Nullable FLOAT value {i} differs.");
    }

    static void AssertNullableBitsEqual(ReadOnlySpan<double?> expected, ReadOnlySpan<double?> actual)
    {
        if (expected.Length != actual.Length)
            throw new InvalidOperationException("Nullable DOUBLE lengths differ.");
        for (var i = 0; i < expected.Length; i++)
            if (expected[i].HasValue != actual[i].HasValue ||
                expected[i].HasValue && BitConverter.DoubleToInt64Bits(expected[i]!.Value) !=
                BitConverter.DoubleToInt64Bits(actual[i]!.Value))
                throw new InvalidOperationException($"Nullable DOUBLE value {i} differs.");
    }
}
