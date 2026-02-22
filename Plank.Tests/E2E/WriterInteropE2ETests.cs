using System.Collections.Immutable;
using Plank.Schema;
using Plank.Writing;
using PlankColumn = Plank.Schema.Column;

namespace Plank.Tests;

internal sealed class WriterInteropE2ETests
{
    const int FuzzRowGroupCount = 2;
    const int MinFuzzRowCount = 12;
    const int MaxFuzzRowCount = 28;
    static readonly int[] FuzzSeeds = [17107, 36433, 56921];

    [Test]
    public async Task RequiredColumnsSingleRowGroupAreReadableByBothImplementations()
    {
        var path = NewPath("single-row-group");
        var rowGroups = new[]
        {
            new ExpectedRowGroup(
                Int32Values: [1, 2, 3, 4],
                Int64Values: [11L, 22L, 33L, 44L],
                DoubleValues: [1.5, 2.5, 3.5, 4.5],
                BinaryValues:
                [
                    [0x01, 0x02],
                    [],
                    [0x10],
                    [0xAA, 0xBB, 0xCC]
                ])
        };

        await WriteFileAsync(path, CompressionKind.None, rowGroups).ConfigureAwait(false);
        try
        {
            await AssertReadableByAllReadersAsync(path, rowGroups).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public async Task RequiredColumnsTwoRowGroupsAreReadableByBothImplementations()
    {
        var path = NewPath("two-row-groups");
        var rowGroups = new[]
        {
            new ExpectedRowGroup(
                Int32Values: [10, 20, 30],
                Int64Values: [100L, 200L, 300L],
                DoubleValues: [10.25, 20.25, 30.25],
                BinaryValues:
                [
                    [0x00],
                    [0x01, 0x02],
                    [0x03, 0x04, 0x05]
                ]),
            new ExpectedRowGroup(
                Int32Values: [7, 8, 9, 10],
                Int64Values: [70L, 80L, 90L, 100L],
                DoubleValues: [7.75, 8.75, 9.75, 10.75],
                BinaryValues:
                [
                    [0x11],
                    [],
                    [0x22, 0x23],
                    [0x24]
                ])
        };

        await WriteFileAsync(path, CompressionKind.None, rowGroups).ConfigureAwait(false);
        try
        {
            await AssertReadableByAllReadersAsync(path, rowGroups).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public async Task RequiredColumnsWithSnappyCompressionAreReadableByBothImplementations()
    {
        var path = NewPath("snappy");
        var rowGroups = new[]
        {
            CreateGeneratedRowGroup(512, offset: 0)
        };

        await WriteFileAsync(path, CompressionKind.Snappy, rowGroups).ConfigureAwait(false);
        try
        {
            await AssertReadableByAllReadersAsync(path, rowGroups).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public async Task RequiredColumnsWithGzipCompressionAreReadableByBothImplementations()
    {
        var path = NewPath("gzip");
        var rowGroups = new[]
        {
            CreateGeneratedRowGroup(512, offset: 1000)
        };

        await WriteFileAsync(path, CompressionKind.Gzip, rowGroups).ConfigureAwait(false);
        try
        {
            await AssertReadableByAllReadersAsync(path, rowGroups).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public async Task RequiredColumnsWithZstdCompressionAreReadableByBothImplementations()
    {
        var path = NewPath("zstd");
        var rowGroups = new[]
        {
            CreateGeneratedRowGroup(256, offset: 5000)
        };

        await WriteFileAsync(path, CompressionKind.Zstd, rowGroups).ConfigureAwait(false);
        try
        {
            await AssertReadableByAllReadersAsync(path, rowGroups).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public async Task RequiredColumnsWithDeltaBinaryPackedEncodingAreReadableByBothImplementations()
    {
        var path = NewPath("delta-binary-packed");
        var schema = CreateSchema(int32Encoding: EncodingKind.DeltaBinaryPacked);
        var rowGroups = new[]
        {
            CreateGeneratedRowGroup(1024, offset: 10000)
        };

        await WriteFileAsync(path, schema, CompressionKind.None, rowGroups).ConfigureAwait(false);
        try
        {
            await AssertReadableByAllReadersAsync(path, rowGroups).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public async Task RequiredColumnsWithDeltaLengthByteArrayEncodingAreReadableByBothImplementations()
    {
        var path = NewPath("delta-length-byte-array");
        var schema = CreateSchema(binaryEncoding: EncodingKind.DeltaLengthByteArray);
        var rowGroups = new[]
        {
            CreateGeneratedRowGroup(600, offset: 21000)
        };

        await WriteFileAsync(path, schema, CompressionKind.None, rowGroups).ConfigureAwait(false);
        try
        {
            await AssertReadableByAllReadersAsync(path, rowGroups).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public async Task RequiredColumnsWithDeltaByteArrayEncodingAreReadableByBothImplementations()
    {
        var path = NewPath("delta-byte-array");
        var schema = CreateSchema(binaryEncoding: EncodingKind.DeltaByteArray);
        var rowGroups = new[]
        {
            CreateGeneratedRowGroupWithSharedBinaryPrefixes(700, offset: 32000)
        };

        await WriteFileAsync(path, schema, CompressionKind.None, rowGroups).ConfigureAwait(false);
        try
        {
            await AssertReadableByAllReadersAsync(path, rowGroups).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public async Task RequiredColumnsWithByteStreamSplitEncodingAreReadableByBothImplementations()
    {
        var path = NewPath("byte-stream-split");
        var schema = CreateSchema(doubleEncoding: EncodingKind.ByteStreamSplit);
        var rowGroups = new[]
        {
            CreateGeneratedRowGroup(512, offset: 45000)
        };

        await WriteFileAsync(path, schema, CompressionKind.None, rowGroups).ConfigureAwait(false);
        try
        {
            await AssertReadableByAllReadersAsync(path, rowGroups).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public async Task RequiredColumnsWithRleDictionaryEncodingAreReadableByBothImplementations()
    {
        var path = NewPath("rle-dictionary");
        var schema = CreateSchema(int32Encoding: EncodingKind.RleDictionary);
        var rowGroups = new[]
        {
            CreateGeneratedRowGroupWithRepeatingIntegers(1024, valueRange: 13)
        };

        await WriteFileAsync(path, schema, CompressionKind.None, rowGroups).ConfigureAwait(false);
        try
        {
            await AssertReadableByAllReadersAsync(path, rowGroups).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public async Task RequiredColumnsWithPlainDictionaryEncodingAreReadableByBothImplementations()
    {
        var path = NewPath("plain-dictionary");
        var schema = CreateSchema(int32Encoding: EncodingKind.PlainDictionary);
        var rowGroups = new[]
        {
            CreateGeneratedRowGroupWithRepeatingIntegers(1024, valueRange: 9)
        };

        await WriteFileAsync(path, schema, CompressionKind.None, rowGroups).ConfigureAwait(false);
        try
        {
            await AssertReadableByAllReadersAsync(path, rowGroups).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public async Task RequiredColumnsFuzzedAcrossSupportedEncodingsRoundTripByBothImplementations()
    {
        foreach (var fuzzCase in EnumerateFuzzCases())
            for (var seedIndex = 0; seedIndex < FuzzSeeds.Length; seedIndex++)
                await WriteAndAssertFuzzCaseAsync(fuzzCase, FuzzSeeds[seedIndex]).ConfigureAwait(false);
    }

    static IEnumerable<FuzzCase> EnumerateFuzzCases()
    {
        yield return new FuzzCase("plain", CreateSchema(), FuzzPattern.General);
        yield return new FuzzCase("delta-binary-packed", CreateSchema(int32Encoding: EncodingKind.DeltaBinaryPacked),
            FuzzPattern.MonotonicIntegers);
        yield return new FuzzCase("byte-stream-split", CreateSchema(doubleEncoding: EncodingKind.ByteStreamSplit),
            FuzzPattern.General);
        yield return new FuzzCase("delta-length-byte-array", CreateSchema(binaryEncoding: EncodingKind.DeltaLengthByteArray),
            FuzzPattern.VariableBinaryLength);
        yield return new FuzzCase("delta-byte-array", CreateSchema(binaryEncoding: EncodingKind.DeltaByteArray),
            FuzzPattern.SharedBinaryPrefix);
        yield return new FuzzCase("rle-dictionary", CreateSchema(int32Encoding: EncodingKind.RleDictionary),
            FuzzPattern.DictionaryFriendly);
        yield return new FuzzCase("plain-dictionary", CreateSchema(int32Encoding: EncodingKind.PlainDictionary),
            FuzzPattern.DictionaryFriendly);
    }

    static async Task WriteAndAssertFuzzCaseAsync(FuzzCase fuzzCase, int seed)
    {
        var path = NewPath($"fuzz-{fuzzCase.Name}-seed-{seed}");
        var rowGroups = CreateFuzzRowGroups(fuzzCase, seed);
        try
        {
            await WriteFileAsync(path, fuzzCase.Schema, CompressionKind.None, rowGroups).ConfigureAwait(false);
            await AssertReadableByAllReadersAsync(path, rowGroups).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    static ExpectedRowGroup[] CreateFuzzRowGroups(FuzzCase fuzzCase, int seed)
    {
        var random = new DeterministicRng(seed);
        var rowGroups = new ExpectedRowGroup[FuzzRowGroupCount];
        for (var rowGroupIndex = 0; rowGroupIndex < rowGroups.Length; rowGroupIndex++)
        {
            var rowCount = random.NextInt(MinFuzzRowCount, MaxFuzzRowCount + 1);
            rowGroups[rowGroupIndex] = CreateFuzzRowGroup(random, rowCount, fuzzCase.Pattern);
        }

        return rowGroups;
    }

    static ExpectedRowGroup CreateFuzzRowGroup(DeterministicRng random, int rowCount, FuzzPattern pattern)
    {
        var int32Values = new int[rowCount];
        var int64Values = new long[rowCount];
        var doubleValues = new double[rowCount];
        var binaryValues = new byte[rowCount][];
        var int32Accumulator = random.NextInt(-20_000, 20_001);
        var int64Accumulator = random.NextInt64(-2_000_000L, 2_000_001L);
        var prefix = CreateRandomBytes(random, minLength: 3, maxLength: 3);
        var int32Dictionary = CreateInt32Dictionary(random, uniqueCount: 11);
        var int64Dictionary = CreateInt64Dictionary(random, uniqueCount: 9);
        var binaryDictionary = CreateBinaryDictionary(random, uniqueCount: 7, minLength: 1, maxLength: 7);

        for (var i = 0; i < rowCount; i++)
        {
            switch (pattern)
            {
                case FuzzPattern.General:
                    int32Values[i] = random.NextInt(-50_000, 50_001);
                    int64Values[i] = random.NextInt64(-5_000_000L, 5_000_001L);
                    doubleValues[i] = (random.NextDouble() - 0.5) * 10_000d;
                    binaryValues[i] = CreateRandomBytes(random, minLength: 0, maxLength: 11);
                    break;
                case FuzzPattern.MonotonicIntegers:
                    int32Accumulator += random.NextInt(0, 9);
                    int64Accumulator += random.NextInt(0, 2_500);
                    int32Values[i] = int32Accumulator;
                    int64Values[i] = int64Accumulator;
                    doubleValues[i] = int32Accumulator * 0.25 + random.NextDouble();
                    binaryValues[i] = CreateRandomBytes(random, minLength: 1, maxLength: 8);
                    break;
                case FuzzPattern.DictionaryFriendly:
                    int32Values[i] = int32Dictionary[random.NextInt(0, int32Dictionary.Length)];
                    int64Values[i] = int64Dictionary[random.NextInt(0, int64Dictionary.Length)];
                    doubleValues[i] = (random.NextInt(0, 8) - 3) * 0.5;
                    binaryValues[i] = binaryDictionary[random.NextInt(0, binaryDictionary.Length)];
                    break;
                case FuzzPattern.VariableBinaryLength:
                    int32Values[i] = random.NextInt(-10_000, 10_001);
                    int64Values[i] = random.NextInt64(-1_000_000L, 1_000_001L);
                    doubleValues[i] = random.NextInt(-1000, 1001) + random.NextDouble();
                    binaryValues[i] = CreateRandomBytes(random, minLength: 1, maxLength: 16);
                    break;
                case FuzzPattern.SharedBinaryPrefix:
                    int32Values[i] = random.NextInt(-10_000, 10_001);
                    int64Values[i] = random.NextInt64(-3_000_000L, 3_000_001L);
                    doubleValues[i] = (random.NextDouble() - 0.5) * 2_000d;
                    binaryValues[i] = CreateBytesWithPrefix(random, prefix, minSuffixLength: 1, maxSuffixLength: 8);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(pattern), pattern, "Unexpected fuzz pattern.");
            }
        }

        return new ExpectedRowGroup(int32Values, int64Values, doubleValues, binaryValues);
    }

    static int[] CreateInt32Dictionary(DeterministicRng random, int uniqueCount)
    {
        var values = new int[uniqueCount];
        for (var i = 0; i < values.Length; i++)
            values[i] = random.NextInt(-2000, 2001);
        return values;
    }

    static long[] CreateInt64Dictionary(DeterministicRng random, int uniqueCount)
    {
        var values = new long[uniqueCount];
        for (var i = 0; i < values.Length; i++)
            values[i] = random.NextInt64(-500_000L, 500_001L);
        return values;
    }

    static byte[][] CreateBinaryDictionary(DeterministicRng random, int uniqueCount, int minLength, int maxLength)
    {
        var values = new byte[uniqueCount][];
        for (var i = 0; i < values.Length; i++)
            values[i] = CreateRandomBytes(random, minLength, maxLength);
        return values;
    }

    static byte[] CreateRandomBytes(DeterministicRng random, int minLength, int maxLength)
    {
        var length = random.NextInt(minLength, maxLength + 1);
        var value = new byte[length];
        random.NextBytes(value);
        return value;
    }

    static byte[] CreateBytesWithPrefix(DeterministicRng random, byte[] prefix, int minSuffixLength, int maxSuffixLength)
    {
        var suffixLength = random.NextInt(minSuffixLength, maxSuffixLength + 1);
        var value = new byte[prefix.Length + suffixLength];
        Array.Copy(prefix, value, prefix.Length);
        random.NextBytes(value.AsSpan(prefix.Length, suffixLength));
        return value;
    }

    static string NewPath(string suffix)
        => Path.Combine(Path.GetTempPath(), $"plank-writer-interop-{suffix}-{Guid.NewGuid():N}.parquet");

    static async Task WriteFileAsync(string path, CompressionKind compression, IReadOnlyList<ExpectedRowGroup> rowGroups)
        => await WriteFileAsync(path, WriterInteropSchema.Schema, compression, rowGroups).ConfigureAwait(false);

    static async Task WriteFileAsync(string path, ParquetSchema schema, CompressionKind compression,
        IReadOnlyList<ExpectedRowGroup> rowGroups)
    {
        using var stream = File.Create(path);
        var writer = ParquetWriter.Create(stream, schema, new ParquetWriterOptions
        {
            Compression = compression
        });
        var columns = schema.Columns;
        var int32Column = writer.CreateSerializedColumn();
        var int64Column = writer.CreateSerializedColumn();
        var doubleColumn = writer.CreateSerializedColumn();
        var binaryColumn = writer.CreateSerializedColumn();

        for (var rowGroupIndex = 0; rowGroupIndex < rowGroups.Count; rowGroupIndex++)
        {
            var rowGroupInput = rowGroups[rowGroupIndex];
            ValidateRowGroupInput(rowGroupInput);
            var rowGroup = writer.StartRowGroup();

            int32Column.Serialize(columns[0], rowGroupInput.Int32Values);
            int64Column.Serialize(columns[1], rowGroupInput.Int64Values);
            doubleColumn.Serialize(columns[2], rowGroupInput.DoubleValues);
            binaryColumn.Serialize(columns[3], rowGroupInput.BinaryValues);

            rowGroup.Write(int32Column);
            rowGroup.Write(int64Column);
            rowGroup.Write(doubleColumn);
            rowGroup.Write(binaryColumn);
        }

        writer.CloseFile();
    }

    static ParquetSchema CreateSchema(EncodingKind int32Encoding = EncodingKind.Plain,
        EncodingKind int64Encoding = EncodingKind.Plain, EncodingKind doubleEncoding = EncodingKind.Plain,
        EncodingKind binaryEncoding = EncodingKind.Plain)
        => new([
            new PlankColumn(WriterInteropSchema.Int32ColumnName, ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: ImmutableArray.Create(int32Encoding))),
            new PlankColumn(WriterInteropSchema.Int64ColumnName, ParquetPhysicalType.Int64,
                new ColumnOptions(encodings: ImmutableArray.Create(int64Encoding))),
            new PlankColumn(WriterInteropSchema.DoubleColumnName, ParquetPhysicalType.Double,
                new ColumnOptions(encodings: ImmutableArray.Create(doubleEncoding))),
            new PlankColumn(WriterInteropSchema.BinaryColumnName, ParquetPhysicalType.ByteArray,
                new ColumnOptions(encodings: ImmutableArray.Create(binaryEncoding)))
        ]);

    static void ValidateRowGroupInput(ExpectedRowGroup rowGroupInput)
    {
        var expectedLength = rowGroupInput.Int32Values.Length;
        if (rowGroupInput.Int64Values.Length != expectedLength
            || rowGroupInput.DoubleValues.Length != expectedLength
            || rowGroupInput.BinaryValues.Length != expectedLength)
            throw new InvalidOperationException("Invalid expected row group input: all column arrays must have the same length.");
    }

    static async Task AssertReadableByAllReadersAsync(string path, IReadOnlyList<ExpectedRowGroup> expected)
    {
        for (var readerIndex = 0; readerIndex < ParquetInteropReaders.All.Count; readerIndex++)
        {
            var reader = ParquetInteropReaders.All[readerIndex];
            ParquetFileReadResult actual;
            try
            {
                actual = await reader.ReadExpectedSchemaAsync(path).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"{reader.Name} failed to read file '{path}'.", ex);
            }

            AssertReadResultMatches(reader.Name, actual, expected);
        }
    }

    static void AssertReadResultMatches(string readerName, ParquetFileReadResult actual, IReadOnlyList<ExpectedRowGroup> expected)
    {
        if (actual.RowGroups.Count != expected.Count)
            throw new InvalidOperationException(
                $"{readerName} row-group count mismatch. Expected {expected.Count}, got {actual.RowGroups.Count}.");

        for (var rowGroupIndex = 0; rowGroupIndex < expected.Count; rowGroupIndex++)
        {
            var expectedRowGroup = expected[rowGroupIndex];
            var actualRowGroup = actual.RowGroups[rowGroupIndex];
            AssertArrayEquals(readerName, rowGroupIndex, WriterInteropSchema.Int32ColumnName, expectedRowGroup.Int32Values, actualRowGroup.Int32Values);
            AssertArrayEquals(readerName, rowGroupIndex, WriterInteropSchema.Int64ColumnName, expectedRowGroup.Int64Values, actualRowGroup.Int64Values);
            AssertArrayEquals(readerName, rowGroupIndex, WriterInteropSchema.DoubleColumnName, expectedRowGroup.DoubleValues, actualRowGroup.DoubleValues);
            AssertByteArrayArrayEquals(readerName, rowGroupIndex, WriterInteropSchema.BinaryColumnName, expectedRowGroup.BinaryValues, actualRowGroup.BinaryValues);
        }
    }

    static void AssertArrayEquals<T>(string readerName, int rowGroupIndex, string columnName, IReadOnlyList<T> expected, IReadOnlyList<T> actual)
        where T : IEquatable<T>
    {
        if (actual.Count != expected.Count)
            throw new InvalidOperationException(
                $"{readerName} row-group {rowGroupIndex} column '{columnName}' length mismatch. Expected {expected.Count}, got {actual.Count}.");

        for (var i = 0; i < expected.Count; i++)
            if (!actual[i].Equals(expected[i]))
                throw new InvalidOperationException(
                    $"{readerName} row-group {rowGroupIndex} column '{columnName}' value mismatch at index {i}. Expected '{expected[i]}', got '{actual[i]}'.");
    }

    static void AssertByteArrayArrayEquals(string readerName, int rowGroupIndex, string columnName, byte[][] expected, byte[][] actual)
    {
        if (actual.Length != expected.Length)
            throw new InvalidOperationException(
                $"{readerName} row-group {rowGroupIndex} column '{columnName}' length mismatch. Expected {expected.Length}, got {actual.Length}.");

        for (var i = 0; i < expected.Length; i++)
            if (!actual[i].SequenceEqual(expected[i]))
                throw new InvalidOperationException(
                    $"{readerName} row-group {rowGroupIndex} column '{columnName}' byte[] mismatch at index {i}.");
    }

    static ExpectedRowGroup CreateGeneratedRowGroup(int count, int offset)
    {
        var int32Values = new int[count];
        var int64Values = new long[count];
        var doubleValues = new double[count];
        var binaryValues = new byte[count][];
        for (var i = 0; i < count; i++)
        {
            var value = i + offset;
            int32Values[i] = value;
            int64Values[i] = value * 1000L;
            doubleValues[i] = value + 0.125;
            binaryValues[i] = [(byte)(value & 0xFF), (byte)((value >> 8) & 0xFF)];
        }

        return new ExpectedRowGroup(int32Values, int64Values, doubleValues, binaryValues);
    }

    static ExpectedRowGroup CreateGeneratedRowGroupWithSharedBinaryPrefixes(int count, int offset)
    {
        var int32Values = new int[count];
        var int64Values = new long[count];
        var doubleValues = new double[count];
        var binaryValues = new byte[count][];
        for (var i = 0; i < count; i++)
        {
            var value = i + offset;
            int32Values[i] = value;
            int64Values[i] = value * 10L;
            doubleValues[i] = value + 0.5;
            binaryValues[i] = [0x61, 0x62, 0x63, (byte)(value & 0x0F), (byte)(value >> 4), 0x7F];
        }

        return new ExpectedRowGroup(int32Values, int64Values, doubleValues, binaryValues);
    }

    static ExpectedRowGroup CreateGeneratedRowGroupWithRepeatingIntegers(int count, int valueRange)
    {
        var int32Values = new int[count];
        var int64Values = new long[count];
        var doubleValues = new double[count];
        var binaryValues = new byte[count][];
        for (var i = 0; i < count; i++)
        {
            var value = i % valueRange;
            int32Values[i] = value;
            int64Values[i] = value * 100L;
            doubleValues[i] = value + 0.25;
            binaryValues[i] = [(byte)value, 0x42];
        }

        return new ExpectedRowGroup(int32Values, int64Values, doubleValues, binaryValues);
    }

    readonly record struct ExpectedRowGroup(
        int[] Int32Values,
        long[] Int64Values,
        double[] DoubleValues,
        byte[][] BinaryValues);

    readonly record struct FuzzCase(string Name, ParquetSchema Schema, FuzzPattern Pattern);

    enum FuzzPattern
    {
        General,
        MonotonicIntegers,
        DictionaryFriendly,
        VariableBinaryLength,
        SharedBinaryPrefix
    }

    sealed class DeterministicRng
    {
        uint _state;

        public DeterministicRng(int seed)
            => _state = seed == 0 ? 0x9E3779B9U : unchecked((uint)seed);

        public int NextInt(int minInclusive, int maxExclusive)
        {
            if (minInclusive >= maxExclusive)
                throw new ArgumentOutOfRangeException(nameof(maxExclusive),
                    "maxExclusive must be greater than minInclusive.");

            var range = (uint)(maxExclusive - minInclusive);
            return minInclusive + (int)(NextUInt32() % range);
        }

        public long NextInt64(long minInclusive, long maxExclusive)
        {
            if (minInclusive >= maxExclusive)
                throw new ArgumentOutOfRangeException(nameof(maxExclusive),
                    "maxExclusive must be greater than minInclusive.");

            var range = (ulong)(maxExclusive - minInclusive);
            return minInclusive + (long)(NextUInt64() % range);
        }

        public double NextDouble()
            => NextUInt64() / ((double)ulong.MaxValue + 1d);

        public void NextBytes(byte[] buffer)
            => NextBytes(buffer.AsSpan());

        public void NextBytes(Span<byte> buffer)
        {
            for (var i = 0; i < buffer.Length; i++)
                buffer[i] = (byte)(NextUInt32() & 0xFF);
        }

        uint NextUInt32()
        {
            _state ^= _state << 13;
            _state ^= _state >> 17;
            _state ^= _state << 5;
            return _state;
        }

        ulong NextUInt64()
            => ((ulong)NextUInt32() << 32) | NextUInt32();
    }
}
