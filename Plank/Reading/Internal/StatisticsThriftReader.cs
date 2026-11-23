namespace Plank.Reading.Internal;

static class StatisticsThriftReader
{
    internal static EncodedStatistics Read(ref CompactProtocolReader reader, CompactProtocolType structType)
    {
        var minimumOffset = 0;
        var minimumLength = 0;
        var maximumOffset = 0;
        var maximumLength = 0;
        var nullCount = 0L;
        var distinctCount = 0L;
        var hasMinimum = false;
        var hasMaximum = false;
        var hasNullCount = false;
        var hasDistinctCount = false;
        var minimumExact = true;
        var maximumExact = true;

        reader.BeginStruct(structType);
        while (reader.TryReadFieldHeader(out var fieldId, out var type, out var inlineBool))
        {
            switch (fieldId)
            {
                // Deprecated min/max (fields 1/2) use signed byte order whatever the column's sort
                // order, and old parquet-mr wrote corrupt binary bounds. Pruning on them can skip
                // matching rows, so they are ignored: only min_value/max_value are bounds.
                case 3:
                    nullCount = reader.ReadI64(type);
                    if (nullCount < 0)
                        throw new CorruptParquetException("Statistics null count cannot be negative.");
                    hasNullCount = true;
                    break;
                case 4:
                    distinctCount = reader.ReadI64(type);
                    if (distinctCount < 0)
                        throw new CorruptParquetException("Statistics distinct count cannot be negative.");
                    hasDistinctCount = true;
                    break;
                case 5:
                {
                    var value = reader.ReadBinary(type);
                    maximumOffset = reader.Offset - value.Length;
                    maximumLength = value.Length;
                    hasMaximum = true;
                    break;
                }
                case 6:
                {
                    var value = reader.ReadBinary(type);
                    minimumOffset = reader.Offset - value.Length;
                    minimumLength = value.Length;
                    hasMinimum = true;
                    break;
                }
                case 7:
                    maximumExact = reader.ReadBool(type, inlineBool);
                    break;
                case 8:
                    minimumExact = reader.ReadBool(type, inlineBool);
                    break;
                default:
                    reader.Skip(type, inlineBool);
                    break;
            }
        }

        return new EncodedStatistics(minimumOffset, minimumLength, maximumOffset, maximumLength,
            nullCount, distinctCount, hasMinimum, hasMaximum, hasNullCount, hasDistinctCount,
            minimumExact, maximumExact);
    }
}
