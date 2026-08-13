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
