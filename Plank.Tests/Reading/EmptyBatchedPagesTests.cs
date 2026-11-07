using Plank.Reading;
using Plank.Reading.Logical;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.PageStrategy;
using Plank.Writing.Thrift;

namespace Plank.Tests.Reading;

internal sealed class EmptyBatchedPagesTests
{
    [Test]
    [MatrixDataSource]
    public void EmptyPageBetweenNonemptyPages(
        [Matrix(true, false)] bool optional,
        [Matrix(ParquetDataPageVersion.V1, ParquetDataPageVersion.V2)] ParquetDataPageVersion version,
        [Matrix(EncodingKind.Plain, EncodingKind.RleDictionary, EncodingKind.DeltaBinaryPacked)] EncodingKind encoding)
    {
        var file = MakeFile(optional, version, encoding, 0, 0, false);
        using var reader = new ParquetReader();
        reader.Reset(new MemoryStream(file));
        if (optional)
        {
            var values = new List<int?>();
            foreach (var buffer in reader.RowGroups[0].Column<int?>(0)) values.AddRange(buffer.Values);
            if (!values.SequenceEqual(new int?[] { 1, 2, 3, 4 })) throw new InvalidOperationException("Values changed.");
        }
        else
        {
            var values = new List<int>();
            foreach (var buffer in reader.RowGroups[0].Column<int>(0)) values.AddRange(buffer.Values);
            if (!values.SequenceEqual([1, 2, 3, 4])) throw new InvalidOperationException("Values changed.");
        }
    }

    [Test]
    [MatrixDataSource]
    public void EmptyBinaryPageBetweenNonemptyPages(
        [Matrix(ParquetDataPageVersion.V1, ParquetDataPageVersion.V2)] ParquetDataPageVersion version,
        [Matrix(EncodingKind.Plain, EncodingKind.RleDictionary, EncodingKind.DeltaLengthByteArray)] EncodingKind encoding)
    {
        var file = MakeBinaryFile(version, encoding);
        using var reader = new ParquetReader();
        reader.Reset(new MemoryStream(file));
        var values = new List<byte[]?>();
        foreach (var buffer in reader.RowGroups[0].Column<byte>(0))
            for (var i = 0; i < buffer.Count; i++)
                values.Add(buffer.IsNull(i) ? null : buffer.GetValue(i).ToArray());
        if (values.Count != 4 || values[0] is null || !values[0]!.AsSpan().SequenceEqual(new byte[] { 1 }) ||
            values[1] is not null || values[2] is null || !values[2]!.AsSpan().SequenceEqual(new byte[] { 2, 3 }) ||
            values[3] is null || values[3]!.Length != 0)
            throw new InvalidOperationException("Binary values changed.");
    }

    [Test]
    [Arguments(ParquetDataPageVersion.V1)]
    [Arguments(ParquetDataPageVersion.V2)]
    public void RejectsTrailingBytesOnEmptyPlainBinaryPage(ParquetDataPageVersion version)
    {
        var file = MakeBinaryFile(version, EncodingKind.Plain, trailingBytes: true);
        Assert.Throws<CorruptParquetException>(() =>
        {
            using var reader = new ParquetReader();
            reader.Reset(new MemoryStream(file));
            foreach (var buffer in reader.RowGroups[0].Column<byte>(0)) _ = buffer.Count;
        });
    }

