using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Writing;

[NotInParallel]
internal sealed class TimestampConversionTests
{
    static readonly TimeUnit[] Units = [TimeUnit.Millis, TimeUnit.Micros, TimeUnit.Nanos];

    /// <summary>
    /// The bulk converter replaces floor division by a constant with a reciprocal multiply, so it has
    /// to agree with the scalar reference at every length — including the ones that leave a partial
    /// vector — and at the ends of the representable tick range.
    /// </summary>
    [Test]
    public void ConvertDateTimesMatchesTheScalarReference()
    {
        var random = new Random(20260817);
        foreach (var unit in Units)
        {
            var (minTicks, maxTicks) = RepresentableTickRange(unit);
            for (var length = 0; length <= 37; length++)
            {
                var values = new DateTime[length];
                for (var i = 0; i < length; i++)
                    values[i] = new DateTime(random.NextInt64(minTicks, maxTicks + 1), DateTimeKind.Unspecified);

                AssertMatchesReference(values, unit, DateTimeKind.Unspecified);
            }

            AssertMatchesReference(BoundaryValues(minTicks, maxTicks, DateTimeKind.Unspecified), unit,
                DateTimeKind.Unspecified);
            AssertMatchesReference(BoundaryValues(minTicks, maxTicks, DateTimeKind.Utc), unit, DateTimeKind.Utc);
        }
    }

    /// <summary>
    /// A tick value one below a multiple of the divisor is where a wrong reciprocal shows up first, and
    /// pre-epoch values are where a wrong floor-vs-truncate rounding does.
    /// </summary>
    [Test]
    public void ConvertDateTimesRoundsDownAroundTheEpoch()
    {
        long[] offsets = [-20_001, -10_001, -10_000, -9_999, -11, -10, -9, -1, 0, 1, 9, 10, 11, 9_999, 10_000];
        var values = new DateTime[offsets.Length];
        for (var i = 0; i < offsets.Length; i++)
            values[i] = new DateTime(DateTime.UnixEpoch.Ticks + offsets[i], DateTimeKind.Unspecified);

        foreach (var unit in Units)
            AssertMatchesReference(values, unit, DateTimeKind.Unspecified);
    }

    [Test]
    public void ConvertNullableDateTimesMatchesScalarDivisionAtBoundariesAndRandomTicks()
    {
        var random = new Random(20260828);
        foreach (var unit in new[] { TimeUnit.Millis, TimeUnit.Micros })
        {
            var divisor = unit == TimeUnit.Millis ? TimeSpan.TicksPerMillisecond : 10;
            long[] boundaryTicks =
            [
                0, 1, divisor - 1, divisor, divisor + 1,
                DateTime.UnixEpoch.Ticks - divisor - 1,
                DateTime.UnixEpoch.Ticks - divisor,
                DateTime.UnixEpoch.Ticks - 1,
                DateTime.UnixEpoch.Ticks,
                DateTime.UnixEpoch.Ticks + 1,
                DateTime.UnixEpoch.Ticks + divisor - 1,
                DateTime.UnixEpoch.Ticks + divisor,
                DateTime.MaxValue.Ticks - divisor,
                DateTime.MaxValue.Ticks - 1,
                DateTime.MaxValue.Ticks
            ];
            var values = new DateTime?[boundaryTicks.Length + 20_000];
            for (var i = 0; i < boundaryTicks.Length; i++)
                values[i] = new DateTime(boundaryTicks[i], DateTimeKind.Utc);
            for (var i = boundaryTicks.Length; i < values.Length; i++)
            {
                if (i % 17 != 0)
                    values[i] = new DateTime(random.NextInt64(0, DateTime.MaxValue.Ticks + 1),
                        DateTimeKind.Utc);
            }

            var actual = new long[values.Length];
            var actualCount = TimestampConversion.ConvertNullableDateTimes(values, actual, unit,
                DateTimeKind.Utc);
            var expectedCount = 0;
            for (var i = 0; i < values.Length; i++)
            {
                if (values[i] is not { } value)
                    continue;
                var expected = TimestampConversion.DivideFloor(value.Ticks - DateTime.UnixEpoch.Ticks,
                    divisor);
                if (actual[expectedCount] != expected)
                    throw new InvalidOperationException(
                        $"{unit} nullable conversion of {value.Ticks} ticks at source index {i} "
                        + $"produced {actual[expectedCount]}, expected {expected}.");
                expectedCount++;
            }

            if (actualCount != expectedCount)
                throw new InvalidOperationException(
                    $"{unit} nullable conversion compacted {actualCount} values, expected {expectedCount}.");
        }
    }

