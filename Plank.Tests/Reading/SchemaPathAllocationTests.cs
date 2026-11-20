using System.Collections.Immutable;
using Plank.Schema;

namespace Plank.Tests.Reading;

[NotInParallel]
internal sealed class SchemaPathAllocationTests
{
    [Test]
    public void SchemaConstructionCopiesEachLeafPathOnce()
    {
        const int leaves = 64;
        const int iterations = 16;
        const int shallowDepth = 2;
        const int deepDepth = 34;
        var shallowSmall = Definitions(leaves, shallowDepth);
        var shallowLarge = Definitions(2 * leaves, shallowDepth);
        var deepSmall = Definitions(leaves, deepDepth);
        var deepLarge = Definitions(2 * leaves, deepDepth);

        // Subtract the N-to-2N increment at shallow depth from the same increment
        // at deep depth. This cancels per-leaf costs unrelated to path length and
        // per-schema costs unrelated to leaf count.
        for (var i = 0; i < 8; i++)
        {
            _ = new ParquetSchema(shallowSmall);
            _ = new ParquetSchema(shallowLarge);
            _ = new ParquetSchema(deepSmall);
            _ = new ParquetSchema(deepLarge);
        }

        var shallowIncrement = MeasureSchemas(shallowLarge, iterations) - MeasureSchemas(shallowSmall, iterations);
        var deepIncrement = MeasureSchemas(deepLarge, iterations) - MeasureSchemas(deepSmall, iterations);
        var actual = deepIncrement - shallowIncrement;

        // Measure the runtime's actual array and joined-name costs for the same
        // paths, rather than relying on object headers or a fixed byte budget.
        var shallowPath = Enumerable.Repeat("g", shallowDepth - 1).Append("c000").ToImmutableArray();
        var deepPath = Enumerable.Repeat("g", deepDepth - 1).Append("c000").ToImmutableArray();
        for (var i = 0; i < 8; i++)
        {
            _ = ImmutableArray.Create<string>(shallowPath.AsSpan());
            _ = ImmutableArray.Create<string>(deepPath.AsSpan());
            _ = string.Join(".", shallowPath);
            _ = string.Join(".", deepPath);
        }
        var samples = leaves * iterations;
        var pathDelta = MeasureCopies(deepPath, samples) - MeasureCopies(shallowPath, samples);
        var nameDelta = MeasureNames(deepPath, samples) - MeasureNames(shallowPath, samples);
        var pathCost = actual - nameDelta;
        Console.WriteLine($"Incremental path cost: {pathCost}; one copy: {pathDelta}; two copies: {2 * pathDelta} bytes.");
        if (pathCost >= pathDelta + pathDelta / 2)
            throw new InvalidOperationException("Schema leaf paths allocated closer to two copies than one.");

        var schema = new ParquetSchema(deepLarge);
        for (var i = 0; i < 2 * leaves; i++)
        {
            var path = schema.LeafPaths[i];
            if (path.Length != deepDepth || path[^1] != $"c{i:D3}" ||
                path.Take(path.Length - 1).Any(segment => segment != "g"))
                throw new InvalidOperationException($"Unexpected path for leaf {i}.");
        }
    }

    static ImmutableArray<ColumnDefinition> Definitions(int count, int depth)
    {
        var children = Enumerable.Range(0, count)
            .Select(i => ColumnDefinition.RequiredLeaf($"c{i:D3}", ParquetPhysicalType.Int32)).ToArray();
        var group = ColumnDefinition.RequiredGroup("g", children);
        for (var i = 2; i < depth; i++)
            group = ColumnDefinition.RequiredGroup("g", group);
        return [group];
    }

    static long MeasureSchemas(ImmutableArray<ColumnDefinition> definitions, int iterations)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
            _ = new ParquetSchema(definitions);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    static long MeasureCopies(ImmutableArray<string> path, int iterations)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
            GC.KeepAlive(ImmutableArray.Create<string>(path.AsSpan()));
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    static long MeasureNames(ImmutableArray<string> path, int iterations)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
            GC.KeepAlive(string.Join(".", path));
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