    static byte[] MakeBinaryFile(ParquetDataPageVersion version, EncodingKind encoding, bool trailingBytes = false)
    {
        var schema = new ParquetSchema([
            ColumnDefinition.OptionalLeaf("value", ParquetPhysicalType.ByteArray,
                new ColumnOptions(encodings: [encoding]),
                pageStrategy: new TwoRows(encoding == EncodingKind.RleDictionary))
        ]);
        using var output = new MemoryStream();
        using (var writer = schema.CreateWriter(output, new ParquetWriterOptions
            { Compression = CompressionKind.None, DataPageVersion = version }))
        {
            var column = writer.CreateSerializedColumn<byte[]?>(schema.LeafColumns[0]);
            column.Serialize([[1], null, [2, 3], []]);
            Insert(column.Pages, version, encoding, true, 0, 0, false, false,
                trailingBytes ? new byte[] { 0x42 } : null);
            writer.StartRowGroup().Write(column);
            writer.CloseFile();
        }
        return output.ToArray();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public void RejectsCorruptEmptyV2(bool optional, bool levels)
    {
        var file = MakeFile(optional, ParquetDataPageVersion.V2, EncodingKind.Plain,
            levels ? 0U : 1U, levels ? 100U : 0U, false);
        Assert.Throws<CorruptParquetException>(() => Drain(file, optional));
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public void RejectsMissingEmptyDeltaHeader(bool optional)
    {
        var file = MakeFile(optional, ParquetDataPageVersion.V2, EncodingKind.DeltaBinaryPacked,
            0, 0, false, omitDeltaHeader: true);
        Assert.Throws<CorruptParquetException>(() => Drain(file, optional));
    }

    [Test]
    public void RejectsOverlongV1DefinitionStream()
    {
        var file = MakeFile(true, ParquetDataPageVersion.V1, EncodingKind.Plain, 0, 0, true);
        Assert.Throws<CorruptParquetException>(() => Drain(file, true));
    }

    static void Drain(byte[] file, bool optional)
    {
        using var reader = new ParquetReader();
        reader.Reset(new MemoryStream(file));
        if (optional) foreach (var buffer in reader.RowGroups[0].Column<int?>(0)) _ = buffer.Values.Length;
        else foreach (var buffer in reader.RowGroups[0].Column<int>(0)) _ = buffer.Values.Length;
    }

    static byte[] MakeFile(bool optional, ParquetDataPageVersion version, EncodingKind encoding,
        uint nullCount, uint levelLength, bool invalidV1, bool omitDeltaHeader = false)
    {
        var options = new ColumnOptions(encodings: [encoding]);
        var strategy = new TwoRows(encoding == EncodingKind.RleDictionary);
        var leaf = optional ? ColumnDefinition.OptionalLeaf("value", ParquetPhysicalType.Int32, options, pageStrategy: strategy)
            : ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int32, options, pageStrategy: strategy);
        var schema = new ParquetSchema([leaf]);
        using var output = new MemoryStream();
        using (var writer = schema.CreateWriter(output, new ParquetWriterOptions { Compression = CompressionKind.None, DataPageVersion = version }))
        {
            if (optional)
            {
                var column = writer.CreateSerializedColumn<int?>(schema.LeafColumns[0]);
                column.Serialize([1, 2, 3, 4]);
                Insert(column.Pages, version, encoding, optional, nullCount, levelLength, invalidV1, omitDeltaHeader);
                writer.StartRowGroup().Write(column);
            }
            else
            {
                var column = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);
                column.Serialize([1, 2, 3, 4]);
                Insert(column.Pages, version, encoding, optional, nullCount, levelLength, invalidV1, omitDeltaHeader);
                writer.StartRowGroup().Write(column);
            }
            writer.CloseFile();
        }
        return output.ToArray();
    }

    static void Insert(PageList pages, ParquetDataPageVersion version, EncodingKind encoding, bool optional,
        uint nullCount, uint levelLength, bool invalidV1, bool omitDeltaHeader, byte[]? extraPayload = null)
    {
        // Append, then rotate the empty page before the last data page.
        var index = pages.Count;
        if (index < 2) throw new InvalidOperationException("Expected multiple data pages.");
        ref var empty = ref pages.Add();
        empty.Header = new BufferWriter(DefaultParquetBufferPool.Shared, 256, 256);
        empty.Content = new BufferWriter(DefaultParquetBufferPool.Shared, 256, 256);
        if (version == ParquetDataPageVersion.V1 && optional)
            empty.Content.Write(invalidV1 ? [100, 0, 0, 0] : [0, 0, 0, 0]);
        if (encoding is EncodingKind.DeltaBinaryPacked or EncodingKind.DeltaLengthByteArray && !omitDeltaHeader)
            empty.Content.Write([0x80, 0x01, 0x04, 0x00, 0x00]);
        if (extraPayload is not null) empty.Content.Write(extraPayload);
        empty.SetDataPageMetadata(0, 0, nullCount, 0, levelLength, encoding);
        var size = empty.Content.WrittenLength;
        empty.UncompressedContentSize = size;
        if (version == ParquetDataPageVersion.V1)
            ParquetMetadataThriftWriter.WriteDataPageHeaderV1(ref empty.Header, 0, encoding, size, size, default, null);
        else
            ParquetMetadataThriftWriter.WriteDataPageHeaderV2(ref empty.Header, 0, 0, nullCount, 0, levelLength, encoding, size, size, false, default, null);
        (pages[index - 1], pages[index]) = (pages[index], pages[index - 1]);
    }

    sealed class TwoRows(bool dictionary) : IPageStrategy
    {
        public DictionaryMode GetDictionaryMode() => dictionary ? DictionaryMode.Forced : DictionaryMode.Disabled;
        public bool ShouldDropDictionary(uint uniqueCount, uint totalRowCount, uint rowsSeen) => false;
        public uint GetNextDataPageRowCount(uint totalRowCount, uint rowsWritten) => Math.Min(2, totalRowCount - rowsWritten);
    }
}
