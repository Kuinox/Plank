using Plank.Schema;
using Plank.Writing;
using PlankColumn = Plank.Schema.Column;

namespace Plank.Tests;

internal sealed class AllocationTests
{
    static readonly int[] SampleValues = [.. Enumerable.Range(0, 1024)];
    static readonly ParquetSchema Schema = new([
        new PlankColumn("A", ParquetPhysicalType.Int32, ColumnOptions.Default)
    ]);
    const int StreamCapacityBytes = 64 * 1024;

    [Test]
    public async Task AllocationDetectorFindsPositiveAndNegativeCases()
    {
        _ = new byte[8];

        var noAllocation = MeasureAllocatedBytes(() =>
        {
            var sum = 0;
            for (var i = 0; i < 32; i++)
                sum += i;
            _ = sum;
        });
        var withAllocation = MeasureAllocatedBytes(() => _ = new byte[64]);

        await Assert.That(noAllocation).IsEqualTo(0L);
        await Assert.That(withAllocation).IsGreaterThan(0L);
    }

    [Test]
    public async Task ReusedWriterSteadyStateWriteIsZeroAllocation()
    {
        using var stream = new MemoryStream(StreamCapacityBytes);
        using var writer = ParquetWriter.Create(stream, Schema, new ParquetWriterOptions
        {
            ExpectedRowGroupCount = 1,
            RowGroupRowCountHint = (uint)SampleValues.Length
        });

        WriteOneFileSync(writer);

        long totalAllocated = 0;
        const int iterations = 200;
        for (var i = 0; i < iterations; i++)
        {
            stream.Position = 0;
            stream.SetLength(0);
            writer.Reset(stream);
            totalAllocated += MeasureAllocatedBytes(() => WriteOneFileSync(writer));
        }

        await Assert.That(totalAllocated).IsEqualTo(0L);
    }

    static void WriteOneFileSync(ParquetWriter writer)
    {
        var rowGroup = writer.StartRowGroup();
        var write = rowGroup.WriteAsync(Schema.Columns[0], SampleValues);
        if (!write.IsCompletedSuccessfully)
            throw new InvalidOperationException("WriteAsync must complete synchronously in the sync write pipeline.");

        writer.CloseFile();
    }

    static long MeasureAllocatedBytes(Action action)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        action();
        var after = GC.GetAllocatedBytesForCurrentThread();
        return after - before;
    }
}
