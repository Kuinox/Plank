using Plank.Reading;
using Plank.Reading.Logical.Internal;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Reading;

internal sealed class DeltaPhysicalTypeTests
{
    [Test]
    [Arguments(ParquetPhysicalType.Float)]
    [Arguments(ParquetPhysicalType.Double)]
    [Arguments(ParquetPhysicalType.Boolean)]
    public void DeltaBinaryPackedRejectsNonIntegerPhysicalTypes(ParquetPhysicalType type)
    {
        var schema = new ParquetSchema([ColumnDefinition.RequiredLeaf("Value", type)]);
        byte[] payload = [0x80, 0x01, 0x04, 0x01, 0x00];
        var header = new PageHeader(PageHeaderType.DataPage, 5, 5, 1, EncodingKind.DeltaBinaryPacked,
            HeaderLength: 1, RepetitionLevelsByteLength: 0, DefinitionLevelsByteLength: 0, NullCount: 0,
            IsCompressed: false, RepetitionLevelEncoding: EncodingKind.Rle,
            DefinitionLevelEncoding: EncodingKind.Rle, RowCount: 1);
        var buffers = default(ColumnReadBuffers<int>);
        var page = default(ColumnChunkReader.EncodedPageState);
        try
        {
            Assert.Throws<CorruptParquetException>(() =>
                ColumnChunkReader.TryStartEncodedPageBatches(header, payload, default,
                    schema.LeafColumns[0].Column, 1, ref buffers, DefaultParquetBufferPool.Shared,
                    ref page, out _));
        }
        finally { buffers.Dispose(); }
    }
}
