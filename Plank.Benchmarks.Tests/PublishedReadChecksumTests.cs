using Plank.Benchmarks.Published;
using Accumulator = Plank.Benchmarks.Published.PublishedReadChecksum.Accumulator;

namespace Plank.Benchmarks.Tests;

internal sealed class PublishedReadChecksumTests
{
    [Test]
    public async Task InteriorValueChangesChecksum()
    {
        var original = DataSet([10L, 20L, 30L]);
        var changed = DataSet([10L, 21L, 30L]);

        await Assert.That(PublishedReadChecksum.Expected(changed).Checksum)
            .IsNotEqualTo(PublishedReadChecksum.Expected(original).Checksum);
    }

    [Test]
    public async Task AddValuesMatchesScalarAdds()
    {
        long[] values = [long.MinValue, -1, 0, 1, long.MaxValue];
        var bulk = Accumulator.StartPiece(3, 5, values.Length);
        bulk.AddValues(values);

        var scalar = Accumulator.StartPiece(3, 5, values.Length);
        foreach (var value in values)
            scalar.AddValue(value);

        await Assert.That(bulk.Finish()).IsEqualTo(scalar.Finish());
    }

    [Test]
    public async Task NullAndEmptyBinaryValuesHaveDistinctChecksums()
    {
        await Assert.That(Checksum(static (ref Accumulator x) => x.AddNull()))
            .IsNotEqualTo(Checksum(static (ref Accumulator x) => x.AddBytes([])));
    }

    [Test]
    public async Task AddBytesUsesLengthOnly()
    {
        await Assert.That(Checksum(static (ref Accumulator x) => x.AddBytes([1, 2, 3])))
            .IsEqualTo(Checksum(static (ref Accumulator x) => x.AddBytes([8, 9, 10])));
    }

    [Test]
    public async Task DateTimeChecksumIgnoresKindAndPreservesTickPrecision()
    {
        const long ticks = 638_591_653_234_567_890L;
        var unspecified = Checksum(static (ref Accumulator x) =>
            x.AddValue(new DateTime(ticks, DateTimeKind.Unspecified)));
        var utc = Checksum(static (ref Accumulator x) =>
            x.AddValue(new DateTime(ticks, DateTimeKind.Utc)));
        var adjacent = Checksum(static (ref Accumulator x) =>
            x.AddValue(new DateTime(ticks + 1, DateTimeKind.Unspecified)));

        await Assert.That(utc).IsEqualTo(unspecified);
        await Assert.That(adjacent).IsNotEqualTo(unspecified);
    }

    delegate void ChecksumStep(ref Accumulator accumulator);

    static ulong Checksum(ChecksumStep step)
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
