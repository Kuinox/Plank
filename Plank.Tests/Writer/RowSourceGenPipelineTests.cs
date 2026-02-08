using Plank.Writing;

#pragma warning disable CA2007
namespace Plank.Tests;

sealed class RowSourceGenPipelineTests
{
    [Test]
    public async Task CreatePipelineWriterRejectsNegativeRowCount()
    {
        using var stream = new MemoryStream();
        using var writer = ParquetWriter.Create(stream, GeneratedSchemaHolder.Schema);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await Task.Run(() => GeneratedSchemaHolder_SchemaPlankRow.CreatePipelineWriter(writer, -1)));
    }

    [Test]
    public async Task CreatePipelineWriterRejectsNullWriter()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await Task.Run(() => GeneratedSchemaHolder_SchemaPlankRow.CreatePipelineWriter(null!, 1)));
    }

    [Test]
    public async Task FlushAsyncRejectsWhenRowsAreNotFullyFilled()
    {
        using var stream = new MemoryStream();
        using var writer = ParquetWriter.Create(stream, GeneratedSchemaHolder.Schema);
        var pipeline = GeneratedSchemaHolder_SchemaPlankRow.CreatePipelineWriter(writer, 2);

        var row = pipeline.GetRow();
        row.id = 1;
        row.flag = true;
        row.amount = 10;
        row.ratio = 1;
        row.score = 2;
        row.blob = [1];
        row.opt_int = 3;
        pipeline.Next();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await pipeline.FlushAsync());
    }

    [Test]
    public async Task CompleteAsyncPropagatesBackgroundWriteFailure()
    {
        using var stream = new MemoryStream();
        using var writer = ParquetWriter.Create(stream, GeneratedSchemaHolder.Schema, new ParquetWriterOptions
        {
            ExpectedRowGroupCount = 1
        });
        var pipeline = GeneratedSchemaHolder_SchemaPlankRow.CreatePipelineWriter(writer, 1);

        FillOneRow(pipeline, id: 1, value: 10);
        await pipeline.FlushAsync();
        FillOneRow(pipeline, id: 2, value: 20);
        await pipeline.FlushAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await pipeline.CompleteAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => pipeline.GetRow()));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => pipeline.Next()));
    }

    [Test]
    public async Task FlushAsyncRequiresRefillAfterPreviousFlush()
    {
        using var stream = new MemoryStream();
        using var writer = ParquetWriter.Create(stream, GeneratedSchemaHolder.Schema, new ParquetWriterOptions
        {
            ExpectedRowGroupCount = 1
        });
        var pipeline = GeneratedSchemaHolder_SchemaPlankRow.CreatePipelineWriter(writer, 1);

        FillOneRow(pipeline, id: 1, value: 10);
        await pipeline.FlushAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await pipeline.FlushAsync());
    }

    [Test]
    public async Task FlushAsyncWithCanceledTokenFaultsPipeline()
    {
        using var stream = new MemoryStream();
        using var writer = ParquetWriter.Create(stream, GeneratedSchemaHolder.Schema, new ParquetWriterOptions
        {
            ExpectedRowGroupCount = 1
        });
        var pipeline = GeneratedSchemaHolder_SchemaPlankRow.CreatePipelineWriter(writer, 1);

        FillOneRow(pipeline, id: 1, value: 10);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await pipeline.FlushAsync(cts.Token);

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await pipeline.CompleteAsync());
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await pipeline.CompleteAsync());
    }

    [Test]
    public async Task ZeroRowPipelineFlushThrows_CurrentBehavior()
    {
        using var stream = new MemoryStream();
        using var writer = ParquetWriter.Create(stream, GeneratedSchemaHolder.Schema, new ParquetWriterOptions
        {
            ExpectedRowGroupCount = 1
        });
        var pipeline = GeneratedSchemaHolder_SchemaPlankRow.CreatePipelineWriter(writer, 0);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await pipeline.FlushAsync());
    }

    [Test]
    public async Task CompleteAsyncWithoutAnyFlushIsNoOp()
    {
        using var stream = new MemoryStream();
        using var writer = ParquetWriter.Create(stream, GeneratedSchemaHolder.Schema);
        var pipeline = GeneratedSchemaHolder_SchemaPlankRow.CreatePipelineWriter(writer, 1);

        await pipeline.CompleteAsync();
    }

    static void FillOneRow(GeneratedSchemaHolder_SchemaPlankRow.PipelineWriter pipeline, int id, long value)
    {
        var row = pipeline.GetRow();
        row.id = id;
        row.flag = (id & 1) == 0;
        row.amount = value;
        row.ratio = value;
        row.score = value;
        row.blob = [checked((byte)id)];
        row.opt_int = id;
        pipeline.Next();
    }
}
#pragma warning restore CA2007