    [Test]
    public void ConvertDateTimesRejectsAMismatchedKindAnywhereInTheSpan()
    {
        foreach (var unit in Units)
            for (var offender = 0; offender < 20; offender++)
            {
                var values = new DateTime[20];
                for (var i = 0; i < values.Length; i++)
                    values[i] = new DateTime(DateTime.UnixEpoch.Ticks + i, DateTimeKind.Utc);
                values[offender] = new DateTime(DateTime.UnixEpoch.Ticks + offender, DateTimeKind.Unspecified);

                var destination = new long[values.Length];
                try
                {
                    TimestampConversion.ConvertDateTimes(values, destination, unit, DateTimeKind.Utc);
                }
                catch (InvalidOperationException exception)
                    when (exception.Message.Contains("Unspecified", StringComparison.Ordinal))
                {
                    continue;
                }

                throw new InvalidOperationException(
                    $"Expected a kind mismatch at index {offender} for {unit} to be rejected.");
            }
    }

    [Test]
    public void ConvertDateTimesRejectsLocalValues()
    {
        var values = new DateTime[16];
        for (var i = 0; i < values.Length; i++)
            values[i] = new DateTime(DateTime.UnixEpoch.Ticks + i, DateTimeKind.Local);

        try
        {
            TimestampConversion.ConvertDateTimes(values, new long[values.Length], TimeUnit.Micros,
                DateTimeKind.Unspecified);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        throw new InvalidOperationException("Expected local DateTime values to be rejected.");
    }

    [Test]
    public void ConvertDateTimesOverflowsForUnrepresentableNanoseconds()
    {
        var values = new DateTime[16];
        Array.Fill(values, new DateTime(DateTime.MaxValue.Ticks, DateTimeKind.Unspecified));

        try
        {
            TimestampConversion.ConvertDateTimes(values, new long[values.Length], TimeUnit.Nanos,
                DateTimeKind.Unspecified);
        }
        catch (OverflowException)
        {
            return;
        }

        throw new InvalidOperationException("Expected nanosecond conversion to overflow at DateTime.MaxValue.");
    }

    static DateTime[] BoundaryValues(long minTicks, long maxTicks, DateTimeKind kind)
    {
        long[] ticks =
        [
            minTicks, minTicks + 1, DateTime.UnixEpoch.Ticks - 10_001, DateTime.UnixEpoch.Ticks - 1,
            DateTime.UnixEpoch.Ticks, DateTime.UnixEpoch.Ticks + 1, maxTicks - 1, maxTicks
        ];
        var values = new DateTime[ticks.Length];
        for (var i = 0; i < ticks.Length; i++)
            values[i] = new DateTime(ticks[i], kind);
        return values;
    }

    /// <summary>The tick values whose representation in <paramref name="unit"/> still fits in an Int64.</summary>
    static (long Min, long Max) RepresentableTickRange(TimeUnit unit)
        => unit == TimeUnit.Nanos
            ? (DateTime.UnixEpoch.Ticks - long.MaxValue / 100, DateTime.UnixEpoch.Ticks + long.MaxValue / 100)
            : (0, DateTime.MaxValue.Ticks);

    static void AssertMatchesReference(DateTime[] values, TimeUnit unit, DateTimeKind expectedKind)
    {
        var actual = new long[values.Length];
        TimestampConversion.ConvertDateTimes(values, actual, unit, expectedKind);

        for (var i = 0; i < values.Length; i++)
        {
            var expected = TimestampConversion.FromDateTimeTicks(values[i].Ticks, unit);
            if (actual[i] != expected)
                throw new InvalidOperationException(
                    $"{unit} conversion of {values[i].Ticks} ticks at index {i} of {values.Length} "
                    + $"produced {actual[i]}, expected {expected}.");
        }
    }
}
