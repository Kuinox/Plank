using Plank.Schema;
using Plank.Writing;
using Plank.Writing.Encoding;

namespace Plank.Tests.Writer;

internal sealed class ByteStreamSplitEncodingTests
{
    static readonly Column Int32Column = new("value", ParquetPhysicalType.Int32);
    static readonly Column Int64Column = new("value", ParquetPhysicalType.Int64);
    static readonly Column FloatColumn = new("value", ParquetPhysicalType.Float);
    static readonly Column DoubleColumn = new("value", ParquetPhysicalType.Double);

    [Test]
    public void NumericBitPatternsHaveExactByteStreamLayout()
    {
        AssertEqual(
            [0x04, 0xD4, 0x00, 0x03, 0xC3, 0x00, 0x02, 0xB2, 0x00, 0x01, 0xA1, 0x00],
            Encode(Int32Column, [0x01020304, unchecked((int)0xA1B2C3D4u), 0]));

        AssertEqual(
            [0x08, 0xFF, 0x07, 0xEE, 0x06, 0xDD, 0x05, 0xCC,
             0x04, 0xBB, 0x03, 0xAA, 0x02, 0x99, 0x01, 0x88],
            Encode(Int64Column, [0x0102030405060708L, unchecked((long)0x8899AABBCCDDEEFFUL)]));

        AssertEqual(
            [0x04, 0xD4, 0x03, 0xC3, 0x02, 0xB2, 0x01, 0xA1],
            Encode(FloatColumn,
            [
                BitConverter.Int32BitsToSingle(0x01020304),
                BitConverter.Int32BitsToSingle(unchecked((int)0xA1B2C3D4u))
            ]));

        AssertEqual(
            [0x08, 0xFF, 0x07, 0xEE, 0x06, 0xDD, 0x05, 0xCC,
             0x04, 0xBB, 0x03, 0xAA, 0x02, 0x99, 0x01, 0x88],
            Encode(DoubleColumn,
            [
                BitConverter.Int64BitsToDouble(0x0102030405060708L),
                BitConverter.Int64BitsToDouble(unchecked((long)0x8899AABBCCDDEEFFUL))
            ]));
    }

    [Test]
    public void NumericValuesMatchEndianIndependentReferenceAcrossVectorBoundaries()
    {
        int[] lengths = [0, 1, 7, 8, 15, 16, 17, 31, 32, 33, 63, 64, 65];
        foreach (var length in lengths)
        {
            var intValues = new int[length];
            var longValues = new long[length];
            var floatValues = new float[length];
            var doubleValues = new double[length];
            var state = unchecked(0x9E3779B9u + (uint)length);
            FillPseudoRandom(System.Runtime.InteropServices.MemoryMarshal.AsBytes(intValues.AsSpan()), ref state);
            FillPseudoRandom(System.Runtime.InteropServices.MemoryMarshal.AsBytes(longValues.AsSpan()), ref state);
            FillPseudoRandom(System.Runtime.InteropServices.MemoryMarshal.AsBytes(floatValues.AsSpan()), ref state);
            FillPseudoRandom(System.Runtime.InteropServices.MemoryMarshal.AsBytes(doubleValues.AsSpan()), ref state);

            AssertEqual(EncodeReference(intValues, 4, static value => unchecked((uint)value)),
                Encode(Int32Column, intValues));
            AssertEqual(EncodeReference(longValues, 8, static value => unchecked((ulong)value)),
                Encode(Int64Column, longValues));
            AssertEqual(EncodeReference(floatValues, 4,
                    static value => unchecked((uint)BitConverter.SingleToInt32Bits(value))),
                Encode(FloatColumn, floatValues));
            AssertEqual(EncodeReference(doubleValues, 8,
                    static value => unchecked((ulong)BitConverter.DoubleToInt64Bits(value))),
                Encode(DoubleColumn, doubleValues));
        }
    }

    [Test]
    public void AlternateIntegerRepresentationsZeroExtendAndPreserveBits()
    {
        byte[] byteValues = [0x00, 0x80, 0xFF];
        ushort[] ushortValues = [0x0000, 0x8001, 0xFFFF];
        uint[] uintValues = [0, 0x80010203, uint.MaxValue];
        ulong[] ulongValues = [0, 0x8001020304050607UL, ulong.MaxValue];

        AssertEqual(EncodeReference(byteValues, 4, static value => value), Encode(Int32Column, byteValues));
        AssertEqual(EncodeReference(ushortValues, 4, static value => value), Encode(Int32Column, ushortValues));
        AssertEqual(EncodeReference(uintValues, 4, static value => value), Encode(Int32Column, uintValues));
        AssertEqual(EncodeReference(ulongValues, 8, static value => value), Encode(Int64Column, ulongValues));
    }

    [Test]
    public void AlternateIntegerRepresentationsMatchReferenceAcrossVectorBoundaries()
    {
        int[] lengths = [0, 1, 7, 8, 15, 16, 17, 31, 32, 33, 63, 64, 65, 127, 128, 129];
        foreach (var length in lengths)
        {
            var byteValues = new byte[length];
            var ushortValues = new ushort[length];
            var state = unchecked(0x85EBCA6Bu + (uint)length);
            FillPseudoRandom(byteValues, ref state);
            FillPseudoRandom(System.Runtime.InteropServices.MemoryMarshal.AsBytes(ushortValues.AsSpan()), ref state);

            AssertEqual(EncodeReference(byteValues, 4, static value => value), Encode(Int32Column, byteValues));
            AssertEqual(EncodeReference(ushortValues, 4, static value => value), Encode(Int32Column, ushortValues));
        }
    }

