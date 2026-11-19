using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Writer;

[NotInParallel]
internal sealed class AppendMemoryAllocationTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public void RetainedBinaryMemoryProjectionOnlyAllocatesItsResult(bool optional)
    {
        const int rowCount = 4096;
        var definition = optional
            ? ColumnDefinition.OptionalLeaf("value", ParquetPhysicalType.ByteArray)
            : ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.ByteArray);
        var schema = new ParquetSchema([definition]);
        byte[][] values = Enumerable.Repeat(new byte[] { 42 }, rowCount).ToArray();
        values[0] = [];
        if (optional)
            values[1] = null!;
        using var stream = new MemoryStream();
        using (var writer = schema.CreateWriter(stream))
        {
            var column = writer.CreateSerializedColumn<byte[]>(schema.LeafColumns[0]);
            column.Serialize(values);
            writer.StartRowGroup().Write(column);
            writer.CloseFile();
        }
        using var reader = schema.CreateReader(new MemoryStream(stream.ToArray()));
        var retained = LatestRowGroupValues.Read(reader.RowGroups[0], schema.Columns.ToArray());

        if (optional)
            Check<ReadOnlyMemory<byte>?>(retained,
                result => result[0] is { IsEmpty: true } && result[1] is null && result[2] is { } present &&
                          present.Span.SequenceEqual(new byte[] { 42 }));
        else
            Check<ReadOnlyMemory<byte>>(retained,
                result => result[0].IsEmpty && result[1].Span.SequenceEqual(new byte[] { 42 }));
    }

    static void Check<T>(LatestRowGroupValues retained, Func<T[], bool> matches)
    {
        for (var i = 0; i < 8; i++)
            _ = retained.GetValues<T>(0);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = retained.GetValues<T>(0);
        var actual = GC.GetAllocatedBytesForCurrentThread() - before;

        before = GC.GetAllocatedBytesForCurrentThread();
        GC.KeepAlive(new T[result.Length]);
        var expected = GC.GetAllocatedBytesForCurrentThread() - before;

        Console.WriteLine($"{typeof(T)}: retained conversion {actual}; result array {expected} bytes.");
        if (actual > expected + 256 || result.Length != 4096 || !matches(result))
            throw new InvalidOperationException("Retained memory projection changed values or allocated more than its result.");
    }
}
