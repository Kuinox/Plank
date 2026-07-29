using Plank.Reading;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Writer;

internal sealed class NegativePreEpochTimestampTests
{
    [Test]
    public void NegativePreEpochSubunitTimestampsUseFloorForValuesAndStatistics()
    {
        var failures = new List<string>();
        VerifyUnit(TimeUnit.Millis, TimeSpan.TicksPerMillisecond, failures);
        VerifyUnit(TimeUnit.Micros, 10, failures);

        if (failures.Count != 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
    }

    static void VerifyUnit(TimeUnit unit, long ticksPerUnit, List<string> failures)
    {
        var schema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int64,
                logicalType: new LogicalType.Timestamp(unit, IsAdjustedToUtc: true))
        ]);
        var input = DateTimeOffset.UnixEpoch.AddTicks(-1);

        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None
        });
        var serialized = writer.CreateSerializedColumn<DateTimeOffset>(schema.LeafColumns[0]);
        serialized.Serialize([input]);

        if (serialized.Statistics.MinBits != -1 || serialized.Statistics.MaxBits != -1)
            failures.Add(
                $"{unit} statistics should floor the negative subunit timestamp to -1, but min/max were {serialized.Statistics.MinBits}/{serialized.Statistics.MaxBits}.");

        writer.StartRowGroup().Write(serialized);
        writer.CloseFile();

        using var reader = schema.CreateReader(new MemoryReadSource(stream.ToArray()));
        var actualValues = new List<DateTimeOffset>();
        foreach (var buffer in reader.RowGroups[0].Column<DateTimeOffset>(0))
            foreach (var value in buffer.Values)
                actualValues.Add(value);
        var actual = actualValues.Single();
        var expected = DateTimeOffset.UnixEpoch.AddTicks(-ticksPerUnit);
        if (actual != expected)
            failures.Add($"{unit} value should decode as {expected:O}, but decoded as {actual:O}.");
    }
}
