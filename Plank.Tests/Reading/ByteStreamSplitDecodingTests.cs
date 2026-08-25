using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Plank.Reading;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Reading;

internal sealed class ByteStreamSplitDecodingTests
{
    [Test]
    public void NumericBitPatternsRoundTripAcrossVectorBoundaries()
    {
        int[] lengths = [1, 3, 4, 7, 8, 15, 16, 17, 31, 32, 33, 63, 64, 65];
        foreach (var length in lengths)
        {
            var intValues = new int[length];
            var longValues = new long[length];
            var floatValues = new float[length];
            var doubleValues = new double[length];
            var state = unchecked(0x9E3779B9u + (uint)length);
            FillPseudoRandom(MemoryMarshal.AsBytes(intValues.AsSpan()), ref state);
            FillPseudoRandom(MemoryMarshal.AsBytes(longValues.AsSpan()), ref state);
            FillPseudoRandom(MemoryMarshal.AsBytes(floatValues.AsSpan()), ref state);
            FillPseudoRandom(MemoryMarshal.AsBytes(doubleValues.AsSpan()), ref state);

            AssertBitsEqual(intValues, RoundTrip(ParquetPhysicalType.Int32, intValues));
            AssertBitsEqual(longValues, RoundTrip(ParquetPhysicalType.Int64, longValues));
            AssertBitsEqual(floatValues, RoundTrip(ParquetPhysicalType.Float, floatValues));
            AssertBitsEqual(doubleValues, RoundTrip(ParquetPhysicalType.Double, doubleValues));
        }
    }

    static T[] RoundTrip<T>(ParquetPhysicalType physicalType, T[] values)
        where T : notnull
    {
        var schema = new ParquetSchema([
            ColumnDefinition.Leaf("value", physicalType,
                new ColumnOptions(ParquetRepetition.Required,
                    ImmutableArray.Create(EncodingKind.ByteStreamSplit)))
        ]);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions { Compression = CompressionKind.None });
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

    static void AssertBitsEqual<T>(ReadOnlySpan<T> expected, ReadOnlySpan<T> actual)
        where T : struct
    {
        var expectedBytes = MemoryMarshal.AsBytes(expected);
        var actualBytes = MemoryMarshal.AsBytes(actual);
        if (!expectedBytes.SequenceEqual(actualBytes))
            throw new InvalidOperationException(
                $"BYTE_STREAM_SPLIT round-trip bits differ. Expected {Convert.ToHexString(expectedBytes)}, " +
                $"got {Convert.ToHexString(actualBytes)}.");
    }

    static void FillPseudoRandom(Span<byte> destination, ref uint state)
    {
        for (var i = 0; i < destination.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            destination[i] = (byte)state;
        }
    }
}
