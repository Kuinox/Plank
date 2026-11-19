using Plank.Schema;

namespace Plank.Tests.Writer;

[NotInParallel]
internal sealed class WriterSetupAllocationTests
{
    [Test]
    public void WriterSetupDoesNotCopyEachSchemaPath()
    {
        if (IntPtr.Size != 8)
            return;
        var leaves = Enumerable.Range(0, 128)
            .Select(i => ColumnDefinition.RequiredLeaf($"column{i}", ParquetPhysicalType.Int32)).ToArray();
        var schema = new ParquetSchema([ColumnDefinition.RequiredGroup("group", leaves)]);
        for (var i = 0; i < 8; i++)
            CreateWriter();

        const int iterations = 16;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
            CreateWriter();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"Writer setup allocated {allocated} bytes over {iterations} iterations.");
        if (allocated > iterations * 53_000)
            throw new InvalidOperationException("Writer setup exceeded the schema path reuse allocation budget.");

        void CreateWriter()
        {
            using var stream = new MemoryStream(capacity: 4);
            using var writer = schema.CreateWriter(stream);
        }
    }
}
