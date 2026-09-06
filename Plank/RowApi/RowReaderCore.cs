using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using System.Runtime.CompilerServices;
using Plank.Reading;
using Plank.Reading.Logical;
using Plank.Schema;

namespace Plank.RowApi;

/// <summary>Provides the column-oriented reading core used by generated row readers.</summary>
/// <remarks>
/// This unstable API supports Plank-generated code and is not intended for direct use by applications.
/// </remarks>
public sealed class RowReaderCore : IDisposable
{
    RowApiColumnReadState[] _states;
    readonly RowApiValueBatch[] _valueBatches;
    readonly ParquetExecutionOptions _execution;
    readonly int _maxReadAhead;
    RowReaderWorkers? _workers;
    RowReaderWorkers.Work[] _work;
    RowReaderWorkers.Work[]? _aheadWork;
    RowApiColumnReadState[]? _aheadStates;
    RowGroup? _aheadGroup;
    ExceptionDispatchInfo? _fault;
    readonly RowApiColumnReadState[] _projectedStates;
    readonly ParquetReader _reader;
    RowGroup _rowGroup;
    ParquetSchemaEvolutionOptions? _schemaEvolution;
    StreamReadSource? _streamSource;
    RowGroupCollection.Enumerator _rowGroups;
    ulong _rowGroupRowsRemaining;
    bool _started;
    bool _hasCurrent;
    bool _disposed;
    int _projectedStateCount;
    bool _batchAdvance;
    int _batchLength;
    int _batchOffset;
    int _currentBatchOffset;

    /// <summary>Initializes a generated row reader over a stream.</summary>
    /// <param name="stream">The source stream.</param>
    /// <param name="schema">The generated row schema.</param>
    /// <param name="columns">The generated column descriptors.</param>
    /// <param name="projection">The selected columns, or <see langword="null"/> for all columns.</param>
    /// <param name="options">The row reader options.</param>
    /// <param name="schemaEvolution">The optional schema-evolution policy.</param>
    public RowReaderCore(Stream stream, ParquetSchema schema, RowApiColumnDescriptor[] columns,
        RowApiColumnDescriptor[]? projection, RowReaderOptions options,
        ParquetSchemaEvolutionOptions? schemaEvolution)
        : this(new StreamReadSource(stream), schema, columns, projection, options, schemaEvolution)
    {
    }

    /// <summary>Initializes a generated row reader over a random-access source.</summary>
    /// <param name="source">The random-access source.</param>
    /// <param name="schema">The generated row schema.</param>
    /// <param name="columns">The generated column descriptors.</param>
    /// <param name="projection">The selected columns, or <see langword="null"/> for all columns.</param>
    /// <param name="options">The row reader options.</param>
    /// <param name="schemaEvolution">The optional schema-evolution policy.</param>
    public RowReaderCore(IParquetReadSource source, ParquetSchema schema, RowApiColumnDescriptor[] columns,
        RowApiColumnDescriptor[]? projection, RowReaderOptions options,
        ParquetSchemaEvolutionOptions? schemaEvolution)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (columns.Length != schema.LeafColumns.Length)
            throw new ArgumentException("Row API column descriptors must match the row API schema column count.",
                nameof(columns));

