using System.Collections.Immutable;
using Plank.Schema;

namespace Plank.Tests.Reading;

[NotInParallel]
internal sealed class SchemaProjectionAllocationTests
{
    [Test]
    public void ValidatedSchemaCreationProjectsLeavesOnlyOnce()
    {
        var leaves = Enumerable.Range(0, 128)
            .Select(i => ColumnDefinition.RequiredLeaf($"column{i}", ParquetPhysicalType.Int32)).ToArray();
        ImmutableArray<ColumnDefinition> definitions = [ColumnDefinition.OptionalGroup("group", leaves)];
        for (var i = 0; i < 8; i++)
        {
            _ = new ParquetSchema(definitions);
            _ = CreateValidated(definitions);
        }

        const int iterations = 16;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
            _ = new ParquetSchema(definitions);
        var constructorBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
            _ = CreateValidated(definitions);
        var validatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"Schema allocations over {iterations} iterations: constructor {constructorBytes}; validated {validatedBytes} bytes.");
        if (validatedBytes > constructorBytes + 256)
            throw new InvalidOperationException("Validated schema creation should allocate no more than the direct constructor.");

        var schema = CreateValidated(definitions);
        if (!schema.LeafPaths[127].AsSpan().SequenceEqual(["group", "column127"]) ||
            schema.LeafColumns[127].MaxDefinitionLevel != 1)
            throw new InvalidOperationException("Validated schema creation changed leaf projection.");
    }

    static ParquetSchema CreateValidated(ImmutableArray<ColumnDefinition> definitions)
        => ParquetSchema.TryCreate(definitions, out var schema, out var error)
            ? schema : throw new InvalidOperationException(error);
}
