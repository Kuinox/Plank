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

    public RowGroupColumn<T> Column<T>(LeafColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);
        var reader = GetReader();
        reader.ValidateRowGroup(this);
        var columnOrdinal = reader.GetColumnOrdinal(column);
        ValidateFlatProjection(column);
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

    public RowGroupColumn<T> Column<T>(int columnOrdinal)
    {
        var reader = GetReader();
        reader.ValidateRowGroup(this);
        var columns = reader.Schema.LeafColumns;
        if ((uint)columnOrdinal >= (uint)columns.Length)
            throw new ArgumentOutOfRangeException(nameof(columnOrdinal), columnOrdinal,
                "Column ordinal is outside the reader schema.");
        var column = columns[columnOrdinal];
        ValidateFlatProjection(column);
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
            Metadata.RowCount, reader.PagePruner);
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
        return new NestedColumnBufferEnumerable<T>(reader.PhysicalReader, Metadata.RowGroupOrdinal,
            physicalColumnOrdinal, reader.Schema.LeafColumns[columnOrdinal], reader.Options.BufferPool,
            Metadata.RowCount, reader.PagePruner);
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
        if ((Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T)) == typeof(TimeOnly) &&
            column.PhysicalType == ParquetPhysicalType.Int32 &&
            column.LogicalType is LogicalType.Time { Unit: TimeUnit.Millis })
            return;
        if ((Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T)) == typeof(decimal))
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

    static void ValidateNestedPhysicalType<T>(Column column)
    {
        if (Nullable.GetUnderlyingType(typeof(T)) is not null)
            throw new NotSupportedException(
                $"Nested column '{column.Name}' exposes nullability through definition levels and must be read as a non-nullable value type.");
        ValidatePhysicalType<T>(column);
    }

    static void ValidateFlatProjection(LeafColumn column)
    {
        if (column.MaxRepetitionLevel == 0)
            return;
        throw new NotSupportedException(
            $"Column '{column.Path}' contains repeated values; use NestedColumn<T> to read its dense values and levels.");
    }
}
