using System.Diagnostics.CodeAnalysis;

namespace Plank.Schema;

public enum EncodingKind : ushort
{
    Plain = 0,
    PlainDictionary = 1,
    RleDictionary = 2,
    Rle = 3,
    BitPacked = 4,
    DeltaBinaryPacked = 5,
    DeltaLengthByteArray = 6,
    DeltaByteArray = 7,
    ByteStreamSplit = 8,

    /// <summary>
    /// Adaptive Lossless floating-Point encoding for <see cref="ParquetPhysicalType.Float"/> and
    /// <see cref="ParquetPhysicalType.Double"/>. This encoding is in Preview in the Parquet format.
    /// </summary>
    [Experimental("PLANK001", UrlFormat = "https://github.com/apache/parquet-format/blob/master/Encodings.md#adaptive-lossless-floating-point-alp--10")]
    Alp = 9
}
