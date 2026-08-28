using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Plank.Schema;

namespace Plank.Writing;

static class TimestampConversion
{
    /// <summary>The <see cref="DateTime"/> flag bits that carry <see cref="DateTimeKind"/>.</summary>
    const ulong KindMask = 0xC000_0000_0000_0000UL;

    const ulong TicksMask = 0x3FFF_FFFF_FFFF_FFFFUL;

    /// <summary>
    /// <c>floor((ticks - epoch) / d)</c> is <c>ticks / d - epoch / d</c> whenever <c>ticks</c> is
    /// non-negative and <c>epoch</c> is an exact multiple of <c>d</c> — which holds for every
    /// <see cref="DateTime.Ticks"/> value and for both the millisecond and microsecond divisors. The
    /// rewrite turns floor division into an unsigned division by a constant, which vectorises.
    /// </summary>
    const long EpochMillis = 62_135_596_800_000L;

    const long EpochMicros = 62_135_596_800_000_000L;

    /// <summary>
    /// Round-up reciprocals for <c>ticks / 10_000</c> and <c>ticks / 10</c>. For
    /// <c>m = ceil(2^(64 + shift) / divisor)</c>, the reciprocal quotient is exact while
    /// <c>ticks * (m * divisor - 2^(64 + shift)) &lt; 2^(64 + shift)</c>. At the maximum
    /// <see cref="DateTime.Ticks"/> value, those bounds are <c>MaxTicks * 2608 &lt; 2^73</c> for
    /// milliseconds and <c>MaxTicks * 4 &lt; 2^64</c> for microseconds. Neither reciprocal is exact
    /// over the full UInt64 range, so they may only be applied to tick values.
    /// </summary>
    const ulong MillisMagic = 944_473_296_573_929_043UL;

    const int MillisShift = 9;

    const ulong MicrosMagic = 1_844_674_407_370_955_162UL;

    const int MicrosShift = 0;

    internal static long DivideFloor(long dividend, long divisor)
    {
        var quotient = Math.DivRem(dividend, divisor, out var remainder);
        return remainder < 0 ? quotient - 1 : quotient;
    }

    internal static long FromDateTimeTicks(long ticks, TimeUnit unit)
    {
        var deltaTicks = ticks - DateTime.UnixEpoch.Ticks;
        return unit switch
        {
            TimeUnit.Millis => DivideFloor(deltaTicks, TimeSpan.TicksPerMillisecond),
            TimeUnit.Micros => DivideFloor(deltaTicks, 10),
            TimeUnit.Nanos => checked(deltaTicks * 100),
            _ => throw new ArgumentOutOfRangeException(nameof(unit), unit,
                "Time unit must be a defined TimeUnit value.")
        };
    }

