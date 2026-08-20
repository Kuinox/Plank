using System.Collections.Immutable;
using ParquetSharp;
using Plank.Reading;
using Plank.Reading.Logical;
using Plank.Schema;
using Plank.Writing;
using PlankColumn = Plank.Schema.ColumnDefinition;
using PlankDataPageVersion = Plank.Writing.ParquetDataPageVersion;
using PlankFileVersion = Plank.Writing.ParquetFileVersion;
using PlankKeyValueMetadata = Plank.Writing.ParquetKeyValueMetadata;
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
    const int MaxFixedLength = 16;

    public static FuzzCase Decode(ReadOnlySpan<byte> data)
        => new Decoder(data).Decode();

    // A leading 0xFF routes the case to the row-oriented writer instead. That
    // pipeline can only be driven through generated code, so it needs its own
    // target rather than another axis of the column spec: see
    // PlankRowApiFuzzTarget. The marker is a whole byte value rather than a bit
    // so the existing corpus keeps decoding exactly as it did — every saved case
    // whose first byte is not 0xFF still describes the same columns.
    const byte RowApiMarker = 0xFF;

    // 0xFE routes to the partitioned dataset writer, for the same reason: it is
    // reachable only through generated code, and it owns files rather than taking
    // a stream. See PlankDatasetFuzzTarget.
    const byte DatasetMarker = 0xFE;

    // 0xFD routes to the file merger: the one write path that does not
    // re-encode, splicing column chunks across and rewriting their offsets.
    const byte MergerMarker = 0xFD;

    // 0xFC inverts this target: Apache Arrow writes the file and Plank has to
    // read it. It lives behind the writer's marker rather than as a selector in
    // the reader target because its input is a plan for a file, not a file — see
    // PlankCrossWriterFuzzTarget.
    const byte CrossWriterMarker = 0xFC;

    public static void Execute(ReadOnlySpan<byte> data)
    {
        if (!data.IsEmpty && data[0] == RowApiMarker)
        {
            PlankRowApiFuzzTarget.Execute(data[1..]);
            return;
        }

        if (!data.IsEmpty && data[0] == DatasetMarker)
        {
            PlankDatasetFuzzTarget.Execute(data[1..]);
            return;
        }

        if (!data.IsEmpty && data[0] == MergerMarker)
        {
            PlankMergerFuzzTarget.Execute(data[1..]);
            return;
        }

        if (!data.IsEmpty && data[0] == CrossWriterMarker)
        {
            PlankCrossWriterFuzzTarget.Execute(data[1..]);
            return;
        }

        Validate(Decode(data));
    }

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
        var settings = fuzzCase.Settings;
        var writer = fuzzCase.Schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = fuzzCase.Compression,
            DataPageVersion = settings.DataPageVersion,
            WritePageCrc = settings.WritePageCrc,
            WritePageIndexes = settings.WritePageIndexes,
            FileVersion = settings.FileVersion,
            TargetDataPageSizeBytes = settings.TargetDataPageSizeBytes,
            BufferChunkSizeBytes = settings.BufferChunkSizeBytes,
            KeyValueMetadata = settings.WithKeyValueMetadata
                ? [new PlankKeyValueMetadata("fuzz", "1"), new PlankKeyValueMetadata("empty", "")]
                : []
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
        : spec.ClrType == typeof(float) ? writer.CreateSerializedColumn<float>(column)
        : spec.ClrType == typeof(float?) ? writer.CreateSerializedColumn<float?>(column)
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
            case SerializedColumn<float> typed:
                typed.Serialize((float[])values);
                return;
            case SerializedColumn<float?> typed:
                typed.Serialize((float?[])values);
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
            case SerializedColumn<float> typed:
                rowGroup.Write(typed);
                return;
            case SerializedColumn<float?> typed:
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
        : spec.ClrType == typeof(float) ? ReadAllBuffers(rowGroup.Column<float>(column))
        : spec.ClrType == typeof(float?) ? ReadAllBuffers(rowGroup.Column<float?>(column))
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
                if (!CanParquetSharpRead(fuzzCase.Columns[columnIndex]))
                    continue;
                var actual = ReadParquetSharpColumn(rowGroup, fuzzCase.Columns[columnIndex], rowCount, columnIndex);
                AssertArraysEqual("ParquetSharp", fuzzCase, rowGroupIndex, columnIndex,
                    fuzzCase.RowGroups[rowGroupIndex][columnIndex], actual);
            }
        }
    }

    // ParquetSharp is the second opinion on everything this target writes, so a
    // column it cannot represent has to be named rather than quietly mismatched.
    //
    // An unannotated FIXED_LEN_BYTE_ARRAY is the one case: its default
    // LogicalTypeFactory maps a descriptor to CLR types by logical type, and for
    // FLBA with no annotation it throws "unsupported logical type None with
    // physical type FixedLenByteArray". LogicalReaderOverride does not help — it
    // renames the element type but goes through the same mapping — and a custom
    // factory would additionally need a FixedLenByteArray-to-byte[] read
    // converter, which ParquetSharp does not ship. A hand-rolled oracle there
    // would be more likely to be wrong than to catch anything.
    //
    // These columns keep Plank's own round-trip check, which still compares every
    // value written against every value read. They lose only the
    // cross-implementation comparison.
    static bool CanParquetSharpRead(ColumnSpec spec)
        => spec.Column.PhysicalType != ParquetPhysicalType.FixedLenByteArray
            || spec.Column.LogicalType is not null;

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

        if (spec.ClrType == typeof(float))
        {
            using var valueReader = rowGroup.Column(columnIndex).LogicalReader<float>();
            return valueReader.ReadAll(rowCount);
        }

        if (spec.ClrType == typeof(float?))
        {
            using var nullableReader = rowGroup.Column(columnIndex).LogicalReader<float?>();
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
        internal FuzzCase(ColumnSpec[] columns, Array[][] rowGroups, CompressionKind compression,
            WriterSettings settings)
        {
            Columns = columns;
            RowGroups = rowGroups;
            Compression = compression;
            Settings = settings;
            Schema = new PlankSchema(columns.Select(static c => c.Column).ToImmutableArray());
        }

        public IReadOnlyList<ColumnSpec> Columns { get; }

        public IReadOnlyList<IReadOnlyList<Array>> RowGroups { get; }

        public PlankSchema Schema { get; }

        public CompressionKind Compression { get; }

        public WriterSettings Settings { get; }

        public string Describe()
            => $"Columns=[{string.Join(", ", Columns.Select(static c => $"{c.Column.Name}:{c.Describe()}"))}], "
               + $"RowGroups={RowGroups.Count}, Compression={Compression}, {Settings.Describe()}";
    }

    /// <summary>The writer options a case asks for, beyond the codec.</summary>
    /// <remarks>
    /// Every one of these was pinned to its default, and each default skips code.
    /// The page size matters most: it defaults to 1 MiB while a case writes at
    /// most 64 rows, so every column the target ever produced fit in a single
    /// page. Page splitting, per-page statistics, the page index and the
    /// multi-page read path were therefore unreachable no matter how long it ran.
    /// </remarks>
    public readonly record struct WriterSettings(
        PlankDataPageVersion DataPageVersion,
        bool WritePageCrc,
        bool WritePageIndexes,
        PlankFileVersion FileVersion,
        uint TargetDataPageSizeBytes,
        uint BufferChunkSizeBytes,
        bool WithKeyValueMetadata)
    {
        public string Describe()
            => $"{DataPageVersion}/{FileVersion}/page={TargetDataPageSizeBytes}B/chunk={BufferChunkSizeBytes}B"
               + $"{(WritePageCrc ? "/crc" : "")}{(WritePageIndexes ? "/pageindex" : "")}"
               + $"{(WithKeyValueMetadata ? "/kv" : "")}";
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
            var settings = PickWriterSettings();
            var columns = CreateColumns();
            var rowGroups = CreateRowGroups(columns);
            return new FuzzCase(columns, rowGroups, compression, settings);
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

        WriterSettings PickWriterSettings()
            => new(
                // V1 pages carry their levels inside the payload; V2 keeps them in
                // the header. Only V2 was ever written.
                DataPageVersion: _cursor.NextInt(0, 2) == 0
                    ? PlankDataPageVersion.V2
                    : PlankDataPageVersion.V1,
                WritePageCrc: _cursor.NextInt(0, 3) == 0,
                // Defaults to on, so the "no page index" footer shape was never
                // written and the reader never had to cope with its absence.
                WritePageIndexes: _cursor.NextInt(0, 4) != 0,
                FileVersion: _cursor.NextInt(0, 4) == 0
                    ? PlankFileVersion.V2
                    : PlankFileVersion.V1,
                // Small enough to force several pages out of a 64-row column, which
                // is the only way to reach page splitting and per-page statistics.
                // The large value keeps the single-page shape in rotation.
                TargetDataPageSizeBytes: _cursor.NextInt(0, 4) switch
                {
                    0 => 64,
                    1 => 256,
                    2 => 4096,
                    _ => 1024 * 1024
                },
                // Drives the buffer growth and segment-spanning paths in
                // BufferWriter, which a single large chunk never exercises.
                BufferChunkSizeBytes: _cursor.NextInt(0, 3) switch
                {
                    0 => 64,
                    1 => 1024,
                    _ => 64 * 1024
                },
                WithKeyValueMetadata: _cursor.NextInt(0, 4) == 0);

        ColumnSpec[] CreateColumns()
        {
            var count = _cursor.NextInt(1, MaxColumnCount + 1);
            var columns = new ColumnSpec[count];
            for (var columnIndex = 0; columnIndex < columns.Length; columnIndex++)
                columns[columnIndex] = CreateColumn(columnIndex);
            return columns;
        }

        // Five of the eight physical types were writable and three were not, so
        // the encoders and statistics for FLOAT and FIXED_LEN_BYTE_ARRAY were
        // never written by anything: PlainEncoding.WriteFloatValues 0/15,
        // WriteFixedLengthByteArrayValues 0/41, and every float path in
        // ColumnStatistics — the vectorized min/max included — flat at zero.
        ColumnSpec CreateColumn(int columnIndex)
            => _cursor.NextInt(0, 7) switch
            {
                0 => CreateBooleanColumn(columnIndex),
                1 => CreateInt32Column(columnIndex),
                2 => CreateInt64Column(columnIndex),
                3 => CreateDoubleColumn(columnIndex),
                4 => CreateFloatColumn(columnIndex),
                5 => CreateFixedLenByteArrayColumn(columnIndex),
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
                EncodingKind.ByteStreamSplit,
                EncodingKind.Alp
            ]);
            var optional = NextOptional();
            return new ColumnSpec(Plank.Schema.ColumnDefinition.Leaf($"c{columnIndex}_dbl", ParquetPhysicalType.Double,
                Options(ParquetPhysicalType.Double, encoding, optional)), optional ? typeof(double?) : typeof(double));
        }

        ColumnSpec CreateFloatColumn(int columnIndex)
        {
            var encoding = PickEncoding([
                EncodingKind.Plain,
                EncodingKind.ByteStreamSplit,
                EncodingKind.Alp
            ]);
            var optional = NextOptional();
            return new ColumnSpec(Plank.Schema.ColumnDefinition.Leaf($"c{columnIndex}_flt", ParquetPhysicalType.Float,
                Options(ParquetPhysicalType.Float, encoding, optional)), optional ? typeof(float?) : typeof(float));
        }

        // A fixed-length column is still byte[] on both sides, so it shares every
        // dispatch with the variable-length one; the width is what differs, and it
        // is fuzzed because the encoder's stride arithmetic depends on it. Widths
        // stay small: the point is the bookkeeping, not the payload.
        ColumnSpec CreateFixedLenByteArrayColumn(int columnIndex)
        {
            var encoding = PickEncoding([
                EncodingKind.Plain,
                EncodingKind.PlainDictionary,
                EncodingKind.RleDictionary
            ]);
            var optional = NextOptional();
            var length = (uint)_cursor.NextInt(1, MaxFixedLength + 1);
            var options = new ColumnOptions(
                optional ? ParquetRepetition.Optional : ParquetRepetition.Required,
                encodings: SingleEncoding(encoding),
                typeLength: length,
                bloomFilter: NextBloomFilter(ParquetPhysicalType.FixedLenByteArray));
            return new ColumnSpec(
                Plank.Schema.ColumnDefinition.Leaf($"c{columnIndex}_flba",
                    ParquetPhysicalType.FixedLenByteArray, options),
                typeof(byte[]));
        }

        ColumnSpec CreateByteArrayColumn(int columnIndex)
        {
            // The dictionary encodings were missing here while the int32, int64
            // and double columns all had them. A byte[] dictionary is a
            // different implementation from a fixed-width one — it hashes the
            // bytes with wyhash and compares them for equality, rather than
            // hashing a value type — so leaving them out left WyHashing (0/88)
            // and ByteArrayComparer (0/22) both entirely unwritten, and with
            // nothing writing them nothing read them back either.
            var encoding = PickEncoding([
                EncodingKind.Plain,
                EncodingKind.DeltaLengthByteArray,
                EncodingKind.DeltaByteArray,
                EncodingKind.PlainDictionary,
                EncodingKind.RleDictionary
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
            : spec.ClrType == typeof(double) ? CreateDoubleValues(spec.Encoding, rowCount)
            : spec.ClrType == typeof(double?) ? Nullable(CreateDoubleValues(spec.Encoding, rowCount))
            : spec.ClrType == typeof(float) ? CreateFloatValues(spec.Encoding, rowCount)
            : spec.ClrType == typeof(float?) ? Nullable(CreateFloatValues(spec.Encoding, rowCount))
            // Both byte[] columns land here; only the fixed-width one constrains
            // the length, and writing the wrong length is a caller error rather
            // than something the writer should have to survive.
            : spec.Column.PhysicalType == ParquetPhysicalType.FixedLenByteArray
                ? CreateFixedLengthValues(checked((int)spec.Column.Options!.TypeLength), rowCount, spec.Optional)
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

        double[] CreateDoubleValues(EncodingKind encoding, int rowCount)
        {
            var values = new double[rowCount];
            for (var i = 0; i < values.Length; i++)
                values[i] = _cursor.NextInt(0, 8) == 0
                    ? SpecialDouble()
                    : encoding == EncodingKind.Alp
                        ? _cursor.NextInt(-1_000_000, 1_000_001) / 100d
                        : (_cursor.NextInt(-1_000_000, 1_000_001) / 128d) + _cursor.NextDouble();
            return values;
        }

        float[] CreateFloatValues(EncodingKind encoding, int rowCount)
        {
            var values = new float[rowCount];
            for (var i = 0; i < values.Length; i++)
                values[i] = _cursor.NextInt(0, 8) == 0
                    ? (float)SpecialDouble()
                    : encoding == EncodingKind.Alp
                        ? _cursor.NextInt(-1_000_000, 1_000_001) / 100f
                        : (_cursor.NextInt(-1_000_000, 1_000_001) / 128f) + (float)_cursor.NextDouble();
            return values;
        }

        // Statistics track a minimum and a maximum, and these are the values that
        // make that ordering awkward: NaN is unordered against everything, the
        // infinities bound it, and negative zero compares equal to zero while
        // encoding differently. Every float and double value the target wrote was
        // an ordinary finite number, so the NaN and signed-zero branches in
        // ColumnStatistics — including the vectorized min/max — never ran.
        double SpecialDouble()
            => _cursor.NextInt(0, 6) switch
            {
                0 => double.NaN,
                1 => double.PositiveInfinity,
                2 => double.NegativeInfinity,
                3 => -0d,
                4 => double.Epsilon,
                _ => 0d
            };

        byte[]?[] CreateFixedLengthValues(int length, int rowCount, bool optional)
        {
            var values = new byte[]?[rowCount];
            // A dictionary over fixed-width values needs repeats for the same
            // reason the variable-width one does.
            var dictionary = new byte[_cursor.NextInt(1, 6)][];
            for (var i = 0; i < dictionary.Length; i++)
                dictionary[i] = CreateRandomBytes(length);
            for (var i = 0; i < values.Length; i++)
                values[i] = optional && _cursor.NextInt(0, 4) == 0
                    ? null
                    : dictionary[_cursor.NextInt(0, dictionary.Length)];
            return values;
        }

        byte[][] CreateByteArrayValues(EncodingKind encoding, int rowCount, bool optional)
        {
            var values = new byte[rowCount][];
            var prefix = CreateRandomBytes(_cursor.NextInt(0, 7));

            // A dictionary only forms if values repeat. All-distinct values make
            // one pointless and the writer correctly falls back to plain, which
            // is what asking for a dictionary encoding over random bytes gets
            // you — the encoding would be requested and never exercised. So the
            // dictionary cases draw from a small pool, and the pool size is
            // fuzzed too: it decides the index bit width, and a pool of one
            // collapses the whole column to a single entry.
            var dictionary = encoding is EncodingKind.PlainDictionary or EncodingKind.RleDictionary
                ? CreateByteArrayDictionary(_cursor.NextInt(1, 9))
                : null;

            for (var i = 0; i < values.Length; i++)
                values[i] = optional && _cursor.NextInt(0, 4) == 0 ? null! : encoding switch
                {
                    EncodingKind.DeltaByteArray => CreateBytesWithPrefix(prefix),
                    EncodingKind.DeltaLengthByteArray => CreateRandomBytes(_cursor.NextInt(0, MaxByteArrayLength + 1)),
                    EncodingKind.PlainDictionary or EncodingKind.RleDictionary =>
                        dictionary![_cursor.NextInt(0, dictionary.Length)],
                    _ => CreateRandomBytes(_cursor.NextInt(0, MaxByteArrayLength + 1))
                };
            return values;
        }

        // Entries share prefixes and include an empty one, because the hash and
        // the equality comparison are what a byte[] dictionary does differently
        // from a fixed-width one, and near-identical keys are what tests them.
        byte[][] CreateByteArrayDictionary(int count)
        {
            var entries = new byte[count][];
            var shared = CreateRandomBytes(_cursor.NextInt(0, 5));
            for (var i = 0; i < entries.Length; i++)
                entries[i] = i == 0 ? [] : CreateBytesWithPrefix(shared);
            return entries;
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
