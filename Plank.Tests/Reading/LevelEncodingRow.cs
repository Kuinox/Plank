using Plank.Schema;

namespace Plank.Tests.Reading;

[ParquetSchema(AllowAllocatingValues = true)]
internal sealed partial class LevelEncodingRow
{
    public int? Flat { get; set; }
    public int[]? Nested { get; set; }
}
