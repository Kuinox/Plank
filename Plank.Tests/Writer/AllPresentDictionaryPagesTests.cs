using Plank.Schema;
using Plank.Writing;
using Plank.Writing.PageStrategy;

namespace Plank.Tests.Writer;

internal sealed class AllPresentDictionaryPagesTests
{
    [Test]
    [Arguments(ParquetDataPageVersion.V1, false)]
    [Arguments(ParquetDataPageVersion.V1, true)]
    [Arguments(ParquetDataPageVersion.V2, false)]
    [Arguments(ParquetDataPageVersion.V2, true)]
    public void AllPresentDictionaryPagesPreserveValuesAndGenericBytes(
        ParquetDataPageVersion version, bool writePageIndexes)
    {
        int?[][] patterns =
        [
            Enumerable.Range(0, 60_003).Select(static i => (int?)(i % 266)).ToArray(),
            Enumerable.Range(0, 60_003).Select(static i => i % 257 == 0 ? null : (int?)(i % 700 - 1)).ToArray(),
            Enumerable.Range(0, 20_003).Select(static i => (int?)i).ToArray(),
            [null, int.MinValue, 7, int.MaxValue, null, 7, 511, 512],
            [null, null, null],
            [42],
            [0, 0, 0]
        ];
        foreach (var targetPageBytes in new uint[] { 1, 1024, 1_048_576 })
        foreach (var pattern in patterns)
        {
            var values = targetPageBytes == 1 ? pattern[..Math.Min(pattern.Length, 33)] : pattern;
            var optimized = Write(values, version, writePageIndexes, targetPageBytes, generic: false);
            var generic = Write(values, version, writePageIndexes, targetPageBytes, generic: true);
            using var stream = new MemoryStream(optimized);
            using var reader = new ParquetSharp.ParquetFileReader(stream, leaveOpen: false);
            using var group = reader.RowGroup(1);
            using var column = group.Column(0).LogicalReader<int?>();
            if (!column.ReadAll(values.Length).AsSpan().SequenceEqual(values))
                throw new InvalidOperationException("All-present dictionary pages changed the decoded values.");
            if (!optimized.AsSpan().SequenceEqual(generic))
                throw new InvalidOperationException("All-present dictionary pages changed the Parquet bytes.");
        }
    }

    static byte[] Write(int?[] values, ParquetDataPageVersion version, bool writePageIndexes,
        uint targetPageBytes, bool generic)
    {
        var options = new ColumnOptions(encodings: [EncodingKind.RleDictionary, EncodingKind.Plain]);
        var template = new ParquetSchema([
            ColumnDefinition.OptionalLeaf("value", ParquetPhysicalType.Int32, options)
        ]);
        var strategy = generic
            ? new GenericStrategy(new DefaultStrategy(template.LeafColumns[0].Column, targetPageBytes))
            : null;
        var schema = new ParquetSchema([
            ColumnDefinition.OptionalLeaf("value", ParquetPhysicalType.Int32, options, pageStrategy: strategy)
        ]);
        using var output = new MemoryStream();
        using var writer = schema.CreateWriter(output, new ParquetWriterOptions
        {
            Compression = CompressionKind.None,
            DataPageVersion = version,
            WritePageIndexes = writePageIndexes,
            TargetDataPageSizeBytes = targetPageBytes
        });
        var serialized = writer.CreateSerializedColumn<int?>(schema.LeafColumns[0]);
        serialized.Serialize([7, 3, 7, 1, null, 42, 3]);
        writer.StartRowGroup().Write(serialized);
        serialized.Serialize(values);
        writer.StartRowGroup().Write(serialized);
        writer.CloseFile();
        return output.ToArray();
    }

    // Delegate the same policy while selecting the generic dictionary builder.
    sealed class GenericStrategy(DefaultStrategy inner) : IPageStrategy
    {
        public DictionaryMode GetDictionaryMode() => inner.GetDictionaryMode();
        public bool ShouldDropDictionary(uint uniqueCount, uint totalRowCount, uint rowsSeen)
            => inner.ShouldDropDictionary(uniqueCount, totalRowCount, rowsSeen);
        public bool TryGetTargetDataPageSizeBytes(out uint sizeBytes)
            => inner.TryGetTargetDataPageSizeBytes(out sizeBytes);
        public uint GetNextDataPageRowCount(uint totalRowCount, uint rowsWritten)
            => inner.GetNextDataPageRowCount(totalRowCount, rowsWritten);
    }
}
