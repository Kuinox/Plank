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
    /// <see cref="ParquetPhysicalType.Double"/>.
    /// </summary>
    /// <remarks>
    /// ALP is a recent addition to the Parquet format and is classified there as Preview: the byte
    /// layout is final, but reader support across the ecosystem is still spreading. Check that every
    /// Parquet library that will read the file supports ALP before selecting it.
    /// See <see href="https://github.com/apache/parquet-format/blob/master/Encodings.md#adaptive-lossless-floating-point-alp--10"/>.
    /// </remarks>
    Alp = 9
}
