using Plank.Benchmarks.Published;
using Accumulator = Plank.Benchmarks.Published.PublishedReadFingerprint.Accumulator;

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

    /// <summary>
    /// The accumulator spreads consecutive values over four independent lanes, so a swap has to be
    /// caught at every distance — including the multiples of four that land both values back in the
    /// lane they came from.
    /// </summary>
    [Test]
    public async Task SwappingValuesAtAnyDistanceChangesFullReadFingerprint()
    {
        long[] values = [10L, 20L, 30L, 40L, 50L, 60L, 70L, 80L, 90L, 100L, 110L, 120L];
        var baseline = PublishedReadFingerprint.Expected(DataSet(values)).Fingerprint;

        for (var first = 0; first < values.Length; first++)
            for (var second = first + 1; second < values.Length; second++)
            {
                var swapped = (long[])values.Clone();
                (swapped[first], swapped[second]) = (swapped[second], swapped[first]);

                await Assert.That(PublishedReadFingerprint.Expected(DataSet(swapped)).Fingerprint)
                    .IsNotEqualTo(baseline);
            }
    }

    [Test]
    public async Task NullAndEmptyBinaryValuesHaveDistinctFingerprints()
    {
        await Assert.That(Fingerprint((ref Accumulator x) => x.AddNull()))
            .IsNotEqualTo(Fingerprint((ref Accumulator x) => x.AddBytes([])));
    }

    [Test]
    public async Task BulkByteFingerprintMatchesScalarAcrossLengthsAndStartingLanes()
    {
        var values = Enumerable.Range(0, 79)
            .Select(static value => unchecked((byte)(value * 197 + 101)))
            .ToArray();

        for (var prefixLength = 0; prefixLength < 4; prefixLength++)
            for (var length = 0; length <= values.Length; length++)
            {
                var scalar = Accumulator.StartPiece(3, 5, prefixLength + length);
                for (var prefix = 0; prefix < prefixLength; prefix++)
                    scalar.AddWord(unchecked((uint)(prefix * 7919)));
                scalar.AddWord(unchecked((uint)length));
                for (var index = 0; index < length; index++)
                    scalar.AddWord(values[index]);

                var bulk = Accumulator.StartPiece(3, 5, prefixLength + length);
                for (var prefix = 0; prefix < prefixLength; prefix++)
                    bulk.AddWord(unchecked((uint)(prefix * 7919)));
                bulk.AddBytes(values.AsSpan(0, length));

                await Assert.That(bulk.Finish()).IsEqualTo(scalar.Finish());
            }
    }

    /// <summary>
    /// Nulls are mixed with a different multiplier rather than an extra round, so a null still has to
    /// stay distinct from the value whose word is zero.
    /// </summary>
    [Test]
    public async Task NullAndZeroValuedEntriesHaveDistinctFingerprints()
    {
        var absent = Fingerprint((ref Accumulator x) => x.AddNull());

        await Assert.That(absent).IsNotEqualTo(Fingerprint((ref Accumulator x) => x.AddValue(0L)));
        await Assert.That(absent).IsNotEqualTo(Fingerprint((ref Accumulator x) => x.AddValue(0)));
        await Assert.That(absent).IsNotEqualTo(Fingerprint((ref Accumulator x) => x.AddValue(false)));
        await Assert.That(absent).IsEqualTo(Fingerprint((ref Accumulator x) => x.AddValue((long?)null)));
    }

    [Test]
    public async Task SignedZeroAndNanPayloadsRemainObservable()
    {
        var negativeZero = BitConverter.Int64BitsToDouble(unchecked((long)0x8000_0000_0000_0000UL));
        var firstNaN = BitConverter.Int64BitsToDouble(unchecked((long)0x7ff8_0000_0000_0001UL));
        var secondNaN = BitConverter.Int64BitsToDouble(unchecked((long)0x7ff8_0000_0000_0002UL));

        await Assert.That(Fingerprint((ref Accumulator x) => x.AddValue(0.0)))
            .IsNotEqualTo(Fingerprint((ref Accumulator x) => x.AddValue(negativeZero)));
        await Assert.That(Fingerprint((ref Accumulator x) => x.AddValue(firstNaN)))
            .IsNotEqualTo(Fingerprint((ref Accumulator x) => x.AddValue(secondNaN)));
    }

    [Test]
    public async Task BulkInt32FingerprintMatchesScalarAcrossLengthsAndChunkBoundaries()
    {
        int[] values =
        [
            int.MinValue, -1, 0, 1, int.MaxValue,
            .. Enumerable.Range(0, 67).Select(static value => unchecked(value * 1_664_525 + 1_013_904_223))
        ];

        for (var length = 0; length <= values.Length; length++)
        {
            var scalar = Accumulator.StartPiece(3, 5, length + 3);
            scalar.AddValue(101);
            scalar.AddValue(-202);
            scalar.AddValue(303);
            for (var index = 0; index < length; index++)
                scalar.AddValue(values[index]);
            var expected = scalar.Finish();

            for (var chunkSize = 1; chunkSize <= 13; chunkSize++)
            {
                var bulk = Accumulator.StartPiece(3, 5, length + 3);
                bulk.AddValues<int>([101, -202, 303]);
                for (var offset = 0; offset < length; offset += chunkSize)
                    bulk.AddValues(values.AsSpan(offset, Math.Min(chunkSize, length - offset)));

                await Assert.That(bulk.Finish()).IsEqualTo(expected);
            }
        }
    }

    [Test]
    public async Task BulkNullableInt32FingerprintMatchesScalarAcrossLengthsAndChunkBoundaries()
    {
        int?[] values =
        [
            null, int.MinValue, -1, 0, 1, null, int.MaxValue,
            .. Enumerable.Range(0, 66).Select(static value => value % 11 == 0
                ? (int?)null
                : unchecked(value * 1_664_525 + 1_013_904_223))
        ];

        for (var length = 0; length <= values.Length; length++)
        {
            var scalar = Accumulator.StartPiece(7, 9, length + 3);
            scalar.AddValue((int?)101);
            scalar.AddValue((int?)null);
            scalar.AddValue((int?)-303);
            for (var index = 0; index < length; index++)
                scalar.AddValue(values[index]);
            var expected = scalar.Finish();

            for (var chunkSize = 1; chunkSize <= 13; chunkSize++)
            {
                var bulk = Accumulator.StartPiece(7, 9, length + 3);
                bulk.AddValues<int?>([101, null, -303]);
                for (var offset = 0; offset < length; offset += chunkSize)
                    bulk.AddValues(values.AsSpan(offset, Math.Min(chunkSize, length - offset)));

                await Assert.That(bulk.Finish()).IsEqualTo(expected);
            }
        }
    }

    [Test]
    public async Task BulkInt64FingerprintMatchesScalarAcrossLengthsAndChunkBoundaries()
    {
        long[] values =
        [
            long.MinValue, -1, 0, 1, long.MaxValue,
            .. Enumerable.Range(0, 67).Select(static value =>
                unchecked(value * 6_364_136_223_846_793_005L + 1_442_695_040_888_963_407L))
        ];

        for (var length = 0; length <= values.Length; length++)
        {
            var scalar = Accumulator.StartPiece(3, 5, length + 3);
            scalar.AddValue(101L);
            scalar.AddValue(-202L);
            scalar.AddValue(303L);
            for (var index = 0; index < length; index++)
                scalar.AddValue(values[index]);
            var expected = scalar.Finish();

            for (var chunkSize = 1; chunkSize <= 13; chunkSize++)
            {
                var bulk = Accumulator.StartPiece(3, 5, length + 3);
                bulk.AddValues<long>([101, -202, 303]);
                for (var offset = 0; offset < length; offset += chunkSize)
                    bulk.AddValues(values.AsSpan(offset, Math.Min(chunkSize, length - offset)));

                await Assert.That(bulk.Finish()).IsEqualTo(expected);
            }
        }
    }

    [Test]
    public async Task BulkNullableInt64FingerprintMatchesScalarAcrossLengthsAndChunkBoundaries()
    {
        long?[] values =
        [
            null, long.MinValue, -1, 0, 1, null, long.MaxValue,
            .. Enumerable.Range(0, 66).Select(static value => value % 11 == 0
                ? (long?)null
                : unchecked(value * 6_364_136_223_846_793_005L + 1_442_695_040_888_963_407L))
        ];

        for (var length = 0; length <= values.Length; length++)
        {
            var scalar = Accumulator.StartPiece(7, 9, length + 3);
            scalar.AddValue((long?)101);
            scalar.AddValue((long?)null);
            scalar.AddValue((long?)-303);
            for (var index = 0; index < length; index++)
                scalar.AddValue(values[index]);
            var expected = scalar.Finish();

            for (var chunkSize = 1; chunkSize <= 13; chunkSize++)
            {
                var bulk = Accumulator.StartPiece(7, 9, length + 3);
                bulk.AddValues<long?>([101, null, -303]);
                for (var offset = 0; offset < length; offset += chunkSize)
                    bulk.AddValues(values.AsSpan(offset, Math.Min(chunkSize, length - offset)));

                await Assert.That(bulk.Finish()).IsEqualTo(expected);
            }
        }
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

        foreach (var tickCount in ticks)
            foreach (var kind in kinds)
            {
                var value = new DateTime(tickCount, kind);
                var utcValue = new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));

                await Assert.That(Fingerprint((ref Accumulator x) => x.AddValue(value)))
                    .IsEqualTo(Fingerprint((ref Accumulator x) => x.AddValue(utcValue)));
            }
    }

    [Test]
    public async Task DateTimeFingerprintIgnoresKindAndPreservesTickPrecision()
    {
        const long ticks = 638_591_653_234_567_890L;
        var unspecified = Fingerprint((ref Accumulator x) => x.AddValue(new DateTime(ticks, DateTimeKind.Unspecified)));
        var utc = Fingerprint((ref Accumulator x) => x.AddValue(new DateTime(ticks, DateTimeKind.Utc)));
        var local = Fingerprint((ref Accumulator x) => x.AddValue(new DateTime(ticks, DateTimeKind.Local)));
        var adjacent = Fingerprint((ref Accumulator x) => x.AddValue(new DateTime(ticks + 1, DateTimeKind.Unspecified)));

        await Assert.That(utc).IsEqualTo(unspecified);
        await Assert.That(local).IsEqualTo(unspecified);
        await Assert.That(adjacent).IsNotEqualTo(unspecified);
    }

    delegate void FingerprintStep(ref Accumulator accumulator);

    static ulong Fingerprint(FingerprintStep step)
    {
        var accumulator = Accumulator.StartPiece(0, 0, 1);
        step(ref accumulator);
        return accumulator.Finish();
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
