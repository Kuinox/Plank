using Plank.Reading;
using Plank.Reading.Physical;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.E2E;

internal sealed class GeneratedNestedColumnMetadataTests
{
    [Test]
    public void ColumnOptionsAndFieldIdsSurviveNestedGenerationAndWriting()
    {
        var schema = NestedMetadataRow.Schema;
        if (schema.Definitions[0].FieldId != 42 || schema.Definitions[1].FieldId != 43 ||
            schema.Definitions[2].FieldId != 44 || schema.Definitions[3].FieldId != 45)
            throw new InvalidOperationException("Declared property field IDs were not preserved.");
        if (schema.Definitions[1].Children[0].FieldId is not null ||
            schema.Definitions[2].Children[0].FieldId != 46)
            throw new InvalidOperationException("Field IDs moved between containers and leaves.");
        foreach (var column in schema.LeafColumns.Take(3))
        {
            if (column.Options.BloomFilter is not { FalsePositiveProbability: 0.002,
                    ExpectedDistinctValueCount: 123, MaximumBytes: 4096 })
                throw new InvalidOperationException("Bloom filter options were not preserved.");
            if (column.Options.Compression != CompressionKind.None ||
                !column.Options.Encodings.SequenceEqual([EncodingKind.Plain]))
                throw new InvalidOperationException("Leaf compression or encoding options were lost.");
        }

        using var stream = new MemoryStream();
        using (var writer = NestedMetadataRow.CreateRowWriter(stream,
                   new ParquetWriterOptions { Compression = CompressionKind.None }))
        {
            var row = writer.GetRow();
            row.Key = 7;
            row.Values = [11, 13];
            row.Details = new NestedMetadataDetails { Code = 17 };
            row.Scores = new Dictionary<int, int> { [19] = 23 };
            writer.Complete();
        }

        using var reader = new ParquetFileReader();
        reader.Reset(new MemoryReadSource(stream.ToArray()));
        var expectedNames = new[] { "row_key", "Values", "Details", "Scores", "Code" };
        var expectedIds = new[] { 42, 43, 44, 45, 46 };
        for (var i = 0; i < expectedNames.Length; i++)
        {
            var found = false;
            foreach (var node in reader.Metadata.SchemaNodes)
                if (reader.Metadata.SchemaNodeNameUtf8(node.Ordinal).SequenceEqual(System.Text.Encoding.UTF8.GetBytes(expectedNames[i])))
                {
                    if (node.FieldId != expectedIds[i])
                        throw new InvalidOperationException("Written field ID differs from the declared ID.");
                    found = true;
                }
            if (!found)
                throw new InvalidOperationException("Written schema node was missing.");
        }
        int[] probes = [7, 11, 17];
        for (var ordinal = 0; ordinal < probes.Length; ordinal++)
        {
            if (!reader.Metadata.ColumnChunk(0, ordinal).HasBloomFilter)
                throw new InvalidOperationException("Generated writer omitted a configured Bloom filter.");
            using var filter = reader.OpenBloomFilter(0, ordinal);
            if (!filter.MightContain(probes[ordinal]))
                throw new InvalidOperationException("Bloom filter returned a false negative.");
        }
        if (reader.Metadata.ColumnChunk(0, 3).HasBloomFilter)
            throw new InvalidOperationException("Unconfigured map leaf unexpectedly has a Bloom filter.");
    }
}

[ParquetSchema(AllowAllocatingValues = true)]
internal sealed partial class NestedMetadataRow
{
    [ParquetColumn("row_key", FieldId = 42, BloomFilter = true,
        BloomFilterFalsePositiveProbability = 0.002, BloomFilterExpectedDistinctValueCount = 123,
        BloomFilterMaximumBytes = 4096, Encodings = [EncodingKind.Plain], Compression = CompressionKind.None)]
    public int Key { get; set; }

    [ParquetColumn(FieldId = 43, BloomFilter = true,
        BloomFilterFalsePositiveProbability = 0.002, BloomFilterExpectedDistinctValueCount = 123,
        BloomFilterMaximumBytes = 4096, Encodings = [EncodingKind.Plain], Compression = CompressionKind.None)]
    public int[] Values { get; set; } = [];

    [ParquetColumn(FieldId = 44)]
    public NestedMetadataDetails Details { get; set; } = new();

    [ParquetColumn(FieldId = 45)]
    public Dictionary<int, int> Scores { get; set; } = [];
}

internal sealed class NestedMetadataDetails
{
    [ParquetColumn(FieldId = 46, BloomFilter = true,
        BloomFilterFalsePositiveProbability = 0.002, BloomFilterExpectedDistinctValueCount = 123,
        BloomFilterMaximumBytes = 4096, Encodings = [EncodingKind.Plain], Compression = CompressionKind.None)]
    public int Code { get; set; }
}
