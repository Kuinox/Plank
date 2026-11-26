using Plank.Reading.Logical;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Reading;

[NotInParallel]
internal sealed class LogicalReaderSetupAllocationTests
{
    [Test]
    public void LenientResetDoesNotMaterializeUnusedChunkPaths()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.RequiredGroup("group", ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int32))
        ]);
        using var stream = new MemoryStream();
        using (var writer = schema.CreateWriter(stream))
        {
            var column = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);
            for (var i = 0; i < 128; i++)
            {
                column.Serialize([i]);
                writer.StartRowGroup().Write(column);
            }
            writer.CloseFile();
        }

        var bytes = stream.ToArray();
        using var strict = new ParquetReader();
        using var lenient = new ParquetReader(new ParquetReaderOptions { Strict = false });
        for (var i = 0; i < 8; i++)
        {
            Reset(strict);
            Reset(lenient);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        Reset(strict);
        var strictBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        Reset(lenient);
        var lenientBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"Logical reset allocated {strictBytes} strict; {lenientBytes} lenient bytes.");
        if (lenientBytes > strictBytes + 256 || lenient.RowGroups.Count != 128 ||
            lenient.RowGroups[127].GetColumnMetadata(0).Definition.Path != "group.value")
            throw new InvalidOperationException("Lenient reset exceeded the setup allocation budget or lost schema metadata.");

        void Reset(ParquetReader reader) => reader.Reset(new MemoryStream(bytes));
    }
}