    /// <summary>
    /// Converts a whole column of <see cref="DateTime"/> values to their Parquet representation,
    /// rejecting any value whose <see cref="DateTime.Kind"/> is not <paramref name="expectedKind"/>.
    /// </summary>
    internal static void ConvertDateTimes(ReadOnlySpan<DateTime> values, Span<long> destination,
        TimeUnit unit, DateTimeKind expectedKind)
    {
        switch (unit)
        {
            case TimeUnit.Millis:
                ConvertScaled(values, destination, expectedKind, MillisMagic, MillisShift, EpochMillis);
                break;
            case TimeUnit.Micros:
                ConvertScaled(values, destination, expectedKind, MicrosMagic, MicrosShift, EpochMicros);
                break;
            case TimeUnit.Nanos:
                ConvertNanos(values, destination, expectedKind);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(unit), unit,
                    "Time unit must be a defined TimeUnit value.");
        }
    }

    /// <summary>
    /// Compacts the present nullable values into <paramref name="destination"/> while converting them
    /// to their Parquet representation. Millisecond and microsecond conversion use the same exact
    /// reciprocal scaling as the required-value bulk path, avoiding a signed division per value.
    /// </summary>
    internal static int ConvertNullableDateTimes(ReadOnlySpan<DateTime?> values, Span<long> destination,
        TimeUnit unit, DateTimeKind expectedKind)
    {
        return unit switch
        {
            TimeUnit.Millis => ConvertNullableScaled(values, destination, expectedKind,
                MillisMagic, MillisShift, EpochMillis),
            TimeUnit.Micros => ConvertNullableScaled(values, destination, expectedKind,
                MicrosMagic, MicrosShift, EpochMicros),
            TimeUnit.Nanos => ConvertNullableNanos(values, destination, expectedKind),
            _ => throw new ArgumentOutOfRangeException(nameof(unit), unit,
                "Time unit must be a defined TimeUnit value.")
        };
    }

    static void ConvertScaled(ReadOnlySpan<DateTime> values, Span<long> destination,
        DateTimeKind expectedKind, ulong magic, int shift, long epoch)
    {
        var source = MemoryMarshal.Cast<DateTime, ulong>(values);
        var start = 0;
        if (TryGetKindBits(expectedKind, out var expectedBits))
            start = ConvertScaledVectorized(source, destination, expectedBits, magic, shift, epoch);

        for (var i = start; i < source.Length; i++)
        {
            RequireKind(values, i, expectedKind);
            destination[i] = ScaleTicks(source[i] & TicksMask, magic, shift, epoch);
        }
    }

    static int ConvertNullableScaled(ReadOnlySpan<DateTime?> values, Span<long> destination,
        DateTimeKind expectedKind, ulong magic, int shift, long epoch)
    {
        var count = 0;
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] is not { } value)
                continue;
            if (value.Kind != expectedKind)
                throw new InvalidOperationException(
                    $"DateTime values must have kind '{expectedKind}', got '{value.Kind}'.");

            destination[count++] = ScaleTicks((ulong)value.Ticks, magic, shift, epoch);
        }

        return count;
    }

    /// <summary>
    /// Converts as many whole vectors as the span holds and returns the index the scalar tail resumes
    /// at. Kind mismatches are only detected, not located: the caller's scalar loop re-walks the block
    /// to raise the error against the offending value.
    /// </summary>
    static int ConvertScaledVectorized(ReadOnlySpan<ulong> source, Span<long> destination,
        ulong expectedBits, ulong magic, int shift, long epoch)
    {
        if (!Vector256.IsHardwareAccelerated || source.Length < Vector256<ulong>.Count
            || destination.Length < source.Length)
            return 0;

        ref var input = ref MemoryMarshal.GetReference(source);
        ref var output = ref MemoryMarshal.GetReference(destination);
        var kindMask = Vector256.Create(KindMask);
        var ticksMask = Vector256.Create(TicksMask);
        var expected = Vector256.Create(expectedBits);
        var magicVector = Vector256.Create(magic);
        var bias = Vector256.Create(epoch);
        var mismatch = Vector256<ulong>.Zero;
        var count = (nuint)source.Length;
        var step = (nuint)Vector256<ulong>.Count;
        nuint i = 0;
        for (; i <= count - step; i += step)
        {
            var bits = Vector256.LoadUnsafe(ref input, i);
            mismatch |= (bits & kindMask) ^ expected;
            var scaled = MultiplyHigh(bits & ticksMask, magicVector) >> shift;
            (scaled.AsInt64() - bias).StoreUnsafe(ref output, i);
        }

        // A mismatch anywhere in the vectorized region means the whole span has to be re-walked by the
        // scalar loop, which is the only path that can name the offending value.
        return mismatch == Vector256<ulong>.Zero ? (int)i : 0;
    }

    static void ConvertNanos(ReadOnlySpan<DateTime> values, Span<long> destination,
        DateTimeKind expectedKind)
    {
        for (var i = 0; i < values.Length; i++)
        {
            RequireKind(values, i, expectedKind);
            destination[i] = checked((values[i].Ticks - DateTime.UnixEpoch.Ticks) * 100);
        }
    }

    static int ConvertNullableNanos(ReadOnlySpan<DateTime?> values, Span<long> destination,
        DateTimeKind expectedKind)
    {
        var count = 0;
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] is not { } value)
                continue;
            if (value.Kind != expectedKind)
                throw new InvalidOperationException(
                    $"DateTime values must have kind '{expectedKind}', got '{value.Kind}'.");

            destination[count++] = checked((value.Ticks - DateTime.UnixEpoch.Ticks) * 100);
        }

        return count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void RequireKind(ReadOnlySpan<DateTime> values, int index, DateTimeKind expectedKind)
    {
        var kind = values[index].Kind;
        if (kind != expectedKind)
            throw new InvalidOperationException(
                $"DateTime values must have kind '{expectedKind}', got '{kind}'.");
    }

    /// <summary>
    /// Maps the two kinds a timestamp column can require onto their raw <see cref="DateTime"/> flag
    /// bits. <see cref="DateTimeKind.Local"/> has two encodings, so it has no single-comparison form
    /// and falls back to the scalar path.
    /// </summary>
    static bool TryGetKindBits(DateTimeKind expectedKind, out ulong bits)
    {
        switch (expectedKind)
        {
            case DateTimeKind.Unspecified:
                bits = 0;
                return true;
            case DateTimeKind.Utc:
                bits = 0x4000_0000_0000_0000UL;
                return true;
            default:
                bits = 0;
                return false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong MultiplyHigh(ulong left, ulong right)
        => Math.BigMul(left, right, out _);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static long ScaleTicks(ulong ticks, ulong magic, int shift, long epoch)
        => (long)(MultiplyHigh(ticks, magic) >> shift) - epoch;

    /// <summary>64x64 to high-64 multiply built from the 32x32 partial products.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector256<ulong> MultiplyHigh(Vector256<ulong> left, Vector256<ulong> right)
    {
        var lowMask = Vector256.Create(0xFFFF_FFFFUL);
        var leftLow = left & lowMask;
        var leftHigh = left >>> 32;
        var rightLow = right & lowMask;
        var rightHigh = right >>> 32;

        var lowLow = leftLow * rightLow;
        var highLow = leftHigh * rightLow;
        var lowHigh = leftLow * rightHigh;
        var highHigh = leftHigh * rightHigh;

        var middle = (lowLow >>> 32) + (highLow & lowMask) + (lowHigh & lowMask);
        return highHigh + (highLow >>> 32) + (lowHigh >>> 32) + (middle >>> 32);
    }
}
