using Plank.Reading;
using Plank.Reading.Logical.Internal;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Reading;

internal sealed class ByteStreamSplitSizeTests
{
    [Test]
    [Arguments(3)]
    [Arguments(5)]
    [Arguments(8)]
    public void RequiredPageRejectsIncorrectPayloadSize(int length)
    {
        var schema = new ParquetSchema([ColumnDefinition.RequiredLeaf("Value", ParquetPhysicalType.Int32)]);
        var column = schema.LeafColumns[0].Column;
        var payload = new byte[length];
        var header = Header(length, 1);
        var buffers = default(ColumnReadBuffers<int>);
        var page = default(ColumnChunkReader.FixedWidthPageState);
        try
        {
            Assert.Throws<CorruptParquetException>(() =>
                ColumnChunkReader.TryStartFixedWidthPageBatches(header, payload, column, 1,
                    ref buffers, DefaultParquetBufferPool.Shared, ref page, out _));
            Assert.Throws<CorruptParquetException>(() =>
                ColumnChunkReader.TryDecodeRequiredPageIntoNative(header, payload, column, 1,
                    ref buffers, DefaultParquetBufferPool.Shared, out _));
        }
        finally { buffers.Dispose(); }
    }

    [Test]
    public void AllNullPageRejectsExtraPayload()
    {
        var schema = new ParquetSchema([ColumnDefinition.OptionalLeaf("Value", ParquetPhysicalType.Float)]);
        var column = schema.LeafColumns[0].Column;
        // Five null definition levels followed by four bytes, although there are no physical values.
        byte[] payload = [10, 0, 1, 2, 3, 4];
        var header = new PageHeader(PageHeaderType.DataPageV2, 6, 6, 5, EncodingKind.ByteStreamSplit,
            HeaderLength: 1, RepetitionLevelsByteLength: 0, DefinitionLevelsByteLength: 2, NullCount: 5,
            IsCompressed: false, RepetitionLevelEncoding: EncodingKind.Rle,
            DefinitionLevelEncoding: EncodingKind.Rle, RowCount: 5);
        var buffers = default(ColumnReadBuffers<float?>);
        var page = default(ColumnChunkReader.FixedWidthPageState);
        try
        {
            Assert.Throws<CorruptParquetException>(() =>
                ColumnChunkReader.TryStartFixedWidthPageBatches(header, payload, column, 5,
                    ref buffers, DefaultParquetBufferPool.Shared, ref page, out _));
            Assert.Throws<CorruptParquetException>(() =>
                ColumnChunkReader.TryDecodeNullablePageIntoNative(header, payload, column, 5,
                    ref buffers, DefaultParquetBufferPool.Shared, out _));
        }
        finally { buffers.Dispose(); }
    }

    static PageHeader Header(int length, uint count)
        => new(PageHeaderType.DataPage, (uint)length, (uint)length, count, EncodingKind.ByteStreamSplit,
            HeaderLength: 1, RepetitionLevelsByteLength: 0, DefinitionLevelsByteLength: 0, NullCount: 0,
            IsCompressed: false, RepetitionLevelEncoding: EncodingKind.Rle,
            DefinitionLevelEncoding: EncodingKind.Rle, RowCount: count);
}
