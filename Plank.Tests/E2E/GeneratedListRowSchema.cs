using Plank.Schema;

namespace Plank.Tests.E2E;

[ParquetSchema(AllowAllocatingValues = true)]
internal sealed partial class GeneratedListRowSchema
{
    public int?[]? Values { get; set; }
}
