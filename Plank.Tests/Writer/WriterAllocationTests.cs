using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Writer;

[NotInParallel]
internal sealed class WriterAllocationTests
{
    // Snappier 1.3.1 creates per-operation compressor state. Remove this budget when its reusable API ships.
    const long ManagedSnappyAllocationBudget = 48;

    static readonly CompressionKind[] _compressionKinds =
    [
        CompressionKind.None,
        CompressionKind.Snappy,
        CompressionKind.Gzip,
        CompressionKind.Zstd,
        CompressionKind.Lz4,
        CompressionKind.Brotli
    ];
    static readonly ParquetDataPageVersion[] _dataPageVersions =
    [
        ParquetDataPageVersion.V1,
        ParquetDataPageVersion.V2
    ];

    [Test]
    public void NonDictionaryWriteChainDoesNotAllocateAfterWarmup()
    {
        var column = ColumnDefinition.Leaf("value", ParquetPhysicalType.Int32,
            new ColumnOptions(ParquetRepetition.Required, [EncodingKind.Plain]));
        var schema = new ParquetSchema([column]);
        using var stream = new MemoryStream(capacity: 1024 * 1024);
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None
        });
        var serialized = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);
        var values = CreateValues(4096);

        for (var i = 0; i < 8; i++)
            WriteOneRowGroup(writer, stream, serialized, values);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        WriteOneRowGroup(writer, stream, serialized, values);
        var after = GC.GetAllocatedBytesForCurrentThread();
        var allocated = after - before;

        if (allocated != 0)
            throw new InvalidOperationException(
                $"Expected zero allocations for steady-state non-dictionary write chain but saw {allocated} bytes.");
    }

    [Test]
    public void LargeNonDictionaryWriteChainDoesNotAllocateAfterWarmup()
    {
        var column = ColumnDefinition.Leaf("value", ParquetPhysicalType.Int32,
            new ColumnOptions(ParquetRepetition.Required, [EncodingKind.Plain]));
        var schema = new ParquetSchema([column]);
        using var stream = new MemoryStream(capacity: 8 * 1024 * 1024);
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None
        });
        var serialized = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);
        var values = CreateValues(1_000_000);

        for (var i = 0; i < 8; i++)
            WriteOneRowGroup(writer, stream, serialized, values);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        WriteOneRowGroup(writer, stream, serialized, values);
        var after = GC.GetAllocatedBytesForCurrentThread();
        var allocated = after - before;

        if (allocated != 0)
            throw new InvalidOperationException(
                $"Expected zero allocations for steady-state large non-dictionary write chain but saw {allocated} bytes.");
    }

    [Test]
    public void ByteArrayWriteChainDoesNotAllocateAfterWarmup()
    {
        var column = ColumnDefinition.Leaf("value", ParquetPhysicalType.ByteArray,
            new ColumnOptions(ParquetRepetition.Required, [EncodingKind.Plain]));
        var schema = new ParquetSchema([column]);
        using var stream = new MemoryStream(capacity: 8 * 1024 * 1024);
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None
        });
        var serialized = writer.CreateSerializedColumn<byte[]>(schema.LeafColumns[0]);
        var values = CreateByteArrayValues(4096);

        for (var i = 0; i < 8; i++)
            WriteOneRowGroup(writer, stream, serialized, values);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        WriteOneRowGroup(writer, stream, serialized, values);
        var after = GC.GetAllocatedBytesForCurrentThread();
        var allocated = after - before;

        if (allocated != 0)
            throw new InvalidOperationException(
                $"Expected zero allocations for steady-state byte array write chain but saw {allocated} bytes.");
    }

    [Test]
    public void DecimalWriteChainDoesNotAllocateAfterWarmup()
    {
        var column = ColumnDefinition.Leaf("value", ParquetPhysicalType.FixedLenByteArray,
            new ColumnOptions(ParquetRepetition.Required, [EncodingKind.Plain], typeLength: 8),
            new LogicalType.Decimal(18, 2));
        var schema = new ParquetSchema([column]);
        using var stream = new MemoryStream(capacity: 1024 * 1024);
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None
        });
        var serialized = writer.CreateSerializedColumn<decimal>(schema.LeafColumns[0]);
        var values = new decimal[4096];
        for (var i = 0; i < values.Length; i++)
            values[i] = (i - 2048) / 100m;

        for (var i = 0; i < 8; i++)
            WriteOneRowGroup(writer, stream, serialized, values);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        WriteOneRowGroup(writer, stream, serialized, values);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        if (allocated != 0)
            throw new InvalidOperationException(
                $"Expected zero allocations for steady-state decimal write chain but saw {allocated} bytes.");
    }

    [Test]
    public void CompressedWriteChainsStayWithinAllocationBudgetAfterWarmup()
    {
        var failures = new List<string>();
        for (var i = 0; i < _compressionKinds.Length; i++)
        {
            var compression = _compressionKinds[i];
            var allocated = MeasureCompressedWriteChainAllocations(compression);
            var budget = compression == CompressionKind.Snappy ? ManagedSnappyAllocationBudget : 0;
            if (allocated > budget)
                failures.Add($"codec '{compression}' allocated {allocated} bytes with a {budget}-byte budget.");
        }

        if (failures.Count > 0)
            throw new InvalidOperationException(
                $"Compressed write-chain allocation budgets were exceeded. Failures: {string.Join(' ', failures)}");
    }

    [Test]
    public void DataPageVersionsDoNotAllocateAfterWarmup()
    {
        var failures = new List<string>();
        for (var i = 0; i < _dataPageVersions.Length; i++)
        {
            var version = _dataPageVersions[i];
            var allocated = MeasureDataPageVersionAllocations(version);
            if (allocated != 0)
                failures.Add($"data page version '{version}' allocated {allocated} bytes.");
        }

        if (failures.Count > 0)
            throw new InvalidOperationException(
                $"Expected zero allocations for steady-state data page version writes. Failures: {string.Join(' ', failures)}");
    }

    [Test]
    public void PerColumnCompressionOverridesDoNotAllocateAfterWarmup()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("inherited", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: [EncodingKind.Plain])),
            ColumnDefinition.RequiredLeaf("overridden", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: [EncodingKind.Plain], compression: CompressionKind.Zstd,
                    compressionLevel: 3))
        ]);
        using var stream = new MemoryStream(capacity: 1024 * 1024);
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.Gzip,
            CompressionLevel = 1
        });
        var inherited = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);
        var overridden = writer.CreateSerializedColumn<int>(schema.LeafColumns[1]);
        var values = CreateValues(4096);

        for (var i = 0; i < 8; i++)
            WriteOneRowGroup(writer, stream, inherited, overridden, values);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        WriteOneRowGroup(writer, stream, inherited, overridden, values);
        var after = GC.GetAllocatedBytesForCurrentThread();
        var allocated = after - before;

        if (allocated != 0)
            throw new InvalidOperationException(
                $"Expected zero allocations for steady-state per-column compression overrides but saw {allocated} bytes.");
    }

    static void WriteOneRowGroup(ParquetWriter writer, MemoryStream stream, SerializedColumn<int> serialized, int[] values)
    {
        writer.Reset(stream);
        serialized.Serialize(values);
        writer.StartRowGroup().Write(serialized);
    }

    static void WriteOneRowGroup(ParquetWriter writer, MemoryStream stream, SerializedColumn<byte[]> serialized,
        byte[][] values)
    {
        writer.Reset(stream);
        serialized.Serialize(values);
        writer.StartRowGroup().Write(serialized);
    }

    static void WriteOneRowGroup(ParquetWriter writer, MemoryStream stream, SerializedColumn<int?> serialized,
        int?[] values)
    {
        writer.Reset(stream);
        serialized.Serialize(values);
        writer.StartRowGroup().Write(serialized);
    }

    static void WriteOneRowGroup(ParquetWriter writer, MemoryStream stream, SerializedColumn<int> first,
        SerializedColumn<int> second, int[] values)
    {
        writer.Reset(stream);
        first.Serialize(values);
        second.Serialize(values);
        var rowGroup = writer.StartRowGroup();
        rowGroup.Write(first);
        rowGroup.Write(second);
    }

    static void WriteOneRowGroup(ParquetWriter writer, MemoryStream stream, SerializedColumn<decimal> serialized,
        decimal[] values)
    {
        writer.Reset(stream);
        serialized.Serialize(values);
        writer.StartRowGroup().Write(serialized);
    }

    static long MeasureCompressedWriteChainAllocations(CompressionKind compression)
    {
        var column = ColumnDefinition.Leaf("value", ParquetPhysicalType.Int32,
            new ColumnOptions(ParquetRepetition.Required, [EncodingKind.Plain]));
        var schema = new ParquetSchema([column]);
        using var stream = new MemoryStream(capacity: 1024 * 1024);
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = compression,
            WritePageCrc = true
        });
        var serialized = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);
        var values = CreateValues(4096);

        for (var i = 0; i < 8; i++)
            WriteOneRowGroup(writer, stream, serialized, values);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        WriteOneRowGroup(writer, stream, serialized, values);
        var after = GC.GetAllocatedBytesForCurrentThread();
        return after - before;
    }

    static long MeasureDataPageVersionAllocations(ParquetDataPageVersion version)
    {
        var column = ColumnDefinition.Leaf("value", ParquetPhysicalType.Int32,
            new ColumnOptions(ParquetRepetition.Optional, [EncodingKind.Plain]));
        var schema = new ParquetSchema([column]);
        using var stream = new MemoryStream(capacity: 1024 * 1024);
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.Gzip,
            DataPageVersion = version
        });
        var serialized = writer.CreateSerializedColumn<int?>(schema.LeafColumns[0]);
        var values = CreateOptionalValues(4096);

        for (var i = 0; i < 8; i++)
            WriteOneRowGroup(writer, stream, serialized, values);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        WriteOneRowGroup(writer, stream, serialized, values);
        var after = GC.GetAllocatedBytesForCurrentThread();
        return after - before;
    }

    static int[] CreateValues(int count)
    {
        var result = new int[count];
        for (var i = 0; i < result.Length; i++)
            result[i] = i;
        return result;
    }

    static int?[] CreateOptionalValues(int count)
    {
        var result = new int?[count];
        for (var i = 0; i < result.Length; i++)
            result[i] = i % 7 == 0 ? null : i;
        return result;
    }

    static byte[][] CreateByteArrayValues(int count)
    {
        var result = new byte[count][];
        for (var i = 0; i < result.Length; i++)
            result[i] = System.Text.Encoding.UTF8.GetBytes($"val-{i % 2048}");
        return result;
    }
}
