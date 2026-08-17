using System.Collections.Immutable;
using Plank.Reading;
using Plank.Reading.Logical;
using Plank.RowApi;
using Plank.Schema;

namespace Plank.Fuzzing;

public static class PlankReaderFuzzTarget
{
    static readonly ParquetSchema[] Schemas = BuildSchemas();

    public static void Execute(ReadOnlySpan<byte> data)
    {
        try
        {
            Run(data.IsEmpty ? (byte)0 : data[0], data.IsEmpty ? [] : data[1..].ToArray());
        }
        catch (Exception ex) when (ex is CorruptParquetException or NotSupportedException or InvalidOperationException) { }
    }

    public static Exception? GetHandledException(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        try
        {
            Run(data.Length == 0 ? (byte)0 : data[0], data.Length == 0 ? [] : data[1..]);
            return null;
        }
        catch (Exception ex) when (ex is CorruptParquetException or NotSupportedException or InvalidOperationException)
        {
            return ex;
        }
    }

    static void Run(byte selector, byte[] fileBytes)
    {
        // Bit 7 routes the input to a decompressor instead of the Parquet reader.
        // It is a separate target sharing this one's harness and corpus, because
        // the codecs cannot be reached through a file: the corpus can only
        // contain codecs the writer can produce, and it cannot produce Lz4Legacy.
        // Checked before anything else so the payload is untouched by the
        // reader's own selector bits.
        if ((selector & 0x80) != 0)
        {
            PlankDecompressorFuzzTarget.Execute(fileBytes);
            return;
        }

        // Bit 6 picks the read source, and it decides more than the 54 lines of
        // StreamReadSource. ParquetFileReader.TryBorrowSource hard-tests for
        // MemoryReadSource, so with the memory source every page read borrows a
        // span straight out of the input — and the other side of both borrow
        // branches in ParquetPageCursor, the ones that rent a buffer and copy
        // into it, had never run. A stream source is also the only one that can
        // fail a read part-way, which is a different failure mode from a span
        // that is simply too short.
        using var source = (selector & 0x40) != 0
            ? new StreamReadSource(new MemoryStream(fileBytes, writable: false))
            : (IParquetReadSource)new MemoryReadSource(fileBytes);

        // Bits 4 and 5 pick the reader options. Both were pinned to their
        // defaults, and both defaults skip code:
        //
        // VerifyPageCrc defaults off, and with it off the reader never hashes a
        // page — ParquetCrc32 measured 0/54 lines, and the mismatch diagnostic
        // had never run either. The corpus now carries CRC-bearing seeds
        // (gen-crc-*), which are inert unless verification is on.
        //
        // Strict defaults on. Non-strict binding is a separate matching pass
        // over the requested schema against the file's, so half the projection
        // cases now take it.
        var options = new ParquetReaderOptions
        {
            VerifyPageCrc = (selector & 0x10) != 0,
            Strict = (selector & 0x20) == 0
        };

        // A quarter of the inputs go through the row-oriented API. It is a
        // separate public reader with its own buffering and projection, and it
        // measured 0% over a 14k-input corpus — neither fuzz target drove it.
        if ((selector & 3) == 2)
        {
            DrainRowApi(source, options.VerifyPageCrc, binaryAsFixedWidth: (selector & 4) != 0);
            return;
        }

        // Of the rest, half bind the file's own schema. Reading through a fixed
        // requested schema — the only thing this target used to do — can never
        // reach a decoder for a type that schema does not name, so FLOAT, INT96,
        // FIXED_LEN_BYTE_ARRAY, every logical type and every compression codec
        // the file declares were unreachable no matter how long it ran. The
        // other half keeps exercising the strict projection path, which is where
        // the requested schema is matched against the file's.
        using var reader = (selector & 1) == 0
            ? OpenWithFileSchema(source, options)
            : Schemas[(selector >> 1) % Schemas.Length].CreateReader(source, options);

        foreach (var group in reader.RowGroups)
            foreach (var column in reader.Schema.LeafColumns)
            {
                DrainMetadata(group, column);
                DrainColumn(group, column);
            }
    }

