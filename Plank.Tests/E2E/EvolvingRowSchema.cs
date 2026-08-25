using Plank.Schema;

namespace Plank.Tests.E2E;

[ParquetSchema]
public sealed partial class EvolvingRowSchema
{
    [ParquetColumn("id")]
    public int Id { get; set; }

    [ParquetColumn("added")]
    public int Added { get; set; }

    [ParquetColumn("maybe")]
    public int? Maybe { get; set; }
}