        _execution = options.Execution;
        _maxReadAhead = options.MaxReadAheadRowGroups;
        _schemaEvolution = schemaEvolution;
        _streamSource = source as StreamReadSource;
        _states = CreateStates(schema, columns);
        _valueBatches = new RowApiValueBatch[_states.Length];
        _work = CreateWork(_states);
        _projectedStates = new RowApiColumnReadState[_states.Length];
        _reader = new ParquetReader(CreateLooseReaderOptions(options));
        _rowGroup = default;
        _rowGroups = default;
        _rowGroupRowsRemaining = 0;
        _started = false;
        _hasCurrent = false;
        _disposed = false;
        _batchAdvance = false;
        _batchLength = 0;
        _batchOffset = 0;
        _currentBatchOffset = 0;
        try
        {
            _reader.Reset(PrepareSource(source));
            ApplyProjection(projection);
            ResolveFileSchema();
            RebuildProjectedStates();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Advances the generated reader to the next row.</summary>
    /// <returns><see langword="true"/> when a row is available; otherwise, <see langword="false"/>.</returns>
    public bool MoveNext()
    {
        ThrowIfDisposed();
        _fault?.Throw();
        if (_batchOffset < _batchLength)
        {
            _currentBatchOffset = _batchOffset++;
            _rowGroupRowsRemaining--;
            _hasCurrent = true;
            return true;
        }
        return MoveNextSlow();
    }

    bool MoveNextSlow()
    {
        Array.Clear(_valueBatches);
        _hasCurrent = false;
        try
        {
            EnsureStarted();
            _hasCurrent = ReadNextRow();
            return _hasCurrent;
        }
        catch (Exception ex)
        {
            Array.Clear(_valueBatches);
            _fault = ExceptionDispatchInfo.Capture(ex);
            throw;
        }
    }

    /// <summary>Resets the generated row reader to a stream and projection.</summary>
    /// <param name="stream">The new source stream.</param>
    /// <param name="projection">The selected columns, or <see langword="null"/> for all columns.</param>
    /// <param name="schemaEvolution">An optional replacement schema-evolution policy.</param>
    public void Reset(Stream stream, RowApiColumnDescriptor[]? projection,
        ParquetSchemaEvolutionOptions? schemaEvolution = null)
    {
        ThrowIfDisposed();
        DrainReadAhead();
        if (_streamSource is null)
            _streamSource = new StreamReadSource(stream);
        else
            _streamSource.Reset(stream);
        Reset(_streamSource, projection, schemaEvolution);
    }

    /// <summary>Resets the generated row reader to a random-access source and projection.</summary>
    /// <param name="source">The new random-access source.</param>
    /// <param name="projection">The selected columns, or <see langword="null"/> for all columns.</param>
    /// <param name="schemaEvolution">An optional replacement schema-evolution policy.</param>
    public void Reset(IParquetReadSource source, RowApiColumnDescriptor[]? projection,
        ParquetSchemaEvolutionOptions? schemaEvolution = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);
        if (schemaEvolution is not null)
            _schemaEvolution = schemaEvolution;

        DrainReadAhead();
        _fault = null;
        ApplyProjection(projection);
        DisposeColumnReaders();
        _reader.Reset(PrepareSource(source));
        _rowGroup = default;
        _rowGroups = default;
        _rowGroupRowsRemaining = 0;
        _started = false;
        _hasCurrent = false;
        _batchLength = 0;
        _batchOffset = 0;
        _currentBatchOffset = 0;
        ResolveFileSchema();
        RebuildProjectedStates();
    }

    /// <summary>Gets a generated property's current value.</summary>
    /// <typeparam name="T">The column's generated CLR value type.</typeparam>
    /// <param name="column">The generated property column.</param>
    /// <returns>A reference to the current value.</returns>
    public ref T GetCurrent<T>(RowApiColumnDescriptor<T> column)
    {
        ThrowIfNotPositioned();
        var state = GetState<T>(column);
        var values = state.CurrentSpan;
        return ref values[GetCurrentIndex(state)];
    }

    /// <summary>Gets a generated property's current value by its schema ordinal.</summary>
    /// <typeparam name="T">The column's generated CLR value type.</typeparam>
    /// <param name="columnIndex">The generated schema ordinal.</param>
    /// <returns>A reference to the current value.</returns>
    /// <remarks>The source generator supplies an ordinal and type validated when the reader is constructed.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe ref T GetCurrent<T>(int columnIndex)
    {
        // Bound addresses exist only while positioned on a validated value batch.
        if ((uint)columnIndex < (uint)_valueBatches.Length)
        {
            ref readonly var batch = ref _valueBatches[columnIndex];
            if (batch.Address != 0 && batch.Type.Equals(typeof(T).TypeHandle))
                return ref Unsafe.Add(
                    ref Unsafe.AsRef<T>((void*)batch.Address), _currentBatchOffset);
        }
        return ref GetCurrentChecked<T>(columnIndex);
    }

    ref T GetCurrentChecked<T>(int columnIndex)
    {
        ThrowIfNotPositioned();
        var state = (RowApiColumnReadState<T>)_states[columnIndex];
        if (!state.Projected && !state.Materialized)
            throw new InvalidOperationException($"Column '{state.PropertyName}' was not selected.");
        var values = state.CurrentSpan;
        return ref values[GetCurrentIndex(state)];
    }

