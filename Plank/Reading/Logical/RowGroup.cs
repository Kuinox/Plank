using Plank.Reading.Logical.Internal;
using Plank.Reading.Internal;
using Plank.Schema;

namespace Plank.Reading.Logical;

public readonly struct RowGroup
{
    internal readonly ParquetReader? Reader;
    internal readonly InternalRowGroupMetadata Metadata;

    internal RowGroup(ParquetReader reader, InternalRowGroupMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (metadata.RowGroupOrdinal < 0)
            throw new ArgumentOutOfRangeException(nameof(metadata), metadata.RowGroupOrdinal,
                "Row group index must be non-negative.");

        Reader = reader;
        Metadata = metadata;
    }

    public int Index
        => Metadata.RowGroupOrdinal;

    public ulong MetadataOffset
        => Metadata.MetadataOffset;

    public ulong ColumnChunkOffset
        => Metadata.ColumnChunkOffset;

    public ulong RowCount
        => Metadata.RowCount;

    internal InternalColumnChunkMetadata[] PreviousColumns
        => Metadata.Columns ?? [];

    /// <summary>Selects a flat leaf as a typed value stream.</summary>
    /// <remarks>Unannotated INT96 timestamps carry no time zone and are read as UTC. Dates written
    /// before 1582 using a hybrid Julian calendar are not rebased.</remarks>
    public RowGroupColumn<T> Column<T>(LeafColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);
        var reader = GetReader();
        reader.ValidateRowGroup(this);
        var columnOrdinal = reader.GetColumnOrdinal(column);
        ValidateFlatProjection(column, columnOrdinal);
        ValidatePhysicalType<T>(column.Column);
        return new RowGroupColumn<T>(this, column, columnOrdinal);
    }

    /// <summary>
    /// Selects a leaf as a dense value stream accompanied by its repetition and definition levels.
    /// </summary>
    public NestedRowGroupColumn<T> NestedColumn<T>(LeafColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);
        var reader = GetReader();
        reader.ValidateRowGroup(this);
        var columnOrdinal = reader.GetColumnOrdinal(column);
        ValidateNestedPhysicalType<T>(column.Column);
        return new NestedRowGroupColumn<T>(this, column, columnOrdinal);
    }

    /// <summary>Selects a flat leaf by ordinal as a typed value stream.</summary>
    /// <remarks>Unannotated INT96 timestamps carry no time zone and are read as UTC. Dates written
    /// before 1582 using a hybrid Julian calendar are not rebased.</remarks>
    public RowGroupColumn<T> Column<T>(int columnOrdinal)
    {
        var reader = GetReader();
        reader.ValidateRowGroup(this);
        var columns = reader.Schema.LeafColumns;
        if ((uint)columnOrdinal >= (uint)columns.Length)
            throw new ArgumentOutOfRangeException(nameof(columnOrdinal), columnOrdinal,
                "Column ordinal is outside the reader schema.");
        var column = columns[columnOrdinal];
        ValidateFlatProjection(column, columnOrdinal);
        ValidatePhysicalType<T>(column.Column);
        return new RowGroupColumn<T>(this, column, columnOrdinal);
    }

    /// <summary>
    /// Selects a leaf as a dense value stream accompanied by its repetition and definition levels.
    /// </summary>
    public NestedRowGroupColumn<T> NestedColumn<T>(int columnOrdinal)
    {
        var reader = GetReader();
        reader.ValidateRowGroup(this);
        var columns = reader.Schema.LeafColumns;
        if ((uint)columnOrdinal >= (uint)columns.Length)
            throw new ArgumentOutOfRangeException(nameof(columnOrdinal), columnOrdinal,
                "Column ordinal is outside the reader schema.");
        var column = columns[columnOrdinal];
        ValidateNestedPhysicalType<T>(column.Column);
        return new NestedRowGroupColumn<T>(this, column, columnOrdinal);
    }

    public ParquetColumnChunkMetadata GetColumnMetadata(LeafColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);
        var reader = GetReader();
        reader.ValidateRowGroup(this);
        return new ParquetColumnChunkMetadata(this, reader.GetColumnOrdinal(column));
    }

    public ParquetColumnChunkMetadata GetColumnMetadata(int columnOrdinal)
    {
        var reader = GetReader();
        reader.ValidateRowGroup(this);
        if ((uint)columnOrdinal >= (uint)reader.Schema.LeafColumns.Length)
            throw new ArgumentOutOfRangeException(nameof(columnOrdinal), columnOrdinal,
                "Column ordinal is outside the reader schema.");
        return new ParquetColumnChunkMetadata(this, columnOrdinal);
    }

    internal ColumnBufferEnumerable<T> EnumerateBuffers<T>(LeafColumn definition, int columnOrdinal)
    {
        var reader = GetReader();
        reader.ValidateRowGroup(this);
        if ((uint)columnOrdinal >= (uint)Metadata.Columns.Length)
            throw new CorruptParquetException(
                $"Column '{definition.Column.Name}' (ordinal {columnOrdinal}) is not present in this row group ({Metadata.Columns.Length} columns)."
            );

        var columnChunk = Metadata.Columns[columnOrdinal];
        var physicalColumnOrdinal = columnChunk.PhysicalColumnOrdinal >= 0
            ? columnChunk.PhysicalColumnOrdinal
            : columnOrdinal;
        return new ColumnBufferEnumerable<T>(reader.PhysicalReader, Metadata.RowGroupOrdinal,
            physicalColumnOrdinal, definition, reader.Options.BufferPool,
            Metadata.RowCount, reader.PagePruner, reader.Options.BorrowRequiredPlainValues,
            reader.Options.BorrowBinaryValues,
            reader.Metadata.Schema.LeafColumns[physicalColumnOrdinal].Options.Repetition);
    }

    internal VariableLengthColumnBufferEnumerable<T> EnumerateVariableLengthBuffers<T>(LeafColumn definition,
        int columnOrdinal)
        => new(EnumerateBuffers<BinaryValueDescriptor>(definition, columnOrdinal));

    internal NestedColumnBufferEnumerable<T> EnumerateNestedBuffers<T>(Column column, int columnOrdinal)
    {
        var reader = GetReader();
        reader.ValidateRowGroup(this);
        if ((uint)columnOrdinal >= (uint)Metadata.Columns.Length)
            throw new CorruptParquetException(
                $"Column '{column.Name}' (ordinal {columnOrdinal}) is not present in this row group ({Metadata.Columns.Length} columns)."
            );

        var columnChunk = Metadata.Columns[columnOrdinal];
        var physicalColumnOrdinal = columnChunk.PhysicalColumnOrdinal >= 0
            ? columnChunk.PhysicalColumnOrdinal
            : columnOrdinal;
        var definition = reader.Schema.LeafColumns[columnOrdinal];
        var physical = reader.Metadata.Schema.LeafColumns[physicalColumnOrdinal];
        // Materialize with the requested column (including any converter), but decode wire levels
        // using the file schema. A required file leaf requested as optional has no definition stream.
        if (definition.MaxRepetitionLevel != physical.MaxRepetitionLevel ||
            definition.MaxDefinitionLevel != physical.MaxDefinitionLevel)
            definition = new LeafColumn(definition.Column, definition.Ordinal, physical.ProjectionInfo);
        return new NestedColumnBufferEnumerable<T>(reader.PhysicalReader, Metadata.RowGroupOrdinal,
            physicalColumnOrdinal, definition, reader.Options.BufferPool, Metadata.RowCount, reader.PagePruner);
    }

    internal VariableLengthNestedColumnBufferEnumerable<T> EnumerateVariableLengthNestedBuffers<T>(Column column,
        int columnOrdinal)
        => new(EnumerateNestedBuffers<BinaryValueDescriptor>(column, columnOrdinal));

    internal ParquetReader GetReader()
        => Reader ?? throw new InvalidOperationException("The row group is not initialized.");

    internal LeafColumn GetColumnDefinition(int columnOrdinal)
    {
        var reader = GetReader();
        reader.ValidateRowGroup(this);
        var columns = reader.Schema.LeafColumns;
        if ((uint)columnOrdinal >= (uint)columns.Length)
            throw new ArgumentOutOfRangeException(nameof(columnOrdinal));
        return columns[columnOrdinal];
    }

    internal InternalColumnChunkMetadata GetColumnChunkMetadata(int columnOrdinal)
    {
        var reader = GetReader();
        reader.ValidateRowGroup(this);
        if ((uint)columnOrdinal >= (uint)Metadata.Columns.Length)
            throw new CorruptParquetException(
                $"Column ordinal {columnOrdinal} is not present in row group {Index}.");
        return Metadata.Columns[columnOrdinal];
    }

    internal ParquetStatistics CreateStatistics(LeafColumn definition, EncodedStatistics statistics)
    {
        var reader = GetReader();
        reader.ValidateRowGroup(this);
        return new ParquetStatistics(reader.PhysicalReader.Metadata.FooterBytes, statistics, definition);
    }

    internal static void ValidatePhysicalType<T>(Column column)
    {
        if (column.Converter is { } converter)
        {
            if (converter.SupportsValueType(typeof(T)))
            {
                if (column.Options.Repetition == ParquetRepetition.Optional &&
                    !converter.IsNullableValueType(typeof(T)))
                    throw new InvalidOperationException(
                        $"Optional column '{column.Name}' must be read as '{converter.ValueType}?'.");
                ValidateTemporalLogicalType(column, converter.PhysicalType);
                return;
            }
            throw new InvalidOperationException(
                $"Column '{column.Name}' uses a converter for '{converter.ValueType}' and cannot be read as '{typeof(T)}'.");
        }

        if (typeof(T) == typeof(byte[]) ||
            typeof(T) == typeof(ReadOnlyMemory<byte>) ||
            typeof(T) == typeof(ReadOnlyMemory<byte>?) ||
            typeof(T) == typeof(string) ||
            typeof(T) == typeof(Guid) ||
            typeof(T) == typeof(Guid?))
            throw new NotSupportedException(
                $"Column '{column.Name}' contains variable-length bytes and must be read as '{typeof(byte)}'.");
        if (typeof(T) == typeof(byte) &&
            column.PhysicalType is ParquetPhysicalType.ByteArray
                or ParquetPhysicalType.FixedLenByteArray
                or ParquetPhysicalType.Int96)
            return;
        if (typeof(T) == typeof(DateOnly) || typeof(T) == typeof(DateOnly?) ||
            typeof(T) == typeof(TimeOnly) || typeof(T) == typeof(TimeOnly?) ||
            typeof(T) == typeof(DateTime) || typeof(T) == typeof(DateTime?) ||
            typeof(T) == typeof(DateTimeOffset) || typeof(T) == typeof(DateTimeOffset?))
            ValidateTemporalLogicalType(column, typeof(T));
        if (column.PhysicalType == ParquetPhysicalType.Int96 && column.LogicalType is null &&
            (typeof(T) == typeof(DateTime) || typeof(T) == typeof(DateTime?) ||
             typeof(T) == typeof(DateTimeOffset) || typeof(T) == typeof(DateTimeOffset?)))
            return;
        if ((typeof(T) == typeof(TimeOnly) || typeof(T) == typeof(TimeOnly?)) &&
            column.PhysicalType == ParquetPhysicalType.Int32 &&
            column.LogicalType is LogicalType.Time { Unit: TimeUnit.Millis })
            return;
        if (typeof(T) == typeof(decimal) || typeof(T) == typeof(decimal?))
        {
            _ = ParquetDecimalConverter.RequireLogicalType(column);
            return;
        }
        var resolution = ParquetTypeMap.ResolvePhysicalType<T>();
        if (!resolution.IsSuccess)
            throw new NotSupportedException(resolution.ErrorMessage);
        if (resolution.PhysicalType == column.PhysicalType)
            return;
        if (resolution.PhysicalType == ParquetPhysicalType.ByteArray && typeof(T) == typeof(byte[]) &&
            column.PhysicalType is ParquetPhysicalType.FixedLenByteArray or ParquetPhysicalType.Int96)
            return;
        throw new InvalidOperationException(
            $"Column '{column.Name}' has physical type {column.PhysicalType} and cannot be read as '{typeof(T)}'.");
    }

    static void ValidateTemporalLogicalType(Column column, Type type)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;
        string required;
        if (target == typeof(DateOnly))
        {
            if (column.PhysicalType != ParquetPhysicalType.Int32 || column.LogicalType is LogicalType.Date)
                return;
            required = nameof(LogicalType.Date);
        }
        else if (target == typeof(TimeOnly))
        {
            if (column.PhysicalType is not (ParquetPhysicalType.Int32 or ParquetPhysicalType.Int64) ||
                column.LogicalType is LogicalType.Time)
                return;
            required = nameof(LogicalType.Time);
        }
        else if (target == typeof(DateTime) || target == typeof(DateTimeOffset))
        {
            if (column.PhysicalType != ParquetPhysicalType.Int64 || column.LogicalType is LogicalType.Timestamp)
                return;
            required = nameof(LogicalType.Timestamp);
        }
        else
            return;
        throw new InvalidOperationException(
            $"Column '{column.Name}' must declare logical type '{required}' to be read as '{type}'.");
    }

    static void ValidateNestedPhysicalType<T>(Column column)
    {
        if (Nullable.GetUnderlyingType(typeof(T)) is not null)
            throw new NotSupportedException(
                $"Nested column '{column.Name}' exposes nullability through definition levels and must be read as a non-nullable value type.");
        ValidatePhysicalType<T>(column);
    }

    void ValidateFlatProjection(LeafColumn column, int columnOrdinal)
    {
        var chunk = GetColumnChunkMetadata(columnOrdinal);
        var physicalOrdinal = chunk.PhysicalColumnOrdinal >= 0 ? chunk.PhysicalColumnOrdinal : columnOrdinal;
        var physicalColumn = GetReader().Metadata.Schema.LeafColumns[physicalOrdinal];
        if (column.MaxRepetitionLevel != 0 || physicalColumn.MaxRepetitionLevel != 0)
            throw new NotSupportedException(
                $"Column '{column.Path}' contains repeated values; use NestedColumn<T> to read its dense values and levels.");
        // Flat decoders represent definition levels as a one-bit presence mask. Both the requested
        // schema and the file must fit that representation; requested nullability cannot change
        // the level bit width encoded in the file.
        if (column.MaxDefinitionLevel > 1 || physicalColumn.MaxDefinitionLevel > 1)
            throw new NotSupportedException(
                $"Column '{column.Path}' contains multiple optional levels; use NestedColumn<T> to read its dense values and levels.");
    }
}
