using System.Collections.Immutable;
using Plank.Reading;
using Plank.Reading.Logical;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Reading;

internal sealed class ReaderRoundTripFuzzTests
{
    const int SeedCount = 32;
    const int MaxColumnCount = 4;
    const int MinRowGroupCount = 1;
    const int MaxRowGroupCount = 3;
    const int MinRowCount = 1;
    const int MaxRowCount = 48;

    [Test]
    public void FuzzedFlatSchemasRoundTripThroughWriterAndReader()
    {
        for (var seed = 1; seed <= SeedCount; seed++)
            ExecuteRoundTrip(seed);
    }

    static void ExecuteRoundTrip(int seed)
    {
        var random = new DeterministicRng(seed);
        var specs = CreateColumnSpecs(random);
        var schema = new ParquetSchema(specs.Select(static s => s.Column).ToImmutableArray());
        var expected = CreateExpectedRowGroups(random, specs);
        var path = Path.Combine(Path.GetTempPath(), $"plank-reader-roundtrip-{seed}-{Guid.NewGuid():N}.parquet");
        try
        {
            WriteFile(schema, specs, expected, path);

            using var stream = File.OpenRead(path);
            using var reader = schema.CreateReader(stream);
            using var rowGroup = reader.CreateReusableRowGroupReader();
            var rowGroupIndex = 0;
            foreach (var token in reader.EnumerateRowGroups())
            {
                reader.OpenRowGroup(token, rowGroup);
                for (var columnIndex = 0; columnIndex < specs.Length; columnIndex++)
                {
                    var actual = ReadColumn(rowGroup, specs[columnIndex]);
                    AssertArraysEqual(seed, rowGroupIndex, specs[columnIndex], expected[rowGroupIndex][columnIndex], actual);
                }
                rowGroupIndex++;
            }

            if (rowGroupIndex != expected.Length)
                throw new InvalidOperationException(
                    $"Seed {seed} produced {expected.Length} row groups but reader enumerated {rowGroupIndex}.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    static ColumnSpec[] CreateColumnSpecs(DeterministicRng random)
    {
        var count = random.NextInt(1, MaxColumnCount + 1);
        var specs = new ColumnSpec[count];
        for (var i = 0; i < specs.Length; i++)
            specs[i] = CreateColumnSpec(random, i);
        return specs;
    }

    static ColumnSpec CreateColumnSpec(DeterministicRng random, int index)
    {
        return random.NextInt(0, 5) switch
        {
            0 => new ColumnSpec(
                new Column($"c{index}_i32", ParquetPhysicalType.Int32,
                    new ColumnOptions(encodings: ImmutableArray.Create(PickInt32Encoding(random)))),
                typeof(int)),
            1 => new ColumnSpec(
                new Column($"c{index}_i64", ParquetPhysicalType.Int64,
                    new ColumnOptions(encodings: ImmutableArray.Create(PickInt64Encoding(random)))),
                typeof(long)),
            2 => new ColumnSpec(
                new Column($"c{index}_dbl", ParquetPhysicalType.Double,
                    new ColumnOptions(encodings: ImmutableArray.Create(PickDoubleEncoding(random)))),
                typeof(double)),
            3 => new ColumnSpec(
                new Column($"c{index}_bin", ParquetPhysicalType.ByteArray,
                    new ColumnOptions(encodings: ImmutableArray.Create(PickByteArrayEncoding(random)))),
                typeof(byte[])),
            _ => new ColumnSpec(
                new Column($"c{index}_bool", ParquetPhysicalType.Boolean,
                    new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.Plain))),
                typeof(bool))
        };
    }

    static EncodingKind PickInt32Encoding(DeterministicRng random)
        => random.NextInt(0, 3) switch
        {
            0 => EncodingKind.Plain,
            1 => EncodingKind.DeltaBinaryPacked,
            _ => random.NextInt(0, 2) == 0 ? EncodingKind.PlainDictionary : EncodingKind.RleDictionary
        };

    static EncodingKind PickInt64Encoding(DeterministicRng random)
        => random.NextInt(0, 2) == 0 ? EncodingKind.Plain : EncodingKind.DeltaBinaryPacked;

    static EncodingKind PickDoubleEncoding(DeterministicRng random)
        => random.NextInt(0, 2) == 0 ? EncodingKind.Plain : EncodingKind.ByteStreamSplit;

    static EncodingKind PickByteArrayEncoding(DeterministicRng random)
        => random.NextInt(0, 3) switch
        {
            0 => EncodingKind.Plain,
            1 => EncodingKind.DeltaLengthByteArray,
            _ => EncodingKind.DeltaByteArray
        };

    static Array[][] CreateExpectedRowGroups(DeterministicRng random, ColumnSpec[] specs)
    {
        var rowGroupCount = random.NextInt(MinRowGroupCount, MaxRowGroupCount + 1);
        var rowGroups = new Array[rowGroupCount][];
        for (var rowGroupIndex = 0; rowGroupIndex < rowGroups.Length; rowGroupIndex++)
        {
            var rowCount = random.NextInt(MinRowCount, MaxRowCount + 1);
            var columns = new Array[specs.Length];
            for (var columnIndex = 0; columnIndex < specs.Length; columnIndex++)
                columns[columnIndex] = CreateValues(random, specs[columnIndex], rowCount);
            rowGroups[rowGroupIndex] = columns;
        }

        return rowGroups;
    }

    static Array CreateValues(DeterministicRng random, ColumnSpec spec, int rowCount)
    {
        return spec.ClrType == typeof(int) ? CreateInt32Values(random, spec.Column.Options.Encodings[0], rowCount)
            : spec.ClrType == typeof(long) ? CreateInt64Values(random, spec.Column.Options.Encodings[0], rowCount)
            : spec.ClrType == typeof(double) ? CreateDoubleValues(random, spec.Column.Options.Encodings[0], rowCount)
            : spec.ClrType == typeof(byte[]) ? CreateByteArrayValues(random, spec.Column.Options.Encodings[0], rowCount)
            : CreateBooleanValues(random, rowCount);
    }

    static int[] CreateInt32Values(DeterministicRng random, EncodingKind encoding, int rowCount)
    {
        var values = new int[rowCount];
        var accumulator = random.NextInt(-5000, 5001);
        var dictionary = CreateInt32Dictionary(random);
        for (var i = 0; i < values.Length; i++)
            values[i] = encoding switch
            {
                EncodingKind.DeltaBinaryPacked => accumulator += random.NextInt(0, 11),
                EncodingKind.PlainDictionary or EncodingKind.RleDictionary => dictionary[random.NextInt(0, dictionary.Length)],
                _ => random.NextInt(-50_000, 50_001)
            };
        return values;
    }

    static long[] CreateInt64Values(DeterministicRng random, EncodingKind encoding, int rowCount)
    {
        var values = new long[rowCount];
        var accumulator = random.NextInt64(-500_000L, 500_001L);
        for (var i = 0; i < values.Length; i++)
            values[i] = encoding == EncodingKind.DeltaBinaryPacked
                ? accumulator += random.NextInt(0, 5000)
                : random.NextInt64(-8_000_000L, 8_000_001L);
        return values;
    }

    static double[] CreateDoubleValues(DeterministicRng random, EncodingKind encoding, int rowCount)
    {
        var values = new double[rowCount];
        for (var i = 0; i < values.Length; i++)
            values[i] = encoding == EncodingKind.ByteStreamSplit
                ? (random.NextInt(-1000, 1001) * 0.125) + random.NextDouble()
                : (random.NextDouble() - 0.5) * 100_000d;
        return values;
    }

    static byte[][] CreateByteArrayValues(DeterministicRng random, EncodingKind encoding, int rowCount)
    {
        var values = new byte[rowCount][];
        var prefix = CreateRandomBytes(random, minLength: 3, maxLength: 3);
        for (var i = 0; i < values.Length; i++)
            values[i] = encoding switch
            {
                EncodingKind.DeltaLengthByteArray => CreateRandomBytes(random, minLength: 0, maxLength: 18),
                EncodingKind.DeltaByteArray => CreateBytesWithPrefix(random, prefix, minSuffixLength: 0, maxSuffixLength: 9),
                _ => CreateRandomBytes(random, minLength: 0, maxLength: 18)
            };
        return values;
    }

    static bool[] CreateBooleanValues(DeterministicRng random, int rowCount)
    {
        var values = new bool[rowCount];
        for (var i = 0; i < values.Length; i++)
            values[i] = random.NextInt(0, 2) == 0;
        return values;
    }

    static int[] CreateInt32Dictionary(DeterministicRng random)
    {
        var values = new int[random.NextInt(2, 10)];
        for (var i = 0; i < values.Length; i++)
            values[i] = random.NextInt(-2048, 2049);
        return values;
    }

    static byte[] CreateRandomBytes(DeterministicRng random, int minLength, int maxLength)
    {
        var value = new byte[random.NextInt(minLength, maxLength + 1)];
        random.NextBytes(value);
        return value;
    }

    static byte[] CreateBytesWithPrefix(DeterministicRng random, byte[] prefix, int minSuffixLength, int maxSuffixLength)
    {
        var suffixLength = random.NextInt(minSuffixLength, maxSuffixLength + 1);
        var value = new byte[prefix.Length + suffixLength];
        prefix.CopyTo(value, 0);
        random.NextBytes(value.AsSpan(prefix.Length, suffixLength));
        return value;
    }

    static void WriteFile(ParquetSchema schema, ColumnSpec[] specs, Array[][] expected, string path)
    {
        using var stream = File.Create(path);
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None
        });
        var serializedColumns = specs.Select(CreateSerializedColumn).ToArray();

