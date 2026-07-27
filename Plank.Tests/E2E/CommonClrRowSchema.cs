using Plank.Schema;

namespace Plank.Tests.E2E;

[ParquetSchema(AllowAllocatingValues = true)]
public sealed partial class CommonClrRowSchema
{
    public string Name { get; set; } = string.Empty;

    public string? Alias { get; set; }

    public Guid Id { get; set; }

    public Guid? ParentId { get; set; }
}
