using System.Collections.Immutable;
using Plank.Reading.Logical;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.PageStrategy;

namespace Plank.Tests.Writer;

internal sealed class FloatingPointDictionaryTests
{
    [Test]
    public void FloatDictionaryPreservesDistinctBitPatterns()
    {
        var expected = new[]
        {
            0f,
            BitConverter.Int32BitsToSingle(unchecked((int)0x80000000)),
            BitConverter.Int32BitsToSingle(0x7FC00001),
            BitConverter.Int32BitsToSingle(0x7FC00002)
        };

        var actual = RoundTrip(expected, ParquetPhysicalType.Float);

        AssertBitsEqual(expected, actual);
    }

    [Test]
    public void DoubleDictionaryPreservesDistinctBitPatterns()
    {
        var expected = new[]
        {
            0d,
            BitConverter.Int64BitsToDouble(unchecked((long)0x8000000000000000)),
            BitConverter.Int64BitsToDouble(0x7FF8000000000001),
            BitConverter.Int64BitsToDouble(0x7FF8000000000002)
        };

        var actual = RoundTrip(expected, ParquetPhysicalType.Double);

        AssertBitsEqual(expected, actual);
    }

    [Test]
    public void OptionalDoubleDictionaryPreservesDistinctBitPatternsAcrossUnsortedRowGroups()
    {
        var first = new double?[]
        {
            3d,
            null,
            BitConverter.Int64BitsToDouble(unchecked((long)0x8000000000000000)),
            1d,
            BitConverter.Int64BitsToDouble(0x7FF8000000000001),
            3d
        };
        var second = new double?[]
        {
            BitConverter.Int64BitsToDouble(0x7FF8000000000002),
            0d,
            null,
            BitConverter.Int64BitsToDouble(unchecked((long)0x8000000000000000)),
            3d,
            BitConverter.Int64BitsToDouble(0x7FF8000000000002)
        };

        var actual = RoundTripOptionalBatches(ForceDictionaryPageStrategy.Shared, first, second);

        AssertNullableBitsEqual([.. first, .. second], actual);
    }

    [Test]
    public void OptionalDoubleDictionaryFallsBackAfterSmallIndexTableFills()
    {
        var first = new double?[] { 3, null, 1, 2, 3, 1 };
        var second = Enumerable.Range(0, 700)
            .Select(static value => value % 41 == 0 ? null : (double?)BitConverter.Int64BitsToDouble(value + 1L))
            .Concat([0d, -0d, BitConverter.Int64BitsToDouble(1), BitConverter.Int64BitsToDouble(700)])
            .ToArray();

        var actual = RoundTripOptionalBatches(ForceDictionaryPageStrategy.Shared, first, second);

        AssertNullableBitsEqual([.. first, .. second], actual);
    }

    [Test]
    public async Task OptionalDoubleDictionaryPreservesSingleValueDropCheckSchedule()
    {
        var strategy = new RecordingMaybeDictionaryStrategy();
        var first = new double?[] { 3, 1 };
        var second = new double?[] { 2 };

        var actual = RoundTripOptionalBatches(strategy, first, second);

        AssertNullableBitsEqual([.. first, .. second], actual);
        await Assert.That(strategy.RowsSeen).IsEquivalentTo([2u]);
    }