    // Draining values only ever exercised the value decoders. Everything a
    // caller reaches through the metadata APIs — page index, offset index,
    // column statistics, bloom filters — parses offsets and lengths straight
    // out of the footer and was never executed at all: PageMetadataReader and
    // BloomFilterReader measured 0% over a 14k-input corpus.
    static void DrainMetadata(RowGroup rowGroup, LeafColumn column)
    {
        var metadata = rowGroup.GetColumnMetadata(column);
        _ = metadata.ValueCount;
        _ = metadata.Compression;
        foreach (var encoding in metadata.Encodings)
            _ = encoding;

        var statistics = metadata.Statistics;
        Consume(statistics.Minimum);
        Consume(statistics.Maximum);
        _ = statistics.NullCount;

        using (var pages = metadata.OpenPages())
            for (var i = 0; i < pages.Count; i++)
            {
                var page = pages[i];
                _ = page.Offset;
                _ = page.CompressedSize;
                _ = page.FirstRowIndex;
                _ = page.RowCount;
                _ = page.IsNullPage;
                Consume(page.Statistics.Minimum);
                Consume(page.Statistics.Maximum);
            }

        if (!metadata.HasBloomFilter)
            return;
        var bloom = metadata.OpenBloomFilter();
        _ = bloom.BitsetSizeBytes;
        Consume(bloom.Bitset);
        _ = bloom.MightContain(0);
        _ = bloom.MightContain(long.MaxValue);
    }

    static void Consume(ReadOnlySpan<byte> bytes)
    {
        for (var i = 0; i < bytes.Length; i++)
            _ = bytes[i];
    }

    // RowReaderCore is normally reached through source-generated row types, but
    // it is public and takes the schema and descriptors directly, so the fuzzer
    // can drive it over whatever schema the file declares.
    static void DrainRowApi(IParquetReadSource source, bool verifyPageCrc, bool binaryAsFixedWidth)
    {
        ParquetSchema schema;
        using (var probe = new ParquetReader())
        {
            probe.Reset(source);
            schema = probe.Schema;
        }

        var descriptors = new RowApiColumnDescriptor[schema.LeafColumns.Length];
        for (var i = 0; i < descriptors.Length; i++)
        {
            var descriptor = CreateRowApiDescriptor(schema.LeafColumns[i], i, binaryAsFixedWidth);
            // A leaf the fuzzer cannot describe (nesting deeper than two levels,
            // or a repeated binary leaf) would otherwise abort the whole file:
            // RowReaderCore requires one descriptor per leaf, in leaf order, so
            // there is no way to describe the rest of the columns without it.
            if (descriptor is null)
                return;
            descriptors[i] = descriptor;
        }

        // The row reader has its own page cursor and so its own CRC call sites,
        // separate from the columnar reader's.
        var rowOptions = verifyPageCrc ? new RowReaderOptions { VerifyPageCrc = true } : RowReaderOptions.Default;
        using var rows = new RowReaderCore(source, schema, descriptors, projection: null,
            rowOptions, schemaEvolution: null);
        while (rows.MoveNext())
            for (var i = 0; i < descriptors.Length; i++)
                ReadCurrent(rows, descriptors[i]);
    }

    // Returns null when the leaf has no describable shape, which the caller
    // treats as "skip this file's row-API pass".
    static RowApiColumnDescriptor? CreateRowApiDescriptor(LeafColumn leaf, int index, bool binaryAsFixedWidth)
    {
        var name = $"p{index}";

        // A repeated leaf needs a nested descriptor; a flat one is rejected, and
        // that rejection is an InvalidOperationException the target swallows —
        // which is why every nested file silently skipped its row-API pass and
        // RowApiNestedColumnReadState measured 0/282 lines.
        if (leaf.MaxRepetitionLevel > 0)
            return CreateNestedDescriptor(leaf, name);

        // A binary leaf has two legitimate descriptions and they reach different
        // read states, so selector bit 2 picks between them rather than the
        // target settling on one. Bit 2 because this path leaves it unused —
        // bits 1-3 only index the requested schema, which the row API ignores:
        //
        //   byte[] -> RowApiBinaryColumnReadState, the variable-length state.
        //             This is the one the target could never reach: it used to
        //             pass byte, whose CreateState builds a fixed-width state,
        //             so GetCurrentBinary threw "not a variable-length byte
        //             column" — caught and swallowed — and the whole row-API
        //             pass aborted on every binary file. 0/82 lines.
        //   byte   -> RowApiColumnReadState<byte>, the fixed-width state over
        //             the same column. Odd but supported, and it enumerates
        //             buffers through a different ColumnChunkReader entry point.
        return leaf.PhysicalType switch
        {
            ParquetPhysicalType.Boolean => new RowApiColumnDescriptor<bool>(name, leaf),
            ParquetPhysicalType.Int32 => new RowApiColumnDescriptor<int>(name, leaf),
            ParquetPhysicalType.Int64 => new RowApiColumnDescriptor<long>(name, leaf),
            ParquetPhysicalType.Float => new RowApiColumnDescriptor<float>(name, leaf),
            ParquetPhysicalType.Double => new RowApiColumnDescriptor<double>(name, leaf),
            _ => binaryAsFixedWidth
                ? new RowApiColumnDescriptor<byte>(name, leaf)
                : new RowApiColumnDescriptor<byte[]>(name, leaf)
        };
    }

