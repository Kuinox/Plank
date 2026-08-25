using Plank.Schema;

namespace Plank.Tests.Reading;

[ParquetSchema]
public sealed partial class ReaderAllocationRowSchema
{
    [ParquetColumn("Value")]
    public int Value { get; set; }
}
