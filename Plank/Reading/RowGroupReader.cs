using Plank.Schema;
using SchemaColumn = Plank.Schema.Column;

namespace Plank.Reading;

public sealed class RowGroupReader : IDisposable
{
    readonly ParquetReader _reader;
    readonly ParquetSharp.RowGroupReader _rowGroup;

    internal RowGroupReader(ParquetReader reader, ParquetSharp.RowGroupReader rowGroup)
    {
        _reader = reader;
        _rowGroup = rowGroup;
    }

    public int RowCount
        => checked((int)_rowGroup.MetaData.NumRows);

    public void Read(SchemaColumn column, Span<int> destination)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (column.PhysicalType != ParquetPhysicalType.Int32)
            throw new InvalidOperationException($"Column '{column.Name}' must be Int32.");
        if (column.Options.Repetition is ParquetRepetition.Repeated)
            throw new NotSupportedException($"Repeated read is not implemented for column '{column.Name}' yet.");
        if (column.Options.Repetition is ParquetRepetition.Optional)
            throw new InvalidOperationException($"Column '{column.Name}' is Optional. Use ReadOptional.");

        var rowCount = RowCount;
        if (destination.Length < rowCount)
            throw new ArgumentException($"Destination must have at least {rowCount} items.", nameof(destination));

        var ordinal = _reader.ResolveOrdinal(column);
        using var columnReader = _rowGroup.Column(ordinal).LogicalReader<int>();
        var values = columnReader.ReadAll(rowCount);
        values.AsSpan().CopyTo(destination);
    }

    public void ReadOptional(SchemaColumn column, Span<int?> destination)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (column.PhysicalType != ParquetPhysicalType.Int32)
            throw new InvalidOperationException($"Column '{column.Name}' must be Int32.");
        if (column.Options.Repetition is ParquetRepetition.Repeated)
            throw new NotSupportedException($"Repeated read is not implemented for column '{column.Name}' yet.");
        if (column.Options.Repetition is not ParquetRepetition.Optional)
            throw new InvalidOperationException($"Column '{column.Name}' is not Optional.");

        var rowCount = RowCount;
        if (destination.Length < rowCount)
            throw new ArgumentException($"Destination must have at least {rowCount} items.", nameof(destination));

        var ordinal = _reader.ResolveOrdinal(column);
        using var columnReader = _rowGroup.Column(ordinal).LogicalReader<int?>();
        var values = columnReader.ReadAll(rowCount);
        values.AsSpan().CopyTo(destination);
    }

    public void Dispose()
        => _rowGroup.Dispose();
}