    // The generated code computes its collection thresholds from the row type it
    // was generated for; the fuzzer only has the leaf, so it reconstructs them:
    // the innermost collection has an element at the leaf's maximum definition
    // level, and each enclosing one sits a level above. That is the correct
    // reading for the shapes the corpus generates, and for anything else it is
    // still a well-formed descriptor — a corrupt file can present any levels it
    // likes, which is the point.
    static RowApiColumnDescriptor? CreateNestedDescriptor(LeafColumn leaf, string name)
    {
        var repetition = leaf.MaxRepetitionLevel;
        var definition = leaf.MaxDefinitionLevel;

        // TShape has to be a closed generic, so only the depths spelled out here
        // are reachable. One and two cover every shape the corpus writes.
        if (repetition is not (1 or 2) || definition < repetition)
            return null;

        var levels = new RowApiCollectionLevel[repetition];
        for (var i = 0; i < repetition; i++)
        {
            var element = definition - (repetition - 1 - i);
            levels[i] = new RowApiCollectionLevel(i + 1, element - 1, element);
        }

        // A nested binary leaf decodes into byte[] dense elements — the read
        // state requires exactly that type and rejects anything else.
        return repetition == 1
            ? leaf.PhysicalType switch
            {
                ParquetPhysicalType.Boolean => new RowApiNestedColumnDescriptor<bool?[], bool>(name, leaf, levels),
                ParquetPhysicalType.Int32 => new RowApiNestedColumnDescriptor<int?[], int>(name, leaf, levels),
                ParquetPhysicalType.Int64 => new RowApiNestedColumnDescriptor<long?[], long>(name, leaf, levels),
                ParquetPhysicalType.Float => new RowApiNestedColumnDescriptor<float?[], float>(name, leaf, levels),
                ParquetPhysicalType.Double => new RowApiNestedColumnDescriptor<double?[], double>(name, leaf, levels),
                _ => new RowApiNestedColumnDescriptor<byte[][], byte[]>(name, leaf, levels)
            }
            : leaf.PhysicalType switch
            {
                ParquetPhysicalType.Boolean => new RowApiNestedColumnDescriptor<bool?[][], bool>(name, leaf, levels),
                ParquetPhysicalType.Int32 => new RowApiNestedColumnDescriptor<int?[][], int>(name, leaf, levels),
                ParquetPhysicalType.Int64 => new RowApiNestedColumnDescriptor<long?[][], long>(name, leaf, levels),
                ParquetPhysicalType.Float => new RowApiNestedColumnDescriptor<float?[][], float>(name, leaf, levels),
                ParquetPhysicalType.Double => new RowApiNestedColumnDescriptor<double?[][], double>(name, leaf, levels),
                _ => new RowApiNestedColumnDescriptor<byte[][][], byte[]>(name, leaf, levels)
            };
    }

