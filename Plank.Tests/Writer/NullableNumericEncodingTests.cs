using System.Runtime.CompilerServices;
using ParquetSharp;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.PageStrategy;
using ParquetDataPageVersion = Plank.Writing.ParquetDataPageVersion;

namespace Plank.Tests.Writer;

internal sealed class NullableNumericEncodingTests
{
    [Test]
    [Arguments(ParquetDataPageVersion.V1)]
    [Arguments(ParquetDataPageVersion.V2)]
    public void RequiredAndNullableValuesRoundTripAcrossNumericEncodings(ParquetDataPageVersion dataPageVersion)
    {
        AssertRoundTrip(CreateBooleanValues(), ParquetPhysicalType.Boolean,
            [EncodingKind.Plain, EncodingKind.Rle], dataPageVersion);
        AssertRoundTrip(CreateInt32Values(), ParquetPhysicalType.Int32,
            [EncodingKind.Plain, EncodingKind.RleDictionary, EncodingKind.DeltaBinaryPacked,
                EncodingKind.ByteStreamSplit], dataPageVersion);
        AssertRoundTrip(CreateInt64Values(), ParquetPhysicalType.Int64,
            [EncodingKind.Plain, EncodingKind.RleDictionary, EncodingKind.DeltaBinaryPacked,
                EncodingKind.ByteStreamSplit], dataPageVersion);
        AssertRoundTrip(CreateFloatValues(), ParquetPhysicalType.Float,
            [EncodingKind.Plain, EncodingKind.RleDictionary, EncodingKind.ByteStreamSplit], dataPageVersion);
        AssertRoundTrip(CreateDoubleValues(), ParquetPhysicalType.Double,
            [EncodingKind.Plain, EncodingKind.RleDictionary, EncodingKind.ByteStreamSplit], dataPageVersion);
    }

    [Test]
    [Arguments(ParquetDataPageVersion.V1)]
    [Arguments(ParquetDataPageVersion.V2)]
    public void ForcedNullablePrimitiveDictionaryUsesOneDataPageAndPreservesStatistics(
        ParquetDataPageVersion dataPageVersion)
    {
        AssertForcedNullableDictionary(CreateInt64Values(), ParquetPhysicalType.Int64, dataPageVersion);

        var doubles = CreateDoubleValues();
        doubles[42] = doubles[2];
        doubles[43] = doubles[2];
        doubles[100] = doubles[3];
        AssertForcedNullableDictionary(doubles, ParquetPhysicalType.Double, dataPageVersion);
    }

    static void AssertForcedNullableDictionary<TValue>(TValue[] requiredValues,
        ParquetPhysicalType physicalType, ParquetDataPageVersion dataPageVersion)
        where TValue : struct
    {
        var values = CreateOptionalValues(requiredValues);
        var schema = new ParquetSchema([
            ColumnDefinition.OptionalLeaf("optional", physicalType,
                new ColumnOptions(encodings: [EncodingKind.RleDictionary]),
                pageStrategy: ForceDictionaryPageStrategy.Shared)
        ]);
        var expectedStatistics = ColumnStatistics.CreateOptional(schema.LeafColumns[0].Column, values);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions { DataPageVersion = dataPageVersion });
        var serialized = writer.CreateSerializedColumn<TValue?>(schema.LeafColumns[0]);

        serialized.Serialize(values);

        AssertStatistics(serialized.Statistics, expectedStatistics, EncodingKind.RleDictionary, "optional");
        var dictionaryPages = 0;
        var dataPages = 0;
        for (var pageIndex = 0; pageIndex < serialized.Pages.Count; pageIndex++)
        {
            ref var page = ref serialized.Pages[pageIndex];
            if (page.Kind == PageKind.Dictionary)
            {
                dictionaryPages++;
                continue;
            }

            dataPages++;
            if (page.RowCount != (uint)values.Length)
                throw new InvalidOperationException(
                    $"Forced dictionary data page row count mismatch. Expected {values.Length}, got {page.RowCount}.");
        }
        if (dictionaryPages != 1 || dataPages != 1)
            throw new InvalidOperationException(
                $"Expected one dictionary and one data page, got {dictionaryPages} dictionary and {dataPages} data pages.");

