using System.Buffers.Binary;
using System.Numerics;
using Plank.BloomFilters;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.Encoding;

namespace Plank.Tests.Writer;

internal sealed class RepresentationContractTests
{
    [Test]
    public void DecimalRepresentationsMatchIndependentPhysicalBytesAndHashes()
    {
        ParquetPhysicalType[] expectedTypes = [ParquetPhysicalType.Int32, ParquetPhysicalType.Int64,
            ParquetPhysicalType.FixedLenByteArray, ParquetPhysicalType.ByteArray];
        if (!ParquetDecimalConverter.PhysicalTypes.SequenceEqual(expectedTypes))
            throw new InvalidOperationException("Update the independent decimal contract cases for the representation manifest.");

        foreach (var physicalType in expectedTypes)
        {
            var column = new Column("value", physicalType,
                new ColumnOptions(typeLength: physicalType == ParquetPhysicalType.FixedLenByteArray ? 12U : 0U),
                logicalType: new LogicalType.Decimal(9, 2));
            foreach (var count in new[] { 0, 1, 7, 8, 31, 32, 33, 127, 128, 129 })
            {
                var values = new decimal[count];
                using var expectedPage = new MemoryStream();
                for (var i = 0; i < count; i++)
                {
                    // Include both signs, zero, sign-extension boundaries and trailing zero scales.
                    int[] unscaledCases = [-999_999_999, -32769, -129, -128, -1, 0, 1, 127, 128, 32768, 999_999_999];
                    var unscaled = unscaledCases[i % unscaledCases.Length];
                    values[i] = unscaled / 100m;
                    var physical = DecimalBytes(physicalType, unscaled);
                    if (physicalType == ParquetPhysicalType.ByteArray)
                    {
                        var prefix = new byte[4];
                        BinaryPrimitives.WriteInt32LittleEndian(prefix, physical.Length);
                        expectedPage.Write(prefix);
                    }
                    expectedPage.Write(physical);
                    if (ParquetDecimalConverter.HashPhysical(column, values[i]) != ParquetBloomFilterHash.Hash(physical))
                        throw new InvalidOperationException($"Decimal physical hash differs for {physicalType}/{unscaled}.");
                }

                var writer = new BufferWriter(DefaultParquetBufferPool.Shared, 4096, 4096);
                try
                {
                    PlainEncoding.WriteValues<decimal>(column, values, ref writer);
                    AssertBytes(ref writer, expectedPage.ToArray(), $"{physicalType}/{count}");
                }
                finally { writer.Dispose(); }
            }
        }
    }

    [Test]
    public void BinaryFramingIsIndependentOfRowShapeAndKnownSize()
    {
        foreach (var physicalType in new[] { ParquetPhysicalType.ByteArray,
                     ParquetPhysicalType.FixedLenByteArray, ParquetPhysicalType.Int96 })
        foreach (var count in new[] { 0, 1, 7, 8, 31, 32, 33, 127, 128, 129 })
        foreach (var allNull in new[] { false, true })
        {
            var column = new Column("value", physicalType,
                new ColumnOptions(typeLength: physicalType == ParquetPhysicalType.FixedLenByteArray ? 4U : 0U));
            var arrays = new byte[count][];
            var memories = new ReadOnlyMemory<byte>?[count];
            using var expected = new MemoryStream();
            for (var i = 0; i < count; i++)
            {
                if (allNull || i % 3 == 1) continue;
                var length = physicalType == ParquetPhysicalType.Int96 ? 12
                    : physicalType == ParquetPhysicalType.FixedLenByteArray ? 4 : i % 9;
                var bytes = Enumerable.Range(0, length).Select(j => unchecked((byte)(i + j))).ToArray();
                arrays[i] = bytes;
                memories[i] = bytes;
                if (physicalType == ParquetPhysicalType.ByteArray)
                {
                    var prefix = new byte[4];
                    BinaryPrimitives.WriteInt32LittleEndian(prefix, length);
                    expected.Write(prefix);
                }
                expected.Write(bytes);
            }
            var expectedBytes = expected.ToArray();
            foreach (var knownSize in new[] { -1, expectedBytes.Length })
            {
                var writer = new BufferWriter(DefaultParquetBufferPool.Shared, 4096, 4096);
                try
                {
                    PlainEncoding.WriteOptionalValues<byte[], OptionalByteArrayRow>(column, arrays, knownSize, ref writer);
                    AssertBytes(ref writer, expectedBytes, $"{physicalType}/array/{count}/{knownSize}");
                    writer.Reset();
                    PlainEncoding.WriteOptionalValues<ReadOnlyMemory<byte>?, OptionalMemoryRow>(column, memories, knownSize, ref writer);
                    AssertBytes(ref writer, expectedBytes, $"{physicalType}/memory/{count}/{knownSize}");
                }
                finally { writer.Dispose(); }
            }
        }
    }

    [Test]
    public void FixedBinaryRejectsWrongLengthsForBothRowShapes()
    {
        foreach (var physicalType in new[] { ParquetPhysicalType.FixedLenByteArray, ParquetPhysicalType.Int96 })
        foreach (var memory in new[] { false, true })
        foreach (var length in new[] { 0, 3, 13 })
        {
            var column = new Column("value", physicalType,
                new ColumnOptions(typeLength: physicalType == ParquetPhysicalType.FixedLenByteArray ? 4U : 0U));
            var writer = new BufferWriter(DefaultParquetBufferPool.Shared, 4096, 4096);
            try
            {
                try
                {
                    if (memory)
                        PlainEncoding.WriteOptionalValues<ReadOnlyMemory<byte>?, OptionalMemoryRow>(column,
                            [null, new byte[length]], -1, ref writer);
                    else
                        PlainEncoding.WriteOptionalValues<byte[], OptionalByteArrayRow>(column,
                            [null!, new byte[length]], -1, ref writer);
                }
                catch (InvalidOperationException) { continue; }
                throw new InvalidOperationException($"Accepted {length} bytes for {physicalType}.");
            }
            finally { writer.Dispose(); }
        }
    }

    static byte[] DecimalBytes(ParquetPhysicalType physicalType, int unscaled)
    {
        if (physicalType == ParquetPhysicalType.Int32)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, unscaled);
            return bytes;
        }
        if (physicalType == ParquetPhysicalType.Int64)
        {
            var bytes = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(bytes, unscaled);
            return bytes;
        }
        var minimal = new BigInteger(unscaled).ToByteArray(isBigEndian: true);
        if (physicalType == ParquetPhysicalType.ByteArray) return minimal;
        var fixedBytes = new byte[12];
        Array.Fill(fixedBytes, unscaled < 0 ? byte.MaxValue : (byte)0);
        minimal.CopyTo(fixedBytes.AsSpan(fixedBytes.Length - minimal.Length));
        return fixedBytes;
    }

    static void AssertBytes(ref BufferWriter writer, byte[] expected, string scenario)
    {
        var actual = new byte[writer.WrittenLength];
        writer.CopyTo(actual);
        if (!actual.AsSpan().SequenceEqual(expected))
            throw new InvalidOperationException($"Physical bytes differ for {scenario}.");
    }
}
