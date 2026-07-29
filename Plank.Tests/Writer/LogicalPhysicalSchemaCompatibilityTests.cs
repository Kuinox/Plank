using Plank.Schema;

namespace Plank.Tests.Writer;

internal sealed class LogicalPhysicalSchemaCompatibilityTests
{
    [Test]
    public void DateLogicalTypeRejectsInt64PhysicalType()
    {
        Assert.Throws<ArgumentException>(() =>
        {
            _ = new ParquetSchema([
                ColumnDefinition.RequiredLeaf("event_date", ParquetPhysicalType.Int64,
                    logicalType: new LogicalType.Date())
            ]);
        });
    }
}
