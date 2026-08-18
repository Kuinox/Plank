namespace Plank.Schema;

/// <summary>
/// A Parquet encoding. The values match the <c>Encoding</c> enum of the Parquet format, so they are
/// the numbers written to and read from file metadata. Value 1 is deliberately absent: the format
/// reserved it for <c>GROUP_VAR_INT</c>, which was never used and has been removed.
/// </summary>
public enum EncodingKind : ushort
{
    Plain = 0,

    /// <summary>Deprecated by the Parquet format; use <see cref="RleDictionary"/> for data pages.</summary>
    PlainDictionary = 2,
    Rle = 3,

    /// <summary>Deprecated by the Parquet format; superseded by <see cref="Rle"/>.</summary>
    BitPacked = 4,
    DeltaBinaryPacked = 5,
    DeltaLengthByteArray = 6,
    DeltaByteArray = 7,
    RleDictionary = 8,
    ByteStreamSplit = 9,

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
    Alp = 10
}
