using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Plank.Schema;

namespace Plank.Reading.Logical.Internal;

static partial class ColumnChunkReader
{
    // Value-type specialization lets each loop inline its unit's arithmetic without
    // a per-value unit switch or indirect converter call.
    interface ITimestampUnit
    {
        static abstract long ToTicks(long raw);
    }

    readonly struct MillisTimestamp : ITimestampUnit
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long ToTicks(long raw)
        {
            // Inclusive bounds already account for scaling and the Unix epoch.
            if (raw < -62_135_596_800_000 || raw > 253_402_300_799_999)
                ThrowTimestampOutOfRange(raw);
            return raw * TimeSpan.TicksPerMillisecond + DateTime.UnixEpoch.Ticks;
        }
    }

    readonly struct MicrosTimestamp : ITimestampUnit
    {
        internal const long Minimum = -62_135_596_800_000_000;
        internal const long Maximum = 253_402_300_799_999_999;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long ToTicks(long raw)
        {
            if (raw < Minimum || raw > Maximum)
                ThrowTimestampOutOfRange(raw);
            return raw * 10 + DateTime.UnixEpoch.Ticks;
        }
    }

    readonly struct NanosTimestamp : ITimestampUnit
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long ToTicks(long raw)
            // Every Int64 nanosecond value fits DateTime after conversion. Preserve
            // truncation toward zero, including negative sub-tick timestamps.
            => raw / 100 + DateTime.UnixEpoch.Ticks;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void ThrowTimestampOutOfRange(long raw)
        => throw new CorruptParquetException(
            $"Timestamp value {raw} is outside the range representable by a date and time.");

    static void MaterializeAllPresentNullableDateTimes(ReadOnlySpan<long> raw,
        Span<DateTime?> destination, TimeUnit unit, DateTimeKind kind)
    {
        switch (unit)
        {
            case TimeUnit.Millis:
                MaterializeAllPresentNullableDateTimes<MillisTimestamp>(raw, destination, kind);
                break;
            case TimeUnit.Micros:
                MaterializeAllPresentNullableDateTimes<MicrosTimestamp>(raw, destination, kind);
                break;
            case TimeUnit.Nanos:
                MaterializeAllPresentNullableDateTimes<NanosTimestamp>(raw, destination, kind);
                break;
            default:
                throw new CorruptParquetException("Timestamp projection requires a timestamp logical type.");
        }
    }

    static void MaterializeDateTimes<T>(ReadOnlySpan<long> raw, Span<T> destination,
        LogicalType? logicalType) where T : struct
    {
        var timestamp = GetTimestampLogicalType(logicalType);
        if (typeof(T) == typeof(DateTimeOffset) && !timestamp.IsAdjustedToUtc)
            throw new NotSupportedException(
                "DateTimeOffset projection is not supported for timestamps with local semantics.");
        var kind = timestamp.IsAdjustedToUtc ? DateTimeKind.Utc : DateTimeKind.Unspecified;
        switch (timestamp.Unit)
        {
            case TimeUnit.Millis:
                MaterializeDateTimes<T, MillisTimestamp>(raw, destination, kind);
                break;
            case TimeUnit.Micros:
                MaterializeDateTimes<T, MicrosTimestamp>(raw, destination, kind);
                break;
            case TimeUnit.Nanos:
                MaterializeDateTimes<T, NanosTimestamp>(raw, destination, kind);
                break;
            default:
                throw new CorruptParquetException("Timestamp projection requires a timestamp logical type.");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static void MaterializeDateTimes<T, TUnit>(ReadOnlySpan<long> raw, Span<T> destination,
        DateTimeKind kind)
        where T : struct
        where TUnit : struct, ITimestampUnit
    {
        // Decode backwards: raw values can occupy the front of the destination,
        // and DateTimeOffset expands each Int64 into a larger value.
        if (typeof(T) == typeof(DateTime))
        {
            var typed = MemoryMarshal.Cast<T, DateTime>(destination);
            for (var i = typed.Length - 1; i >= 0; i--)
                typed[i] = new DateTime(TUnit.ToTicks(raw[i]), kind);
        }
        else if (typeof(T) == typeof(DateTimeOffset))
        {
            var typed = MemoryMarshal.Cast<T, DateTimeOffset>(destination);
            for (var i = typed.Length - 1; i >= 0; i--)
                typed[i] = new DateTimeOffset(TUnit.ToTicks(raw[i]), TimeSpan.Zero);
        }
        else
            throw new InvalidOperationException($"Timestamp materialization declined '{typeof(T)}'.");
    }
}
