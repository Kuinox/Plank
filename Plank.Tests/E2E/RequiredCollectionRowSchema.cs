using Plank.Schema;

namespace Plank.Tests.E2E;

[ParquetSchema(AllowAllocatingValues = true)]
internal sealed partial class RequiredCollectionRowSchema
{
    public List<int> Values { get; set; } = [];

    public Dictionary<int, int> Scores { get; set; } = [];
}
