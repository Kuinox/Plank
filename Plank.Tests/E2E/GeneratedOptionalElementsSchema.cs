using Plank.Schema;

namespace Plank.Tests.E2E;

[ParquetSchema(AllowAllocatingValues = true)]
internal sealed partial class GeneratedOptionalElementsSchema
{
    public List<OptionalEntry?>? Items { get; set; }

    public int?[]?[]? Grid { get; set; }
}

internal sealed record OptionalEntry
{
    public int? Price { get; init; }

    public string? Label { get; init; }
}
