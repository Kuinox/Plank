using Plank.Schema;
using Plank.Writing;
using Plank.Writing.PageStrategy;

namespace Plank.Tests.Writer;

internal sealed class NullableDecimalByteArrayTests
{
    [Test]
    [Arguments(ParquetDataPageVersion.V1, false, false)]
    [Arguments(ParquetDataPageVersion.V2, false, false)]
    [Arguments(ParquetDataPageVersion.V1, true, false)]
    [Arguments(ParquetDataPageVersion.V2, true, false)]
    [Arguments(ParquetDataPageVersion.V1, false, true)]
    [Arguments(ParquetDataPageVersion.V2, false, true)]
    public void NullableDecimalsRespectSmallPageBudgets(ParquetDataPageVersion version,
        bool dictionary, bool allNull)
    {
        decimal?[] expected = allNull ? new decimal?[14] :
        [
            null, -decimal.MaxValue / 100, -327.69m, -327.68m, -1.29m, -1.28m, -0.01m,
            0m, null, 1.27m, 1.28m, 327.67m, 327.68m, decimal.MaxValue / 100
        ];
        var schema = new ParquetSchema([
            ColumnDefinition.OptionalLeaf("amount", ParquetPhysicalType.ByteArray,
                new ColumnOptions(encodings: [EncodingKind.Plain, EncodingKind.RleDictionary]),
                new LogicalType.Decimal(29, 2), new SmallPages(dictionary))
        ]);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None,
            DataPageVersion = version
        });
        var serialized = writer.CreateSerializedColumn<decimal?>(schema.LeafColumns[0]);
        serialized.Serialize(expected);
        writer.StartRowGroup().Write(serialized);
        writer.CloseFile();

        using var input = new MemoryStream(stream.ToArray());
        using var reader = schema.CreateReader(input);
        var column = reader.RowGroups[0].Column<decimal?>(0);
        using var pages = column.Metadata.OpenPages();
        if (pages.Count < 2)
            throw new InvalidOperationException("The small page budget did not produce multiple pages.");
        var actual = new List<decimal?>();
        foreach (var buffer in column)
            actual.AddRange(buffer.Values);
        if (!actual.SequenceEqual(expected))
            throw new InvalidOperationException("Nullable BYTE_ARRAY decimal values changed across page boundaries.");
    }

    sealed class SmallPages(bool dictionary) : IPageStrategy
    {
        public DictionaryMode GetDictionaryMode() => dictionary ? DictionaryMode.Forced : DictionaryMode.Disabled;
        public bool ShouldDropDictionary(uint uniqueCount, uint totalRowCount, uint rowsSeen) => false;
        public uint GetNextDataPageRowCount(uint totalRowCount, uint rowsWritten) => totalRowCount - rowsWritten;
        public bool TryGetTargetDataPageSizeBytes(out uint sizeBytes)
        {
            sizeBytes = 9;
            return true;
        }
    }
}