    static void ReadCurrent(RowReaderCore rows, RowApiColumnDescriptor descriptor)
    {
        switch (descriptor)
        {
            case RowApiColumnDescriptor<bool> typed: _ = rows.GetCurrent(typed); break;
            case RowApiColumnDescriptor<int> typed: _ = rows.GetCurrent(typed); break;
            case RowApiColumnDescriptor<long> typed: _ = rows.GetCurrent(typed); break;
            case RowApiColumnDescriptor<float> typed: _ = rows.GetCurrent(typed); break;
            case RowApiColumnDescriptor<double> typed: _ = rows.GetCurrent(typed); break;
            case RowApiColumnDescriptor<byte[]> typed: Consume(rows.GetCurrentBinary(typed).Value); break;
            case RowApiColumnDescriptor<byte> typed: _ = rows.GetCurrent(typed); break;

            // The materialized shape is what the repetition-level bookkeeping
            // produces, so walking it is what checks that bookkeeping.
            case RowApiNestedColumnDescriptor<bool?[], bool> typed: Consume(rows.GetCurrentNested(typed)); break;
            case RowApiNestedColumnDescriptor<int?[], int> typed: Consume(rows.GetCurrentNested(typed)); break;
            case RowApiNestedColumnDescriptor<long?[], long> typed: Consume(rows.GetCurrentNested(typed)); break;
            case RowApiNestedColumnDescriptor<float?[], float> typed: Consume(rows.GetCurrentNested(typed)); break;
            case RowApiNestedColumnDescriptor<double?[], double> typed: Consume(rows.GetCurrentNested(typed)); break;

            case RowApiNestedColumnDescriptor<bool?[][], bool> typed: Consume(rows.GetCurrentNested(typed)); break;
            case RowApiNestedColumnDescriptor<int?[][], int> typed: Consume(rows.GetCurrentNested(typed)); break;
            case RowApiNestedColumnDescriptor<long?[][], long> typed: Consume(rows.GetCurrentNested(typed)); break;
            case RowApiNestedColumnDescriptor<float?[][], float> typed: Consume(rows.GetCurrentNested(typed)); break;
            case RowApiNestedColumnDescriptor<double?[][], double> typed: Consume(rows.GetCurrentNested(typed)); break;

            case RowApiNestedColumnDescriptor<byte[][], byte[]> typed: ConsumeBinaryShape(rows.GetCurrentNested(typed)); break;
            case RowApiNestedColumnDescriptor<byte[][][], byte[]> typed:
            {
                var shape = rows.GetCurrentNested(typed);
                if (shape is not null)
                    for (var i = 0; i < shape.Length; i++)
                        ConsumeBinaryShape(shape[i]);
                break;
            }
        }
    }

    static void ConsumeBinaryShape(byte[][]? shape)
    {
        if (shape is null)
            return;
        for (var i = 0; i < shape.Length; i++)
            Consume(shape[i]);
    }

    static void Consume(byte[]? value)
    {
        if (value is null)
            return;
        for (var i = 0; i < value.Length; i++)
            _ = value[i];
    }

    static void Consume<T>(T?[]? shape) where T : struct
    {
        if (shape is null)
            return;
        for (var i = 0; i < shape.Length; i++)
            _ = shape[i];
    }

    static void Consume<T>(T?[][]? shape) where T : struct
    {
        if (shape is null)
            return;
        for (var i = 0; i < shape.Length; i++)
            Consume(shape[i]);
    }

    static ParquetReader OpenWithFileSchema(IParquetReadSource source, ParquetReaderOptions options)
    {
        var reader = new ParquetReader(options);
        reader.Reset(source);
        return reader;
    }

