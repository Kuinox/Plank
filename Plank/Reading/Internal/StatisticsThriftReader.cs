namespace Plank.Reading.Internal;

static class StatisticsThriftReader
{
    internal static EncodedStatistics Read(ref CompactProtocolReader reader)
    {
        var legacyMinimumOffset = 0;
        var legacyMinimumLength = 0;
        var legacyMaximumOffset = 0;
        var legacyMaximumLength = 0;
        var minimumOffset = 0;
        var minimumLength = 0;
        var maximumOffset = 0;
        var maximumLength = 0;
        var nullCount = 0L;
        var distinctCount = 0L;
        var hasLegacyMinimum = false;
        var hasLegacyMaximum = false;
        var hasMinimum = false;
        var hasMaximum = false;
        var hasNullCount = false;
        var hasDistinctCount = false;
        var minimumExact = true;
        var maximumExact = true;

        reader.BeginStruct();
        while (reader.TryReadFieldHeader(out var fieldId, out var type, out var inlineBool))
        {
            switch (fieldId)
            {
                case 1:
                {
                    var value = reader.ReadBinary();
                    legacyMaximumOffset = reader.Offset - value.Length;
                    legacyMaximumLength = value.Length;
                    hasLegacyMaximum = true;
                    break;
                }
                case 2:
                {
                    var value = reader.ReadBinary();
                    legacyMinimumOffset = reader.Offset - value.Length;
                    legacyMinimumLength = value.Length;
                    hasLegacyMinimum = true;
                    break;
                }
                case 3:
                    nullCount = reader.ReadI64();
                    if (nullCount < 0)
                        throw new CorruptParquetException("Statistics null count cannot be negative.");
                    hasNullCount = true;
                    break;
                case 4:
                    distinctCount = reader.ReadI64();
                    if (distinctCount < 0)
                        throw new CorruptParquetException("Statistics distinct count cannot be negative.");
                    hasDistinctCount = true;
                    break;
                case 5:
                {
                    var value = reader.ReadBinary();
                    maximumOffset = reader.Offset - value.Length;
                    maximumLength = value.Length;
                    hasMaximum = true;
                    break;
                }
                case 6:
                {
                    var value = reader.ReadBinary();
                    minimumOffset = reader.Offset - value.Length;
                    minimumLength = value.Length;
                    hasMinimum = true;
                    break;
                }
                case 7:
                    maximumExact = reader.ReadBool(inlineBool);
                    break;
                case 8:
                    minimumExact = reader.ReadBool(inlineBool);
                    break;
                default:
                    reader.Skip(type, inlineBool);
                    break;
            }
        }

        if (!hasMinimum && hasLegacyMinimum)
        {
            minimumOffset = legacyMinimumOffset;
            minimumLength = legacyMinimumLength;
            hasMinimum = true;
        }
        if (!hasMaximum && hasLegacyMaximum)
        {
            maximumOffset = legacyMaximumOffset;
            maximumLength = legacyMaximumLength;
            hasMaximum = true;
        }

        return new EncodedStatistics(minimumOffset, minimumLength, maximumOffset, maximumLength,
            nullCount, distinctCount, hasMinimum, hasMaximum, hasNullCount, hasDistinctCount,
            minimumExact, maximumExact);
    }
}
