using Plank.Schema;

namespace Plank.Tests.E2E;

[ParquetSchema]
internal sealed partial class CustomMappedRowSchema
{
    [ParquetColumn("id", Converter = typeof(CustomMappedValueConverter),
        Encodings = [EncodingKind.DeltaBinaryPacked])]
    public CustomMappedValue Id { get; set; }

    [ParquetColumn("parent_id", Converter = typeof(CustomMappedValueConverter),
        Encodings = [EncodingKind.RleDictionary])]
    public CustomMappedValue? ParentId { get; set; }
}