    // Every physical type has to be drained through the CLR type the reader
    // accepts for it, and an optional column has to be read as a nullable, or
    // the reader rejects the call before decoding anything. Missing cases here
    // are silent: the column is skipped and its decoder never runs, which is how
    // FLOAT, INT96 and FIXED_LEN_BYTE_ARRAY went unfuzzed.
    static void DrainColumn(RowGroup rowGroup, LeafColumn column)
    {
        // A repeated leaf has to go through NestedColumn<T>; the flat API refuses
        // it. Skipping them left the repetition-level decoding — the fiddliest
        // bookkeeping in the format — entirely unexercised.
        if (column.MaxRepetitionLevel > 0)
        {
            DrainNestedColumn(rowGroup, column);
            return;
        }

        // Reading an annotated column as its physical type skips the conversion
        // entirely — that is why seeding logical-type files changed nothing on
        // its own, and why ParquetDecimalConverter stayed at 0% with decimal
        // files in the corpus. The logical type has to pick the CLR type.
        if (column.LogicalType is not null && TryDrainLogicalColumn(rowGroup, column))
            return;

        // A non-zero max definition level means the value can be absent, whether
        // because the leaf itself is optional or because an ancestor group is.
        var optional = column.MaxDefinitionLevel > 0;
        switch (column.PhysicalType)
        {
            case ParquetPhysicalType.Boolean:
                if (optional) DrainBuffers(rowGroup.Column<bool?>(column));
                else DrainBuffers(rowGroup.Column<bool>(column));
                break;
            case ParquetPhysicalType.Int32:
                if (optional) DrainBuffers(rowGroup.Column<int?>(column));
                else DrainBuffers(rowGroup.Column<int>(column));
                break;
            case ParquetPhysicalType.Int64:
                if (optional) DrainBuffers(rowGroup.Column<long?>(column));
                else DrainBuffers(rowGroup.Column<long>(column));
                break;
            case ParquetPhysicalType.Float:
                if (optional) DrainBuffers(rowGroup.Column<float?>(column));
                else DrainBuffers(rowGroup.Column<float>(column));
                break;
            case ParquetPhysicalType.Double:
                if (optional) DrainBuffers(rowGroup.Column<double?>(column));
                else DrainBuffers(rowGroup.Column<double>(column));
                break;
            // ByteArray, FixedLenByteArray and Int96 are all read as spans of bytes.
            case ParquetPhysicalType.ByteArray:
            case ParquetPhysicalType.FixedLenByteArray:
            case ParquetPhysicalType.Int96:
                DrainBinaryBuffers(rowGroup.Column<byte>(column));
                break;
        }
    }

    // Returns false when the annotation has no distinct CLR representation, so
    // the caller falls back to the physical type. A mismatch between the
    // annotation and the physical type is a corrupt file rather than a target
    // bug, so those surface as the exceptions the harness already expects.
    static bool TryDrainLogicalColumn(RowGroup rowGroup, LeafColumn column)
    {
        var optional = column.MaxDefinitionLevel > 0;
        switch (column.LogicalType)
        {
            case LogicalType.Date:
                if (optional) DrainBuffers(rowGroup.Column<DateOnly?>(column));
                else DrainBuffers(rowGroup.Column<DateOnly>(column));
                return true;
            case LogicalType.Time:
                if (optional) DrainBuffers(rowGroup.Column<TimeOnly?>(column));
                else DrainBuffers(rowGroup.Column<TimeOnly>(column));
                return true;
            case LogicalType.Timestamp:
                if (optional) DrainBuffers(rowGroup.Column<DateTime?>(column));
                else DrainBuffers(rowGroup.Column<DateTime>(column));
                return true;
            case LogicalType.Decimal:
                if (optional) DrainBuffers(rowGroup.Column<decimal?>(column));
                else DrainBuffers(rowGroup.Column<decimal>(column));
                return true;
            case LogicalType.Uuid:
                if (optional) DrainBuffers(rowGroup.Column<Guid?>(column));
                else DrainBuffers(rowGroup.Column<Guid>(column));
                return true;
            case LogicalType.Int { BitWidth: 8, IsSigned: false }:
                if (optional) DrainBuffers(rowGroup.Column<byte?>(column));
                else DrainBuffers(rowGroup.Column<byte>(column));
                return true;
            case LogicalType.Int { BitWidth: 16, IsSigned: false }:
                if (optional) DrainBuffers(rowGroup.Column<ushort?>(column));
                else DrainBuffers(rowGroup.Column<ushort>(column));
                return true;
            case LogicalType.Int { BitWidth: 32, IsSigned: false }:
                if (optional) DrainBuffers(rowGroup.Column<uint?>(column));
                else DrainBuffers(rowGroup.Column<uint>(column));
                return true;
            case LogicalType.Int { BitWidth: 64, IsSigned: false }:
                if (optional) DrainBuffers(rowGroup.Column<ulong?>(column));
                else DrainBuffers(rowGroup.Column<ulong>(column));
                return true;
            default:
                // String, Json, Bson, Enum and the rest stay byte spans, which
                // the physical path already drains.
                return false;
        }
    }

