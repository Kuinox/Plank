using Plank.Schema;

namespace Plank.Writing;

static class TimestampConversion
{
    internal static long DivideFloor(long dividend, long divisor)
    {
        var quotient = Math.DivRem(dividend, divisor, out var remainder);
        return remainder < 0 ? quotient - 1 : quotient;
    }

    internal static long FromDateTimeTicks(long ticks, TimeUnit unit)
        => unit switch
        {
            TimeUnit.Millis => ticks / TimeSpan.TicksPerMillisecond -
                DateTime.UnixEpoch.Ticks / TimeSpan.TicksPerMillisecond,
            TimeUnit.Micros => ticks / 10 - DateTime.UnixEpoch.Ticks / 10,
            TimeUnit.Nanos => checked((ticks - DateTime.UnixEpoch.Ticks) * 100),
            _ => throw new ArgumentOutOfRangeException(nameof(unit), unit,
                "Time unit must be a defined TimeUnit value.")
        };
}
