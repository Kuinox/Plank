using Plank.Schema;

namespace Plank.Tests.E2E;

[ParquetSchema]
public sealed partial class GuidByteStreamSplitRowSchema
{
    [ParquetColumn("id", Encodings = [EncodingKind.ByteStreamSplit])]
    public Guid Id { get; set; }
}
