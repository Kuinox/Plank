namespace Plank.Schema;

static class EncodingCompatibility
{
    internal static void Validate(Column column)
        => Validate(column.Name, column.PhysicalType, column.Options);

    internal static void Validate(string name, ParquetPhysicalType physicalType, ColumnOptions options)
    {
        foreach (var encoding in options.Encodings)
            if (!IsSupported(physicalType, encoding))
                throw new NotSupportedException(
                    $"Encoding '{encoding}' does not support physical type '{physicalType}' for column '{name}'.");
    }

    internal static bool IsSupported(ParquetPhysicalType physicalType, EncodingKind encoding)
        => encoding switch
        {
            EncodingKind.Plain => true,
            EncodingKind.PlainDictionary or EncodingKind.RleDictionary => true,
            EncodingKind.Rle => physicalType == ParquetPhysicalType.Boolean,
            EncodingKind.BitPacked => false,
            EncodingKind.DeltaBinaryPacked =>
                physicalType is ParquetPhysicalType.Int32 or ParquetPhysicalType.Int64,
            EncodingKind.DeltaLengthByteArray => physicalType == ParquetPhysicalType.ByteArray,
            EncodingKind.DeltaByteArray => physicalType == ParquetPhysicalType.ByteArray,
            EncodingKind.ByteStreamSplit =>
                physicalType is ParquetPhysicalType.Int32 or ParquetPhysicalType.Int64
                    or ParquetPhysicalType.Float or ParquetPhysicalType.Double
                    or ParquetPhysicalType.FixedLenByteArray,
            _ => false
        };
}
