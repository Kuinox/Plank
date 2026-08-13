using Plank.Benchmarks.Published;

namespace Plank.Benchmarks.Tests;

internal sealed class PlankPublishedBenchmarkWriterTests
{
    [Test]
    public void PrepareValuesConvertsTimestampsToPhysicalMicrosWithoutChangingLogicalValues()
    {
        var epoch = DateTime.SpecifyKind(DateTime.UnixEpoch, DateTimeKind.Unspecified);
        DateTime[] required = [epoch.AddTicks(-1), epoch, epoch.AddTicks(11)];
        DateTime?[] optional = [epoch.AddTicks(-1), null, epoch.AddTicks(11)];
        var int64 = new long[] { 1, 2, 3 };
        var dataSet = CreateDataSet(required, optional, int64);

        var prepared = PlankPublishedBenchmarkWriter.PrepareValues(dataSet);

        if (prepared[0][0] is not long[] requiredMicros || !requiredMicros.SequenceEqual([-1L, 0L, 1L]))
            throw new InvalidOperationException("Required timestamps were not prepared as physical microseconds.");
        if (prepared[1][0] is not long?[] optionalMicros || !optionalMicros.SequenceEqual([-1L, null, 1L]))
            throw new InvalidOperationException("Optional timestamps were not prepared as physical microseconds.");
        if (!ReferenceEquals(dataSet.Columns[0].Values[0], required) || !required.SequenceEqual([
                epoch.AddTicks(-1), epoch, epoch.AddTicks(11)]))
            throw new InvalidOperationException("Preparing required timestamps changed the logical benchmark values.");
        if (!ReferenceEquals(dataSet.Columns[1].Values[0], optional) || !optional.SequenceEqual([
                epoch.AddTicks(-1), null, epoch.AddTicks(11)]))
            throw new InvalidOperationException("Preparing optional timestamps changed the logical benchmark values.");
        if (!ReferenceEquals(prepared[2][0], int64))
            throw new InvalidOperationException("Preparing values should reuse non-converted benchmark arrays.");
    }

    [Test]
    public void PrepareValuesRejectsTimestampKindsThatDoNotMatchTheBenchmarkSchema()
    {
        var invalid = new[] { DateTime.UnixEpoch };
        var dataSet = CreateDataSet(invalid, Array.Empty<DateTime?>());

        Assert.Throws<InvalidOperationException>(() => PlankPublishedBenchmarkWriter.PrepareValues(dataSet));
    }

    [Test]
    public async Task PreparedTimestampWriteIsAuditedAgainstLogicalDateTimes()
    {
        var epoch = DateTime.SpecifyKind(DateTime.UnixEpoch, DateTimeKind.Unspecified);
        var dataSet = CreateDataSet(
            [epoch.AddTicks(-10), epoch, epoch.AddTicks(20)],
            [epoch.AddTicks(-10), null, epoch.AddTicks(20)]);
        using var stream = new NonClosingMemoryStream();
        using var writer = new PlankPublishedBenchmarkWriter(dataSet, 1);

        var outputBytes = await PublishedBenchmarkAuditor.WriteAndValidateAsync(
            writer, dataSet, stream, CancellationToken.None);

        await Assert.That(outputBytes).IsGreaterThan(0);
    }

    static PublishedBenchmarkDataSet CreateDataSet(DateTime[] required, DateTime?[] optional, long[]? int64 = null)
        => new()
        {
            SuiteId = "synthetic",
            Id = "timestamps",
            Label = "Timestamps",
            Encoding = "plain",
            ThroughputUnit = "million values/s",
            Columns =
            [
                new PublishedBenchmarkDataSet.Column
                {
                    Name = "required",
                    Kind = BenchmarkColumnKind.Timestamp,
                    Nullable = false,
                    Values = [required]
                },
                new PublishedBenchmarkDataSet.Column
                {
                    Name = "optional",
                    Kind = BenchmarkColumnKind.Timestamp,
                    Nullable = true,
                    Values = [optional]
                },
                new PublishedBenchmarkDataSet.Column
                {
                    Name = "int64",
                    Kind = BenchmarkColumnKind.Int64,
                    Nullable = false,
                    Values = [int64 ?? new long[required.Length]]
                }
            ]
        };
}
