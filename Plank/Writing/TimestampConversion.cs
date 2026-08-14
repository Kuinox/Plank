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
}
