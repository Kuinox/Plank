using Plank.Reading;
using Plank.Reading.Logical.Internal;
using Plank.Reading.Physical;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Reading;

internal sealed class PageEncodingCompatibilityTests
{
    [Test]
    [Arguments(ParquetPhysicalType.Float)]
    [Arguments(ParquetPhysicalType.Double)]
    [Arguments(ParquetPhysicalType.Boolean)]
    public void DeltaBinaryPackedRejectsNonIntegerPhysicalTypes(ParquetPhysicalType type)
    {
        // PR #174's payload and physical types, exercised at the shared page dispatch.
        byte[] payload = [0x80, 0x01, 0x04, 0x01, 0x00];
        AssertRejected(PageHeaderType.DataPage, type, EncodingKind.DeltaBinaryPacked, payload);
    }

    [Test]
    public void InvalidValueEncodingsAreRejectedForEveryPageKind()
    {
        foreach (var pageType in new[] { PageHeaderType.DataPage, PageHeaderType.DataPageV2, PageHeaderType.DictionaryPage })
        foreach (var (type, encoding) in new[] {
            (ParquetPhysicalType.Float, EncodingKind.DeltaBinaryPacked),
            (ParquetPhysicalType.Int32, EncodingKind.DeltaLengthByteArray),
            (ParquetPhysicalType.Int32, EncodingKind.DeltaByteArray),
            (ParquetPhysicalType.Int32, EncodingKind.Rle),
            (ParquetPhysicalType.Boolean, EncodingKind.ByteStreamSplit),
            (ParquetPhysicalType.ByteArray, EncodingKind.ByteStreamSplit),
            (ParquetPhysicalType.Int64, EncodingKind.Alp),
            (ParquetPhysicalType.Int32, EncodingKind.BitPacked) })
            AssertRejected(pageType, type, encoding, []);
    }

    [Test]
    public void DictionaryPagesRejectNonPlainEncodingsEvenWhenCompatible()
    {
        foreach (var encoding in new[] { EncodingKind.RleDictionary, EncodingKind.DeltaBinaryPacked, EncodingKind.ByteStreamSplit })
            AssertRejected(PageHeaderType.DictionaryPage, ParquetPhysicalType.Int32, encoding, []);
    }

    [Test]
    public void OldDictionaryPageEncodingIsDecodedAsPlain()
    {
        var column = new ParquetSchema([ColumnDefinition.RequiredLeaf("Value", ParquetPhysicalType.Int32)]).LeafColumns[0].Column;
        var buffers = default(ColumnReadBuffers<int>);
        try
        {
            var header = Header(PageHeaderType.DictionaryPage, EncodingKind.PlainDictionary);
            if (!ColumnChunkReader.TryDecodeDictionaryPageIntoNative(header, [42, 0, 0, 0], column, ref buffers, DefaultParquetBufferPool.Shared))
                throw new InvalidOperationException("Legacy dictionary page was not decoded.");
        }
        finally { buffers.Dispose(); }
    }

    [Test]
    public void DataPageLegacyDictionaryAndLevelEncodingsPassValidation()
    {
        var column = new ParquetSchema([ColumnDefinition.RequiredLeaf("Value", ParquetPhysicalType.Int32)]).LeafColumns[0].Column;
        var buffers = default(ColumnReadBuffers<int>);
        try
        {
            foreach (var pageType in new[] { PageHeaderType.DataPage, PageHeaderType.DataPageV2 })
            {
                var header = Header(pageType, EncodingKind.PlainDictionary) with { RepetitionLevelEncoding = EncodingKind.BitPacked, DefinitionLevelEncoding = EncodingKind.Rle };
                if (ColumnChunkReader.TryDecodeDictionaryPageIntoNative(header, [], column, ref buffers, DefaultParquetBufferPool.Shared))
                    throw new InvalidOperationException("Data page treated as dictionary page.");
            }
        }
        finally { buffers.Dispose(); }
    }

    [Test]
    [Arguments(ParquetDataPageVersion.V1, false)]
    [Arguments(ParquetDataPageVersion.V2, false)]
    [Arguments(ParquetDataPageVersion.V1, true)]
    [Arguments(ParquetDataPageVersion.V2, true)]
    public void FlatAndNestedReadersRejectMalformedPageEncoding(ParquetDataPageVersion version, bool nested)
    {
        var schema = new ParquetSchema([ColumnDefinition.Leaf("value", ParquetPhysicalType.Int32,
            new ColumnOptions(nested ? ParquetRepetition.Repeated : ParquetRepetition.Required, [EncodingKind.Plain]))]);
        using var output = new MemoryStream();
        using (var writer = schema.CreateWriter(output, new ParquetWriterOptions { Compression = CompressionKind.None, DataPageVersion = version }))
        {
            var group = writer.StartRowGroup();
            if (nested)
            {
                var column = group.CreateSerializedColumn<int[]>(schema.LeafColumns[0]);
                column.Serialize([[1, 2], [3]]);
                group.Write(column);
            }
            else
            {
                var column = group.CreateSerializedColumn<int>(schema.LeafColumns[0]);
                column.Serialize([1, 2]);
                group.Write(column);
            }
            writer.CloseFile();
        }
        var file = output.ToArray();
        int offset;
        using (var physical = new ParquetFileReader())
        {
            physical.Reset(new MemoryStream(file, writable: false));
            offset = checked((int)physical.Metadata.ColumnChunk(0, 0).DataPageOffset);
        }
        var original = PageHeaderReader.Read(file.AsSpan(offset));
        var changed = false;
        for (var i = offset; i < offset + original.HeaderLength; i++)
        {
            if (file[i] != 0) continue;
            file[i] = 20; // compact protocol zigzag encoding of ALP (10)
            try
            {
                var candidate = PageHeaderReader.Read(file.AsSpan(offset));
                if (candidate == original with { Encoding = EncodingKind.Alp }) { changed = true; break; }
            }
            catch (Exception) { /* Not the encoding field. */ }
            file[i] = 0;
        }
        if (!changed) throw new InvalidOperationException("Could not locate the page encoding.");
        using var source = new MemoryReadSource(file);
        using var reader = schema.CreateReader(source);
        Assert.Throws<CorruptParquetException>(() =>
        {
            if (nested)
                foreach (var buffer in reader.RowGroups[0].NestedColumn<int>(0)) { _ = buffer.Count; }
            else
                foreach (var buffer in reader.RowGroups[0].Column<int>(0)) { _ = buffer.Count; }
        });
    }

    static void AssertRejected(PageHeaderType pageType, ParquetPhysicalType type, EncodingKind encoding, byte[] payload)
    {
        var column = new ParquetSchema([ColumnDefinition.RequiredLeaf("Value", type)]).LeafColumns[0].Column;
        var buffers = default(ColumnReadBuffers<int>);
        try
        {
            var header = Header(pageType, encoding);
            Assert.Throws<CorruptParquetException>(() => ColumnChunkReader.TryDecodeDictionaryPageIntoNative(
                header, payload, column, ref buffers, DefaultParquetBufferPool.Shared));
        }
        finally { buffers.Dispose(); }
    }

    static PageHeader Header(PageHeaderType type, EncodingKind encoding)
        => new(type, 5, 5, 1, encoding, HeaderLength: 1, RepetitionLevelsByteLength: 0,
            DefinitionLevelsByteLength: 0, NullCount: 0, IsCompressed: false,
            RepetitionLevelEncoding: EncodingKind.Rle, DefinitionLevelEncoding: EncodingKind.Rle, RowCount: 1);
}
