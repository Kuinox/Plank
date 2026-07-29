namespace Plank.Writing;

static class TimestampConversion
{
    internal static long DivideFloor(long dividend, long divisor)
    {
        var quotient = Math.DivRem(dividend, divisor, out var remainder);
        return remainder < 0 ? quotient - 1 : quotient;
    }
}
