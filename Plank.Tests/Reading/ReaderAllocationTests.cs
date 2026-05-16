using System.Collections.Immutable;
using Plank.Reading;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.PageStrategy;

namespace Plank.Tests.Reading;

internal sealed class ReaderAllocationTests
{
    [Test]
    public void ReaderResetDoesNotAllocateAfterWarmup()
    {
        var schema = new ParquetSchema([
            new Column("Value", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.Plain)))
        ]);
        var path = CreateFile(schema, CreateValues(4096));
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = schema.CreateReader(stream);

            for (var i = 0; i < 8; i++)
                reader.Reset(stream);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var before = GC.GetAllocatedBytesForCurrentThread();
            reader.Reset(stream);
            var after = GC.GetAllocatedBytesForCurrentThread();
            var allocated = after - before;

            if (allocated != 0)
                throw new InvalidOperationException(
                    $"Expected zero allocations for steady-state reader reset but saw {allocated} bytes.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void ColumnPageEnumerationDoesNotAllocateAfterWarmup()
    {
        var schema = new ParquetSchema([
            new Column("Value", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.Plain)))
        ]);
        var path = CreateFile(schema, CreateValues(4096));
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = schema.CreateReader(stream);
            var token = EnumerateTokens(reader)[0];
            using var rowGroup = reader.OpenRowGroup(stream, token);
            for (var i = 0; i < 8; i++)
                _ = SumValues(rowGroup, schema.Columns[0]);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var before = GC.GetAllocatedBytesForCurrentThread();
            _ = SumValues(rowGroup, schema.Columns[0]);
            var after = GC.GetAllocatedBytesForCurrentThread();
            var allocated = after - before;

            if (allocated != 0)
                throw new InvalidOperationException(
                    $"Expected zero allocations for steady-state column page enumeration but saw {allocated} bytes.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void DictionaryColumnPageEnumerationDoesNotAllocateAfterWarmup()
    {
        var schema = new ParquetSchema([
            new Column("Value", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.RleDictionary)))
        ])
        {
            PageStrategiesByColumnName = ImmutableDictionary<string, IPageStrategy>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add("Value", ForceDictionaryPageStrategy.Shared)
        };
        var path = CreateFile(schema, CreateLowCardinalityValues(4096));
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = schema.CreateReader(stream);
            var token = EnumerateTokens(reader)[0];
            using var rowGroup = reader.OpenRowGroup(stream, token);
            for (var i = 0; i < 8; i++)
                _ = SumValues(rowGroup, schema.Columns[0]);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var before = GC.GetAllocatedBytesForCurrentThread();
            _ = SumValues(rowGroup, schema.Columns[0]);
            var after = GC.GetAllocatedBytesForCurrentThread();
            var allocated = after - before;

            if (allocated != 0)
                throw new InvalidOperationException(
                    $"Expected zero allocations for steady-state dictionary column page enumeration but saw {allocated} bytes.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void DeltaBinaryPackedColumnPageEnumerationDoesNotAllocateAfterWarmup()
    {
        var schema = new ParquetSchema([
            new Column("Value", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.DeltaBinaryPacked)))
        ]);
        var path = CreateFile(schema, CreateValues(4096));
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = schema.CreateReader(stream);
            var token = EnumerateTokens(reader)[0];
            using var rowGroup = reader.OpenRowGroup(stream, token);
            var firstPage = rowGroup.Column<int>(schema.Columns[0]).Pages.GetEnumerator();
            if (!firstPage.MoveNext() || firstPage.Current.Encoding != EncodingKind.DeltaBinaryPacked)
                throw new InvalidOperationException("Expected delta binary packed test data.");
            firstPage.Dispose();

            for (var i = 0; i < 8; i++)
                _ = SumValues(rowGroup, schema.Columns[0]);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var before = GC.GetAllocatedBytesForCurrentThread();
            _ = SumValues(rowGroup, schema.Columns[0]);
            var after = GC.GetAllocatedBytesForCurrentThread();
            var allocated = after - before;

            if (allocated != 0)
                throw new InvalidOperationException(
                    $"Expected zero allocations for steady-state delta binary packed column page enumeration but saw {allocated} bytes.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void BooleanRleColumnPageEnumerationDoesNotAllocateAfterWarmup()
    {
        var schema = new ParquetSchema([
            new Column("Value", ParquetPhysicalType.Boolean,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.Rle)))
        ]);
        var path = CreateFile(schema, CreateBooleanValues(4096));
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = schema.CreateReader(stream);
            var token = EnumerateTokens(reader)[0];
            using var rowGroup = reader.OpenRowGroup(stream, token);

            for (var i = 0; i < 8; i++)
                _ = CountTrueValues(rowGroup, schema.Columns[0]);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var before = GC.GetAllocatedBytesForCurrentThread();
            _ = CountTrueValues(rowGroup, schema.Columns[0]);
            var after = GC.GetAllocatedBytesForCurrentThread();
            var allocated = after - before;

            if (allocated != 0)
                throw new InvalidOperationException(
                    $"Expected zero allocations for steady-state boolean RLE column page enumeration but saw {allocated} bytes.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void ByteStreamSplitColumnPageEnumerationDoesNotAllocateAfterWarmup()
    {
        var schema = new ParquetSchema([
            new Column("Value", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.ByteStreamSplit)))
        ]);
        var path = CreateFile(schema, CreateValues(4096));
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = schema.CreateReader(stream);
            var token = EnumerateTokens(reader)[0];
            using var rowGroup = reader.OpenRowGroup(stream, token);

            for (var i = 0; i < 8; i++)
                _ = SumValues(rowGroup, schema.Columns[0]);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var before = GC.GetAllocatedBytesForCurrentThread();
            _ = SumValues(rowGroup, schema.Columns[0]);
            var after = GC.GetAllocatedBytesForCurrentThread();
            var allocated = after - before;

            if (allocated != 0)
                throw new InvalidOperationException(
                    $"Expected zero allocations for steady-state byte-stream-split column page enumeration but saw {allocated} bytes.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void DeltaLengthByteArrayColumnPageEnumerationDoesNotAllocateAfterWarmup()
        => AssertByteArrayColumnPageEnumerationDoesNotAllocateAfterWarmup(EncodingKind.DeltaLengthByteArray);

    [Test]
    public void DeltaByteArrayColumnPageEnumerationDoesNotAllocateAfterWarmup()
        => AssertByteArrayColumnPageEnumerationDoesNotAllocateAfterWarmup(EncodingKind.DeltaByteArray);

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

    static string CreateFile(ParquetSchema schema, int[] values)
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-reader-alloc-{Guid.NewGuid():N}.parquet");
        using var stream = File.Create(path);
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None
        });
        var serialized = writer.CreateSerializedColumn<int>(schema.Columns[0]);
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
        var serialized = writer.CreateSerializedColumn<bool>(schema.Columns[0]);
        serialized.Serialize(values);
        writer.StartRowGroup().Write(serialized);
        writer.CloseFile();
        return path;
    }

    static string CreateFile(ParquetSchema schema, byte[][] values)
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-reader-alloc-{Guid.NewGuid():N}.parquet");
        using var stream = File.Create(path);
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None
        });
        var serialized = writer.CreateSerializedColumn<byte[]>(schema.Columns[0]);
        serialized.Serialize(values);
        writer.StartRowGroup().Write(serialized);
        writer.CloseFile();
        return path;
    }

    static int SumValues(RowGroupReader rowGroup, Column column)
    {
        var sum = 0;
        foreach (var page in rowGroup.Column<int>(column).Pages)
            foreach (var value in page.Values.Span)
                sum += value;
        return sum;
    }

    static int CountTrueValues(RowGroupReader rowGroup, Column column)
    {
        var count = 0;
        foreach (var page in rowGroup.Column<bool>(column).Pages)
            foreach (var value in page.Values.Span)
                if (value)
                    count++;
        return count;
    }

    static int SumByteLengths(RowGroupReader rowGroup, Column column)
    {
        var sum = 0;
        foreach (var page in rowGroup.Column<byte[]>(column).Pages)
            foreach (var value in page.Values.Span)
                sum += value.Length;
        return sum;
    }

    static void AssertByteArrayColumnPageEnumerationDoesNotAllocateAfterWarmup(EncodingKind encoding)
    {
        var schema = new ParquetSchema([
            new Column("Value", ParquetPhysicalType.ByteArray,
                new ColumnOptions(encodings: ImmutableArray.Create(encoding)))
        ]);
        var path = CreateFile(schema, CreateByteArrayValues(4096));
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = schema.CreateReader(stream);
            var token = EnumerateTokens(reader)[0];
            using var rowGroup = reader.OpenRowGroup(stream, token);

            for (var i = 0; i < 8; i++)
                _ = SumByteLengths(rowGroup, schema.Columns[0]);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var before = GC.GetAllocatedBytesForCurrentThread();
            _ = SumByteLengths(rowGroup, schema.Columns[0]);
            var after = GC.GetAllocatedBytesForCurrentThread();
            var allocated = after - before;

            if (allocated != 0)
                throw new InvalidOperationException(
                    $"Expected zero allocations for steady-state {encoding} column page enumeration but saw {allocated} bytes.");
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

    static RowGroupToken[] EnumerateTokens(ParquetReader reader)
    {
        var tokens = new List<RowGroupToken>();
        foreach (var token in reader.EnumerateRowGroups())
            tokens.Add(token);
        return tokens.ToArray();
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