        for (var rowGroupIndex = 0; rowGroupIndex < expected.Length; rowGroupIndex++)
        {
            var rowGroup = writer.StartRowGroup();
            for (var columnIndex = 0; columnIndex < specs.Length; columnIndex++)
            {
                SerializeColumn(serializedColumns[columnIndex], expected[rowGroupIndex][columnIndex]);
                WriteColumn(rowGroup, serializedColumns[columnIndex]);
            }
        }

        writer.CloseFile();

        object CreateSerializedColumn(ColumnSpec spec)
            => spec.ClrType == typeof(int) ? writer.CreateSerializedColumn<int>(spec.Column)
            : spec.ClrType == typeof(long) ? writer.CreateSerializedColumn<long>(spec.Column)
            : spec.ClrType == typeof(double) ? writer.CreateSerializedColumn<double>(spec.Column)
            : spec.ClrType == typeof(byte[]) ? writer.CreateSerializedColumn<byte[]>(spec.Column)
            : writer.CreateSerializedColumn<bool>(spec.Column);
    }

    static void SerializeColumn(object serializedColumn, Array values)
    {
        switch (serializedColumn)
        {
            case SerializedColumn<int> typed:
                typed.Serialize((int[])values);
                return;
            case SerializedColumn<long> typed:
                typed.Serialize((long[])values);
                return;
            case SerializedColumn<double> typed:
                typed.Serialize((double[])values);
                return;
            case SerializedColumn<byte[]> typed:
                typed.Serialize((byte[][])values);
                return;
            case SerializedColumn<bool> typed:
                typed.Serialize((bool[])values);
                return;
            default:
                throw new InvalidOperationException($"Unsupported serialized column type '{serializedColumn.GetType()}'.");
        }
    }

    static void WriteColumn(RowGroupWriter rowGroup, object serializedColumn)
    {
        switch (serializedColumn)
        {
            case SerializedColumn<int> typed:
                rowGroup.Write(typed);
                return;
            case SerializedColumn<long> typed:
                rowGroup.Write(typed);
                return;
            case SerializedColumn<double> typed:
                rowGroup.Write(typed);
                return;
            case SerializedColumn<byte[]> typed:
                rowGroup.Write(typed);
                return;
            case SerializedColumn<bool> typed:
                rowGroup.Write(typed);
                return;
            default:
                throw new InvalidOperationException($"Unsupported serialized column type '{serializedColumn.GetType()}'.");
        }
    }

    static Array ReadColumn(RowGroupReader rowGroup, ColumnSpec spec)
        => spec.ClrType == typeof(int) ? ReadAllPages(rowGroup.Column<int>(spec.Column).Pages)
        : spec.ClrType == typeof(long) ? ReadAllPages(rowGroup.Column<long>(spec.Column).Pages)
        : spec.ClrType == typeof(double) ? ReadAllPages(rowGroup.Column<double>(spec.Column).Pages)
        : spec.ClrType == typeof(byte[]) ? ReadAllPages(rowGroup.Column<byte[]>(spec.Column).Pages)
        : ReadAllPages(rowGroup.Column<bool>(spec.Column).Pages);

    static void AssertArraysEqual(int seed, int rowGroupIndex, ColumnSpec spec, Array expected, Array actual)
    {
        if (expected.Length != actual.Length)
            throw new InvalidOperationException(
                $"Seed {seed}, row group {rowGroupIndex}, column '{spec.Column.Name}' ({Describe(spec)}) length mismatch. Expected {expected.Length}, got {actual.Length}.");

        if (spec.ClrType == typeof(byte[]))
        {
            var expectedBytes = (byte[][])expected;
            var actualBytes = (byte[][])actual;
            for (var i = 0; i < expectedBytes.Length; i++)
                if (!actualBytes[i].SequenceEqual(expectedBytes[i]))
                    throw new InvalidOperationException(
                        $"Seed {seed}, row group {rowGroupIndex}, column '{spec.Column.Name}' ({Describe(spec)}) byte[] mismatch at index {i}.");
            return;
        }

        for (var i = 0; i < expected.Length; i++)
            if (!Equals(actual.GetValue(i), expected.GetValue(i)))
                throw new InvalidOperationException(
                    $"Seed {seed}, row group {rowGroupIndex}, column '{spec.Column.Name}' ({Describe(spec)}) mismatch at index {i}. Expected '{expected.GetValue(i)}', got '{actual.GetValue(i)}'.");
    }

    static string Describe(ColumnSpec spec)
        => $"{spec.Column.PhysicalType}/{spec.Column.Options.Encodings[0]}";

    static T[] ReadAllPages<T>(ColumnPageEnumerable<T> pages)
    {
        var values = new List<T>();
        foreach (var page in pages)
            foreach (var value in page.Values.Span)
                values.Add(value);
        return values.ToArray();
    }

    readonly record struct ColumnSpec(Column Column, Type ClrType);

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
