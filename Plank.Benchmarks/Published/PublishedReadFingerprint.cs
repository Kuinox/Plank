using System.Text;

namespace Plank.Benchmarks.Published;

static class PublishedReadFingerprint
{
    const ulong Offset = 14695981039346656037UL;
    const ulong Prime = 1099511628211UL;

    public static PublishedReadResult Expected(PublishedBenchmarkDataSet dataSet)
    {
        var aggregate = Start();
        long valueCount = 0;
        for (var rowGroupIndex = 0; rowGroupIndex < dataSet.RowGroupCount; rowGroupIndex++)
            for (var columnIndex = 0; columnIndex < dataSet.Columns.Count; columnIndex++)
            {
                var values = dataSet.Columns[columnIndex].Values[rowGroupIndex];
                var fingerprint = StartPiece(columnIndex, rowGroupIndex, values.Length);
                for (var sampleIndex = 0; sampleIndex < 3 && values.Length != 0; sampleIndex++)
                    fingerprint = AddValue(fingerprint, values.GetValue(SamplePosition(sampleIndex, values.Length)));
                var piece = new PublishedReadResult(values.Length, fingerprint);
                aggregate = Combine(aggregate, piece);
                valueCount = checked(valueCount + values.Length);
            }
        return new PublishedReadResult(valueCount, aggregate);
    }

    public static ulong Start()
        => Offset;

    public static ulong StartPiece(int columnIndex, int rowGroupIndex, int valueCount)
    {
        var hash = AddUInt64(Offset, unchecked((uint)columnIndex));
        hash = AddUInt64(hash, unchecked((uint)rowGroupIndex));
        return AddUInt64(hash, unchecked((uint)valueCount));
    }

    public static ulong Combine(ulong aggregate, PublishedReadResult piece)
    {
        aggregate = AddUInt64(aggregate, unchecked((ulong)piece.ValueCount));
        return AddUInt64(aggregate, piece.Fingerprint);
    }

    public static int SamplePosition(int sampleIndex, int valueCount)
        => sampleIndex switch
        {
            0 => 0,
            1 => valueCount / 2,
            2 => valueCount - 1,
            _ => throw new ArgumentOutOfRangeException(nameof(sampleIndex))
        };

    public static ulong AddValue(ulong hash, object? value)
        => value switch
        {
            null => AddUInt64(hash, ulong.MaxValue),
            bool boolean => AddUInt64(hash, boolean ? 1UL : 0UL),
            int integer => AddUInt64(hash, unchecked((uint)integer)),
            long integer => AddUInt64(hash, unchecked((ulong)integer)),
            double number => AddUInt64(hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(number))),
            DateTime timestamp => AddUInt64(hash,
                unchecked((ulong)new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)).UtcTicks)),
            DateTimeOffset timestamp => AddUInt64(hash, unchecked((ulong)timestamp.UtcTicks)),
            string text => AddBytes(hash, Encoding.UTF8.GetBytes(text)),
            byte[] bytes => AddBytes(hash, bytes),
            _ => throw new NotSupportedException($"Unsupported fingerprint value '{value.GetType()}'.")
        };

    public static ulong AddBytes(ulong hash, ReadOnlySpan<byte> value)
    {
        hash = AddUInt64(hash, unchecked((uint)value.Length));
        for (var index = 0; index < value.Length; index++)
        {
            hash ^= value[index];
            hash *= Prime;
        }
        return hash;
    }

    static ulong AddUInt64(ulong hash, ulong value)
    {
        for (var shift = 0; shift < 64; shift += 8)
        {
            hash ^= (byte)(value >> shift);
            hash *= Prime;
        }
        return hash;
    }
}
