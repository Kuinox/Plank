namespace Plank.Writing;

internal static partial class ParquetThriftWriter
{
    enum PageType
    {
        DataPageV2 = 3
    }

    enum ParquetType
    {
        Boolean = 0,
        Int32 = 1,
        Int64 = 2,
        Int96 = 3,
        Float = 4,
        Double = 5,
        ByteArray = 6,
        FixedLenByteArray = 7
    }

    enum FieldRepetitionType
    {
        Required = 0,
        Optional = 1,
        Repeated = 2
    }

    enum ParquetEncoding
    {
        Plain = 0,
        PlainDictionary = 2,
        Rle = 3,
        BitPacked = 4,
        DeltaBinaryPacked = 5,
        DeltaLengthByteArray = 6,
        DeltaByteArray = 7,
        RleDictionary = 8,
        ByteStreamSplit = 9
    }

    enum CompressionCodec
    {
        Uncompressed = 0,
        Snappy = 1,
        Gzip = 2,
        Brotli = 4,
        Lz4 = 5,
        Zstd = 6
    }

    enum ConvertedType
    {
        Utf8 = 0,
        List = 3,
        Date = 6,
        TimeMicros = 8,
        TimestampMicros = 10
    }

    enum CompactType : byte
    {
        Stop = 0,
        BooleanTrue = 1,
        BooleanFalse = 2,
        Byte = 3,
        I16 = 4,
        I32 = 5,
        I64 = 6,
        Double = 7,
        Binary = 8,
        List = 9,
        Set = 10,
        Map = 11,
        Struct = 12
    }
}
