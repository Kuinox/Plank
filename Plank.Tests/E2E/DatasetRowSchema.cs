using Plank.Schema;

namespace Plank.Tests.E2E;

[ParquetSchema]
internal sealed partial class DatasetRowSchema
{
    public int Value { get; set; }

    public ReadOnlyMemory<byte> Path { get; set; }
}
