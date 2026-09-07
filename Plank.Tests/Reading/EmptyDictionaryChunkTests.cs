using Plank.Reading;
using Plank.Reading.Logical;
using Plank.Reading.Physical;
using Plank.Tests.Reading.ParquetTesting;

namespace Plank.Tests.Reading;

internal sealed class EmptyDictionaryChunkTests
{
    [Test]
    public void DictionaryOnlyChunksCanBeInspectedAndRead()
    {
        // Arrow's empty row groups can contain a dictionary page without a data
        // page: data_page_offset is zero, dictionary_page_offset is not.
        var bytes = ParquetTestingCorpus.ReadAllBytes("data/column_chunk_key_value_metadata.parquet");
        using var reader = new ParquetReader();
        reader.Reset(new MemoryStream(bytes));
        var rowGroup = reader.RowGroups[0];
        if (rowGroup.RowCount != 0)
            throw new InvalidOperationException("Expected an empty row group.");

        for (var column = 0; column < reader.Schema.LeafColumns.Length; column++)
        {
            var chunk = reader.PhysicalReader.Metadata.ColumnChunk(0, column);
            if (chunk.DataPageOffset != 0 || chunk.DictionaryPageOffset == 0 ||
                chunk.ChunkOffset != chunk.DictionaryPageOffset)
                throw new InvalidOperationException("The chunk must start at its dictionary page.");

            var physicalCount = 0;
            foreach (var page in reader.PhysicalReader.OpenPages(0, column))
            {
                if (page.Header.Type != PageHeaderType.DictionaryPage || page.Header.ValueCount != 0)
                    throw new InvalidOperationException("Expected an empty dictionary page.");
                physicalCount++;
            }
            if (physicalCount != 1)
                throw new InvalidOperationException("Expected exactly one physical dictionary page.");

            using var pages = rowGroup.GetColumnMetadata(column).OpenPages();
            if (pages.Count != 0)
                throw new InvalidOperationException("An empty chunk must have no data pages.");

            foreach (var buffer in rowGroup.Column<int?>(reader.Schema.LeafColumns[column]))
                if (buffer.Count != 0)
                    throw new InvalidOperationException("An empty chunk must yield no values.");
        }
    }
}
