using System.Collections.Immutable;
using System.Globalization;
using DuckDB.NET.Data;
using Plank.BloomFilters;
using Plank.Reading;
using Plank.Reading.Physical;
using Plank.Reading.Physical.Internal;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Writer;

[NotInParallel]
internal sealed class BloomFilterTests
{
    [Test]
    public void XxHash64MatchesPublishedVectors()
    {
        AssertHash([], 0xEF46DB3751D8E999UL);
        AssertHash("a"u8, 0xD24EC4F1A98C6E5BUL);
        AssertHash("abc"u8, 0x44BC2CF5AD770999UL);
        AssertHash("0123456789abcdef0123456789abcdef"u8, 0x642A94958E71E6C5UL);

        Span<byte> allByteValues = stackalloc byte[256];
        for (var i = 0; i < allByteValues.Length; i++)
            allByteValues[i] = (byte)i;
        AssertHash(allByteValues, 0x1FACBE8406CD904BUL);
    }

    [Test]
    public void SplitBlockFilterAcceptsAnyWholeNumberOfBlocks()
    {
        Span<byte> bitset = stackalloc byte[96];
        var hash = XxHash64.Hash("three blocks"u8);
        SplitBlockBloomFilter.InsertHash(bitset, hash);
        if (!SplitBlockBloomFilter.MightContainHash(bitset, hash))
            throw new InvalidOperationException("Three-block Bloom filter produced a false negative.");
    }

