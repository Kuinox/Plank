using System.Collections.Immutable;
using Plank.Reading;
using Plank.Reading.Logical;
using Plank.Schema;

namespace Plank.RowApi;

public sealed class RowReaderCore : IDisposable
{
    readonly RowApiColumnReadState[] _states;
    readonly ParquetReader _reader;
    RowGroup _rowGroup;
    ParquetSchemaEvolutionOptions? _schemaEvolution;
    StreamReadSource? _streamSource;
    RowGroupCollection.Enumerator _rowGroups;
    ulong _rowGroupRowsRemaining;
    bool _started;
    bool _hasCurrent;
    bool _disposed;

    public RowReaderCore(Stream stream, ParquetSchema schema, RowApiColumnDescriptor[] columns,
        ulong projection, RowReaderOptions options, ParquetSchemaEvolutionOptions? schemaEvolution)
        : this(new StreamReadSource(stream), schema, columns, projection, options, schemaEvolution)
    {
    }

    public RowReaderCore(IParquetReadSource source, ParquetSchema schema, RowApiColumnDescriptor[] columns,
        ulong projection, RowReaderOptions options, ParquetSchemaEvolutionOptions? schemaEvolution)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (columns.Length != schema.Columns.Length)
            throw new ArgumentException("Row API column descriptors must match the row API schema column count.",
                nameof(columns));

        _schemaEvolution = schemaEvolution;
        _streamSource = source as StreamReadSource;
        _states = CreateStates(columns);
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
    }

    public bool MoveNext()
    {
        ThrowIfDisposed();
        EnsureStarted();
        _hasCurrent = ReadNextRow();
        return _hasCurrent;
    }

    public void Reset(Stream stream, ulong projection, ParquetSchemaEvolutionOptions? schemaEvolution = null)
    {
        if (_streamSource is null)
            _streamSource = new StreamReadSource(stream);
        else
            _streamSource.Reset(stream);
        Reset(_streamSource, projection, schemaEvolution);
    }

    public void Reset(IParquetReadSource source, ulong projection, ParquetSchemaEvolutionOptions? schemaEvolution = null)
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
    }

    public Span<T> GetCurrentSpan<T>(int columnIndex)
        => GetState<T>(columnIndex).CurrentSpan;

    public int GetCurrentIndex(int columnIndex)
    {
        if ((uint)columnIndex >= (uint)_states.Length)
            throw new ArgumentOutOfRangeException(nameof(columnIndex), columnIndex,
                "Column index is outside the row API schema.");
        return _states[columnIndex].CurrentIndex;
    }

    public void ThrowIfNotPositioned()
    {
        ThrowIfDisposed();
        if (!_hasCurrent)
            throw new InvalidOperationException("The row reader is not positioned on a row.");
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        DisposeColumnReaders();
        _reader.Dispose();
        _disposed = true;
    }

    static RowApiColumnReadState[] CreateStates(RowApiColumnDescriptor[] columns)
    {
        var states = new RowApiColumnReadState[columns.Length];
        for (var i = 0; i < columns.Length; i++)
        {
            var column = columns[i] ?? throw new ArgumentException("Row API column descriptors cannot contain null values.",
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

    void ApplyProjection(ulong projection)
    {
        for (var i = 0; i < _states.Length; i++)
            _states[i].ResetForProjection(projection);
    }

    bool ReadNextRow()
    {
        while (_rowGroupRowsRemaining == 0)
            if (!OpenNextRowGroup())
            {
                _hasCurrent = false;
                return false;
            }

        for (var i = 0; i < _states.Length; i++)
            _states[i].Advance();

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

    RowApiColumnReadState<T> GetState<T>(int columnIndex)
    {
        if ((uint)columnIndex >= (uint)_states.Length)
            throw new ArgumentOutOfRangeException(nameof(columnIndex), columnIndex,
                "Column index is outside the row API schema.");

        if (_states[columnIndex] is RowApiColumnReadState<T> state)
            return state;

        throw new InvalidOperationException(
            $"Row API column '{_states[columnIndex].PropertyName}' cannot be read as {typeof(T)}.");
    }
}
