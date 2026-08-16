using System.Collections.Immutable;
using ParquetSharp;
using Plank.Reading;
using Plank.Reading.Logical;
using Plank.Schema;
using Plank.Writing;
using PlankColumn = Plank.Schema.ColumnDefinition;
using PlankReader = Plank.Reading.Logical.ParquetReader;
using PlankRowGroup = Plank.Reading.Logical.RowGroup;
using PlankRowGroupWriter = Plank.Writing.RowGroupWriter;
using PlankSchema = Plank.Schema.ParquetSchema;
using PlankWriter = Plank.Writing.ParquetWriter;

namespace Plank.Fuzzing;

public static class PlankWriterFuzzTarget
{
    const int MaxColumnCount = 5;
    const int MaxRowGroupCount = 3;
    const int MaxRowCount = 64;
    const int MaxByteArrayLength = 32;

    public static FuzzCase Decode(ReadOnlySpan<byte> data)
        => new Decoder(data).Decode();

    public static void Execute(ReadOnlySpan<byte> data)
        => Validate(Decode(data));

    public static void Validate(FuzzCase fuzzCase)
    {
        ArgumentNullException.ThrowIfNull(fuzzCase);
        using var ms = new MemoryStream();
        WriteToStream(fuzzCase, ms);

        // CloseFile() closes the stream it was writing to, so the written bytes
        // have to be taken from the MemoryStream rather than rewinding it.
        // (ToArray still works on a closed MemoryStream.)
        var bytes = ms.ToArray();
        AssertPlankCanRead(new MemoryStream(bytes, writable: false), fuzzCase);
        AssertParquetSharpCanRead(new MemoryStream(bytes, writable: false), fuzzCase);
    }

