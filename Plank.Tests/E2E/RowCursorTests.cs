using Plank.Reading;
using Plank.Writing;

namespace Plank.Tests.E2E;

internal sealed class RowCursorTests
{
    [Test]
    public void CursorRefreshesAcrossMixedAccessAndReset()
    {
        using var first = new MemoryStream();
        using var second = new MemoryStream();
        using var writer = DatasetRowSchema.CreateRowWriter(first, new ParquetWriterOptions
        {
            RowApiMaxParallelism = 1,
            RowApiInitialRowCapacity = 1,
            TargetRowGroupSizeBytes = 24
        });
        var cursor = writer.CreateCursor();
        var other = writer.CreateCursor();
        for (var file = 0; file < 2; file++)
        {
            for (var i = 0; i < 32; i++)
            {
                if (i % 3 == 0)
                {
                    cursor.NextRow();
                    cursor.Value = i;
                    cursor.Path = new byte[i];
                }
                else if (i % 3 == 1)
                {
                    var row = writer.GetRow();
                    row.Value = i;
                    row.Path = new byte[i];
                }
                else
                {
                    other.NextRow();
                    other.Value = i;
                    other.Path = new byte[i];
                }
            }
            writer.Complete();
            var rejected = false;
            try { cursor.NextRow(); }
            catch (InvalidOperationException) { rejected = true; }
            if (!rejected)
                throw new InvalidOperationException("Completed writer accepted a cursor row.");
            if (file == 0)
                writer.Reset(second);
        }

        Verify(first.ToArray());
        Verify(second.ToArray());
        writer.Dispose();
        var disposedRejected = false;
        try { cursor.NextRow(); }
        catch (ObjectDisposedException) { disposedRejected = true; }
        if (!disposedRejected)
            throw new InvalidOperationException("Disposed writer accepted a cursor row.");
    }

    [Test]
    public void CreatingCursorDoesNotReserveRowAndUnpositionedSetterFaults()
    {
        using var stream = new MemoryStream();
        using var writer = DatasetRowSchema.CreateRowWriter(stream, maxParallelism: 1);
        var cursor = writer.CreateCursor();
        var rejected = false;
        try { cursor.Value = 42; }
        catch (NullReferenceException) { rejected = true; }
        if (!rejected)
            throw new InvalidOperationException("Unpositioned cursor silently accepted an assignment.");
        writer.Complete();
        using var source = new MemoryReadSource(stream.ToArray());
        using var reader = DatasetRowSchema.CreateRowReader(source);
        if (reader.MoveNext())
            throw new InvalidOperationException("Creating a cursor reserved an unwanted row.");
    }

    [Test]
    public void CursorRejectsZeroCapacityBeforeBindingRefs()
    {
        using var stream = new MemoryStream();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            using var writer = DatasetRowSchema.CreateRowWriter(stream, new ParquetWriterOptions
            {
                RowApiMaxParallelism = 1,
                RowApiInitialRowCapacity = 0
            });
            var cursor = writer.CreateCursor();
            cursor.NextRow();
        });
    }

    static void Verify(byte[] bytes)
    {
        using var source = new MemoryReadSource(bytes);
        using var reader = DatasetRowSchema.CreateRowReader(source);
        var index = 0;
        while (reader.MoveNext())
        {
            if (reader.Current.Value != index || reader.Current.Path.Value.Length != index)
                throw new InvalidOperationException($"Mixed cursor row {index} was corrupted.");
            index++;
        }
        if (index != 32)
            throw new InvalidOperationException($"Expected 32 rows, got {index}.");
    }
}