    /// <summary>Gets the current zero-copy value for a variable-length byte column.</summary>
    /// <typeparam name="T">The column's generated CLR binary type.</typeparam>
    /// <param name="column">The generated property column.</param>
    /// <returns>The current binary value and its null state.</returns>
    public RowReaderBinaryValue GetCurrentBinary<T>(RowApiColumnDescriptor<T> column)
    {
        ThrowIfNotPositioned();
        var state = GetBinaryState(column);
        var value = state.CurrentValue;
        if (!value.IsEmpty)
            return new RowReaderBinaryValue(value);
        return new RowReaderBinaryValue(state.CurrentIsNull
            ? default
            : RowReaderBinaryValue.NonNullEmpty);
    }

    /// <summary>Gets an allocating generated nested property's current leaf shape.</summary>
    /// <typeparam name="TShape">The generated jagged leaf shape.</typeparam>
    /// <typeparam name="TElement">The dense physical leaf value type.</typeparam>
    /// <param name="column">The generated nested leaf descriptor.</param>
    /// <returns>A reference to the current materialized shape.</returns>
    public ref TShape GetCurrentNested<TShape, TElement>(
        RowApiNestedColumnDescriptor<TShape, TElement> column)
    {
        ThrowIfNotPositioned();
        var state = GetNestedState(column);
        return ref state.Current;
    }

    /// <summary>Throws if the generated reader is not positioned on a row.</summary>
    public void ThrowIfNotPositioned()
    {
        ThrowIfDisposed();
        if (!_hasCurrent)
            throw new InvalidOperationException("The row reader is not positioned on a row.");
    }

