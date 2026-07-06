using Plank.Schema;
using Plank.Writing;

namespace Plank.RowApi;

public abstract class PipelineRowWriterBase<TSlot> : RowWriterBase<TSlot>
    where TSlot : RowBufferSlot
{
    readonly Action<int>? _onFlush;
    readonly string _workerThreadNamePrefix;
    TSlot _active;
    bool _completed;

    protected PipelineRowWriterBase(Stream stream, ParquetSchema schema, uint maxParallelism, Action<int>? onFlush,
        ParquetWriterOptions options, int rowBatchSize, string workerThreadNamePrefix)
        : base(stream, schema, maxParallelism, options)
    {
        if (rowBatchSize < 0)
            throw new ArgumentOutOfRangeException(nameof(rowBatchSize), rowBatchSize, "Row batch size must be non-negative.");
        ArgumentException.ThrowIfNullOrEmpty(workerThreadNamePrefix);

        RowBatchSize = rowBatchSize;
        _onFlush = onFlush;
        _workerThreadNamePrefix = workerThreadNamePrefix;
        InitializeSlots();
        _active = TakeInitialSlot();
        _completed = false;
    }

    protected int RowBatchSize { get; }

    protected override void SerializeSlot(TSlot slot)
        => slot.SerializeColumns();

    protected override void WriteSerializedSlot(TSlot slot, RowGroupWriter rowGroupWriter)
        => slot.WriteSerialized(rowGroupWriter);

    protected override void OnSlotWritten(TSlot slot)
        => _onFlush?.Invoke(slot.Count);

    protected override void ResetSlotForReuse(TSlot slot)
        => slot.ResetForReuse();

    protected override string WorkerThreadNamePrefix
        => _workerThreadNamePrefix;

    protected TSlot GetSlotForRow()
    {
        ThrowIfFaulted();
        if (_completed)
            throw new InvalidOperationException("Pipeline writer is already completed.");
        return _active;
    }

    protected void NextRow()
    {
        ThrowIfFaulted();
        if (_completed)
            throw new InvalidOperationException("Pipeline writer is already completed.");

        _active.Next();
        if (!_active.IsFull)
            return;

        _active = EnqueueAndTakeFree(_active);
    }

    protected void CompleteWriter()
    {
        ThrowIfFaulted();
        if (_completed)
            return;

        Complete(_active, !_active.IsEmpty);
        _completed = true;
    }
}
