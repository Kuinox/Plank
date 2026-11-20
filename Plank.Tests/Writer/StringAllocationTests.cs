using System.Text;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Writer;

[NotInParallel]
internal sealed class StringAllocationTests
{
    [Test]
    [Arguments(ParquetRepetition.Required)]
    [Arguments(ParquetRepetition.Optional)]
    public void StringWriteChainDoesNotAllocateAfterWarmup(ParquetRepetition repetition)
    {
        var schema = new ParquetSchema([
            ColumnDefinition.Leaf("value", ParquetPhysicalType.ByteArray,
                new ColumnOptions(repetition, [EncodingKind.Plain]))
        ]);
        using var stream = new MemoryStream(capacity: 128 * 1024);
        using var writer = schema.CreateWriter(stream, new ParquetWriterOptions { Compression = CompressionKind.None });
        var serialized = writer.CreateSerializedColumn<string>(schema.LeafColumns[0]);
        var values = new string[8192];
        for (var i = 0; i < values.Length; i++)
            values[i] = repetition == ParquetRepetition.Optional && i % 3 == 0 ? null! : i % 2 == 0 ? "é" : "hello";
        for (var i = 0; i < 8; i++)
            WriteRowGroup();

        var before = GC.GetAllocatedBytesForCurrentThread();
        WriteRowGroup();
        var writeBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        if (writeBytes != 0)
            throw new InvalidOperationException($"String serialization allocated {writeBytes} bytes.");

        void WriteRowGroup()
        {
            writer.Reset(stream);
            serialized.Serialize(values);
            writer.StartRowGroup().Write(serialized);
        }
    }

    [Test]
    [Arguments(EncodingKind.Plain)]
    [Arguments(EncodingKind.RleDictionary)]
    [Arguments(EncodingKind.DeltaByteArray)]
    public void StringScratchSurvivesChunkRolloverAndInvalidInput(EncodingKind encoding)
    {
        var schema = new ParquetSchema([
            ColumnDefinition.OptionalLeaf("text", ParquetPhysicalType.ByteArray, new ColumnOptions(encodings: [encoding]))
        ]);
        var large = new string('界', 24000);
        string[] first = [null!, "", "一", large, "🦆"];
        string[] second = [large + "!", null!, "二", "", "é"];
        using var stream = new MemoryStream();
        using (var writer = schema.CreateWriter(stream))
        {
            var column = writer.CreateSerializedColumn<string>(schema.LeafColumns[0]);
            column.Serialize(first);
            writer.StartRowGroup().Write(column);
            Assert.Throws<EncoderFallbackException>(() => column.Serialize([large, "\uD800"]));
            column.Serialize(second);
            writer.StartRowGroup().Write(column);
            writer.CloseFile();
        }
        using var reader = schema.CreateReader(new MemoryStream(stream.ToArray()));
        var actual = new List<string?>();
        foreach (var group in reader.RowGroups)
            foreach (var buffer in group.Column<byte>(0))
                for (var i = 0; i < buffer.Count; i++)
                    actual.Add(buffer.IsNull(i) ? null : Encoding.UTF8.GetString(buffer.GetValue(i)));
        if (!actual.SequenceEqual(first.Concat(second)))
            throw new InvalidOperationException("String values changed across scratch reuse.");
    }
}