    /// <summary>Releases resources owned by the generated row reader core.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        DrainReadAhead();
        _workers?.Dispose();
        _workers = null;
        DisposeColumnReaders();
        foreach (var work in _work)
            work.Dispose();
        if (_aheadWork is not null)
            foreach (var work in _aheadWork)
                work.Dispose();
        _reader.Dispose();
        _disposed = true;
    }

    static RowApiColumnReadState[] CreateStates(ParquetSchema schema, RowApiColumnDescriptor[] columns)
    {
        var states = new RowApiColumnReadState[columns.Length];
        for (var i = 0; i < columns.Length; i++)
        {
            var column = columns[i] ?? throw new ArgumentException("Row API column descriptors cannot contain null values.",
                nameof(columns));
            if (!ReferenceEquals(column.Column, schema.LeafColumns[i]))
                throw new ArgumentException(
                    "Row API column descriptors must match their row API schema columns.",
                    nameof(columns));
            states[i] = column.CreateState();
        }

        return states;
    }

    static ParquetReaderOptions CreateLooseReaderOptions(RowReaderOptions options)
        => new()
        {
            BufferPool = options.BufferPool,
            Strict = false,
            VerifyPageCrc = options.VerifyPageCrc,
            BorrowRequiredPlainValues = false,
            BorrowBinaryValues = true
        };

    void ApplyProjection(RowApiColumnDescriptor[]? projection)
    {
        if (projection is null)
        {
            for (var i = 0; i < _states.Length; i++)
                _states[i].ResetForProjection(true);
            return;
        }

        for (var i = 0; i < projection.Length; i++)
            _ = GetSchemaState(projection[i], nameof(projection));

        for (var i = 0; i < _states.Length; i++)
            _states[i].ResetForProjection(false);

        for (var i = 0; i < projection.Length; i++)
            GetSchemaState(projection[i], nameof(projection)).ResetForProjection(true);
    }

    bool ReadNextRow()
    {
        while (_rowGroupRowsRemaining == 0)
            if (!OpenNextRowGroup())
            {
                _hasCurrent = false;
                return false;
            }

        if (_batchAdvance)
        {
            if (_batchOffset == _batchLength)
                PrepareBatch();
            _currentBatchOffset = _batchOffset++;
        }
        else
        {
            if (_workers is null)
            {
                for (var i = 0; i < _projectedStateCount; i++)
                    _projectedStates[i].Advance();
            }
            else
            {
                var queued = false;
                for (var i = 0; i < _projectedStateCount; i++)
                {
                    var state = _projectedStates[i];
                    state.CurrentIndex++;
                    if ((uint)state.CurrentIndex >= (uint)state.BufferedValueCount)
                    {
                        QueueBuffer(state);
                        queued = true;
                    }
                    else
                        state.Prefetched = false;
                }
                if (queued)
                    WaitForWork(_work);
            }
        }

        _rowGroupRowsRemaining--;
        return true;
    }

    bool OpenNextRowGroup()
    {
        while (true)
        {
            DisposeColumnReaders();
            if (_aheadGroup is { } ahead)
            {
                WaitForWork(_aheadWork!);
                (_states, _aheadStates) = (_aheadStates!, _states);
                (_work, _aheadWork) = (_aheadWork!, _work);
                _aheadGroup = null;
                _rowGroup = ahead;
                RebuildProjectedStates();
            }
            else
            {
                if (!_rowGroups.MoveNext())
                    return false;
                _rowGroup = _rowGroups.Current;
                if (_rowGroup.RowCount == 0)
                    continue;
                for (var i = 0; i < _states.Length; i++)
                {
                    var state = _states[i];
                    state.Prefetched = false;
                    state.ResetBufferState();
                    if (state.Projected)
                        state.Open(_rowGroup);
                    if (state.Materialized)
                        state.SetMissingValue();
                }
            }
            _rowGroupRowsRemaining = _rowGroup.RowCount;
            _batchLength = 0;
            _batchOffset = 0;
            _currentBatchOffset = 0;
            if (_rowGroupRowsRemaining == 0)
                continue;

            StartReadAhead();

            return true;
        }
    }

    void ResolveFileSchema()
    {
        var fileColumns = _reader.Metadata.Schema.Columns;
        for (var i = 0; i < _states.Length; i++)
        {
            var state = _states[i];
            var ordinal = ResolveColumnOrdinal(fileColumns, state.Column, state.Column.Name, state.PropertyName,
                state.Projected);
            if (ordinal < 0)
            {
                if (state.Projected)
                    state.ResetForMissingMaterialized();
                else
                    state.ResetForMissingUnprojected();
                continue;
            }

            state.Ordinal = ordinal;
            state.Materialized = false;
        }
    }

    void RebuildProjectedStates()
    {
        _projectedStateCount = 0;
        _batchAdvance = true;
        for (var i = 0; i < _states.Length; i++)
        {
            var state = _states[i];
            // A materialized missing column exposes one row-independent default
            // value and therefore must never participate in advancement.
            if (state.Projected)
            {
                _projectedStates[_projectedStateCount++] = state;
                _batchAdvance &= state.SupportsBatchAdvance;
            }
        }
        _batchAdvance &= _projectedStateCount != 0;
    }

    void PrepareBatch()
    {
        var consumedRows = _batchLength;
        var availableRows = int.MaxValue;
        if (_workers is null)
        {
            for (var i = 0; i < _projectedStateCount; i++)
                availableRows = Math.Min(availableRows, _projectedStates[i].PrepareBatch(consumedRows));
        }
        else
        {
            for (var i = 0; i < _projectedStateCount; i++)
            {
                var state = _projectedStates[i];
                if (state.CurrentIndex >= 0)
                    state.CurrentIndex = checked(state.CurrentIndex + consumedRows);
                if (state.CurrentIndex < 0 || state.CurrentIndex == state.BufferedValueCount)
                    QueueBuffer(state);
                else if ((uint)state.CurrentIndex > (uint)state.BufferedValueCount)
                    throw new CorruptParquetException($"Column '{state.PropertyName}' advanced beyond its current value buffer.");
            }
            WaitForWork(_work);
            for (var i = 0; i < _projectedStateCount; i++)
            {
                var state = _projectedStates[i];
                availableRows = Math.Min(availableRows, state.BufferedValueCount - state.CurrentIndex);
            }
        }

        if (availableRows <= 0)
            throw new CorruptParquetException("A projected row API column produced an empty value batch.");
        // The minimum available count bounds every access until the next refill.
        for (var i = 0; i < _states.Length; i++)
            _valueBatches[i] = _states[i].GetValueBatch();
        _batchLength = checked((int)Math.Min((ulong)availableRows, _rowGroupRowsRemaining));
        _batchOffset = 0;
    }

    int GetCurrentIndex(RowApiColumnReadState state)
        => state.Materialized || !_batchAdvance
            ? state.CurrentIndex
            : checked(state.CurrentIndex + _currentBatchOffset);

    int ResolveColumnOrdinal(ImmutableArray<Column> fileColumns, Column expected, string columnName, string propertyName,
        bool projected)
    {
        if (!projected)
            return -1;

        for (var i = 0; i < fileColumns.Length; i++)
        {
            var actual = fileColumns[i];
            if (actual.Name != expected.Name)
                continue;

            ValidatePhysicalType(actual, expected, columnName);
            ValidateLogicalType(actual, expected, columnName);
            ValidateRepetition(actual, expected, columnName);
            ValidateMaterializedType(actual, expected, columnName, propertyName);
            return i;
        }

        if (_schemaEvolution?.MissingColumns == MissingColumnEvolutionBehavior.MaterializeDefault)
            return -1;

        throw new InvalidOperationException($"Column '{columnName}' was not found in the file schema.");
    }

    void ValidatePhysicalType(Column actual, Column expected, string columnName)
    {
        if (actual.PhysicalType == expected.PhysicalType)
            return;

        if (_schemaEvolution?.PhysicalTypes == SchemaTypeEvolutionBehavior.AllowCompatible)
            throw new InvalidOperationException(
                $"Column '{columnName}' changed physical type from {expected.PhysicalType} to {actual.PhysicalType}, and no compatible materialization is available.");
        throw new InvalidOperationException(
            $"Column '{columnName}' has physical type {actual.PhysicalType}, expected {expected.PhysicalType}.");
    }

    void ValidateLogicalType(Column actual, Column expected, string columnName)
    {
        if (EqualityComparer<LogicalType?>.Default.Equals(actual.LogicalType, expected.LogicalType))
            return;
        if (_schemaEvolution?.LogicalTypes == SchemaTypeEvolutionBehavior.AllowCompatible && expected.LogicalType is null &&
            actual.LogicalType is LogicalType.Int integer && integer.IsSigned)
            if ((expected.PhysicalType == ParquetPhysicalType.Int32 && integer.BitWidth == 32) ||
                (expected.PhysicalType == ParquetPhysicalType.Int64 && integer.BitWidth == 64))
                return;

        throw new InvalidOperationException($"Column '{columnName}' has a different logical type than the row API schema.");
    }

    void ValidateRepetition(Column actual, Column expected, string columnName)
    {
        var actualRepetition = NormalizeRepetition(actual.Options.Repetition);
        var expectedRepetition = NormalizeRepetition(expected.Options.Repetition);
        if (actualRepetition == expectedRepetition)
            return;
        if (actualRepetition == ParquetRepetition.Required && expectedRepetition == ParquetRepetition.Optional &&
            _schemaEvolution?.Repetition >= RepetitionEvolutionBehavior.AllowRequiredToOptional)
            return;
        if (actualRepetition == ParquetRepetition.Optional && expectedRepetition == ParquetRepetition.Required &&
            _schemaEvolution?.Repetition == RepetitionEvolutionBehavior.AllowRequiredToOptionalAndOptionalToRequired)
            throw new InvalidOperationException(
                $"Column '{columnName}' became optional, but row API non-null materialization is not safe.");
        throw new InvalidOperationException(
            $"Column '{columnName}' has repetition {actualRepetition}, expected {expectedRepetition}.");
    }

    void ValidateMaterializedType(Column actual, Column expected, string columnName, string propertyName)
    {
        if (actual.PhysicalType == expected.PhysicalType)
            return;
        if (_schemaEvolution?.MaterializedTypes == SchemaTypeEvolutionBehavior.AllowCompatible)
            throw new InvalidOperationException($"Column '{columnName}' cannot be materialized into row API property '{propertyName}'.");
    }

    static ParquetRepetition NormalizeRepetition(ParquetRepetition repetition)
        => repetition == ParquetRepetition.Unspecified ? ParquetRepetition.Required : repetition;

    void EnsureStarted()
    {
        if (_started)
            return;

        if (_execution.WorkerCount > 1 && _projectedStateCount != 0)
        {
            var count = Math.Min(_execution.WorkerCount, _projectedStateCount);
            if (_workers is not null && _workers.Count < count)
            {
                _workers.Dispose();
                _workers = null;
            }
            _workers ??= new RowReaderWorkers(_execution, count);
        }
        _rowGroups = _reader.RowGroups.GetEnumerator();
        _started = true;
    }

    void DisposeColumnReaders()
    {
        Array.Clear(_valueBatches);
        for (var i = 0; i < _states.Length; i++)
        {
            _states[i].Prefetched = false;
            _states[i].DisposeBuffers();
        }
    }

    static RowReaderWorkers.Work[] CreateWork(RowApiColumnReadState[] states)
        => states.Select(state => new RowReaderWorkers.Work(state)).ToArray();

    IParquetReadSource PrepareSource(IParquetReadSource source)
        => _execution.WorkerCount == 1 || source is MemoryReadSource or FileReadSource or StreamReadSource
            ? source
            : new RowReaderSynchronizedSource(source);

    void QueueBuffer(RowApiColumnReadState state)
    {
        var work = _work[state.Descriptor.Column.Ordinal];
        work.PrefetchGroup = null;
        _workers!.Enqueue(work);
    }

    static void WaitForWork(RowReaderWorkers.Work[] work)
    {
        // Always join every job, including after a sibling failed.
        foreach (var item in work)
            item.Done.Wait();
        foreach (var item in work)
            item.Fault?.Throw();
    }

    void StartReadAhead()
    {
        if (_workers is null || _maxReadAhead == 0)
            return;
        while (_rowGroups.MoveNext())
        {
            var group = _rowGroups.Current;
            if (group.RowCount == 0)
                continue;
            if (_aheadStates is null)
            {
                _aheadStates = _states.Select(state => state.Descriptor.CreateState()).ToArray();
                _aheadWork = CreateWork(_aheadStates);
            }
            _aheadGroup = group;
            for (var i = 0; i < _states.Length; i++)
            {
                var state = _aheadStates[i];
                state.ResetBufferState();
                state.Prefetched = false;
                state.Projected = _states[i].Projected;
                state.Materialized = _states[i].Materialized;
                state.Ordinal = _states[i].Ordinal;
                var work = _aheadWork![i];
                work.Fault = null;
                if (state.Materialized)
                    state.SetMissingValue();
                if (!state.Projected)
                    continue;
                work.PrefetchGroup = group;
                _workers.Enqueue(work);
            }
            return;
        }
    }

    void DrainReadAhead()
    {
        foreach (var work in _work)
        {
            work.Done.Wait();
            work.Fault = null;
        }
        if (_aheadWork is not null)
            foreach (var work in _aheadWork)
            {
                work.Done.Wait();
                work.Fault = null;
                work.State.Prefetched = false;
                work.State.DisposeBuffers();
            }
        _aheadGroup = null;
    }

    void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(RowReaderCore));
    }

    RowApiColumnReadState GetSchemaState(RowApiColumnDescriptor column, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(column, parameterName);
        var columnIndex = column.Column.Ordinal;
        if ((uint)columnIndex < (uint)_states.Length &&
            ReferenceEquals(_states[columnIndex].Descriptor, column))
            return _states[columnIndex];

        throw new ArgumentException("The column does not belong to this row API schema.", parameterName);
    }

    RowApiColumnReadState GetSelectedState(RowApiColumnDescriptor column)
    {
        var state = GetSchemaState(column, nameof(column));
        if (!state.Projected && !state.Materialized)
            throw new InvalidOperationException($"Column '{state.PropertyName}' was not selected.");
        return state;
    }

    RowApiColumnReadState<T> GetState<T>(RowApiColumnDescriptor<T> column)
    {
        var state = GetSelectedState(column);
        if (state is RowApiColumnReadState<T> typedState)
            return typedState;

        throw new InvalidOperationException(
            $"Row API column '{state.PropertyName}' cannot be read as {typeof(T)}.");
    }

    RowApiBinaryColumnReadState GetBinaryState<T>(RowApiColumnDescriptor<T> column)
    {
        var state = GetSelectedState(column);
        if (state is RowApiBinaryColumnReadState binaryState)
            return binaryState;

        throw new InvalidOperationException(
            $"Row API column '{state.PropertyName}' is not a variable-length byte column.");
    }

    RowApiNestedColumnReadState<TShape, TElement> GetNestedState<TShape, TElement>(
        RowApiNestedColumnDescriptor<TShape, TElement> column)
    {
        var state = GetSelectedState(column);
        if (state is RowApiNestedColumnReadState<TShape, TElement> nestedState)
            return nestedState;

        throw new InvalidOperationException(
            $"Row API column '{state.PropertyName}' cannot be read as nested shape {typeof(TShape)}.");
    }
}
