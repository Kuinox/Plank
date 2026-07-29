using Plank.Reading;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Reading;

internal sealed class OptionalAncestorDefinitionLevelDiscoveryTests
{
    [Test]
    public void OptionalAncestorProducesDefinitionLevelsForRequiredLeaf()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.OptionalGroup("parent",
                ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int32))
        ]);
        int?[] expected = [10, null, 30];

        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None
        });
        var serialized = writer.CreateSerializedColumn<int?>(schema.LeafColumns.Single());
        serialized.Serialize(expected);
        writer.StartRowGroup().Write(serialized);
        writer.CloseFile();

        using var reader = schema.CreateReader(new MemoryReadSource(stream.ToArray()));
        var actual = new List<int?>();
        foreach (var buffer in reader.RowGroups[0].Column<int?>(schema.LeafColumns.Single()))
            actual.AddRange(buffer.Values);

        if (!actual.ToArray().AsSpan().SequenceEqual(expected))
            throw new InvalidOperationException(
                $"Expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}].");
    }
}