    static void DrainNestedColumn(RowGroup rowGroup, LeafColumn column)
    {
        switch (column.PhysicalType)
        {
            case ParquetPhysicalType.Boolean: DrainNested(rowGroup.NestedColumn<bool>(column)); break;
            case ParquetPhysicalType.Int32: DrainNested(rowGroup.NestedColumn<int>(column)); break;
            case ParquetPhysicalType.Int64: DrainNested(rowGroup.NestedColumn<long>(column)); break;
            case ParquetPhysicalType.Float: DrainNested(rowGroup.NestedColumn<float>(column)); break;
            case ParquetPhysicalType.Double: DrainNested(rowGroup.NestedColumn<double>(column)); break;
            case ParquetPhysicalType.ByteArray:
            case ParquetPhysicalType.FixedLenByteArray:
            case ParquetPhysicalType.Int96:
                DrainNested(rowGroup.NestedColumn<byte>(column));
                break;
        }
    }

    // The levels are the point: they are what says which row a value belongs to
    // and how deeply it nests, and they are decoded from the page rather than
    // the schema, so a corrupt file controls them.
    static void DrainNested<T>(NestedRowGroupColumn<T> buffers)
    {
        foreach (var buffer in buffers)
        {
            _ = buffer.RowCount;
            _ = buffer.StartsWithContinuation;
            var repetition = buffer.RepetitionLevels;
            for (var i = 0; i < repetition.Length; i++)
                _ = repetition[i];
            var definition = buffer.DefinitionLevels;
            for (var i = 0; i < definition.Length; i++)
                _ = definition[i];
            var values = buffer.Values.Values;
            for (var i = 0; i < values.Length; i++)
                _ = values[i];
        }
    }

    static void DrainBuffers<T>(RowGroupColumn<T> buffers)
    {
        foreach (var buffer in buffers)
        {
            var span = buffer.Values;
            for (var i = 0; i < span.Length; i++)
                _ = span[i];
        }
    }

    // Variable-length byte[] columns are read as RowGroupColumn<byte>: one span
    // per row rather than one flat value span per buffer. Touching every byte
    // is what exercises the offset/length bookkeeping we want fuzzed.
    static void DrainBinaryBuffers(RowGroupColumn<byte> buffers)
    {
        foreach (var buffer in buffers)
            for (var i = 0; i < buffer.Count; i++)
            {
                if (buffer.IsNull(i)) continue;
                var value = buffer.GetValue(i);
                for (var j = 0; j < value.Length; j++)
                    _ = value[j];
            }
    }

    static ParquetSchema[] BuildSchemas()
        =>
        [
            // 0: single int32 plain
            Schema(Col("c0", ParquetPhysicalType.Int32, EncodingKind.Plain)),
            // 1: single byte[] plain
            Schema(Col("c0", ParquetPhysicalType.ByteArray, EncodingKind.Plain)),
            // 2: int32 + bool
            Schema(Col("c0", ParquetPhysicalType.Int32, EncodingKind.DeltaBinaryPacked),
                   Col("c1", ParquetPhysicalType.Boolean, EncodingKind.Plain)),
            // 3: int64 + double
            Schema(Col("c0", ParquetPhysicalType.Int64, EncodingKind.Plain),
                   Col("c1", ParquetPhysicalType.Double, EncodingKind.Plain)),
            // 4: int32 rle-dict
            Schema(Col("c0", ParquetPhysicalType.Int32, EncodingKind.RleDictionary)),
            // 5: byte[] delta-length
            Schema(Col("c0", ParquetPhysicalType.ByteArray, EncodingKind.DeltaLengthByteArray)),
            // 6: all five types, plain
            Schema(Col("c0", ParquetPhysicalType.Boolean, EncodingKind.Plain),
                   Col("c1", ParquetPhysicalType.Int32, EncodingKind.Plain),
                   Col("c2", ParquetPhysicalType.Int64, EncodingKind.Plain),
                   Col("c3", ParquetPhysicalType.Double, EncodingKind.Plain),
                   Col("c4", ParquetPhysicalType.ByteArray, EncodingKind.Plain)),
            // 7: byte[] delta-byte-array
            Schema(Col("c0", ParquetPhysicalType.ByteArray, EncodingKind.DeltaByteArray)),
        ];

    static ParquetSchema Schema(params ColumnDefinition[] columns)
        => new(columns.ToImmutableArray());

    static ColumnDefinition Col(string name, ParquetPhysicalType type, EncodingKind encoding)
        => ColumnDefinition.Leaf(name, type, new ColumnOptions(encodings: ImmutableArray.Create(encoding)));
}
