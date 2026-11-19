using System.Collections.Immutable;
using Plank.Schema;

namespace Plank.Tests.Reading;

[NotInParallel]
internal sealed class SchemaPathAllocationTests
{
    [Test]
    public void SchemaConstructionCopiesEachLeafPathOnce()
    {
        if (IntPtr.Size != 8)
            return;
        var leaves = Enumerable.Range(0, 128)
            .Select(i => ColumnDefinition.RequiredLeaf($"column{i}", ParquetPhysicalType.Int32)).ToArray();
        ImmutableArray<ColumnDefinition> definitions = [ColumnDefinition.OptionalGroup("group", leaves)];
        for (var i = 0; i < 8; i++)
            _ = new ParquetSchema(definitions);

        const int iterations = 16;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
            _ = new ParquetSchema(definitions);
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"Direct schema construction allocated {bytes} bytes over {iterations} iterations.");
        if (bytes > iterations * 62_500)
            throw new InvalidOperationException("Schema construction exceeded the single-copy allocation budget.");

        var schema = new ParquetSchema(definitions);
        for (var i = 0; i < leaves.Length; i++)
            if (!schema.LeafPaths[i].AsSpan().SequenceEqual(["group", leaves[i].Name]))
                throw new InvalidOperationException($"Unexpected path for leaf {i}.");
    }
}
