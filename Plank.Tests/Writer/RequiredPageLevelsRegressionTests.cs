using Plank.BloomFilters;
using Plank.Reading;
using Plank.Reading.Logical.Internal;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Writer;

internal sealed class RequiredPageLevelsRegressionTests
{
    [Test]
    [Arguments(ParquetPhysicalType.Int32)]
    [Arguments(ParquetPhysicalType.Int64)]
    public void RequiredDeltaPageRejectsDefinitionLevels(ParquetPhysicalType physicalType)
    {
        var header = new PageHeader(PageHeaderType.DataPageV2, 0, 0, 1024,
            EncodingKind.DeltaBinaryPacked, 0, 0, 128, 146, false, EncodingKind.Rle, EncodingKind.Rle, 1024);
        var column = new Column("value", physicalType);
        Assert.Throws<CorruptParquetException>(() =>
        {
            using var pool = new DefaultParquetBufferPool();
            var page = new ColumnChunkReader.EncodedPageState();
            if (physicalType == ParquetPhysicalType.Int32)
            {
                var buffers = new ColumnReadBuffers<int>();
                ColumnChunkReader.TryStartEncodedPageBatches(header, new byte[128], default, column,
                    1024, ref buffers, pool, ref page, out _);
            }
            else
            {
                var buffers = new ColumnReadBuffers<long>();
                ColumnChunkReader.TryStartEncodedPageBatches(header, new byte[128], default, column,
                    1024, ref buffers, pool, ref page, out _);
            }
        });
    }
}
