using System.Collections.Immutable;
using Plank.Schema;
using Plank.Writing;
using PlankColumn = Plank.Schema.Column;

#pragma warning disable CA2007
namespace Plank.Tests;

internal sealed class AllocationTests
{
    static readonly int[] SampleValues = Enumerable.Range(0, 1024).ToArray();
    static readonly ParquetSchema Schema = new(ImmutableArray.Create(
        new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default)));

    [Test]
    public async Task MeasureAllocationsForSingleFileWrite()
    {
        var allocatedBytes = await MeasureAllocatedBytesAsync(WriteSingleFileAsync);
        Console.WriteLine($"Single file write allocated bytes: {allocatedBytes}");
        await Assert.That(allocatedBytes).IsGreaterThan(0L);
    }

    [Test]
    public async Task MeasureAllocationsForWriterReuseAcrossFiles()
    {
        var allocatedBytes = await MeasureAllocatedBytesAsync(WriteManyFilesWithSingleWriterAsync);
        Console.WriteLine($"Writer reuse allocated bytes: {allocatedBytes}");
        await Assert.That(allocatedBytes).IsGreaterThan(0L);
    }

    static async ValueTask WriteSingleFileAsync()
    {
        await using var stream = new MemoryStream(64 * 1024);
        await using var writer = ParquetWriter.Create(stream, Schema, new ParquetWriterOptions
        {
            ExpectedRowGroupCount = 1,
            RowGroupRowCountHint = (uint)SampleValues.Length
        });
        var rowGroup = writer.StartRowGroup();
        await rowGroup.WriteAsync(Schema.Columns[0], SampleValues).ConfigureAwait(false);
        await writer.CompleteAsync().ConfigureAwait(false);
    }

    static async ValueTask WriteManyFilesWithSingleWriterAsync()
    {
        const int fileCount = 32;
        var options = new ParquetWriterOptions
        {
            ExpectedRowGroupCount = 1,
            RowGroupRowCountHint = (uint)SampleValues.Length
        };

        await using var initialStream = new MemoryStream(64 * 1024);
        await using var writer = ParquetWriter.Create(initialStream, Schema, options);
        await WriteOneFileAsync(writer).ConfigureAwait(false);

        for (var i = 1; i < fileCount; i++)
        {
            await using var stream = new MemoryStream(64 * 1024);
            writer.Reset(stream);
            await WriteOneFileAsync(writer).ConfigureAwait(false);
        }
    }

    static async ValueTask WriteOneFileAsync(ParquetWriter writer)
    {
        var rowGroup = writer.StartRowGroup();
        await rowGroup.WriteAsync(Schema.Columns[0], SampleValues).ConfigureAwait(false);
        await writer.CompleteAsync().ConfigureAwait(false);
    }

    static async ValueTask<long> MeasureAllocatedBytesAsync(Func<ValueTask> action)
    {
        await action().ConfigureAwait(false);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetTotalAllocatedBytes(true);
        await action().ConfigureAwait(false);
        var after = GC.GetTotalAllocatedBytes(true);
        return after - before;
    }
}
#pragma warning restore CA2007