    static void WriteToStream(FuzzCase fuzzCase, Stream stream)
    {
        var writer = fuzzCase.Schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = fuzzCase.Compression
        });
        var serializedColumns = new object[fuzzCase.Columns.Count];
        for (var columnIndex = 0; columnIndex < serializedColumns.Length; columnIndex++)
            serializedColumns[columnIndex] = CreateSerializedColumn(writer, fuzzCase.Schema.LeafColumns[columnIndex],
                fuzzCase.Columns[columnIndex]);

        for (var rowGroupIndex = 0; rowGroupIndex < fuzzCase.RowGroups.Count; rowGroupIndex++)
        {
            var rowGroup = writer.StartRowGroup();
            for (var columnIndex = 0; columnIndex < fuzzCase.Columns.Count; columnIndex++)
            {
                SerializeColumn(serializedColumns[columnIndex], fuzzCase.RowGroups[rowGroupIndex][columnIndex]);
                WriteColumn(rowGroup, serializedColumns[columnIndex]);
            }
        }

        writer.CloseFile();
    }

    static object CreateSerializedColumn(PlankWriter writer, LeafColumn column, ColumnSpec spec)
        => spec.ClrType == typeof(bool) ? writer.CreateSerializedColumn<bool>(column)
        : spec.ClrType == typeof(bool?) ? writer.CreateSerializedColumn<bool?>(column)
        : spec.ClrType == typeof(int) ? writer.CreateSerializedColumn<int>(column)
        : spec.ClrType == typeof(int?) ? writer.CreateSerializedColumn<int?>(column)
        : spec.ClrType == typeof(long) ? writer.CreateSerializedColumn<long>(column)
        : spec.ClrType == typeof(long?) ? writer.CreateSerializedColumn<long?>(column)
        : spec.ClrType == typeof(double) ? writer.CreateSerializedColumn<double>(column)
        : spec.ClrType == typeof(double?) ? writer.CreateSerializedColumn<double?>(column)
        : writer.CreateSerializedColumn<byte[]>(column);

    static void SerializeColumn(object serializedColumn, Array values)
    {
        switch (serializedColumn)
        {
            case SerializedColumn<bool> typed:
                typed.Serialize((bool[])values);
                return;
            case SerializedColumn<bool?> typed:
                typed.Serialize((bool?[])values);
                return;
            case SerializedColumn<int> typed:
                typed.Serialize((int[])values);
                return;
            case SerializedColumn<int?> typed:
                typed.Serialize((int?[])values);
                return;
            case SerializedColumn<long> typed:
                typed.Serialize((long[])values);
                return;
            case SerializedColumn<long?> typed:
                typed.Serialize((long?[])values);
                return;
            case SerializedColumn<double> typed:
                typed.Serialize((double[])values);
                return;
            case SerializedColumn<double?> typed:
                typed.Serialize((double?[])values);
                return;
            case SerializedColumn<byte[]> typed:
                typed.Serialize((byte[][])values);
                return;
            default:
                throw new InvalidOperationException($"Unsupported serialized column type '{serializedColumn.GetType()}'.");
        }
    }

    static void WriteColumn(PlankRowGroupWriter rowGroup, object serializedColumn)
    {
        switch (serializedColumn)
        {
            case SerializedColumn<bool> typed:
                rowGroup.Write(typed);
                return;
            case SerializedColumn<bool?> typed:
                rowGroup.Write(typed);
                return;
            case SerializedColumn<int> typed:
                rowGroup.Write(typed);
                return;
            case SerializedColumn<int?> typed:
                rowGroup.Write(typed);
                return;
            case SerializedColumn<long> typed:
                rowGroup.Write(typed);
                return;
            case SerializedColumn<long?> typed:
                rowGroup.Write(typed);
                return;
            case SerializedColumn<double> typed:
                rowGroup.Write(typed);
                return;
            case SerializedColumn<double?> typed:
                rowGroup.Write(typed);
                return;
            case SerializedColumn<byte[]> typed:
                rowGroup.Write(typed);
                return;
            default:
                throw new InvalidOperationException($"Unsupported serialized column type '{serializedColumn.GetType()}'.");
        }
    }

    static void AssertPlankCanRead(Stream stream, FuzzCase fuzzCase)
    {
        using var reader = fuzzCase.Schema.CreateReader(stream);
        var rowGroupIndex = 0;
        foreach (var rowGroup in reader.RowGroups)
        {
            for (var columnIndex = 0; columnIndex < fuzzCase.Columns.Count; columnIndex++)
            {
                var actual = ReadPlankColumn(rowGroup, fuzzCase.Schema.LeafColumns[columnIndex],
                    fuzzCase.Columns[columnIndex]);
                AssertArraysEqual("Plank", fuzzCase, rowGroupIndex, columnIndex,
                    fuzzCase.RowGroups[rowGroupIndex][columnIndex], actual);
            }
            rowGroupIndex++;
        }

        if (rowGroupIndex != fuzzCase.RowGroups.Count)
            throw new InvalidOperationException(
                $"Plank row-group count mismatch. Expected {fuzzCase.RowGroups.Count}, got {rowGroupIndex}.");
    }

    static Array ReadPlankColumn(PlankRowGroup rowGroup, LeafColumn column, ColumnSpec spec)
        => spec.ClrType == typeof(bool) ? ReadAllBuffers(rowGroup.Column<bool>(column))
        : spec.ClrType == typeof(bool?) ? ReadAllBuffers(rowGroup.Column<bool?>(column))
        : spec.ClrType == typeof(int) ? ReadAllBuffers(rowGroup.Column<int>(column))
        : spec.ClrType == typeof(int?) ? ReadAllBuffers(rowGroup.Column<int?>(column))
        : spec.ClrType == typeof(long) ? ReadAllBuffers(rowGroup.Column<long>(column))
        : spec.ClrType == typeof(long?) ? ReadAllBuffers(rowGroup.Column<long?>(column))
        : spec.ClrType == typeof(double) ? ReadAllBuffers(rowGroup.Column<double>(column))
        : spec.ClrType == typeof(double?) ? ReadAllBuffers(rowGroup.Column<double?>(column))
        : ReadAllBinaryBuffers(rowGroup.Column<byte>(column));

    static void AssertParquetSharpCanRead(Stream stream, FuzzCase fuzzCase)
    {
        using var reader = new ParquetFileReader(stream, leaveOpen: true);
        var rowGroupCount = checked((int)reader.FileMetaData.NumRowGroups);
        if (rowGroupCount != fuzzCase.RowGroups.Count)
            throw new InvalidOperationException(
                $"ParquetSharp row-group count mismatch. Expected {fuzzCase.RowGroups.Count}, got {rowGroupCount}.");

        for (var rowGroupIndex = 0; rowGroupIndex < rowGroupCount; rowGroupIndex++)
        {
            using var rowGroup = reader.RowGroup(rowGroupIndex);
            var expectedRowCount = fuzzCase.RowGroups[rowGroupIndex][0].Length;
            var rowCount = checked((int)rowGroup.MetaData.NumRows);
            if (rowCount != expectedRowCount)
                throw new InvalidOperationException(
                    $"ParquetSharp row-group {rowGroupIndex} row count mismatch. Expected {expectedRowCount}, got {rowCount}.");

            for (var columnIndex = 0; columnIndex < fuzzCase.Columns.Count; columnIndex++)
            {
                var actual = ReadParquetSharpColumn(rowGroup, fuzzCase.Columns[columnIndex], rowCount, columnIndex);
                AssertArraysEqual("ParquetSharp", fuzzCase, rowGroupIndex, columnIndex,
                    fuzzCase.RowGroups[rowGroupIndex][columnIndex], actual);
            }
        }
    }

    static Array ReadParquetSharpColumn(ParquetSharp.RowGroupReader rowGroup, ColumnSpec spec, int rowCount,
        int columnIndex)
    {
        if (spec.ClrType == typeof(bool))
        {
            using var valueReader = rowGroup.Column(columnIndex).LogicalReader<bool>();
            return valueReader.ReadAll(rowCount);
        }

        if (spec.ClrType == typeof(bool?))
        {
            using var nullableReader = rowGroup.Column(columnIndex).LogicalReader<bool?>();
            return nullableReader.ReadAll(rowCount);
        }

        if (spec.ClrType == typeof(int))
        {
            using var valueReader = rowGroup.Column(columnIndex).LogicalReader<int>();
            return valueReader.ReadAll(rowCount);
        }

        if (spec.ClrType == typeof(int?))
        {
            using var nullableReader = rowGroup.Column(columnIndex).LogicalReader<int?>();
            return nullableReader.ReadAll(rowCount);
        }

        if (spec.ClrType == typeof(long))
        {
            using var valueReader = rowGroup.Column(columnIndex).LogicalReader<long>();
            return valueReader.ReadAll(rowCount);
        }

        if (spec.ClrType == typeof(long?))
        {
            using var nullableReader = rowGroup.Column(columnIndex).LogicalReader<long?>();
            return nullableReader.ReadAll(rowCount);
        }

        if (spec.ClrType == typeof(double))
        {
            using var valueReader = rowGroup.Column(columnIndex).LogicalReader<double>();
            return valueReader.ReadAll(rowCount);
        }

        if (spec.ClrType == typeof(double?))
        {
            using var nullableReader = rowGroup.Column(columnIndex).LogicalReader<double?>();
            return nullableReader.ReadAll(rowCount);
        }

        using var bytesReader = rowGroup.Column(columnIndex).LogicalReader<byte[]>();
        return bytesReader.ReadAll(rowCount);
    }

    static T[] ReadAllBuffers<T>(RowGroupColumn<T> buffers)
    {
        var values = new List<T>();
        foreach (var buffer in buffers)
            foreach (var value in buffer.Values)
                values.Add(value);
        return values.ToArray();
    }

    // Variable-length byte[] columns are read as RowGroupColumn<byte>, one
    // span per row, rather than as RowGroupColumn<byte[]>.
    // A null must come back as null, not as an empty array: an optional column can
    // legitimately hold both, and collapsing them would make the round-trip check
    // blind to a writer that confused the two.
    static byte[]?[] ReadAllBinaryBuffers(RowGroupColumn<byte> buffers)
    {
        var values = new List<byte[]?>();
        foreach (var buffer in buffers)
            for (var i = 0; i < buffer.Count; i++)
                values.Add(buffer.IsNull(i) ? null : buffer.GetValue(i).ToArray());
        return values.ToArray();
    }

    static void AssertArraysEqual(string readerName, FuzzCase fuzzCase, int rowGroupIndex, int columnIndex,
        Array expected, Array actual)
    {
        var spec = fuzzCase.Columns[columnIndex];
        if (expected.Length != actual.Length)
            throw new InvalidOperationException(
                $"{readerName} row-group {rowGroupIndex} column {columnIndex} '{spec.Column.Name}' ({spec.Describe()}) length mismatch. Expected {expected.Length}, got {actual.Length}.");

        if (spec.ClrType == typeof(byte[]))
        {
            AssertByteArraysEqual(readerName, spec, rowGroupIndex, columnIndex, (byte[]?[])expected, (byte[]?[])actual);
            return;
        }

        for (var rowIndex = 0; rowIndex < expected.Length; rowIndex++)
            if (!Equals(actual.GetValue(rowIndex), expected.GetValue(rowIndex)))
                throw new InvalidOperationException(
                    $"{readerName} row-group {rowGroupIndex} column {columnIndex} '{spec.Column.Name}' ({spec.Describe()}) value mismatch at row {rowIndex}. Expected '{expected.GetValue(rowIndex)}', got '{actual.GetValue(rowIndex)}'.");
    }

    static void AssertByteArraysEqual(string readerName, ColumnSpec spec, int rowGroupIndex, int columnIndex,
        byte[]?[] expected, byte[]?[] actual)
    {
        for (var rowIndex = 0; rowIndex < expected.Length; rowIndex++)
            if (expected[rowIndex] is null != actual[rowIndex] is null ||
                (expected[rowIndex] is not null && !actual[rowIndex].SequenceEqual(expected[rowIndex])))
                throw new InvalidOperationException(
                    $"{readerName} row-group {rowGroupIndex} column {columnIndex} '{spec.Column.Name}' ({spec.Describe()}) byte[] mismatch at row {rowIndex}.");
    }

    static ImmutableArray<EncodingKind> SingleEncoding(EncodingKind encoding)
        => ImmutableArray.Create(encoding);

    public sealed class FuzzCase
    {
        internal FuzzCase(ColumnSpec[] columns, Array[][] rowGroups, CompressionKind compression)
        {
            Columns = columns;
            RowGroups = rowGroups;
            Compression = compression;
            Schema = new PlankSchema(columns.Select(static c => c.Column).ToImmutableArray());
        }

        public IReadOnlyList<ColumnSpec> Columns { get; }

        public IReadOnlyList<IReadOnlyList<Array>> RowGroups { get; }

        public PlankSchema Schema { get; }

        public CompressionKind Compression { get; }

        public string Describe()
            => $"Columns=[{string.Join(", ", Columns.Select(static c => $"{c.Column.Name}:{c.Describe()}"))}], RowGroups={RowGroups.Count}, Compression={Compression}";
    }

    public readonly record struct ColumnSpec(PlankColumn Column, Type ClrType)
    {
        public EncodingKind Encoding
            => Column.Options!.Encodings[0];

        public bool Optional
            => Column.Options!.Repetition == ParquetRepetition.Optional;

        public string Describe()
            => $"{Column.PhysicalType}/{Encoding}{(Optional ? "/optional" : "")}" +
               $"{(Column.Options!.BloomFilter is null ? "" : "/bloom")}";
    }

    sealed class Decoder
    {
        readonly ByteCursor _cursor;

        public Decoder(ReadOnlySpan<byte> data)
            => _cursor = new ByteCursor(data);

        public FuzzCase Decode()
        {
            var compression = PickCompression();
            var columns = CreateColumns();
            var rowGroups = CreateRowGroups(columns);
            return new FuzzCase(columns, rowGroups, compression);
        }

        // Compression used to be pinned to None, which left every codec — and
        // the round-trip through it — outside anything this target could
        // generate. Lz4Legacy is excluded: the writer cannot produce it.
        CompressionKind PickCompression()
            => _cursor.NextInt(0, 6) switch
            {
                0 => CompressionKind.None,
                1 => CompressionKind.Snappy,
                2 => CompressionKind.Gzip,
                3 => CompressionKind.Zstd,
                4 => CompressionKind.Lz4,
                _ => CompressionKind.Brotli
            };

        ColumnSpec[] CreateColumns()
        {
            var count = _cursor.NextInt(1, MaxColumnCount + 1);
            var columns = new ColumnSpec[count];
            for (var columnIndex = 0; columnIndex < columns.Length; columnIndex++)
                columns[columnIndex] = CreateColumn(columnIndex);
            return columns;
        }

        ColumnSpec CreateColumn(int columnIndex)
            => _cursor.NextInt(0, 5) switch
            {
                0 => CreateBooleanColumn(columnIndex),
                1 => CreateInt32Column(columnIndex),
                2 => CreateInt64Column(columnIndex),
                3 => CreateDoubleColumn(columnIndex),
                _ => CreateByteArrayColumn(columnIndex)
            };

        // Optional columns are where definition levels, the nullable encode
        // paths and the statistics null counts live; the target generated none,
        // so ColumnStatistics and much of SerializedColumn were never written.
        bool NextOptional()
            => _cursor.NextInt(0, 2) == 0;

        // A bloom filter is a separate structure with its own footer offsets.
        // BloomFilterBuilder sat at 1.4% because nothing asked for one.
        ParquetBloomFilterOptions? NextBloomFilter(ParquetPhysicalType physicalType)
            => physicalType != ParquetPhysicalType.Boolean && _cursor.NextInt(0, 4) == 0
                ? ParquetBloomFilterOptions.Default
                : null;

        ColumnOptions Options(ParquetPhysicalType physicalType, EncodingKind encoding, bool optional)
            => new(optional ? ParquetRepetition.Optional : ParquetRepetition.Required,
                encodings: SingleEncoding(encoding), bloomFilter: NextBloomFilter(physicalType));

        ColumnSpec CreateBooleanColumn(int columnIndex)
        {
            var optional = NextOptional();
            return new ColumnSpec(Plank.Schema.ColumnDefinition.Leaf($"c{columnIndex}_bool",
                ParquetPhysicalType.Boolean, Options(ParquetPhysicalType.Boolean, EncodingKind.Plain, optional)),
                optional ? typeof(bool?) : typeof(bool));
        }

        ColumnSpec CreateInt32Column(int columnIndex)
        {
            var encoding = PickEncoding([
                EncodingKind.Plain,
                EncodingKind.DeltaBinaryPacked,
                EncodingKind.PlainDictionary,
                EncodingKind.RleDictionary
            ]);
            var optional = NextOptional();
            return new ColumnSpec(Plank.Schema.ColumnDefinition.Leaf($"c{columnIndex}_i32", ParquetPhysicalType.Int32,
                Options(ParquetPhysicalType.Int32, encoding, optional)), optional ? typeof(int?) : typeof(int));
        }

        ColumnSpec CreateInt64Column(int columnIndex)
        {
            var encoding = PickEncoding([
                EncodingKind.Plain,
                EncodingKind.DeltaBinaryPacked,
                EncodingKind.PlainDictionary,
                EncodingKind.RleDictionary
            ]);
            var optional = NextOptional();
            return new ColumnSpec(Plank.Schema.ColumnDefinition.Leaf($"c{columnIndex}_i64", ParquetPhysicalType.Int64,
                Options(ParquetPhysicalType.Int64, encoding, optional)), optional ? typeof(long?) : typeof(long));
        }

        ColumnSpec CreateDoubleColumn(int columnIndex)
        {
            var encoding = PickEncoding([
                EncodingKind.Plain,
                EncodingKind.ByteStreamSplit
            ]);
            var optional = NextOptional();
            return new ColumnSpec(Plank.Schema.ColumnDefinition.Leaf($"c{columnIndex}_dbl", ParquetPhysicalType.Double,
                Options(ParquetPhysicalType.Double, encoding, optional)), optional ? typeof(double?) : typeof(double));
        }

        ColumnSpec CreateByteArrayColumn(int columnIndex)
        {
            var encoding = PickEncoding([
                EncodingKind.Plain,
                EncodingKind.DeltaLengthByteArray,
                EncodingKind.DeltaByteArray
            ]);
            var optional = NextOptional();
            return new ColumnSpec(Plank.Schema.ColumnDefinition.Leaf($"c{columnIndex}_bin", ParquetPhysicalType.ByteArray,
                Options(ParquetPhysicalType.ByteArray, encoding, optional)), typeof(byte[]));
        }

        EncodingKind PickEncoding(ReadOnlySpan<EncodingKind> encodings)
            => encodings[_cursor.NextInt(0, encodings.Length)];

        Array[][] CreateRowGroups(ColumnSpec[] columns)
        {
            var rowGroupCount = _cursor.NextInt(1, MaxRowGroupCount + 1);
            var rowGroups = new Array[rowGroupCount][];
            for (var rowGroupIndex = 0; rowGroupIndex < rowGroups.Length; rowGroupIndex++)
            {
                var rowCount = _cursor.NextInt(1, MaxRowCount + 1);
                var rowGroup = new Array[columns.Length];
                for (var columnIndex = 0; columnIndex < columns.Length; columnIndex++)
                    rowGroup[columnIndex] = CreateValues(columns[columnIndex], rowCount);
                rowGroups[rowGroupIndex] = rowGroup;
            }
            return rowGroups;
        }

        Array CreateValues(ColumnSpec spec, int rowCount)
            => spec.ClrType == typeof(bool) ? CreateBooleanValues(rowCount)
            : spec.ClrType == typeof(bool?) ? Nullable(CreateBooleanValues(rowCount))
            : spec.ClrType == typeof(int) ? CreateInt32Values(spec.Encoding, rowCount)
            : spec.ClrType == typeof(int?) ? Nullable(CreateInt32Values(spec.Encoding, rowCount))
            : spec.ClrType == typeof(long) ? CreateInt64Values(spec.Encoding, rowCount)
            : spec.ClrType == typeof(long?) ? Nullable(CreateInt64Values(spec.Encoding, rowCount))
            : spec.ClrType == typeof(double) ? CreateDoubleValues(rowCount)
            : spec.ClrType == typeof(double?) ? Nullable(CreateDoubleValues(rowCount))
            : CreateByteArrayValues(spec.Encoding, rowCount, spec.Optional);

        // Punch holes in an already-generated column. Doing it here rather than
        // in each generator keeps the value distributions identical between the
        // required and optional cases, so the only difference under test is the
        // definition levels.
        TValue?[] Nullable<TValue>(TValue[] values) where TValue : struct
        {
            var result = new TValue?[values.Length];
            for (var i = 0; i < values.Length; i++)
                result[i] = _cursor.NextInt(0, 4) == 0 ? null : values[i];
            return result;
        }

        bool[] CreateBooleanValues(int rowCount)
        {
            var values = new bool[rowCount];
            for (var i = 0; i < values.Length; i++)
                values[i] = _cursor.NextInt(0, 2) == 0;
            return values;
        }

        int[] CreateInt32Values(EncodingKind encoding, int rowCount)
        {
            var values = new int[rowCount];
            var accumulator = _cursor.NextInt(-100_000, 100_001);
            var dictionary = CreateInt32Dictionary();
            for (var i = 0; i < values.Length; i++)
                values[i] = encoding switch
                {
                    EncodingKind.DeltaBinaryPacked => accumulator += _cursor.NextInt(-2, 11),
                    EncodingKind.PlainDictionary or EncodingKind.RleDictionary =>
                        dictionary[_cursor.NextInt(0, dictionary.Length)],
                    _ => _cursor.NextInt(-1_000_000, 1_000_001)
                };
            return values;
        }

        long[] CreateInt64Values(EncodingKind encoding, int rowCount)
        {
            var values = new long[rowCount];
            var accumulator = _cursor.NextInt64(-1_000_000L, 1_000_001L);
            var dictionary = CreateInt64Dictionary();
            for (var i = 0; i < values.Length; i++)
                values[i] = encoding switch
                {
                    EncodingKind.DeltaBinaryPacked => accumulator += _cursor.NextInt(-4, 8193),
                    EncodingKind.PlainDictionary or EncodingKind.RleDictionary =>
                        dictionary[_cursor.NextInt(0, dictionary.Length)],
                    _ => _cursor.NextInt64(-10_000_000_000L, 10_000_000_001L)
                };
            return values;
        }

        double[] CreateDoubleValues(int rowCount)
        {
            var values = new double[rowCount];
            for (var i = 0; i < values.Length; i++)
                values[i] = (_cursor.NextInt(-1_000_000, 1_000_001) / 128d) + _cursor.NextDouble();
            return values;
        }

        byte[][] CreateByteArrayValues(EncodingKind encoding, int rowCount, bool optional)
        {
            var values = new byte[rowCount][];
            var prefix = CreateRandomBytes(_cursor.NextInt(0, 7));
            for (var i = 0; i < values.Length; i++)
                values[i] = optional && _cursor.NextInt(0, 4) == 0 ? null! : encoding switch
                {
                    EncodingKind.DeltaByteArray => CreateBytesWithPrefix(prefix),
                    EncodingKind.DeltaLengthByteArray => CreateRandomBytes(_cursor.NextInt(0, MaxByteArrayLength + 1)),
                    _ => CreateRandomBytes(_cursor.NextInt(0, MaxByteArrayLength + 1))
                };
            return values;
        }

        int[] CreateInt32Dictionary()
        {
            var values = new int[_cursor.NextInt(1, 9)];
            for (var i = 0; i < values.Length; i++)
                values[i] = _cursor.NextInt(-4096, 4097);
            return values;
        }

        long[] CreateInt64Dictionary()
        {
            var values = new long[_cursor.NextInt(1, 9)];
            for (var i = 0; i < values.Length; i++)
                values[i] = _cursor.NextInt64(-1_000_000L, 1_000_001L);
            return values;
        }

        byte[] CreateBytesWithPrefix(byte[] prefix)
        {
            var suffix = CreateRandomBytes(_cursor.NextInt(0, MaxByteArrayLength + 1 - prefix.Length));
            var value = new byte[prefix.Length + suffix.Length];
            prefix.CopyTo(value, 0);
            suffix.CopyTo(value, prefix.Length);
            return value;
        }

        byte[] CreateRandomBytes(int length)
        {
            var value = new byte[length];
            _cursor.NextBytes(value);
            return value;
        }
    }

    sealed class ByteCursor
    {
        readonly byte[] _data;
        int _offset;

        public ByteCursor(ReadOnlySpan<byte> data)
            => _data = data.ToArray();

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

        public void NextBytes(Span<byte> buffer)
        {
            for (var i = 0; i < buffer.Length; i++)
                buffer[i] = NextByte();
        }

        byte NextByte()
            => _data.Length == 0 ? (byte)0 : _data[_offset++ % _data.Length];

        uint NextUInt32()
        {
            uint value = NextByte();
            value |= (uint)NextByte() << 8;
            value |= (uint)NextByte() << 16;
            value |= (uint)NextByte() << 24;
            return value;
        }

        ulong NextUInt64()
            => ((ulong)NextUInt32() << 32) | NextUInt32();
    }

}
