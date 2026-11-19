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
    public void StringWriteChainOnlyAllocatesUtf8PayloadsAfterWarmup(ParquetRepetition repetition)
    {
        var schema = new ParquetSchema([
            ColumnDefinition.Leaf("value", ParquetPhysicalType.ByteArray,
                new ColumnOptions(repetition, [EncodingKind.Plain]))
        ]);
        using var stream = new MemoryStream(capacity: 128 * 1024);
        using var writer = schema.CreateWriter(stream, new ParquetWriterOptions { Compression = CompressionKind.None });
        var serialized = writer.CreateSerializedColumn<string>(schema.LeafColumns[0]);
        var values = new string[4096];
        for (var i = 0; i < values.Length; i++)
            values[i] = repetition == ParquetRepetition.Optional && i % 3 == 0 ? null! : i % 2 == 0 ? "é" : "hello";
        for (var i = 0; i < 8; i++)
            WriteRowGroup();

        var before = GC.GetAllocatedBytesForCurrentThread();
        foreach (var value in values)
            if (value is not null)
                GC.KeepAlive(Encoding.UTF8.GetBytes(value));
        var payloadBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        before = GC.GetAllocatedBytesForCurrentThread();
        WriteRowGroup();
        var writeBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"{repetition}: UTF-8 payloads {payloadBytes}; row group write {writeBytes} bytes.");
        if (writeBytes > payloadBytes + 512)
            throw new InvalidOperationException("String serialization allocated more than the UTF-8 payloads.");

        void WriteRowGroup()
        {
            writer.Reset(stream);
            serialized.Serialize(values);
            writer.StartRowGroup().Write(serialized);
        }
    }
}
