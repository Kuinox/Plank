namespace Plank.Schema;

public enum EncodingKind
{
    Plain = 0,
    PlainDictionary = 1,
    RleDictionary = 2,
    Rle = 3,
    BitPacked = 4,
    DeltaBinaryPacked = 5,
    DeltaLengthByteArray = 6,
    DeltaByteArray = 7,
    ByteStreamSplit = 8
}
