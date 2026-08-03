using Plank.Schema;

namespace Plank.Tests.E2E;

[ParquetSchema]
public sealed partial class DecimalRowSchema
{
    [ParquetColumn("amount", Precision = 18, Scale = 4)]
    public decimal Amount { get; set; }

    [ParquetColumn("optional_amount", Precision = 29, Scale = 8,
        Encodings = [EncodingKind.ByteStreamSplit])]
    public decimal? OptionalAmount { get; set; }
}
