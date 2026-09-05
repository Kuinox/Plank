using Plank.Reading;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.PageStrategy;

namespace Plank.Tests.Reading;

internal sealed class RequiredToOptionalEvolutionTests
{
    [Test]
    [Arguments(ParquetDataPageVersion.V1, EncodingKind.Plain)]
    [Arguments(ParquetDataPageVersion.V2, EncodingKind.Plain)]
    [Arguments(ParquetDataPageVersion.V1, EncodingKind.RleDictionary)]
    [Arguments(ParquetDataPageVersion.V2, EncodingKind.RleDictionary)]
    [Arguments(ParquetDataPageVersion.V1, EncodingKind.ByteStreamSplit)]
    [Arguments(ParquetDataPageVersion.V2, EncodingKind.ByteStreamSplit)]
    [Arguments(ParquetDataPageVersion.V1, EncodingKind.DeltaBinaryPacked)]
    [Arguments(ParquetDataPageVersion.V2, EncodingKind.DeltaBinaryPacked)]
    public void RequiredNumericPagesReadIntoNullableBuffersAcrossBatches(
        ParquetDataPageVersion version, EncodingKind encoding)
    {
        var expected = Enumerable.Range(0, 40_003).Select(i => i % 251 - 125).ToArray();
        var file = Write(ParquetPhysicalType.Int32, expected, version, encoding);
        var requested = new ParquetSchema([ColumnDefinition.OptionalLeaf("value", ParquetPhysicalType.Int32)]);
        using var source = new MemoryReadSource(file);
        using var reader = requested.CreateReader(source);
        var offset = 0;
        var batchCount = 0;
        foreach (var buffer in reader.RowGroups[0].Column<int?>(0))
        {
            batchCount++;
            foreach (var value in buffer.Values)
            {
                if (offset >= expected.Length || value != expected[offset])
                    throw new InvalidOperationException($"{version}/{encoding}: value mismatch at row {offset}.");
                offset++;
            }
        }
        if (offset != expected.Length || batchCount < 2)
            throw new InvalidOperationException($"Expected {expected.Length} rows across multiple batches, got {offset} in {batchCount}.");
        if (reader.Schema.LeafColumns[0].Options.Repetition != ParquetRepetition.Optional ||
            reader.Metadata.Schema.LeafColumns[1].Options.Repetition != ParquetRepetition.Required)
            throw new InvalidOperationException("Requested nullability and physical repetition must remain distinct.");
    }

    [Test]
    [Arguments(ParquetDataPageVersion.V1, EncodingKind.Plain)]
    [Arguments(ParquetDataPageVersion.V2, EncodingKind.Plain)]
    [Arguments(ParquetDataPageVersion.V1, EncodingKind.RleDictionary)]
    [Arguments(ParquetDataPageVersion.V2, EncodingKind.RleDictionary)]
    [Arguments(ParquetDataPageVersion.V1, EncodingKind.DeltaLengthByteArray)]
    [Arguments(ParquetDataPageVersion.V2, EncodingKind.DeltaLengthByteArray)]
    [Arguments(ParquetDataPageVersion.V1, EncodingKind.DeltaByteArray)]
    [Arguments(ParquetDataPageVersion.V2, EncodingKind.DeltaByteArray)]
    public void RequiredBinaryPagesReadThroughOptionalProjection(
        ParquetDataPageVersion version, EncodingKind encoding)
    {
        byte[][] expected = [[], "alpha"u8.ToArray(), "alphabet"u8.ToArray(), "alpha"u8.ToArray()];
        var file = Write(ParquetPhysicalType.ByteArray, expected, version, encoding);
        var requested = new ParquetSchema([ColumnDefinition.OptionalLeaf("value", ParquetPhysicalType.ByteArray)]);
        using var source = new MemoryReadSource(file);
        using var reader = requested.CreateReader(source);
        var offset = 0;
        foreach (var buffer in reader.RowGroups[0].Column<byte>(0))
        {
            for (var i = 0; i < buffer.Count; i++)
            {
                if (offset >= expected.Length || buffer.IsNull(i) ||
                    !buffer.GetValue(i).SequenceEqual(expected[offset]))
                    throw new InvalidOperationException($"{version}/{encoding}: binary mismatch at row {offset}.");
                offset++;
            }
        }
        if (offset != expected.Length)
            throw new InvalidOperationException($"Expected {expected.Length} binary values, got {offset}.");
    }

    static byte[] Write<T>(ParquetPhysicalType type, T[] values,
        ParquetDataPageVersion version, EncodingKind encoding)
    {
        var schema = new ParquetSchema([
            ColumnDefinition.OptionalLeaf("unselected", ParquetPhysicalType.Int32),
            ColumnDefinition.RequiredLeaf("value", type, new ColumnOptions(encodings: [encoding]),
                pageStrategy: new SinglePageStrategy(encoding == EncodingKind.RleDictionary))
        ]);
        using var output = new MemoryStream();
        using var writer = schema.CreateWriter(output, new ParquetWriterOptions
        {
            Compression = CompressionKind.None,
            DataPageVersion = version
        });
        // The selected required column follows an optional column in the file. Its physical
        // repetition must be resolved through the projection, not the requested ordinal.
        var unselected = writer.CreateSerializedColumn<int?>(schema.LeafColumns[0]);
        unselected.Serialize(new int?[values.Length]);
        var column = writer.CreateSerializedColumn<T>(schema.LeafColumns[1]);
        column.Serialize(values);
        var group = writer.StartRowGroup();
        group.Write(unselected);
        group.Write(column);
        writer.CloseFile();
        return output.ToArray();
    }

    sealed class SinglePageStrategy(bool dictionary) : IPageStrategy
    {
        public DictionaryMode GetDictionaryMode()
            => dictionary ? DictionaryMode.Forced : DictionaryMode.Disabled;

        public bool ShouldDropDictionary(uint uniqueCount, uint totalRowCount, uint rowsSeen)
            => false;

        public uint GetNextDataPageRowCount(uint totalRowCount, uint rowsWritten)
            => totalRowCount - rowsWritten;
    }
}
