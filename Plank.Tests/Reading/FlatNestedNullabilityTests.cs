using Plank.Reading.Logical;
using Plank.Schema;

namespace Plank.Tests.Reading;

internal sealed class FlatNestedNullabilityTests
{
    [Test]
    [Arguments("nested-1.0-False.parquet")]
    [Arguments("nested-1.0-True.parquet")]
    [Arguments("nested-2.0-False.parquet")]
    [Arguments("nested-2.0-True.parquet")]
    public void FlatProjectionRejectsMultipleOptionalLevelsAndNestedProjectionPreservesValues(string fixture)
    {
        using var reader = new ParquetReader();
        reader.Reset(File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Reading", "Fixtures", fixture)));
        var group = reader.RowGroups[0];
        var leaf = reader.Schema.LeafColumns[0];

        AssertNestedGuidance(() => group.Column<int?>(0));
        AssertNestedGuidance(() => group.Column<int?>(leaf));

        var values = new List<int>();
        var definitions = new List<int>();
        foreach (var buffer in group.NestedColumn<int>(leaf))
        {
            values.AddRange(buffer.Values.Values);
            definitions.AddRange(buffer.DefinitionLevels);
        }
        if (!values.ToArray().AsSpan().SequenceEqual([42, 99]) ||
            !definitions.ToArray().AsSpan().SequenceEqual([0, 1, 2, 2]))
            throw new InvalidOperationException("Nested projection must preserve both values and all definition levels.");
    }

    [Test]
    [Arguments("nested-1.0-False.parquet")]
    [Arguments("nested-1.0-True.parquet")]
    [Arguments("nested-2.0-False.parquet")]
    [Arguments("nested-2.0-True.parquet")]
    public void RequestedSchemaCannotHideMultiplePhysicalOptionalLevels(string fixture)
    {
        var requested = new ParquetSchema([
            ColumnDefinition.RequiredGroup("obj",
                ColumnDefinition.OptionalLeaf("x", ParquetPhysicalType.Int32))
        ]);
        using var reader = requested.CreateReader(
            File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Reading", "Fixtures", fixture)));
        var group = reader.RowGroups[0];

        AssertNestedGuidance(() => group.Column<int?>(0));
        AssertNestedGuidance(() => group.Column<int?>(requested.LeafColumns[0]));
    }

    static void AssertNestedGuidance(Action selectColumn)
    {
        try
        {
            selectColumn();
        }
        catch (NotSupportedException exception) when
            (exception.Message.Contains("obj.x", StringComparison.Ordinal) &&
             exception.Message.Contains("NestedColumn<T>", StringComparison.Ordinal))
        {
            return;
        }
        throw new InvalidOperationException("Flat projection must reject nested nullability with NestedColumn<T> guidance.");
    }
}
