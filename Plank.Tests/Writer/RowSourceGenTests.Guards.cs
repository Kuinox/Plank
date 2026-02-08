using Plank.Writing;

#pragma warning disable CA2007
namespace Plank.Tests;

sealed class RowSourceGenGuardTests
{
    [Test]
    public async Task CreateWriterRejectsNegativeRowCount()
    {
        using var stream = new MemoryStream();
        using var writer = ParquetWriter.Create(stream, GeneratedSchemaHolder.Schema);
        var rowGroup = writer.StartRowGroup();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await Task.Run(() => GeneratedSchemaHolder_SchemaPlankRow.CreateWriter(rowGroup, -1)));
    }

    [Test]
    public async Task GetRowAndNextRejectWhenNoSlotsRemain()
    {
        using var stream = new MemoryStream();
        using var writer = ParquetWriter.Create(stream, GeneratedSchemaHolder.Schema);
        var rowGroup = writer.StartRowGroup();
        var rows = GeneratedSchemaHolder_SchemaPlankRow.CreateWriter(rowGroup, 1);

        rows.Next();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => rows.GetRow()));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => rows.Next()));
    }

    [Test]
    public async Task WriteAsyncRejectsWhenNotAllRowsWereFilled()
    {
        using var stream = new MemoryStream();
        using var writer = ParquetWriter.Create(stream, GeneratedSchemaHolder.Schema);
        var rowGroup = writer.StartRowGroup();
        var rows = GeneratedSchemaHolder_SchemaPlankRow.CreateWriter(rowGroup, 2);

        var row = rows.GetRow();
        row.id = 7;
        row.flag = true;
        row.amount = 10;
        row.ratio = 1;
        row.score = 2;
        row.blob = [1];
        row.opt_int = 3;
        rows.Next();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rows.WriteAsync());
    }

    [Test]
    public async Task WriteAsyncRejectsSecondCallAndFurtherRowMutation()
    {
        using var stream = new MemoryStream();
        using var writer = ParquetWriter.Create(stream, GeneratedSchemaHolder.Schema);
        var rowGroup = writer.StartRowGroup();
        var rows = GeneratedSchemaHolder_SchemaPlankRow.CreateWriter(rowGroup, 1);

        var row = rows.GetRow();
        row.id = 1;
        row.flag = false;
        row.amount = 2;
        row.ratio = 3;
        row.score = 4;
        row.blob = [5];
        row.opt_int = null;
        rows.Next();
        await rows.WriteAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rows.WriteAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => rows.GetRow()));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => rows.Next()));
    }

    [Test]
    public async Task WriteAsyncHonorsPreCanceledToken()
    {
        using var stream = new MemoryStream();
        using var writer = ParquetWriter.Create(stream, GeneratedSchemaHolder.Schema);
        var rowGroup = writer.StartRowGroup();
        var rows = GeneratedSchemaHolder_SchemaPlankRow.CreateWriter(rowGroup, 1);

        var row = rows.GetRow();
        row.id = 1;
        row.flag = true;
        row.amount = 2;
        row.ratio = 3;
        row.score = 4;
        row.blob = [5];
        row.opt_int = null;
        rows.Next();

        var canceled = new CancellationToken(canceled: true);
        await Assert.ThrowsAsync<TaskCanceledException>(async () =>
            await rows.WriteAsync(canceled));
    }

    [Test]
    public async Task EmptyRowWriterWriteAsyncThrowsForZeroRows_CurrentBehavior()
    {
        using var stream = new MemoryStream();
        using var writer = ParquetWriter.Create(stream, GeneratedSchemaHolder.Schema);
        var rowGroup = writer.StartRowGroup();
        var rows = GeneratedSchemaHolder_SchemaPlankRow.CreateWriter(rowGroup, 0);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => rows.GetRow()));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rows.WriteAsync());
    }
}
