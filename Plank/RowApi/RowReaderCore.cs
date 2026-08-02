using System.Collections.Immutable;
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
    readonly RowApiColumnReadState[] _states;
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

        _schemaEvolution = schemaEvolution;
        _streamSource = source as StreamReadSource;
        _states = CreateStates(schema, columns);
        _projectedStates = new RowApiColumnReadState[_states.Length];
        _reader = new ParquetReader(CreateLooseReaderOptions(options));
        _reader.Reset(source);
        _rowGroup = default;
        _rowGroups = default;
        _rowGroupRowsRemaining = 0;
        _started = false;
        _hasCurrent = false;
        _disposed = false;
        ApplyProjection(projection);
        ResolveFileSchema();
        RebuildProjectedStates();
    }

    /// <summary>Advances the generated reader to the next row.</summary>
    /// <returns><see langword="true"/> when a row is available; otherwise, <see langword="false"/>.</returns>
    public bool MoveNext()
    {
        ThrowIfDisposed();
        EnsureStarted();
        _hasCurrent = ReadNextRow();
        return _hasCurrent;
    }

    /// <summary>Resets the generated row reader to a stream and projection.</summary>
    /// <param name="stream">The new source stream.</param>
    /// <param name="projection">The selected columns, or <see langword="null"/> for all columns.</param>
    /// <param name="schemaEvolution">An optional replacement schema-evolution policy.</param>
    public void Reset(Stream stream, RowApiColumnDescriptor[]? projection,
        ParquetSchemaEvolutionOptions? schemaEvolution = null)
    {
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

        ApplyProjection(projection);
        DisposeColumnReaders();
        _reader.Reset(source);
        _rowGroup = default;
        _rowGroups = default;
        _rowGroupRowsRemaining = 0;
        _started = false;
        _hasCurrent = false;
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
        return ref values[state.CurrentIndex];
    }

    /// <summary>Gets the current zero-copy value for a variable-length byte column.</summary>
    /// <typeparam name="T">The column's generated CLR binary type.</typeparam>
    /// <param name="column">The generated property column.</param>
    /// <returns>The current binary value and its null state.</returns>
    public RowReaderBinaryValue GetCurrentBinary<T>(RowApiColumnDescriptor<T> column)
    {
        ThrowIfNotPositioned();
        var state = GetBinaryState(column);
        return new RowReaderBinaryValue(state.CurrentValue, state.CurrentIsNull);
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

        DisposeColumnReaders();
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
            Strict = false
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

        for (var i = 0; i < _projectedStateCount; i++)
            _projectedStates[i].Advance();

        _rowGroupRowsRemaining--;
        return true;
    }

    bool OpenNextRowGroup()
    {
        while (true)
        {
            DisposeColumnReaders();
            if (!_rowGroups.MoveNext())
                return false;

            _rowGroup = _rowGroups.Current;
            _rowGroupRowsRemaining = _rowGroup.RowCount;
            if (_rowGroupRowsRemaining == 0)
                continue;

            for (var i = 0; i < _states.Length; i++)
            {
                var state = _states[i];
                state.ResetBufferState();
                if (state.Projected)
                    state.Open(_rowGroup);
                if (state.Materialized)
                    state.SetMissingValue();
            }

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
        for (var i = 0; i < _states.Length; i++)
        {
            var state = _states[i];
            if (state.Projected)
                _projectedStates[_projectedStateCount++] = state;
        }
    }

    int ResolveColumnOrdinal(ImmutableArray<Column> fileColumns, Column expected, string columnName, string propertyName,
        bool projected)
    {
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

        if (!projected)
            return -1;
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

        _rowGroups = _reader.RowGroups.GetEnumerator();
        _started = true;
    }

    void DisposeColumnReaders()
    {
        for (var i = 0; i < _states.Length; i++)
            _states[i].DisposeBuffers();
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
}
