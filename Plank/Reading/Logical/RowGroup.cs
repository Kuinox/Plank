using Plank.Reading.Logical.Internal;
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
        ValidatePhysicalType<T>(column.Column);
        return new RowGroupColumn<T>(this, column, columnOrdinal);
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
        ValidatePhysicalType<T>(column.Column);
        return new RowGroupColumn<T>(this, column, columnOrdinal);
    }

    internal ColumnBufferEnumerable<T> EnumerateBuffers<T>(Column column, int columnOrdinal)
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
        return new ColumnBufferEnumerable<T>(reader.PhysicalReader, Metadata.RowGroupOrdinal,
            physicalColumnOrdinal, column, columnChunk, reader.Options.BufferPool, Metadata.RowCount);
    }

    ParquetReader GetReader()
        => Reader ?? throw new InvalidOperationException("The row group is not initialized.");

    static void ValidatePhysicalType<T>(Column column)
    {
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
}
