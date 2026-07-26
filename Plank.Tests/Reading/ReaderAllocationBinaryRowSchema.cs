using Plank.Schema;

namespace Plank.Tests.Reading;

[ParquetSchema]
public sealed partial class ReaderAllocationBinaryRowSchema
{
    [ParquetColumn("Value", Encodings = [EncodingKind.DeltaByteArray])]
    public byte[]? Value { get; set; }
}
