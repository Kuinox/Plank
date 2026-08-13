using Plank.Benchmarks.Published;

namespace Plank.Benchmarks.Tests;

internal sealed class SyntheticBenchmarkDataTests
{
    [Test]
    public async Task DataIsDeterministicAndWide()
    {
        var first = SyntheticBenchmarkData.Create(64, 3);
        var second = SyntheticBenchmarkData.Create(64, 3);

        await Assert.That(first.Count).IsEqualTo(21);
        await Assert.That(first.Select(static dataSet => (dataSet.DataTypes.Single(), dataSet.Encoding)))
            .IsEquivalentTo(
            [
                ("boolean", "plain"),
                ("boolean", "rle"),
                ("int32", "plain"),
                ("int32", "dictionary"),
                ("int32", "delta_binary_packed"),
                ("int32", "byte_stream_split"),
                ("int64", "plain"),
                ("int64", "dictionary"),
                ("int64", "delta_binary_packed"),
                ("int64", "byte_stream_split"),
                ("timestamp", "plain"),
                ("timestamp", "dictionary"),
                ("timestamp", "delta_binary_packed"),
                ("timestamp", "byte_stream_split"),
                ("double", "plain"),
                ("double", "dictionary"),
                ("double", "byte_stream_split"),
                ("string", "plain"),
                ("string", "dictionary"),
                ("string", "delta_length_byte_array"),
                ("string", "delta_byte_array")
            ]);
        for (var caseIndex = 0; caseIndex < first.Count; caseIndex++)
        {
            await Assert.That(first[caseIndex].Columns.Count).IsEqualTo(3);
            await Assert.That(first[caseIndex].ValueCount).IsEqualTo(192);
            for (var columnIndex = 0; columnIndex < first[caseIndex].Columns.Count; columnIndex++)
                await Assert.That(ArraysEqual(
                    first[caseIndex].Columns[columnIndex].Values[0],
                    second[caseIndex].Columns[columnIndex].Values[0])).IsTrue();
        }
    }

    static bool ArraysEqual(Array left, Array right)
        => left.Cast<object?>().SequenceEqual(right.Cast<object?>());
}
