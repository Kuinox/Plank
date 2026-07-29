using Plank.Schema;

namespace Plank.Tests.Writer;

internal sealed class UnprojectableRuntimeSchemaTests
{
    [Test]
    public void EmptyGroupIsRejectedInsteadOfSilentlyProducingNoLeaves()
    {
        Assert.Throws<ArgumentException>(() =>
        {
            _ = new ParquetSchema([
                ColumnDefinition.RequiredGroup("empty")
            ]);
        });
    }
}