    [Test]
    public void WriterEmitsPerColumnBloomFiltersAndReaderQueriesThem()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("indexed", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.Plain),
                    bloomFilter: new ParquetBloomFilterOptions
                    {
                        FalsePositiveProbability = 0.001,
                        ExpectedDistinctValueCount = 100
                    })),
            ColumnDefinition.RequiredLeaf("plain", ParquetPhysicalType.Int32)
        ]);
        var bytes = WriteTwoColumnFile(schema, [1, 7, 42, 100], [2, 8, 43, 101]);

        using var reader = new ParquetFileReader();
        reader.Reset(new MemoryReadSource(bytes));
        var indexed = reader.Metadata.ColumnChunk(0, 0);
        var plain = reader.Metadata.ColumnChunk(0, 1);
        if (!indexed.HasBloomFilter || indexed.BloomFilterOffset == 0 || indexed.BloomFilterLength == 0)
            throw new InvalidOperationException("Configured column did not advertise a Bloom filter.");
        if (plain.HasBloomFilter || plain.BloomFilterOffset != 0 || plain.BloomFilterLength != 0)
            throw new InvalidOperationException("Unconfigured column unexpectedly advertised a Bloom filter.");
        if (indexed.BloomFilterOffset < plain.ChunkOffset + plain.TotalCompressedSize)
            throw new InvalidOperationException("Bloom filters were not written after the row group's column chunks.");

        using var filter = reader.OpenBloomFilter(0, 0);
        if (filter.BitsetSizeBytes != 256)
            throw new InvalidOperationException($"Expected a 256-byte bitset, got {filter.BitsetSizeBytes}.");
        if (indexed.BloomFilterLength <= filter.BitsetSizeBytes)
            throw new InvalidOperationException("Bloom-filter length did not include its Thrift header.");
        int[] present = [1, 7, 42, 100];
        for (var i = 0; i < present.Length; i++)
            if (!filter.MightContain(present[i]))
                throw new InvalidOperationException($"Bloom filter produced a false negative for {present[i]}.");
        if (filter.MightContain(987_654_321))
            throw new InvalidOperationException("Deterministic absent-value probe unexpectedly matched.");

        try
        {
            _ = reader.OpenBloomFilter(0, 1);
            throw new InvalidOperationException("Opening an absent Bloom filter did not fail.");
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("does not have", StringComparison.Ordinal))
        {
        }
    }

    [Test]
    public void OptionalBinaryAndRepeatedValuesHaveNoFalseNegatives()
    {
        var bloom = new ParquetBloomFilterOptions { FalsePositiveProbability = 0.001 };
        var schema = new ParquetSchema([
            ColumnDefinition.OptionalLeaf("binary", ParquetPhysicalType.ByteArray,
                new ColumnOptions(bloomFilter: bloom)),
            ColumnDefinition.List("numbers",
                ColumnDefinition.RequiredLeaf("ignored", ParquetPhysicalType.Int32,
                    new ColumnOptions(bloomFilter: bloom)))
        ]);
        var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream);
        var binary = writer.CreateSerializedColumn<byte[]>(schema.LeafColumns[0]);
        var repeated = writer.CreateSerializedColumn<int[]>(schema.LeafColumns[1]);
        binary.Serialize(["alpha"u8.ToArray(), null!, "omega"u8.ToArray()]);
        repeated.Serialize([[3, 5], [], [8, 13, 21]]);
        var rowGroup = writer.StartRowGroup();
        rowGroup.Write(binary);
        rowGroup.Write(repeated);
        writer.CloseFile();

        using var reader = new ParquetFileReader();
        reader.Reset(new MemoryReadSource(stream.ToArray()));
        using var binaryFilter = reader.OpenBloomFilter(0, 0);
        using var repeatedFilter = reader.OpenBloomFilter(0, 1);
        if (!binaryFilter.MightContain("alpha"u8) || !binaryFilter.MightContain("omega"u8))
            throw new InvalidOperationException("Binary Bloom filter produced a false negative.");
        int[] present = [3, 5, 8, 13, 21];
        for (var i = 0; i < present.Length; i++)
            if (!repeatedFilter.MightContain(present[i]))
                throw new InvalidOperationException($"Repeated Bloom filter produced a false negative for {present[i]}.");
    }

    [Test]
    public void PhysicalValueRepresentationsHaveNoFalseNegatives()
    {
        var bloom = ParquetBloomFilterOptions.Default;
        var schema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("long_value", ParquetPhysicalType.Int64,
                new ColumnOptions(bloomFilter: bloom)),
            ColumnDefinition.RequiredLeaf("float_value", ParquetPhysicalType.Float,
                new ColumnOptions(bloomFilter: bloom)),
            ColumnDefinition.RequiredLeaf("double_value", ParquetPhysicalType.Double,
                new ColumnOptions(bloomFilter: bloom)),
            ColumnDefinition.RequiredLeaf("unsigned_value", ParquetPhysicalType.Int32,
                new ColumnOptions(bloomFilter: bloom), new LogicalType.Int(32, false)),
            ColumnDefinition.RequiredLeaf("uuid", ParquetPhysicalType.FixedLenByteArray,
                new ColumnOptions(typeLength: 16, bloomFilter: bloom), new LogicalType.Uuid())
        ]);
        var firstGuid = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var secondGuid = Guid.Parse("ffeeddcc-bbaa-9988-7766-554433221100");
        var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream);
        var rowGroup = writer.StartRowGroup();
        WriteColumn(rowGroup, schema.LeafColumns[0], new long[] { long.MinValue, 42 });
        WriteColumn(rowGroup, schema.LeafColumns[1], new float[] { -1.25F, float.PositiveInfinity });
        WriteColumn(rowGroup, schema.LeafColumns[2], new double[] { Math.PI, -0D });
        WriteColumn(rowGroup, schema.LeafColumns[3], new uint[] { uint.MaxValue, 123 });
        WriteColumn(rowGroup, schema.LeafColumns[4], new[] { firstGuid, secondGuid });
        writer.CloseFile();

        using var reader = new ParquetFileReader();
        reader.Reset(new MemoryReadSource(stream.ToArray()));
        using var int64Filter = reader.OpenBloomFilter(0, 0);
        using var floatFilter = reader.OpenBloomFilter(0, 1);
        using var doubleFilter = reader.OpenBloomFilter(0, 2);
        using var unsignedFilter = reader.OpenBloomFilter(0, 3);
        using var uuidFilter = reader.OpenBloomFilter(0, 4);
        if (!int64Filter.MightContain(long.MinValue) || !int64Filter.MightContain(42L) ||
            !floatFilter.MightContain(-1.25F) || !floatFilter.MightContain(float.PositiveInfinity) ||
            !doubleFilter.MightContain(Math.PI) || !doubleFilter.MightContain(-0D) ||
            !unsignedFilter.MightContain(uint.MaxValue) || !unsignedFilter.MightContain(123U) ||
            !uuidFilter.MightContain(firstGuid) || !uuidFilter.MightContain(secondGuid))
            throw new InvalidOperationException("A physical value representation produced a Bloom-filter false negative.");
    }

    [Test]
    public void LogicalMetadataOpensBloomFilter()
    {
        var schema = CreateIntSchema();
        var bytes = WriteSingleColumnFile(schema, [10, 20, 30]);
        using var reader = schema.CreateReader(new MemoryReadSource(bytes));
        var metadata = reader.RowGroups[0].GetColumnMetadata(0);
        if (!metadata.HasBloomFilter)
            throw new InvalidOperationException("Logical metadata did not expose the Bloom filter.");
        using var filter = metadata.OpenBloomFilter();
        if (!filter.MightContain(20))
            throw new InvalidOperationException("Logical Bloom-filter lookup produced a false negative.");
    }

    [Test]
    public void ReusedSerializedColumnClearsAndResizesEachRowGroupFilter()
    {
        var schema = CreateIntSchema();
        var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream);
        var serialized = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);
        var many = Enumerable.Range(0, 1000).ToArray();
        serialized.Serialize(many);
        writer.StartRowGroup().Write(serialized);
        serialized.Serialize([99_999]);
        writer.StartRowGroup().Write(serialized);
        writer.CloseFile();

        using var reader = new ParquetFileReader();
        reader.Reset(new MemoryReadSource(stream.ToArray()));
        using var first = reader.OpenBloomFilter(0, 0);
        using var second = reader.OpenBloomFilter(1, 0);
        if (first.BitsetSizeBytes <= second.BitsetSizeBytes)
            throw new InvalidOperationException("Bloom-filter sizing was not recomputed for each row group.");
        if (!first.MightContain(123) || !second.MightContain(99_999))
            throw new InvalidOperationException("A reused serialized column produced a Bloom-filter false negative.");
        if (second.MightContain(123))
            throw new InvalidOperationException("The second row group's Bloom filter retained stale bits.");
    }

    [Test]
    public void SerializedColumnCannotOverwriteRetainedBloomFilter()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("indexed", ParquetPhysicalType.Int32,
                new ColumnOptions(bloomFilter: ParquetBloomFilterOptions.Default)),
            ColumnDefinition.RequiredLeaf("tail", ParquetPhysicalType.Int32)
        ]);
        var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream);
        var indexed = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);
        var tail = writer.CreateSerializedColumn<int>(schema.LeafColumns[1]);
        indexed.Serialize([1, 2]);
        tail.Serialize([3, 4]);
        var rowGroup = writer.StartRowGroup();
        rowGroup.Write(indexed);
        try
        {
            indexed.Serialize([99, 100]);
            throw new InvalidOperationException("Retained Bloom filter was overwritten.");
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("retained", StringComparison.Ordinal))
        {
        }

        rowGroup.Write(tail);
        indexed.Serialize([99, 100]);
        writer.CloseFile();
    }

    [Test]
    public void MalformedBloomFilterHeaderIsRejected()
    {
        var schema = CreateIntSchema();
        var bytes = WriteSingleColumnFile(schema, [1, 2, 3]);
        using (var metadataReader = new ParquetFileReader())
        {
            metadataReader.Reset(new MemoryReadSource(bytes));
            var offset = checked((int)metadataReader.Metadata.ColumnChunk(0, 0).BloomFilterOffset);
            bytes[offset] = 0x18;
        }

        using var reader = new ParquetFileReader();
        reader.Reset(new MemoryReadSource(bytes));
        try
        {
            using var _ = reader.OpenBloomFilter(0, 0);
        }
        catch (CorruptParquetException)
        {
            return;
        }

        throw new InvalidOperationException("Malformed Bloom-filter header was accepted.");
    }

    [Test]
    public void InvalidBloomFilterBitsetSizeIsRejected()
    {
        var schema = CreateIntSchema();
        var bytes = WriteSingleColumnFile(schema, [1, 2, 3]);
        using (var metadataReader = new ParquetFileReader())
        {
            metadataReader.Reset(new MemoryReadSource(bytes));
            var offset = checked((int)metadataReader.Metadata.ColumnChunk(0, 0).BloomFilterOffset);
            bytes[offset + 1] = 0x42; // Zig-zag encoded 33-byte bitset.
        }

        using var reader = new ParquetFileReader();
        reader.Reset(new MemoryReadSource(bytes));
        try
        {
            using var _ = reader.OpenBloomFilter(0, 0);
        }
        catch (CorruptParquetException)
        {
            return;
        }

        throw new InvalidOperationException("Invalid Bloom-filter bitset size was accepted.");
    }

    [Test]
    public void UnsupportedBloomFilterAlgorithmIsReported()
    {
        var schema = CreateIntSchema();
        var bytes = WriteSingleColumnFile(schema, [1, 2, 3]);
        using (var metadataReader = new ParquetFileReader())
        {
            metadataReader.Reset(new MemoryReadSource(bytes));
            var offset = checked((int)metadataReader.Metadata.ColumnChunk(0, 0).BloomFilterOffset);
            bytes[offset + 3] = 0x2C; // Unknown union alternative 2 containing an empty marker.
        }

        using var reader = new ParquetFileReader();
        reader.Reset(new MemoryReadSource(bytes));
        try
        {
            using var _ = reader.OpenBloomFilter(0, 0);
        }
        catch (NotSupportedException)
        {
            return;
        }

        throw new InvalidOperationException("Unsupported Bloom-filter algorithm was accepted.");
    }

    [Test]
    public void InconsistentBloomFilterMetadataLengthIsRejected()
    {
        var schema = CreateIntSchema();
        var bytes = WriteSingleColumnFile(schema, [1, 2, 3]);
        using var reader = new ParquetFileReader();
        reader.Reset(new MemoryReadSource(bytes));
        var chunk = reader.Metadata.ColumnChunk(0, 0);
        try
        {
            using var _ = BloomFilterReader.Open(reader,
                chunk with { BloomFilterLength = chunk.BloomFilterLength - 1 });
        }
        catch (CorruptParquetException)
        {
            return;
        }

        throw new InvalidOperationException("Inconsistent Bloom-filter metadata length was accepted.");
    }

    [Test]
    public void BloomFilterOptionsRejectInvalidConfigurationAndBooleanColumns()
    {
        AssertInvalidOptions(new ParquetBloomFilterOptions { FalsePositiveProbability = 0 });
        AssertInvalidOptions(new ParquetBloomFilterOptions { FalsePositiveProbability = 1 });
        AssertInvalidOptions(new ParquetBloomFilterOptions { ExpectedDistinctValueCount = 0 });
        AssertInvalidOptions(new ParquetBloomFilterOptions { MaximumBytes = 1000 });

        try
        {
            _ = new ParquetSchema([
                ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Boolean,
                    new ColumnOptions(bloomFilter: ParquetBloomFilterOptions.Default))
            ]);
        }
        catch (NotSupportedException)
        {
            return;
        }

        throw new InvalidOperationException("Boolean Bloom-filter configuration was accepted.");
    }

    [Test]
    public void BloomFilterWriteChainDoesNotAllocateAfterWarmup()
    {
        var schema = CreateIntSchema();
        using var stream = new MemoryStream(capacity: 1024 * 1024);
        var writer = schema.CreateWriter(stream);
        var serialized = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);
        var values = Enumerable.Range(0, 4096).ToArray();

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
                $"Expected zero allocations for the steady-state Bloom-filter write chain, saw {allocated} bytes.");
    }

    [Test]
    public void RepeatedBloomFilterWriteChainDoesNotAddAllocations()
    {
        var bloomSchema = new ParquetSchema([
            ColumnDefinition.List("numbers",
                ColumnDefinition.RequiredLeaf("ignored", ParquetPhysicalType.Int32,
                    new ColumnOptions(bloomFilter: ParquetBloomFilterOptions.Default)))
        ]);
        var plainSchema = new ParquetSchema([
            ColumnDefinition.List("numbers",
                ColumnDefinition.RequiredLeaf("ignored", ParquetPhysicalType.Int32))
        ]);
        using var bloomStream = new MemoryStream(capacity: 1024 * 1024);
        using var plainStream = new MemoryStream(capacity: 1024 * 1024);
        var bloomWriter = bloomSchema.CreateWriter(bloomStream);
        var plainWriter = plainSchema.CreateWriter(plainStream);
        var bloomSerialized = bloomWriter.CreateSerializedColumn<int[]>(bloomSchema.LeafColumns[0]);
        var plainSerialized = plainWriter.CreateSerializedColumn<int[]>(plainSchema.LeafColumns[0]);
        var values = new[] { new[] { 1, 2, 3 }, Array.Empty<int>(), new[] { 5, 8, 13 } };

        for (var i = 0; i < 8; i++)
        {
            WriteRepeatedRowGroup(bloomWriter, bloomStream, bloomSerialized, values);
            WriteRepeatedRowGroup(plainWriter, plainStream, plainSerialized, values);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        WriteRepeatedRowGroup(plainWriter, plainStream, plainSerialized, values);
        var plainAllocated = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        WriteRepeatedRowGroup(bloomWriter, bloomStream, bloomSerialized, values);
        var bloomAllocated = GC.GetAllocatedBytesForCurrentThread() - before;
        if (bloomAllocated != plainAllocated)
            throw new InvalidOperationException(
                $"Repeated Bloom-filter writes allocated {bloomAllocated} bytes versus {plainAllocated} without a filter.");
    }

    [Test]
    public void DuckDbReadsBloomFilteredFile()
    {
        var schema = CreateIntSchema();
        var bytes = WriteSingleColumnFile(schema, [1, 2, 3, 4]);
        var path = Path.Combine(Path.GetTempPath(), $"plank-bloom-{Guid.NewGuid():N}.parquet");
        File.WriteAllBytes(path, bytes);
        try
        {
            using var connection = new DuckDBConnection("Data Source=:memory:");
            connection.Open();
            using var command = connection.CreateCommand();
#pragma warning disable CA2100
            command.CommandText = $"SELECT sum(value)::BIGINT FROM read_parquet('{path.Replace("'", "''", StringComparison.Ordinal)}') WHERE value = 3";
#pragma warning restore CA2100
            var result = command.ExecuteScalar();
            if (Convert.ToInt64(result, CultureInfo.InvariantCulture) != 3)
                throw new InvalidOperationException("DuckDB did not read the Bloom-filtered file correctly.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    static ParquetSchema CreateIntSchema()
        => new([
            ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int32,
                new ColumnOptions(bloomFilter: new ParquetBloomFilterOptions
                {
                    FalsePositiveProbability = 0.001
                }))
        ]);

    static byte[] WriteSingleColumnFile(ParquetSchema schema, int[] values)
    {
        var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream);
        var serialized = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);
        serialized.Serialize(values);
        writer.StartRowGroup().Write(serialized);
        writer.CloseFile();
        return stream.ToArray();
    }

    static byte[] WriteTwoColumnFile(ParquetSchema schema, int[] first, int[] second)
    {
        var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream);
        var firstSerialized = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);
        var secondSerialized = writer.CreateSerializedColumn<int>(schema.LeafColumns[1]);
        firstSerialized.Serialize(first);
        secondSerialized.Serialize(second);
        var rowGroup = writer.StartRowGroup();
        rowGroup.Write(firstSerialized);
        rowGroup.Write(secondSerialized);
        writer.CloseFile();
        return stream.ToArray();
    }

    static void WriteOneRowGroup(ParquetWriter writer, MemoryStream stream, SerializedColumn<int> serialized,
        int[] values)
    {
        writer.Reset(stream);
        serialized.Serialize(values);
        writer.StartRowGroup().Write(serialized);
    }

    static void WriteRepeatedRowGroup(ParquetWriter writer, MemoryStream stream,
        SerializedColumn<int[]> serialized, int[][] values)
    {
        writer.Reset(stream);
        serialized.Serialize(values);
        writer.StartRowGroup().Write(serialized);
    }

    static void WriteColumn<T>(RowGroupWriter rowGroup, LeafColumn column, T[] values)
    {
        var serialized = rowGroup.CreateSerializedColumn<T>(column);
        serialized.Serialize(values);
        rowGroup.Write(serialized);
    }

    static void AssertHash(ReadOnlySpan<byte> value, ulong expected)
    {
        var actual = XxHash64.Hash(value);
        if (actual != expected)
            throw new InvalidOperationException($"XXH64 mismatch. Expected {expected:X16}, got {actual:X16}.");
    }

    static void AssertInvalidOptions(ParquetBloomFilterOptions bloom)
    {
        try
        {
            _ = new ColumnOptions(bloomFilter: bloom);
        }
        catch (ArgumentOutOfRangeException)
        {
            return;
        }

        throw new InvalidOperationException("Invalid Bloom-filter options were accepted.");
    }
}
