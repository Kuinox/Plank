using ParquetSharp;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.PageStrategy;

namespace Plank.Tests.Writer;

internal sealed class RequiredByteArrayDictionaryTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public void ForcedDictionaryPreservesDistinctArraysAcrossSortedAndUnsortedWrites(bool sorted)
    {
        var first = CreateValues(sorted);
        var second = CreateValues(sorted);
        var schema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.ByteArray,
                new ColumnOptions(encodings: [EncodingKind.RleDictionary]),
                pageStrategy: ForceDictionaryPageStrategy.Shared)
        ]);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions { Compression = CompressionKind.None });
        var serialized = writer.CreateSerializedColumn<byte[]>(schema.LeafColumns[0]);

        WriteRowGroup(writer, serialized, first);
        WriteRowGroup(writer, serialized, second);
        writer.CloseFile();

        using var readStream = new MemoryStream(stream.ToArray(), writable: false);
        using var reader = new ParquetFileReader(readStream, leaveOpen: false);
        AssertRowGroup(reader, 0, first);
        AssertRowGroup(reader, 1, second);
    }

    [Test]
    public void ForcedDictionaryRejectsNullAfterEmptyPayload()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.ByteArray,
                new ColumnOptions(encodings: [EncodingKind.RleDictionary]),
                pageStrategy: ForceDictionaryPageStrategy.Shared)
        ]);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions { Compression = CompressionKind.None });
        var serialized = writer.CreateSerializedColumn<byte[]>(schema.LeafColumns[0]);
        byte[][] values = [[], null!];

        try
        {
            serialized.Serialize(values);
        }
        catch (InvalidOperationException exception)
            when (exception.Message.Contains("does not support null values", StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException("A required byte-array dictionary accepted a null value after an empty payload.");
    }

    [Test]
    public void ForcedDictionaryPreservesRepeatedCycleWithDivergentTail()
    {
        var cycle = Enumerable.Range(0, 17)
            .Select(static i => System.Text.Encoding.UTF8.GetBytes($"cycle-{i:D2}"))
            .ToArray();
        var values = new byte[cycle.Length * 5 + 4][];
        for (var i = 0; i < cycle.Length * 5; i++)
            values[i] = cycle[i % cycle.Length].ToArray();
        // The cycle shortcut must stop at the first mismatch and let the ordinary map path add and
        // subsequently reuse new dictionary entries.
        values[cycle.Length * 5] = "divergent-a"u8.ToArray();
        values[cycle.Length * 5 + 1] = cycle[3].ToArray();
        values[cycle.Length * 5 + 2] = "divergent-b"u8.ToArray();
        values[cycle.Length * 5 + 3] = "divergent-a"u8.ToArray();

        var schema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.ByteArray,
                new ColumnOptions(encodings: [EncodingKind.RleDictionary]),
                pageStrategy: ForceDictionaryPageStrategy.Shared)
        ]);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions { Compression = CompressionKind.None });
        var serialized = writer.CreateSerializedColumn<byte[]>(schema.LeafColumns[0]);
        serialized.Serialize(values);
        if (GetDictionaryValueCount(serialized.Pages) != cycle.Length + 2)
            throw new InvalidOperationException("The divergent tail did not produce the expected dictionary entries.");
        writer.StartRowGroup().Write(serialized);
        writer.CloseFile();

        using var reader = new ParquetFileReader(new MemoryStream(stream.ToArray(), writable: false));
        AssertRowGroup(reader, 0, values);
    }

    static void WriteRowGroup(Plank.Writing.ParquetWriter writer, SerializedColumn<byte[]> serialized,
        byte[][] values)
    {
        serialized.Serialize(values);
        var dictionaryValueCount = GetDictionaryValueCount(serialized.Pages);
        if (dictionaryValueCount != 3)
            throw new InvalidOperationException(
                $"Expected three content-distinct dictionary values, got {dictionaryValueCount}.");
        writer.StartRowGroup().Write(serialized);
    }

    static uint GetDictionaryValueCount(PageList pages)
    {
        for (var i = 0; i < pages.Count; i++)
            if (pages[i].Kind == PageKind.Dictionary)
                return pages[i].DictionaryValueCount;
        throw new InvalidOperationException("Expected a dictionary page.");
    }

    static void AssertRowGroup(ParquetFileReader reader, int rowGroupIndex, byte[][] expected)
    {
        using var rowGroup = reader.RowGroup(rowGroupIndex);
        using var column = rowGroup.Column(0).LogicalReader<byte[]>();
        var actual = column.ReadAll(expected.Length);
        if (actual.Length != expected.Length)
            throw new InvalidOperationException(
                $"Expected {expected.Length} values in row group {rowGroupIndex}, got {actual.Length}.");
        for (var i = 0; i < expected.Length; i++)
            if (!actual[i].AsSpan().SequenceEqual(expected[i]))
                throw new InvalidOperationException(
                    $"Value {i} in row group {rowGroupIndex} did not round-trip.");
    }

    static byte[][] CreateValues(bool sorted)
        => sorted
            ?
            [
                "alpha"u8.ToArray(),
                "alpha"u8.ToArray(),
                "beta"u8.ToArray(),
                "gamma"u8.ToArray()
            ]
            :
            [
                "gamma"u8.ToArray(),
                "alpha"u8.ToArray(),
                "gamma"u8.ToArray(),
                "beta"u8.ToArray()
            ];
}