        writer.StartRowGroup().Write(serialized);
        writer.CloseFile();
        using var readStream = new MemoryStream(stream.ToArray(), writable: false);
        using var reader = new ParquetFileReader(readStream, leaveOpen: false);
        using var rowGroup = reader.RowGroup(0);
        using var column = rowGroup.Column(0).LogicalReader<TValue?>();
        AssertColumn(column.ReadAll(values.Length), values, EncodingKind.RleDictionary, "optional");
    }

    static void AssertRoundTrip<TValue>(TValue[] requiredValues, ParquetPhysicalType physicalType,
        ReadOnlySpan<EncodingKind> encodings, ParquetDataPageVersion dataPageVersion)
        where TValue : struct
    {
        var optionalValues = CreateOptionalValues(requiredValues);
        for (var encodingIndex = 0; encodingIndex < encodings.Length; encodingIndex++)
        {
            var encoding = encodings[encodingIndex];
            var strategy = new FixedRowsPageStrategy(33,
                encoding == EncodingKind.RleDictionary ? DictionaryMode.Forced : DictionaryMode.Disabled);
            var schema = new ParquetSchema([
                ColumnDefinition.RequiredLeaf("required", physicalType,
                    new ColumnOptions(encodings: [encoding]), pageStrategy: strategy),
                ColumnDefinition.OptionalLeaf("optional", physicalType,
                    new ColumnOptions(encodings: [encoding]), pageStrategy: strategy)
            ]);
            var expectedRequiredStatistics = ColumnStatistics.Create(schema.LeafColumns[0].Column, requiredValues, 0);
            var expectedOptionalStatistics = ColumnStatistics.CreateOptional(schema.LeafColumns[1].Column,
                optionalValues);

            using var stream = new MemoryStream();
            var writer = schema.CreateWriter(stream, new ParquetWriterOptions
            {
                DataPageVersion = dataPageVersion,
                WritePageIndexes = true
            });
            var required = writer.CreateSerializedColumn<TValue>(schema.LeafColumns[0]);
            var optional = writer.CreateSerializedColumn<TValue?>(schema.LeafColumns[1]);
            required.Serialize(requiredValues);
            optional.Serialize(optionalValues);
            AssertStatistics(required.Statistics, expectedRequiredStatistics, encoding, "required");
            AssertStatistics(optional.Statistics, expectedOptionalStatistics, encoding, "optional");

            var rowGroup = writer.StartRowGroup();
            rowGroup.Write(required);
            rowGroup.Write(optional);
            writer.CloseFile();

            using var readStream = new MemoryStream(stream.ToArray(), writable: false);
            using var reader = new ParquetFileReader(readStream, leaveOpen: false);
            using var parquetRowGroup = reader.RowGroup(0);
            using var requiredReader = parquetRowGroup.Column(0).LogicalReader<TValue>();
            using var optionalReader = parquetRowGroup.Column(1).LogicalReader<TValue?>();
            AssertColumn(requiredReader.ReadAll(requiredValues.Length), requiredValues, encoding, "required");
            AssertColumn(optionalReader.ReadAll(optionalValues.Length), optionalValues, encoding, "optional");
        }

        AssertAllNullPlainRoundTrip<TValue>(physicalType, dataPageVersion, requiredValues.Length);
    }

    static void AssertAllNullPlainRoundTrip<TValue>(ParquetPhysicalType physicalType,
        ParquetDataPageVersion dataPageVersion, int count)
        where TValue : struct
    {
        var strategy = new FixedRowsPageStrategy(33, DictionaryMode.Disabled);
        var schema = new ParquetSchema([
            ColumnDefinition.OptionalLeaf("all_null", physicalType,
                new ColumnOptions(encodings: [EncodingKind.Plain]), pageStrategy: strategy)
        ]);
        var expected = new TValue?[count];
        var expectedStatistics = ColumnStatistics.CreateOptional(schema.LeafColumns[0].Column, expected);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            DataPageVersion = dataPageVersion,
            WritePageIndexes = true
        });
        var serialized = writer.CreateSerializedColumn<TValue?>(schema.LeafColumns[0]);
        serialized.Serialize(expected);
        AssertStatistics(serialized.Statistics, expectedStatistics, EncodingKind.Plain, "all-null");
        writer.StartRowGroup().Write(serialized);
        writer.CloseFile();

        using var readStream = new MemoryStream(stream.ToArray(), writable: false);
        using var reader = new ParquetFileReader(readStream, leaveOpen: false);
        using var rowGroup = reader.RowGroup(0);
        using var column = rowGroup.Column(0).LogicalReader<TValue?>();
        AssertColumn(column.ReadAll(expected.Length), expected, EncodingKind.Plain, "all-null");
    }

    static TValue?[] CreateOptionalValues<TValue>(ReadOnlySpan<TValue> values)
        where TValue : struct
    {
        var optional = new TValue?[values.Length];
        for (var i = 0; i < values.Length; i++)
            if (i != 0 && i != values.Length - 1 && i % 17 != 0 && i % 33 != 0)
                optional[i] = values[i];
        return optional;
    }

    static bool[] CreateBooleanValues()
    {
        var values = new bool[259];
        for (var i = 0; i < values.Length; i++)
            values[i] = ((i / 7) & 1) == 0;
        return values;
    }

    static int[] CreateInt32Values()
    {
        var values = new int[259];
        for (var i = 0; i < values.Length; i++)
            values[i] = i switch
            {
                1 => -1_000_000_000,
                2 => 1_000_000_000,
                _ => unchecked((i * 7919) ^ (i << 23)) % 1_000_000_000
            };
        return values;
    }

    static long[] CreateInt64Values()
    {
        var values = new long[259];
        for (var i = 0; i < values.Length; i++)
            values[i] = i switch
            {
                1 => -4_000_000_000_000_000,
                2 => 4_000_000_000_000_000,
                _ => unchecked((long)((ulong)i * 0x9E3779B97F4A7C15UL)) % 4_000_000_000_000_000
            };
        return values;
    }

    static float[] CreateFloatValues()
    {
        var values = new float[259];
        for (var i = 0; i < values.Length; i++)
        {
            var bits = i switch
            {
                1 => unchecked((int)0x80000000U),
                2 => 0x7FC00001,
                3 => 0x7FC00002,
                4 => 0,
                5 => 0x7F800000,
                6 => unchecked((int)0xFF800000U),
                _ => unchecked((int)(0x3F800000U ^ ((uint)i * 0x00010001U)))
            };
            values[i] = BitConverter.Int32BitsToSingle(bits);
        }
        return values;
    }

    static double[] CreateDoubleValues()
    {
        var values = new double[259];
        for (var i = 0; i < values.Length; i++)
        {
            var bits = i switch
            {
                1 => unchecked((long)0x8000000000000000UL),
                2 => 0x7FF8000000000001L,
                3 => 0x7FF8000000000002L,
                4 => 0,
                5 => 0x7FF0000000000000L,
                6 => unchecked((long)0xFFF0000000000000UL),
                _ => unchecked((long)(0x3FF0000000000000UL ^ ((ulong)i * 0x0000000100010001UL)))
            };
            values[i] = BitConverter.Int64BitsToDouble(bits);
        }
        return values;
    }

    static void AssertStatistics(ColumnStatistics actual, ColumnStatistics expected, EncodingKind encoding,
        string column)
    {
        if (actual.ValueKind != expected.ValueKind || actual.MinBits != expected.MinBits
            || actual.MaxBits != expected.MaxBits || actual.NullCount != expected.NullCount
            || actual.DistinctCount != expected.DistinctCount || actual.NanCount != expected.NanCount
            || actual.HasStatistics != expected.HasStatistics)
            throw new InvalidOperationException(
                $"{encoding} {column} statistics changed after nullable compaction.");
    }

    static void AssertColumn<TValue>(TValue[] actual, ReadOnlySpan<TValue> expected,
        EncodingKind encoding, string column)
    {
        if (actual.Length != expected.Length)
            throw new InvalidOperationException(
                $"{encoding} {column} length mismatch. Expected {expected.Length}, got {actual.Length}.");

        for (var i = 0; i < expected.Length; i++)
            if (!ValuesEqual(actual[i], expected[i]))
                throw new InvalidOperationException($"{encoding} {column} value mismatch at index {i}.");
    }

    static bool ValuesEqual<TValue>(TValue left, TValue right)
    {
        if (typeof(TValue) == typeof(float))
            return BitConverter.SingleToInt32Bits(Unsafe.As<TValue, float>(ref left)) ==
                   BitConverter.SingleToInt32Bits(Unsafe.As<TValue, float>(ref right));
        if (typeof(TValue) == typeof(float?))
        {
            var leftValue = Unsafe.As<TValue, float?>(ref left);
            var rightValue = Unsafe.As<TValue, float?>(ref right);
            return leftValue.HasValue == rightValue.HasValue &&
                   (!leftValue.HasValue || BitConverter.SingleToInt32Bits(leftValue.Value) ==
                       BitConverter.SingleToInt32Bits(rightValue!.Value));
        }
        if (typeof(TValue) == typeof(double))
            return BitConverter.DoubleToInt64Bits(Unsafe.As<TValue, double>(ref left)) ==
                   BitConverter.DoubleToInt64Bits(Unsafe.As<TValue, double>(ref right));
        if (typeof(TValue) == typeof(double?))
        {
            var leftValue = Unsafe.As<TValue, double?>(ref left);
            var rightValue = Unsafe.As<TValue, double?>(ref right);
            return leftValue.HasValue == rightValue.HasValue &&
                   (!leftValue.HasValue || BitConverter.DoubleToInt64Bits(leftValue.Value) ==
                       BitConverter.DoubleToInt64Bits(rightValue!.Value));
        }
        return EqualityComparer<TValue>.Default.Equals(left, right);
    }

    sealed class FixedRowsPageStrategy : IPageStrategy
    {
        readonly uint _rowsPerPage;
        readonly DictionaryMode _dictionaryMode;

        internal FixedRowsPageStrategy(uint rowsPerPage, DictionaryMode dictionaryMode)
        {
            _rowsPerPage = rowsPerPage;
            _dictionaryMode = dictionaryMode;
        }

        public DictionaryMode GetDictionaryMode()
            => _dictionaryMode;

        public bool ShouldDropDictionary(uint uniqueCount, uint totalRowCount, uint rowsSeen)
            => false;

        public bool ShouldStartNewDataPage(uint totalRowCount, uint rowsWritten, uint currentPageRowCount)
            => currentPageRowCount >= _rowsPerPage;
    }
}
