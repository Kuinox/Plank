using System.Runtime.CompilerServices;
using Plank.Schema;
using Plank.Writing;

namespace Plank.RowApi;

/// <summary>Coordinates buffered row batches for a generated pipeline row writer.</summary>
/// <typeparam name="TSlot">The generated buffer-slot type.</typeparam>
/// <remarks>
/// This unstable API supports Plank-generated code and is not intended for direct use by applications.
/// </remarks>
public abstract class PipelineRowWriterBase<TSlot> : RowWriterBase<TSlot>
    where TSlot : RowBufferSlot
{
    readonly Action<int>? _onFlush;
    readonly ulong _targetRowGroupSizeBytes;
    ulong _bufferedSizeBytes;
    TSlot _active;
    bool _completed;

    /// <summary>Initializes the infrastructure for a generated pipeline row writer.</summary>
    /// <param name="stream">The destination stream.</param>
    /// <param name="schema">The generated Parquet schema.</param>
    /// <param name="maxParallelism">The maximum number of serialization workers.</param>
    /// <param name="onFlush">An optional callback invoked with each flushed row count.</param>
    /// <param name="options">The Parquet writer options.</param>
    /// <param name="rowBatchSize">The number of rows in each generated buffer slot.</param>
    /// <param name="workerThreadNamePrefix">The worker-thread name prefix.</param>
    protected PipelineRowWriterBase(Stream stream, ParquetSchema schema, uint maxParallelism, Action<int>? onFlush,
        ParquetWriterOptions options, int rowBatchSize, string workerThreadNamePrefix)
        : base(stream, schema, maxParallelism, options)
    {
        if (rowBatchSize < 0)
            throw new ArgumentOutOfRangeException(nameof(rowBatchSize), rowBatchSize, "Row batch size must be non-negative.");
        ArgumentException.ThrowIfNullOrEmpty(workerThreadNamePrefix);

        RowBatchSize = rowBatchSize;
        _onFlush = onFlush;
        _targetRowGroupSizeBytes = options.TargetRowGroupSizeBytes;
        WorkerThreadNamePrefix = workerThreadNamePrefix;
        InitializeSlots();
        _active = TakeInitialSlot();
        _bufferedSizeBytes = 0;
        _completed = false;
    }

    /// <summary>Initializes a rolling generated pipeline row writer.</summary>
    /// <param name="file">The reusable destination used for each produced file.</param>
    /// <param name="filePath">Selects the path of each produced file.</param>
    /// <param name="schema">The generated Parquet schema.</param>
    /// <param name="maxParallelism">The maximum number of serialization workers.</param>
    /// <param name="onFlush">An optional callback invoked with each flushed row count.</param>
    /// <param name="options">The Parquet writer options.</param>
    /// <param name="rowBatchSize">The initial row capacity of each generated buffer slot.</param>
    /// <param name="workerThreadNamePrefix">The worker-thread name prefix.</param>
    protected PipelineRowWriterBase(IParquetWriteSource file, ParquetFilePath filePath, ParquetSchema schema,
        uint maxParallelism, Action<int>? onFlush, ParquetWriterOptions options, int rowBatchSize,
        string workerThreadNamePrefix)
        : base(file, filePath, schema, maxParallelism, options)
    {
        if (rowBatchSize < 0)
            throw new ArgumentOutOfRangeException(nameof(rowBatchSize), rowBatchSize, "Row batch size must be non-negative.");
        ArgumentException.ThrowIfNullOrEmpty(workerThreadNamePrefix);

        RowBatchSize = rowBatchSize;
        _onFlush = onFlush;
        _targetRowGroupSizeBytes = options.TargetRowGroupSizeBytes;
        WorkerThreadNamePrefix = workerThreadNamePrefix;
        InitializeSlots();
        _active = TakeInitialSlot();
        _bufferedSizeBytes = 0;
        _completed = false;
    }

    /// <summary>Gets the generated writer's row-batch size.</summary>
    protected int RowBatchSize { get; }

    /// <inheritdoc />
    protected override void SerializeSlot(TSlot slot)
        => slot.SerializeColumns();

    /// <inheritdoc />
    protected override void WriteSerializedSlot(TSlot slot, RowGroupWriter rowGroupWriter)
        => slot.WriteSerialized(rowGroupWriter);

    /// <inheritdoc />
    protected override void OnSlotWritten(TSlot slot)
        => _onFlush?.Invoke(slot.Count);

    /// <inheritdoc />
    protected override void ResetSlotForReuse(TSlot slot)
        => slot.ResetForReuse();

    /// <inheritdoc />
    protected override string WorkerThreadNamePrefix { get; }

    /// <summary>Gets the current buffer slot for generated row assignment.</summary>
    /// <returns>The current writable slot.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected TSlot GetSlotForRow()
    {
        ThrowIfFaulted();
        if (_completed)
            throw new InvalidOperationException("Pipeline writer is already completed.");
        return _active;
    }

    /// <summary>Advances the generated writer to its next row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected void NextRow()
    {
        ThrowIfFaulted();
        if (_completed)
            throw new InvalidOperationException("Pipeline writer is already completed.");
        NextVariableRowCore(_active.GetRowSize());
    }

    /// <summary>Advances a generated fixed-width writer to its next row.</summary>
    /// <param name="rowsPerGroup">The generated row-count cutoff for one row group.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected void NextFixedRow(int rowsPerGroup)
    {
        ThrowIfFaulted();
        if (_completed)
            throw new InvalidOperationException("Pipeline writer is already completed.");

        _active.Next();
        if (_active.Count < rowsPerGroup && (!_active.IsFull || _active.Grow()))
            return;

        _active = EnqueueAndTakeFree(_active);
    }

    /// <summary>Advances a generated variable-width writer to its next row.</summary>
    /// <param name="rowSizeBytes">The generated estimate of the current row's buffered size.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected void NextVariableRow(ulong rowSizeBytes)
    {
        ThrowIfFaulted();
        if (_completed)
            throw new InvalidOperationException("Pipeline writer is already completed.");

        NextVariableRowCore(rowSizeBytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void NextVariableRowCore(ulong rowSizeBytes)
    {
        _bufferedSizeBytes = checked(_bufferedSizeBytes + rowSizeBytes);
        _active.Next();
        if (_bufferedSizeBytes < _targetRowGroupSizeBytes && (!_active.IsFull || _active.Grow()))
            return;

        _active = EnqueueAndTakeFree(_active);
        _bufferedSizeBytes = 0;
    }

    /// <summary>Calculates the generated row-count cutoff for a fixed-width schema.</summary>
    /// <param name="fixedRowSizeBytes">The generated fixed size of one row.</param>
    /// <returns>The number of rows to buffer before flushing.</returns>
    protected int GetFixedRowsPerGroup(ulong fixedRowSizeBytes)
    {
        if (fixedRowSizeBytes == 0)
            throw new ArgumentOutOfRangeException(nameof(fixedRowSizeBytes), fixedRowSizeBytes,
                "Fixed row size must be greater than zero.");

        var rowCount = _targetRowGroupSizeBytes / fixedRowSizeBytes;
        if (_targetRowGroupSizeBytes % fixedRowSizeBytes != 0)
            rowCount++;
        return checked((int)Math.Min(rowCount, (ulong)int.MaxValue));
    }

    /// <summary>Flushes pending rows and completes the generated writer.</summary>
    protected void CompleteWriter()
    {
        ThrowIfFaulted();
        if (_completed)
            return;

        Complete(_active, !_active.IsEmpty);
        _completed = true;
    }

    /// <summary>Resets a completed generated writer to a new destination stream.</summary>
    /// <param name="stream">The new destination stream.</param>
    protected void ResetWriter(Stream stream)
    {
        if (!_completed)
            throw new InvalidOperationException("Pipeline writer must be completed before it can be reset.");

        ResetPipeline(stream);
        _active = TakeInitialSlot();
        _bufferedSizeBytes = 0;
        _completed = false;
    }
}
