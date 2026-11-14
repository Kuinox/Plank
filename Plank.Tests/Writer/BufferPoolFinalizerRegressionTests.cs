using Plank.BloomFilters;
using Plank.Reading;
using Plank.Reading.Logical.Internal;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Writer;

internal sealed class BufferPoolFinalizerRegressionTests
{
    [Test]
    public void FailedPoolConstructionCanBeFinalized()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            using var pool = new DefaultParquetBufferPool((ParquetBufferRetentionPolicy)(-1));
        });
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            using var pool = new DefaultParquetBufferPool(ParquetBufferRetentionPolicy.Adaptive, -1);
        });
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }
}
