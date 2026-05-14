using System.Collections.Immutable;
using ParquetSharp;
using Plank.Reading;
using Plank.Schema;
using Plank.Writing;
using PlankColumn = Plank.Schema.Column;
using PlankReader = Plank.Reading.ParquetReader;
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

        var path = Path.Combine(Path.GetTempPath(), $"plank-sharpfuzz-{Guid.NewGuid():N}.parquet");
        var phase = "write";
        var succeeded = false;

        try
        {
            WriteFile(fuzzCase, path);
            phase = "plank-read";
            AssertPlankCanRead(path, fuzzCase);
            phase = "parquetsharp-read";
            AssertParquetSharpCanRead(path, fuzzCase);
            succeeded = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var pathDetail = ShouldPreserveFailureFile()
                ? $" File preserved at '{path}'."
                : $" Temp file was '{path}'.";
            throw new InvalidOperationException(
                $"Fuzz phase '{phase}' failed. {fuzzCase.Describe()}.{pathDetail}", ex);
        }
        finally
        {
            if ((succeeded || !ShouldPreserveFailureFile()) && File.Exists(path))
                File.Delete(path);
        }
    }

    static bool ShouldPreserveFailureFile()
        => string.Equals(Environment.GetEnvironmentVariable("PLANK_FUZZ_PRESERVE_FAILURES"), "1",
            StringComparison.Ordinal);

    static void WriteFile(FuzzCase fuzzCase, string path)
    {
        using var stream = File.Create(path);
        var writer = fuzzCase.Schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None
        });
        var serializedColumns = new object[fuzzCase.Columns.Count];
        for (var columnIndex = 0; columnIndex < serializedColumns.Length; columnIndex++)
            serializedColumns[columnIndex] = CreateSerializedColumn(writer, fuzzCase.Columns[columnIndex]);

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

    static object CreateSerializedColumn(PlankWriter writer, ColumnSpec spec)
        => spec.ClrType == typeof(bool) ? writer.CreateSerializedColumn<bool>(spec.Column)
        : spec.ClrType == typeof(int) ? writer.CreateSerializedColumn<int>(spec.Column)
        : spec.ClrType == typeof(long) ? writer.CreateSerializedColumn<long>(spec.Column)
        : spec.ClrType == typeof(double) ? writer.CreateSerializedColumn<double>(spec.Column)
        : writer.CreateSerializedColumn<byte[]>(spec.Column);

    static void SerializeColumn(object serializedColumn, Array values)
    {
        switch (serializedColumn)
        {
            case SerializedColumn<bool> typed:
                typed.Serialize((bool[])values);
                return;
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
            default:
                throw new InvalidOperationException($"Unsupported serialized column type '{serializedColumn.GetType()}'.");
        }
    }

    static void AssertPlankCanRead(string path, FuzzCase fuzzCase)
    {
        using var stream = File.OpenRead(path);
        using var reader = fuzzCase.Schema.CreateReader(stream);
        var tokens = EnumerateTokens(reader);
        if (tokens.Length != fuzzCase.RowGroups.Count)
            throw new InvalidOperationException(
                $"Plank row-group count mismatch. Expected {fuzzCase.RowGroups.Count}, got {tokens.Length}.");

        for (var rowGroupIndex = 0; rowGroupIndex < tokens.Length; rowGroupIndex++)
        {
            using var rowGroup = reader.OpenRowGroup(stream, tokens[rowGroupIndex]);
            for (var columnIndex = 0; columnIndex < fuzzCase.Columns.Count; columnIndex++)
            {
                var actual = ReadPlankColumn(rowGroup, fuzzCase.Columns[columnIndex]);
                AssertArraysEqual("Plank", fuzzCase, rowGroupIndex, columnIndex,
                    fuzzCase.RowGroups[rowGroupIndex][columnIndex], actual);
            }
        }
    }

    static RowGroupToken[] EnumerateTokens(PlankReader reader)
    {
        var tokens = new List<RowGroupToken>();
        foreach (var token in reader.EnumerateRowGroups())
            tokens.Add(token);
        return tokens.ToArray();
    }

    static Array ReadPlankColumn(Plank.Reading.RowGroupReader rowGroup, ColumnSpec spec)
        => spec.ClrType == typeof(bool) ? ReadAllPages(rowGroup.Column<bool>(spec.Column).Pages)
        : spec.ClrType == typeof(int) ? ReadAllPages(rowGroup.Column<int>(spec.Column).Pages)
        : spec.ClrType == typeof(long) ? ReadAllPages(rowGroup.Column<long>(spec.Column).Pages)
        : spec.ClrType == typeof(double) ? ReadAllPages(rowGroup.Column<double>(spec.Column).Pages)
        : ReadAllPages(rowGroup.Column<byte[]>(spec.Column).Pages);

    static void AssertParquetSharpCanRead(string path, FuzzCase fuzzCase)
    {
        using var reader = new ParquetFileReader(path);
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

        if (spec.ClrType == typeof(int))
        {
            using var valueReader = rowGroup.Column(columnIndex).LogicalReader<int>();
            return valueReader.ReadAll(rowCount);
        }

        if (spec.ClrType == typeof(long))
        {
            using var valueReader = rowGroup.Column(columnIndex).LogicalReader<long>();
            return valueReader.ReadAll(rowCount);
        }

        if (spec.ClrType == typeof(double))
        {
            using var valueReader = rowGroup.Column(columnIndex).LogicalReader<double>();
            return valueReader.ReadAll(rowCount);
        }

        using var bytesReader = rowGroup.Column(columnIndex).LogicalReader<byte[]>();
        return bytesReader.ReadAll(rowCount);
    }

    static T[] ReadAllPages<T>(ColumnPageEnumerable<T> pages)
    {
        var values = new List<T>();
        foreach (var page in pages)
            foreach (var value in page.Values.Span)
                values.Add(value);
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
            AssertByteArraysEqual(readerName, spec, rowGroupIndex, columnIndex, (byte[][])expected, (byte[][])actual);
            return;
        }

        for (var rowIndex = 0; rowIndex < expected.Length; rowIndex++)
            if (!Equals(actual.GetValue(rowIndex), expected.GetValue(rowIndex)))
                throw new InvalidOperationException(
                    $"{readerName} row-group {rowGroupIndex} column {columnIndex} '{spec.Column.Name}' ({spec.Describe()}) value mismatch at row {rowIndex}. Expected '{expected.GetValue(rowIndex)}', got '{actual.GetValue(rowIndex)}'.");
    }

    static void AssertByteArraysEqual(string readerName, ColumnSpec spec, int rowGroupIndex, int columnIndex,
        byte[][] expected, byte[][] actual)
    {
        for (var rowIndex = 0; rowIndex < expected.Length; rowIndex++)
            if (!actual[rowIndex].SequenceEqual(expected[rowIndex]))
                throw new InvalidOperationException(
                    $"{readerName} row-group {rowGroupIndex} column {columnIndex} '{spec.Column.Name}' ({spec.Describe()}) byte[] mismatch at row {rowIndex}.");
    }

    static ImmutableArray<EncodingKind> SingleEncoding(EncodingKind encoding)
        => ImmutableArray.Create(encoding);

    public sealed class FuzzCase
    {
        internal FuzzCase(ColumnSpec[] columns, Array[][] rowGroups)
        {
            Columns = columns;
            RowGroups = rowGroups;
            Schema = new PlankSchema(columns.Select(static c => c.Column).ToImmutableArray());
        }

        public IReadOnlyList<ColumnSpec> Columns { get; }

        public IReadOnlyList<IReadOnlyList<Array>> RowGroups { get; }

        public PlankSchema Schema { get; }

        public string Describe()
            => $"Columns=[{string.Join(", ", Columns.Select(static c => $"{c.Column.Name}:{c.Describe()}"))}], RowGroups={RowGroups.Count}";
    }

    public readonly record struct ColumnSpec(PlankColumn Column, Type ClrType)
    {
        public string Describe()
            => $"{Column.PhysicalType}/{Column.Options.Encodings[0]}";
    }

    sealed class Decoder
    {
        readonly ByteCursor _cursor;

        public Decoder(ReadOnlySpan<byte> data)
            => _cursor = new ByteCursor(data);

        public FuzzCase Decode()
        {
            var columns = CreateColumns();
            var rowGroups = CreateRowGroups(columns);
            return new FuzzCase(columns, rowGroups);
        }

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

        ColumnSpec CreateBooleanColumn(int columnIndex)
            => new(new PlankColumn($"c{columnIndex}_bool", ParquetPhysicalType.Boolean,
                new ColumnOptions(encodings: SingleEncoding(EncodingKind.Plain))), typeof(bool));

        ColumnSpec CreateInt32Column(int columnIndex)
        {
            var encoding = PickEncoding([
                EncodingKind.Plain,
                EncodingKind.DeltaBinaryPacked,
                EncodingKind.PlainDictionary,
                EncodingKind.RleDictionary
            ]);
            return new ColumnSpec(new PlankColumn($"c{columnIndex}_i32", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: SingleEncoding(encoding))), typeof(int));
        }

        ColumnSpec CreateInt64Column(int columnIndex)
        {
            var encoding = PickEncoding([
                EncodingKind.Plain,
                EncodingKind.DeltaBinaryPacked,
                EncodingKind.PlainDictionary,
                EncodingKind.RleDictionary
            ]);
            return new ColumnSpec(new PlankColumn($"c{columnIndex}_i64", ParquetPhysicalType.Int64,
                new ColumnOptions(encodings: SingleEncoding(encoding))), typeof(long));
        }

        ColumnSpec CreateDoubleColumn(int columnIndex)
        {
            var encoding = PickEncoding([
                EncodingKind.Plain,
                EncodingKind.ByteStreamSplit
            ]);
            return new ColumnSpec(new PlankColumn($"c{columnIndex}_dbl", ParquetPhysicalType.Double,
                new ColumnOptions(encodings: SingleEncoding(encoding))), typeof(double));
        }

        ColumnSpec CreateByteArrayColumn(int columnIndex)
        {
            var encoding = PickEncoding([
                EncodingKind.Plain,
                EncodingKind.DeltaLengthByteArray,
                EncodingKind.DeltaByteArray
            ]);
            return new ColumnSpec(new PlankColumn($"c{columnIndex}_bin", ParquetPhysicalType.ByteArray,
                new ColumnOptions(encodings: SingleEncoding(encoding))), typeof(byte[]));
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
            : spec.ClrType == typeof(int) ? CreateInt32Values(spec.Column.Options.Encodings[0], rowCount)
            : spec.ClrType == typeof(long) ? CreateInt64Values(spec.Column.Options.Encodings[0], rowCount)
            : spec.ClrType == typeof(double) ? CreateDoubleValues(rowCount)
            : CreateByteArrayValues(spec.Column.Options.Encodings[0], rowCount);

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

        byte[][] CreateByteArrayValues(EncodingKind encoding, int rowCount)
        {
            var values = new byte[rowCount][];
            var prefix = CreateRandomBytes(_cursor.NextInt(0, 7));
            for (var i = 0; i < values.Length; i++)
                values[i] = encoding switch
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
        uint _state;
        int _offset;

        public ByteCursor(ReadOnlySpan<byte> data)
        {
            _data = data.ToArray();
            _state = 2166136261U;
            for (var i = 0; i < _data.Length; i++)
                _state = (_state ^ _data[i]) * 16777619U;
            if (_state == 0)
                _state = 0x9E3779B9U;
        }

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
        {
            if (_offset < _data.Length)
                return _data[_offset++];

            return (byte)(NextFallbackUInt32() & 0xFF);
        }

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

        uint NextFallbackUInt32()
        {
            _state ^= _state << 13;
            _state ^= _state >> 17;
            _state ^= _state << 5;
            return _state;
        }
    }

}
