using System.Buffers.Binary;
using System.Collections.Immutable;
using Plank.Reading;
using Plank.Reading.Physical;
using Plank.Reading.Logical;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.PageStrategy;

namespace Plank.Tests.Reading;

[NotInParallel]
internal sealed class ReaderAllocationTests
{
    // Snappier 1.3.1 creates per-operation decompressor state. Remove this budget when its reusable API ships.
    const long ManagedSnappyAllocationBudget = 80;

    static readonly CompressionKind[] _compressionKinds =
    [
        CompressionKind.None,
        CompressionKind.Snappy,
        CompressionKind.Gzip,
        CompressionKind.Zstd,
        CompressionKind.Lz4,
        CompressionKind.Brotli
    ];
    static readonly ParquetPagePruner _pagePruner =
        static (in ParquetDataPageMetadata page) => MaximumIsAtLeast6000(in page);

    static bool MaximumIsAtLeast6000(in ParquetDataPageMetadata page)
    {
        var statistics = page.Statistics;
        return !statistics.HasMaximum ||
            statistics.Maximum.Length != sizeof(int) ||
            BinaryPrimitives.ReadInt32LittleEndian(statistics.Maximum) >= 6000;
    }

    [Test]
    public void RowGroupIndexAccessDoesNotAllocateAfterWarmup()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.Leaf("Value", ParquetPhysicalType.Int32)
        ]);
        var path = CreateFile(schema, CreateValues(16));
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = schema.CreateReader(stream);
            for (var i = 0; i < 8; i++)
                _ = reader.RowGroups[0].RowCount;

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var before = GC.GetAllocatedBytesForCurrentThread();
            _ = reader.RowGroups[0].RowCount;
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            if (allocated != 0)
                throw new InvalidOperationException(
                    $"Expected zero allocations for row group index access but saw {allocated} bytes.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void RowGroupEnumerationDoesNotAllocateAfterWarmup()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.Leaf("Value", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.Plain)))
        ]);
        var path = CreateFile(schema, CreateValues(4096));
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new ParquetReader();
            reader.Reset(stream);
            for (var i = 0; i < 8; i++)
                _ = CountRowGroups(reader);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var before = GC.GetAllocatedBytesForCurrentThread();
            _ = CountRowGroups(reader);
            var after = GC.GetAllocatedBytesForCurrentThread();
            var allocated = after - before;

            if (allocated != 0)
                throw new InvalidOperationException(
                    $"Expected zero allocations for steady-state row group enumeration but saw {allocated} bytes.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void PhysicalReaderMetadataAndPageIterationDoNotAllocateAfterWarmup()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.Leaf("Value", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.Plain)))
        ]);
        var path = CreateFile(schema, CreateValues(4096), CompressionKind.None, writePageCrc: true);
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new ParquetFileReader(new ParquetFileReaderOptions { VerifyPageCrc = true });
            for (var i = 0; i < 8; i++)
            {
                reader.Reset(stream);
                _ = ReadPhysicalPayloadBytes(reader);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var before = GC.GetAllocatedBytesForCurrentThread();
            var bytes = ReadPhysicalPayloadBytes(reader);
            var after = GC.GetAllocatedBytesForCurrentThread();
            var allocated = after - before;

            if (bytes == 0)
                throw new InvalidOperationException("Expected at least one physical page payload.");
            if (allocated != 0)
                throw new InvalidOperationException(
                    $"Expected physical metadata access and page iteration to allocate zero bytes but saw {allocated} bytes.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void ColumnBufferEnumerationDoesNotAllocateAfterWarmup()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.Leaf("Value", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.Plain)))
        ]);
        var path = CreateFile(schema, CreateValues(4096));
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = schema.CreateReader(stream);
            var rowGroup = reader.RowGroups[0];
            for (var i = 0; i < 8; i++)
                _ = SumValues(rowGroup, schema.LeafColumns[0]);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var before = GC.GetAllocatedBytesForCurrentThread();
            _ = SumValues(rowGroup, schema.LeafColumns[0]);
            var after = GC.GetAllocatedBytesForCurrentThread();
            var allocated = after - before;

            if (allocated != 0)
                throw new InvalidOperationException(
                    $"Expected zero allocations for steady-state column buffer enumeration but saw {allocated} bytes.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void DecimalColumnBufferEnumerationDoesNotAllocateAfterWarmup()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.Leaf("Value", ParquetPhysicalType.FixedLenByteArray,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.ByteStreamSplit), typeLength: 8),
                new LogicalType.Decimal(18, 2))
        ]);
        var values = new decimal[4096];
        for (var i = 0; i < values.Length; i++)
            values[i] = (i - 2048) / 100m;
        var path = CreateFile(schema, values);
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = schema.CreateReader(stream);
            var rowGroup = reader.RowGroups[0];
            for (var i = 0; i < 8; i++)
                _ = SumDecimalValues(rowGroup, schema.LeafColumns[0]);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var before = GC.GetAllocatedBytesForCurrentThread();
            _ = SumDecimalValues(rowGroup, schema.LeafColumns[0]);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            if (allocated != 0)
                throw new InvalidOperationException(
                    $"Expected zero allocations for steady-state decimal enumeration but saw {allocated} bytes.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void PrunedColumnBufferEnumerationDoesNotAllocateAfterWarmup()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.Leaf("Value", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.Plain)))
        ]);
        var path = Path.Combine(Path.GetTempPath(), $"plank-reader-pruner-alloc-{Guid.NewGuid():N}.parquet");
        try
        {
            using (var output = File.Create(path))
            {
                var writer = schema.CreateWriter(output, new ParquetWriterOptions
                {
                    TargetDataPageSizeBytes = 128,
                    WritePageIndexes = true
                });
                var serialized = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);
                serialized.Serialize(CreateValues(4096));
                writer.StartRowGroup().Write(serialized);
                writer.CloseFile();
            }

            using var stream = File.OpenRead(path);
            using var reader = schema.CreateReader(stream, pagePruner: _pagePruner);
            var rowGroup = reader.RowGroups[0];
            for (var i = 0; i < 8; i++)
                _ = SumValues(rowGroup, schema.LeafColumns[0]);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var before = GC.GetAllocatedBytesForCurrentThread();
            var sum = SumValues(rowGroup, schema.LeafColumns[0]);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            if (sum == 0)
                throw new InvalidOperationException("Expected the page pruner to retain values.");
            if (allocated != 0)
                throw new InvalidOperationException(
                    $"Expected zero allocations for steady-state pruned enumeration but saw {allocated} bytes.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void PageMetadataEnumerationDoesNotAllocate()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.Leaf("Value", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.Plain)))
        ]);
        var path = CreateFile(schema, CreateValues(4096));
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = schema.CreateReader(stream);
            using var pages = reader.RowGroups[0].GetColumnMetadata(0).OpenPages();
            for (var i = 0; i < 8; i++)
                _ = CountPageMetadata(pages);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var before = GC.GetAllocatedBytesForCurrentThread();
            var count = CountPageMetadata(pages);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            if (count == 0)
                throw new InvalidOperationException("Expected page metadata.");
            if (allocated != 0)
                throw new InvalidOperationException(
                    $"Expected page metadata enumeration to allocate zero bytes but saw {allocated} bytes.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void DictionaryColumnBufferEnumerationDoesNotAllocateAfterWarmup()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.Leaf("Value", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.RleDictionary)),
                pageStrategy: ForceDictionaryPageStrategy.Shared)
        ]);
        var path = CreateFile(schema, CreateLowCardinalityValues(4096));
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = schema.CreateReader(stream);
            var rowGroup = reader.RowGroups[0];
            for (var i = 0; i < 8; i++)
                _ = SumValues(rowGroup, schema.LeafColumns[0]);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var before = GC.GetAllocatedBytesForCurrentThread();
            _ = SumValues(rowGroup, schema.LeafColumns[0]);
            var after = GC.GetAllocatedBytesForCurrentThread();
            var allocated = after - before;

            if (allocated != 0)
                throw new InvalidOperationException(
                    $"Expected zero allocations for steady-state dictionary column buffer enumeration but saw {allocated} bytes.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void DeltaBinaryPackedColumnBufferEnumerationDoesNotAllocateAfterWarmup()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.Leaf("Value", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.DeltaBinaryPacked)))
        ]);
        var path = CreateFile(schema, CreateValues(4096));
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = schema.CreateReader(stream);
            var rowGroup = reader.RowGroups[0];
            var firstBuffer = rowGroup.Column<int>(schema.LeafColumns[0]).GetEnumerator();
            if (!firstBuffer.MoveNext())
                throw new InvalidOperationException("Expected test data.");
            firstBuffer.Dispose();

            for (var i = 0; i < 8; i++)
                _ = SumValues(rowGroup, schema.LeafColumns[0]);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var before = GC.GetAllocatedBytesForCurrentThread();
            _ = SumValues(rowGroup, schema.LeafColumns[0]);
            var after = GC.GetAllocatedBytesForCurrentThread();
            var allocated = after - before;

            if (allocated != 0)
                throw new InvalidOperationException(
                    $"Expected zero allocations for steady-state delta binary packed column buffer enumeration but saw {allocated} bytes.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void BooleanRleColumnBufferEnumerationDoesNotAllocateAfterWarmup()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.Leaf("Value", ParquetPhysicalType.Boolean,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.Rle)))
        ]);
        var path = CreateFile(schema, CreateBooleanValues(4096));
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = schema.CreateReader(stream);
            var rowGroup = reader.RowGroups[0];

            for (var i = 0; i < 8; i++)
                _ = CountTrueValues(rowGroup, schema.LeafColumns[0]);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var before = GC.GetAllocatedBytesForCurrentThread();
            _ = CountTrueValues(rowGroup, schema.LeafColumns[0]);
            var after = GC.GetAllocatedBytesForCurrentThread();
            var allocated = after - before;

            if (allocated != 0)
                throw new InvalidOperationException(
                    $"Expected zero allocations for steady-state boolean RLE column buffer enumeration but saw {allocated} bytes.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void ByteStreamSplitColumnBufferEnumerationDoesNotAllocateAfterWarmup()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.Leaf("Value", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.ByteStreamSplit)))
        ]);
        var path = CreateFile(schema, CreateValues(4096));
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = schema.CreateReader(stream);
            var rowGroup = reader.RowGroups[0];

            for (var i = 0; i < 8; i++)
                _ = SumValues(rowGroup, schema.LeafColumns[0]);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var before = GC.GetAllocatedBytesForCurrentThread();
            _ = SumValues(rowGroup, schema.LeafColumns[0]);
            var after = GC.GetAllocatedBytesForCurrentThread();
            var allocated = after - before;

            if (allocated != 0)
                throw new InvalidOperationException(
                    $"Expected zero allocations for steady-state byte-stream-split column buffer enumeration but saw {allocated} bytes.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void CompressedColumnBufferEnumerationStaysWithinAllocationBudgetAfterWarmup()
    {
        var failures = new List<string>();
        for (var i = 0; i < _compressionKinds.Length; i++)
        {
            var compression = _compressionKinds[i];
            try
            {
                var allocated = MeasureCompressedColumnBufferEnumerationAllocations(compression);
                var budget = compression == CompressionKind.Snappy ? ManagedSnappyAllocationBudget : 0;
                if (allocated > budget)
                    failures.Add($"codec '{compression}' allocated {allocated} bytes with a {budget}-byte budget.");
            }
            catch (CorruptParquetException ex)
            {
                failures.Add($"codec '{compression}' threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        if (failures.Count > 0)
            throw new InvalidOperationException(
                $"Compressed column-buffer allocation budgets were exceeded. Failures: {string.Join(' ', failures)}");
    }

    [Test]
    public void DeltaLengthByteArrayColumnBufferEnumerationDoesNotAllocateAfterWarmup()
        => AssertByteArrayColumnBufferEnumerationDoesNotAllocateAfterWarmup(EncodingKind.DeltaLengthByteArray);

    [Test]
    public void DeltaByteArrayColumnBufferEnumerationDoesNotAllocateAfterWarmup()
        => AssertByteArrayColumnBufferEnumerationDoesNotAllocateAfterWarmup(EncodingKind.DeltaByteArray);

    [Test]
    public void OptionalPackedBinaryColumnEnumerationDoesNotAllocateAfterWarmup()
    {
        var encodings = new[]
        {
            EncodingKind.Plain,
            EncodingKind.DeltaLengthByteArray,
            EncodingKind.DeltaByteArray,
            EncodingKind.RleDictionary
        };
        foreach (var encoding in encodings)
            AssertByteArrayColumnBufferEnumerationDoesNotAllocateAfterWarmup(encoding, optional: true);
    }

    [Test]
    public void GeneratedRowReaderDoesNotAllocateBatchBuffersAfterWarmup()
    {
        var path = CreateFile(ReaderAllocationRowSchema.Schema, CreateValues(4096));
        try
        {
            var bytes = File.ReadAllBytes(path);
            var source = new MemoryReadSource(bytes);
            using var reader = ReaderAllocationRowSchema.CreateRowReader(source);
            for (var i = 0; i < 8; i++)
            {
                reader.Reset(source);
                _ = SumGeneratedRows(reader);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            reader.Reset(source);
            var before = GC.GetAllocatedBytesForCurrentThread();
            _ = SumGeneratedRows(reader);
            var after = GC.GetAllocatedBytesForCurrentThread();
            var allocated = after - before;

            if (allocated != 0)
                throw new InvalidOperationException(
                    $"Expected generated row reader steady-state iteration to allocate zero bytes but saw {allocated} bytes.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void GeneratedBinaryRowReaderDoesNotAllocateValuesAfterWarmup()
    {
        byte[]?[] values = CreateByteArrayValues(4096);
        for (var i = 0; i < values.Length; i += 7)
            values[i] = null;
        var path = CreateFile(ReaderAllocationBinaryRowSchema.Schema, values);
        try
        {
            var bytes = File.ReadAllBytes(path);
            var source = new MemoryReadSource(bytes);
            using var reader = ReaderAllocationBinaryRowSchema.CreateRowReader(source);
            for (var i = 0; i < 8; i++)
            {
                reader.Reset(source);
                _ = SumGeneratedBinaryRows(reader);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            reader.Reset(source);
            var before = GC.GetAllocatedBytesForCurrentThread();
            var byteCount = SumGeneratedBinaryRows(reader);
            var after = GC.GetAllocatedBytesForCurrentThread();
            var allocated = after - before;

            if (byteCount == 0)
                throw new InvalidOperationException("Expected generated binary values.");
            if (allocated != 0)
                throw new InvalidOperationException(
                    $"Expected generated binary row iteration to allocate zero bytes but saw {allocated} bytes.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    static string CreateFile(ParquetSchema schema, int[] values)
        => CreateFile(schema, values, CompressionKind.None);

    static string CreateFile(ParquetSchema schema, int[] values, CompressionKind compression,
        bool writePageCrc = false)
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-reader-alloc-{Guid.NewGuid():N}.parquet");
        using var stream = File.Create(path);
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = compression,
            WritePageCrc = writePageCrc
        });
        var serialized = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);
        serialized.Serialize(values);
        writer.StartRowGroup().Write(serialized);
        writer.CloseFile();
        return path;
    }

    static string CreateFile(ParquetSchema schema, bool[] values)
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-reader-alloc-{Guid.NewGuid():N}.parquet");
        using var stream = File.Create(path);
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None
        });
        var serialized = writer.CreateSerializedColumn<bool>(schema.LeafColumns[0]);
        serialized.Serialize(values);
        writer.StartRowGroup().Write(serialized);
        writer.CloseFile();
        return path;
    }

    static string CreateFile(ParquetSchema schema, decimal[] values)
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-reader-decimal-alloc-{Guid.NewGuid():N}.parquet");
        using var stream = File.Create(path);
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None
        });
        var serialized = writer.CreateSerializedColumn<decimal>(schema.LeafColumns[0]);
        serialized.Serialize(values);
        writer.StartRowGroup().Write(serialized);
        writer.CloseFile();
        return path;
    }

    static string CreateFile(ParquetSchema schema, byte[]?[] values)
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-reader-alloc-{Guid.NewGuid():N}.parquet");
        using var stream = File.Create(path);
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None
        });
        var serialized = writer.CreateSerializedColumn<byte[]>(schema.LeafColumns[0]);
        serialized.Serialize((byte[][])(object)values);
        writer.StartRowGroup().Write(serialized);
        writer.CloseFile();
        return path;
    }

    static int SumValues(RowGroup rowGroup, LeafColumn column)
    {
        var sum = 0;
        foreach (var buffer in rowGroup.Column<int>(column))
            foreach (var value in buffer.Values)
                sum += value;
        return sum;
    }

    static int CountTrueValues(RowGroup rowGroup, LeafColumn column)
    {
        var count = 0;
        foreach (var buffer in rowGroup.Column<bool>(column))
            foreach (var value in buffer.Values)
                if (value)
                    count++;
        return count;
    }

    static decimal SumDecimalValues(RowGroup rowGroup, LeafColumn column)
    {
        var sum = 0m;
        foreach (var buffer in rowGroup.Column<decimal>(column))
            foreach (var value in buffer.Values)
                sum += value;
        return sum;
    }

    static int SumByteLengths(RowGroup rowGroup, LeafColumn column)
    {
        var sum = 0;
        foreach (var buffer in rowGroup.Column<byte>(column))
            for (var i = 0; i < buffer.Count; i++)
                sum += buffer.GetValue(i).Length;
        return sum;
    }

    static void AssertByteArrayColumnBufferEnumerationDoesNotAllocateAfterWarmup(EncodingKind encoding,
        bool optional = false)
    {
        var schema = new ParquetSchema([
            ColumnDefinition.Leaf("Value", ParquetPhysicalType.ByteArray,
                new ColumnOptions(optional ? ParquetRepetition.Optional : ParquetRepetition.Required,
                    ImmutableArray.Create(encoding)))
        ]);
        byte[]?[] values = CreateByteArrayValues(4096);
        if (optional)
            for (var i = 0; i < values.Length; i += 7)
                values[i] = null;
        var path = CreateFile(schema, values);
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = schema.CreateReader(stream);
            var rowGroup = reader.RowGroups[0];

            for (var i = 0; i < 8; i++)
                _ = SumByteLengths(rowGroup, schema.LeafColumns[0]);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var before = GC.GetAllocatedBytesForCurrentThread();
            _ = SumByteLengths(rowGroup, schema.LeafColumns[0]);
            var after = GC.GetAllocatedBytesForCurrentThread();
            var allocated = after - before;

            if (allocated != 0)
                throw new InvalidOperationException(
                    $"Expected zero allocations for steady-state {encoding} column buffer enumeration but saw {allocated} bytes.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    static int SumGeneratedRows(ReaderAllocationRowSchema.RowReader reader)
    {
        var sum = 0;
        while (reader.MoveNext())
            sum += reader.Current.Value;
        return sum;
    }

    static int SumGeneratedBinaryRows(ReaderAllocationBinaryRowSchema.RowReader reader)
    {
        var sum = 0;
        while (reader.MoveNext())
        {
            var row = reader.Current;
            if (!row.ValueIsNull)
                sum += row.Value.Length;
        }
        return sum;
    }

    static int ReadPhysicalPayloadBytes(ParquetFileReader reader)
    {
        var metadata = reader.Metadata;
        var column = metadata.ColumnSchema(0);
        var rowGroup = metadata.RowGroup(0);
        var total = metadata.ColumnPathSegmentUtf8(column.Ordinal, 0).Length;
        using var cursor = reader.OpenPages(rowGroup.Ordinal, 0);
        while (cursor.MoveNext())
            total += cursor.CurrentPayload.Length;
        return total;
    }

    static long MeasureCompressedColumnBufferEnumerationAllocations(CompressionKind compression)
    {
        var schema = new ParquetSchema([
            ColumnDefinition.Leaf("Value", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.Plain)))
        ]);
        var path = CreateFile(schema, CreateValues(4096), compression);
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = schema.CreateReader(stream);
            var rowGroup = reader.RowGroups[0];
            for (var i = 0; i < 8; i++)
                _ = SumValues(rowGroup, schema.LeafColumns[0]);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var before = GC.GetAllocatedBytesForCurrentThread();
            _ = SumValues(rowGroup, schema.LeafColumns[0]);
            var after = GC.GetAllocatedBytesForCurrentThread();
            return after - before;
        }
        finally
        {
            File.Delete(path);
        }
    }

    static int CountRowGroups(ParquetReader reader)
    {
        var count = 0;
        foreach (var _ in reader.RowGroups)
            count++;
        return count;
    }

    static int CountPageMetadata(ParquetDataPageMetadataCollection pages)
    {
        var count = 0;
        foreach (var page in pages)
            count += page.PageOrdinal + 1;
        return count;
    }

    static int[] CreateValues(int count)
    {
        var values = new int[count];
        for (var i = 0; i < values.Length; i++)
            values[i] = i * 3;
        return values;
    }

    static int[] CreateLowCardinalityValues(int count)
    {
        var values = new int[count];
        for (var i = 0; i < values.Length; i++)
            values[i] = i & 63;
        return values;
    }

    static bool[] CreateBooleanValues(int count)
    {
        var values = new bool[count];
        for (var i = 0; i < values.Length; i++)
            values[i] = (i & 1) == 0;
        return values;
    }

    static byte[][] CreateByteArrayValues(int count)
    {
        var values = new byte[count][];
        for (var i = 0; i < values.Length; i++)
            values[i] = [(byte)(i & 0xFF), (byte)((i >> 1) & 0xFF), (byte)((i >> 2) & 0xFF)];
        return values;
    }
}
