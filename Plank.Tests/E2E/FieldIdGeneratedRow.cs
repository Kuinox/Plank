using Plank.Schema;

namespace Plank.Tests.E2E;

[ParquetSchema]
internal sealed partial class FieldIdGeneratedRow
{
    [ParquetColumn(FieldId = 73)]
    public int Value { get; set; }
}
