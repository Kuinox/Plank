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

    [Test]
    public void DateLogicalTypeCannotBeMutatedToInt64PhysicalType()
    {
        var definition = ColumnDefinition.RequiredLeaf("event_date", ParquetPhysicalType.Int32,
            logicalType: new LogicalType.Date()) with
        {
            PhysicalType = ParquetPhysicalType.Int64
        };

        Assert.Throws<ArgumentException>(() => _ = new ParquetSchema([definition]));
    }
}