    static T[] RoundTrip<T>(T[] values, ParquetPhysicalType physicalType)
        where T : struct
    {
        var schema = new ParquetSchema([
            ColumnDefinition.Leaf("value", physicalType,
                new ColumnOptions(ParquetRepetition.Required, [EncodingKind.RleDictionary]),
                pageStrategy: ForceDictionaryPageStrategy.Shared)
        ]);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None
        });
        var serialized = writer.CreateSerializedColumn<T>(schema.LeafColumns[0]);
        serialized.Serialize(values);
        writer.StartRowGroup().Write(serialized);
        writer.CloseFile();

        using var readStream = new MemoryStream(stream.ToArray(), writable: false);
        using var reader = schema.CreateReader(readStream);
        var actual = new List<T>(values.Length);
        foreach (var rowGroup in reader.RowGroups)
            foreach (var buffer in rowGroup.Column<T>(schema.LeafColumns[0]))
                actual.AddRange(buffer.Values);
        return actual.ToArray();
    }

    static double?[] RoundTripOptionalBatches(IPageStrategy pageStrategy, params double?[][] batches)
    {
        var schema = new ParquetSchema([
            ColumnDefinition.Leaf("value", ParquetPhysicalType.Double,
                new ColumnOptions(ParquetRepetition.Optional, [EncodingKind.RleDictionary]),
                pageStrategy: pageStrategy)
        ]);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None,
            WritePageIndexes = true
        });
        var serialized = writer.CreateSerializedColumn<double?>(schema.LeafColumns[0]);
        foreach (var batch in batches)
        {
            serialized.Serialize(batch);
            writer.StartRowGroup().Write(serialized);
        }
        writer.CloseFile();

        using var readStream = new MemoryStream(stream.ToArray(), writable: false);
        using var reader = schema.CreateReader(readStream);
        var actual = new List<double?>(batches.Sum(static batch => batch.Length));
        foreach (var rowGroup in reader.RowGroups)
            foreach (var buffer in rowGroup.Column<double?>(schema.LeafColumns[0]))
                actual.AddRange(buffer.Values);
        return actual.ToArray();
    }

    sealed class RecordingMaybeDictionaryStrategy : IPageStrategy
    {
        public List<uint> RowsSeen { get; } = [];

        public DictionaryMode GetDictionaryMode()
            => DictionaryMode.Maybe;

        public bool ShouldDropDictionary(uint uniqueCount, uint totalRowCount, uint rowsSeen)
        {
            RowsSeen.Add(rowsSeen);
            return false;
        }

        public uint GetNextDataPageRowCount(uint totalRowCount, uint rowsWritten)
            => totalRowCount - rowsWritten;
    }

    static void AssertBitsEqual(float[] expected, float[] actual)
    {
        if (actual.Length != expected.Length)
            throw new InvalidOperationException($"Expected {expected.Length} values, got {actual.Length}.");

        for (var i = 0; i < expected.Length; i++)
            if (BitConverter.SingleToInt32Bits(actual[i]) != BitConverter.SingleToInt32Bits(expected[i]))
                throw new InvalidOperationException(
                    $"Float bit pattern mismatch at {i}: expected 0x{BitConverter.SingleToInt32Bits(expected[i]):X8}, " +
                    $"got 0x{BitConverter.SingleToInt32Bits(actual[i]):X8}.");
    }

    static void AssertBitsEqual(double[] expected, double[] actual)
    {
        if (actual.Length != expected.Length)
            throw new InvalidOperationException($"Expected {expected.Length} values, got {actual.Length}.");

        for (var i = 0; i < expected.Length; i++)
            if (BitConverter.DoubleToInt64Bits(actual[i]) != BitConverter.DoubleToInt64Bits(expected[i]))
                throw new InvalidOperationException(
                    $"Double bit pattern mismatch at {i}: expected 0x{BitConverter.DoubleToInt64Bits(expected[i]):X16}, " +
                    $"got 0x{BitConverter.DoubleToInt64Bits(actual[i]):X16}.");
    }

    static void AssertNullableBitsEqual(double?[] expected, double?[] actual)
    {
        if (actual.Length != expected.Length)
            throw new InvalidOperationException($"Expected {expected.Length} values, got {actual.Length}.");

        for (var i = 0; i < expected.Length; i++)
        {
            if (!expected[i].HasValue || !actual[i].HasValue)
            {
                if (expected[i].HasValue != actual[i].HasValue)
                    throw new InvalidOperationException($"Null mismatch at {i}.");
                continue;
            }

            if (BitConverter.DoubleToInt64Bits(actual[i]!.Value)
                != BitConverter.DoubleToInt64Bits(expected[i]!.Value))
                throw new InvalidOperationException(
                    $"Double bit pattern mismatch at {i}: expected " +
                    $"0x{BitConverter.DoubleToInt64Bits(expected[i]!.Value):X16}, got " +
                    $"0x{BitConverter.DoubleToInt64Bits(actual[i]!.Value):X16}.");
        }
    }
}
