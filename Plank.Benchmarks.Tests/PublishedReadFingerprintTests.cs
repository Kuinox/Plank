using Plank.Benchmarks.Published;

namespace Plank.Benchmarks.Tests;

internal sealed class PublishedReadFingerprintTests
{
    [Test]
    public async Task InteriorValueChangesFullReadFingerprint()
    {
        var original = DataSet([10L, 20L, 30L, 40L, 50L, 60L, 70L]);
        var changed = DataSet([10L, 21L, 30L, 40L, 50L, 60L, 70L]);

        await Assert.That(PublishedReadFingerprint.Expected(changed).Fingerprint)
            .IsNotEqualTo(PublishedReadFingerprint.Expected(original).Fingerprint);
    }

    [Test]
    public async Task NullAndEmptyBinaryValuesHaveDistinctFingerprints()
    {
        var start = PublishedReadFingerprint.Start();

        await Assert.That(PublishedReadFingerprint.AddNull(start))
            .IsNotEqualTo(PublishedReadFingerprint.AddBytes(start, []));
    }

    [Test]
    public async Task SignedZeroAndNanPayloadsRemainObservable()
    {
        var start = PublishedReadFingerprint.Start();
        var negativeZero = BitConverter.Int64BitsToDouble(unchecked((long)0x8000_0000_0000_0000UL));
        var firstNaN = BitConverter.Int64BitsToDouble(unchecked((long)0x7ff8_0000_0000_0001UL));
        var secondNaN = BitConverter.Int64BitsToDouble(unchecked((long)0x7ff8_0000_0000_0002UL));

        await Assert.That(PublishedReadFingerprint.AddValue(start, 0.0))
            .IsNotEqualTo(PublishedReadFingerprint.AddValue(start, negativeZero));
        await Assert.That(PublishedReadFingerprint.AddValue(start, firstNaN))
            .IsNotEqualTo(PublishedReadFingerprint.AddValue(start, secondNaN));
    }

    [Test]
    public async Task DateTimeFingerprintMatchesUtcDateTimeOffsetAcrossKindsAndBounds()
    {
        long[] ticks =
        [
            DateTime.MinValue.Ticks,
            DateTime.MinValue.Ticks + 1,
            DateTime.UnixEpoch.Ticks - 1,
            DateTime.UnixEpoch.Ticks,
            DateTime.UnixEpoch.Ticks + 1,
            638_591_653_234_567_890L,
            DateTime.MaxValue.Ticks - 1,
            DateTime.MaxValue.Ticks
        ];
        DateTimeKind[] kinds = [DateTimeKind.Unspecified, DateTimeKind.Utc, DateTimeKind.Local];
        var start = PublishedReadFingerprint.Start();

        foreach (var tickCount in ticks)
            foreach (var kind in kinds)
            {
                var value = new DateTime(tickCount, kind);
                var utcValue = new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));

                await Assert.That(PublishedReadFingerprint.AddValue(start, value))
                    .IsEqualTo(PublishedReadFingerprint.AddValue(start, utcValue));
            }
    }

    [Test]
    public async Task DateTimeFingerprintIgnoresKindAndPreservesTickPrecision()
    {
        const long ticks = 638_591_653_234_567_890L;
        var start = PublishedReadFingerprint.Start();
        var unspecified = PublishedReadFingerprint.AddValue(start,
            new DateTime(ticks, DateTimeKind.Unspecified));
        var utc = PublishedReadFingerprint.AddValue(start, new DateTime(ticks, DateTimeKind.Utc));
        var local = PublishedReadFingerprint.AddValue(start, new DateTime(ticks, DateTimeKind.Local));
        var adjacent = PublishedReadFingerprint.AddValue(start,
            new DateTime(ticks + 1, DateTimeKind.Unspecified));

        await Assert.That(utc).IsEqualTo(unspecified);
        await Assert.That(local).IsEqualTo(unspecified);
        await Assert.That(adjacent).IsNotEqualTo(unspecified);
    }

    static PublishedBenchmarkDataSet DataSet(long[] values)
        => new()
        {
            SuiteId = "test",
            Id = "interior",
            Label = "Interior value",
            Encoding = "plain",
            ThroughputUnit = "values/s",
            Columns =
            [
                new PublishedBenchmarkDataSet.Column
                {
                    Name = "value",
                    Kind = BenchmarkColumnKind.Int64,
                    Nullable = false,
                    Values = [values]
                }
            ]
        };
}
