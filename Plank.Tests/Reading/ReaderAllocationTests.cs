using System.Collections.Immutable;
using Plank.Reading;
using Plank.Schema;
using Plank.Writing;

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

    static int SumValues(RowGroupReader rowGroup, Column column)
    {
        var sum = 0;
        foreach (var page in rowGroup.Column<int>(column).Pages)
            foreach (var value in page.Values.Span)
                sum += value;
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
}