    [Test]
    public void DecimalsSplitTheirUnscaledIntegerCarrier()
    {
        // Beyond MaxStackConvertedValues so adjacent conversion chunks and lane offsets are covered.
        int[] lengths = [0, 1, 3, 33, 256, 257, 512];
        foreach (var length in lengths)
        {
            var int32Column = DecimalColumn(ParquetPhysicalType.Int32, precision: 9, scale: 2);
            var int64Column = DecimalColumn(ParquetPhysicalType.Int64, precision: 18, scale: 2);
            var int32Values = new decimal[length];
            var int64Values = new decimal[length];
            for (var i = 0; i < length; i++)
            {
                int32Values[i] = (i % 2 == 0 ? 1 : -1) * (i * 7919m % 9_999_999m) / 100m;
                int64Values[i] = (i % 2 == 0 ? 1 : -1) * (i * 7_919_000_017m % 999_999_999_999m) / 100m;
            }

            AssertEqual(
                EncodeReference(int32Values, 4,
                    value => unchecked((uint)ParquetDecimalConverter.ToInt32(value, int32Column))),
                Encode(int32Column, int32Values));
            AssertEqual(
                EncodeReference(int64Values, 8,
                    value => unchecked((ulong)ParquetDecimalConverter.ToInt64(value, int64Column))),
                Encode(int64Column, int64Values));
        }
    }

    static Column DecimalColumn(ParquetPhysicalType physicalType, int precision, int scale)
        => new("value", physicalType, logicalType: new LogicalType.Decimal(precision, scale));

    [Test]
    public void LargeDecimalCarrierConversionDoesNotAllocate()
    {
        var column = DecimalColumn(ParquetPhysicalType.Int64, precision: 18, scale: 2);
        var values = new decimal[4096];
        for (var i = 0; i < values.Length; i++)
            values[i] = (i - 2048) / 100m;

        var bufferSize = checked((uint)(values.Length * sizeof(long)));
        var writer = new BufferWriter(DefaultParquetBufferPool.Shared, bufferSize, bufferSize);
        try
        {
            for (var i = 0; i < 4; i++)
            {
                writer.Reset();
                ByteStreamSplitEncoding.WriteValues(column, values, ref writer);
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            writer.Reset();
            ByteStreamSplitEncoding.WriteValues(column, values, ref writer);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            if (allocated != 0)
                throw new InvalidOperationException(
                    $"Expected zero allocations for decimal byte-stream-split encoding but saw {allocated} bytes.");
        }
        finally
        {
            writer.Dispose();
        }
    }

    [Test]
    public void FixedLengthByteArraysHaveExactByteStreamLayout()
    {
        var column = new Column("value", ParquetPhysicalType.FixedLenByteArray,
            new ColumnOptions(typeLength: 3));
        byte[][] values = [[0x00, 0x01, 0x02], [0x10, 0x11, 0x12], [0x20, 0x21, 0x22]];

        AssertEqual([0x00, 0x10, 0x20, 0x01, 0x11, 0x21, 0x02, 0x12, 0x22], Encode(column, values));
    }

    [Test]
    public void GuidsUseCanonicalBigEndianBytesBeforeSplitting()
    {
        var column = new Column("value", ParquetPhysicalType.FixedLenByteArray,
            new ColumnOptions(typeLength: 16));
        Guid[] values =
        [
            Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
            Guid.Parse("ffeeddcc-bbaa-9988-7766-554433221100")
        ];

        AssertEqual(
            [0x00, 0xFF, 0x11, 0xEE, 0x22, 0xDD, 0x33, 0xCC,
             0x44, 0xBB, 0x55, 0xAA, 0x66, 0x99, 0x77, 0x88,
             0x88, 0x77, 0x99, 0x66, 0xAA, 0x55, 0xBB, 0x44,
             0xCC, 0x33, 0xDD, 0x22, 0xEE, 0x11, 0xFF, 0x00],
            Encode(column, values));
    }

    static byte[] Encode<T>(Column column, T[] values)
        where T : notnull
    {
        var bufferSize = checked((uint)Math.Max(4_096, values.Length * 16));
        var writer = new BufferWriter(DefaultParquetBufferPool.Shared, bufferSize, bufferSize);
        try
        {
            ByteStreamSplitEncoding.WriteValues(column, values, ref writer);
            var result = new byte[writer.WrittenLength];
            writer.CopyTo(result);
            return result;
        }
        finally
        {
            writer.Dispose();
        }
    }

    static byte[] EncodeReference<T>(ReadOnlySpan<T> values, int byteWidth, Func<T, ulong> getBits)
    {
        var result = new byte[checked(values.Length * byteWidth)];
        for (var lane = 0; lane < byteWidth; lane++)
            for (var i = 0; i < values.Length; i++)
                result[lane * values.Length + i] = (byte)(getBits(values[i]) >> (lane * 8));
        return result;
    }

    static void AssertEqual(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual)
    {
        if (!actual.SequenceEqual(expected))
            throw new InvalidOperationException(
                $"BYTE_STREAM_SPLIT bytes differ. Expected {Convert.ToHexString(expected)}, got {Convert.ToHexString(actual)}.");
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
