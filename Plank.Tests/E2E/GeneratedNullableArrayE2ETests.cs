using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.E2E;

internal sealed class GeneratedNullableArrayE2ETests
{
    [Test]
    [Arguments(ParquetDataPageVersion.V1)]
    [Arguments(ParquetDataPageVersion.V2)]
    public void NullableReferenceAndValueArraysRoundTrip(ParquetDataPageVersion pageVersion)
    {
        using var stream = new MemoryStream();
        using (var writer = GeneratedNullableArraySchema.CreateRowWriter(stream, maxParallelism: 1,
                   new ParquetWriterOptions { Compression = CompressionKind.None, DataPageVersion = pageVersion }))
        {
            for (var index = 0; index < 3; index++)
            {
                var expected = CreateValues(index);
                var row = writer.GetRow();
                row.Names = expected.Names;
                row.NameGroups = expected.NameGroups;
                row.NameLists = expected.NameLists;
                row.Payloads = expected.Payloads;
                row.Numbers = expected.Numbers;
                row.Identifiers = expected.Identifiers;
                row.Dates = expected.Dates;
            }
            writer.Complete();
        }

        using var readStream = new MemoryStream(stream.ToArray());
        using var reader = GeneratedNullableArraySchema.CreateRowReader(readStream);
        var rowIndex = 0;
        foreach (var row in reader)
        {
            var expected = CreateValues(rowIndex);
            AssertSequence(row.Names, expected.Names, nameof(row.Names));
            AssertNestedSequence(row.NameGroups, expected.NameGroups, nameof(row.NameGroups));
            AssertNestedSequence(row.NameLists, expected.NameLists, nameof(row.NameLists));
            AssertNestedSequence(row.Payloads, expected.Payloads, nameof(row.Payloads));
            AssertSequence(row.Numbers, expected.Numbers, nameof(row.Numbers));
            AssertSequence(row.Identifiers, expected.Identifiers, nameof(row.Identifiers));
            AssertSequence(row.Dates, expected.Dates, nameof(row.Dates));
            rowIndex++;
        }
        if (rowIndex != 3)
            throw new InvalidOperationException($"Expected 3 rows, read {rowIndex}.");
    }

    static GeneratedNullableArraySchema CreateValues(int row)
        => row switch
        {
            0 => new(),
            1 => new()
            {
                Names = [], NameGroups = [], NameLists = [], Payloads = [], Numbers = [],
                Identifiers = [], Dates = []
            },
            _ => new()
            {
                Names = ["", null, "snowman: ☃"],
                NameGroups = [["", null, "雪"], [], [null]],
                NameLists = [["", null, "雪"], [], [null]],
                Payloads = [null, [], [0, byte.MaxValue], [1, 2, 3]],
                Numbers = [int.MinValue, null, 0, int.MaxValue],
                Identifiers = [Guid.Empty, null, new Guid("00112233-4455-6677-8899-aabbccddeeff")],
                Dates = [DateOnly.MinValue, null, DateOnly.MaxValue]
            }
        };

    static void AssertSequence<T>(IEnumerable<T>? actual, IEnumerable<T>? expected, string name)
    {
        if (actual is null || expected is null)
        {
            if (actual is not null || expected is not null)
                throw new InvalidOperationException($"Null array differs for {name}.");
            return;
        }
        if (!actual.SequenceEqual(expected))
            throw new InvalidOperationException($"Values differ for {name}.");
    }

    static void AssertNestedSequence<T>(IEnumerable<IEnumerable<T>?>? actual,
        IEnumerable<IEnumerable<T>?>? expected, string name)
    {
        if (actual is null || expected is null)
        {
            if (actual is not null || expected is not null)
                throw new InvalidOperationException($"Null array differs for {name}.");
            return;
        }
        var actualValues = actual.ToArray();
        var expectedValues = expected.ToArray();
        if (actualValues.Length != expectedValues.Length)
            throw new InvalidOperationException($"Array length differs for {name}.");
        for (var index = 0; index < expectedValues.Length; index++)
            AssertSequence(actualValues[index], expectedValues[index], name);
    }
}
