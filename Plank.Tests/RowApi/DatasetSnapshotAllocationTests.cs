using Plank.Dataset;
using Plank.RowApi;
using Plank.Schema;

namespace Plank.Tests.RowApi;

internal sealed class DatasetSnapshotAllocationTests
{
    [Test]
    [Arguments(1024)]
    [Arguments(70000)]
    public async Task SnapshotPromotionAndReuseDoNotAllocate(int payloadSize)
    {
        var schema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("Payload", ParquetPhysicalType.ByteArray),
            ColumnDefinition.OptionalLeaf("OptionalPayload", ParquetPhysicalType.ByteArray),
            ColumnDefinition.RequiredLeaf("Memory", ParquetPhysicalType.ByteArray),
            ColumnDefinition.OptionalLeaf("OptionalMemory", ParquetPhysicalType.ByteArray)
        ]);
        var columns = DatasetBufferSlot.CreateSnapshotColumns([
            new RowApiColumnDescriptor<byte[]>("Payload", schema.LeafColumns[0]),
            new RowApiColumnDescriptor<byte[]>("OptionalPayload", schema.LeafColumns[1]),
            new RowApiColumnDescriptor<ReadOnlyMemory<byte>>("Memory", schema.LeafColumns[2]),
            new RowApiColumnDescriptor<ReadOnlyMemory<byte>?>("OptionalMemory", schema.LeafColumns[3])
        ]);
        using var pool = new DefaultParquetBufferPool();
        var parked = new DatasetBufferSlot(columns, 8, pool);
        var active = new DatasetBufferSlot(columns, 8, pool);
        var bytes = new byte[payloadSize];
        try
        {
            Cycle(parked, active, bytes);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 20; i++)
                Cycle(parked, active, bytes);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            await Assert.That(allocated).IsEqualTo(0);
        }
        finally
        {
            parked.ResetForReuseAndSize();
            active.ResetForReuseAndSize();
        }
    }

    static void Cycle(DatasetBufferSlot parked, DatasetBufferSlot active, byte[] bytes)
    {
        for (var i = 0; i < 8; i++)
            SetRow(parked, i, bytes);
        for (var i = 0; i < 8; i++)
        {
            parked.MoveRowTo(i, active, i);
            active.NextSized();
            parked.ClearRow(i);
            // Reuse each parked position while the promoted row still owns its snapshot.
            SetRow(parked, i, bytes);
        }
        parked.ResetForReuseAndSize();
        active.ResetForReuseAndSize();
    }

    static void SetRow(DatasetBufferSlot slot, int index, byte[] bytes)
    {
        slot.SetSnapshotValue(0, index, bytes);
        slot.SetSnapshotValue<byte[]?>(1, index, index % 3 == 0 ? null : index % 3 == 1 ? [] : bytes);
        slot.SetSnapshotValue<ReadOnlyMemory<byte>>(2, index, bytes.AsMemory(1, bytes.Length - 2));
        slot.SetSnapshotValue<ReadOnlyMemory<byte>?>(3, index,
            index % 3 == 0 ? null : index % 3 == 1 ? ReadOnlyMemory<byte>.Empty : bytes.AsMemory(2, bytes.Length - 4));
    }
}
