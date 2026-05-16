using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Writer;

[NotInParallel]
internal sealed class WriterAllocationTests
{
    [Test]
    public void NonDictionaryWriteChainDoesNotAllocateAfterWarmup()
    {
        var column = new Column("value", ParquetPhysicalType.Int32,
            new ColumnOptions(ParquetRepetition.Required, [EncodingKind.Plain]));
        var schema = new ParquetSchema([column]);
        using var stream = new MemoryStream(capacity: 1024 * 1024);
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None
        });
        var serialized = writer.CreateSerializedColumn<int>(column);
        var values = CreateValues(4096);

        for (var i = 0; i < 8; i++)
            WriteOneRowGroup(writer, stream, serialized, values);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        WriteOneRowGroup(writer, stream, serialized, values);
        var after = GC.GetAllocatedBytesForCurrentThread();
        var allocated = after - before;

        if (allocated != 0)
            throw new InvalidOperationException(
                $"Expected zero allocations for steady-state non-dictionary write chain but saw {allocated} bytes.");
    }

    [Test]
    public void LargeNonDictionaryWriteChainDoesNotAllocateAfterWarmup()
    {
        var column = new Column("value", ParquetPhysicalType.Int32,
            new ColumnOptions(ParquetRepetition.Required, [EncodingKind.Plain]));
        var schema = new ParquetSchema([column]);
        using var stream = new MemoryStream(capacity: 8 * 1024 * 1024);
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None
        });
        var serialized = writer.CreateSerializedColumn<int>(column);
        var values = CreateValues(1_000_000);

        for (var i = 0; i < 8; i++)
            WriteOneRowGroup(writer, stream, serialized, values);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        WriteOneRowGroup(writer, stream, serialized, values);
        var after = GC.GetAllocatedBytesForCurrentThread();
        var allocated = after - before;

        if (allocated != 0)
            throw new InvalidOperationException(
                $"Expected zero allocations for steady-state large non-dictionary write chain but saw {allocated} bytes.");
    }

    [Test]
    public void ByteArrayWriteChainDoesNotAllocateAfterWarmup()
    {
        var column = new Column("value", ParquetPhysicalType.ByteArray,
            new ColumnOptions(ParquetRepetition.Required, [EncodingKind.Plain]));
        var schema = new ParquetSchema([column]);
        using var stream = new MemoryStream(capacity: 8 * 1024 * 1024);
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None
        });
        var serialized = writer.CreateSerializedColumn<byte[]>(column);
        var values = CreateByteArrayValues(4096);

        for (var i = 0; i < 8; i++)
            WriteOneRowGroup(writer, stream, serialized, values);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        WriteOneRowGroup(writer, stream, serialized, values);
        var after = GC.GetAllocatedBytesForCurrentThread();
        var allocated = after - before;

        if (allocated != 0)
            throw new InvalidOperationException(
                $"Expected zero allocations for steady-state byte array write chain but saw {allocated} bytes.");
    }

    static void WriteOneRowGroup(ParquetWriter writer, MemoryStream stream, SerializedColumn<int> serialized, int[] values)
    {
        writer.Reset(stream);
        serialized.Serialize(values);
        writer.StartRowGroup().Write(serialized);
    }

    static void WriteOneRowGroup(ParquetWriter writer, MemoryStream stream, SerializedColumn<byte[]> serialized,
        byte[][] values)
    {
        writer.Reset(stream);
        serialized.Serialize(values);
        writer.StartRowGroup().Write(serialized);
    }

    static int[] CreateValues(int count)
    {
        var result = new int[count];
        for (var i = 0; i < result.Length; i++)
            result[i] = i;
        return result;
    }

    static byte[][] CreateByteArrayValues(int count)
    {
        var result = new byte[count][];
        for (var i = 0; i < result.Length; i++)
            result[i] = System.Text.Encoding.UTF8.GetBytes($"val-{i % 2048}");
        return result;
    }
}
