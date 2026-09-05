using Plank.Schema;

namespace Plank.Tests.E2E;

[ParquetSchema(AllowAllocatingValues = true)]
internal sealed partial class GeneratedNullableArraySchema
{
    public string?[]? Names { get; set; }

    public string?[][]? NameGroups { get; set; }

    public List<string?>[]? NameLists { get; set; }

    public byte[]?[]? Payloads { get; set; }

    public int?[]? Numbers { get; set; }

    public Guid?[]? Identifiers { get; set; }

    public DateOnly?[]? Dates { get; set; }
}
