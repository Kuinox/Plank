namespace Plank;

public readonly struct ColumnWriter<T>
{
    private readonly RowGroupWriter _group;
    private readonly ParquetSchema.Column<T> _column;

    internal ColumnWriter(RowGroupWriter group, ParquetSchema.Column<T> column)
    {
        _group = group;
        _column = column;
    }

    public SerializedColumn Serialize(ReadOnlySpan<T> values)
    {
        return _group.Serialize(_column, values, null, null);
    }

    public ColumnEncodeStage<T> Encode(EncodingKind encoding)
    {
        return new ColumnEncodeStage<T>(_group, _column, encoding);
    }

    public ValueTask WriteAsync(ReadOnlySpan<T> values, CancellationToken cancellationToken = default)
    {
        return Serialize(values).WriteAsync(cancellationToken);
    }
}

public readonly struct ColumnEncodeStage<T>
{
    private readonly RowGroupWriter _group;
    private readonly ParquetSchema.Column<T> _column;
    private readonly EncodingKind _encoding;

    internal ColumnEncodeStage(RowGroupWriter group, ParquetSchema.Column<T> column, EncodingKind encoding)
    {
        _group = group;
        _column = column;
        _encoding = encoding;
    }

    public ColumnCompressStage<T> Compress(CompressionKind compression)
    {
        return new ColumnCompressStage<T>(_group, _column, _encoding, compression);
    }

    public SerializedColumn Serialize(ReadOnlySpan<T> values)
    {
        return _group.Serialize(_column, values, _encoding, null);
    }

    public ValueTask WriteAsync(ReadOnlySpan<T> values, CancellationToken cancellationToken = default)
    {
        return Serialize(values).WriteAsync(cancellationToken);
    }
}

public readonly struct ColumnCompressStage<T>
{
    private readonly RowGroupWriter _group;
    private readonly ParquetSchema.Column<T> _column;
    private readonly EncodingKind _encoding;
    private readonly CompressionKind _compression;

    internal ColumnCompressStage(RowGroupWriter group, ParquetSchema.Column<T> column, EncodingKind encoding, CompressionKind compression)
    {
        _group = group;
        _column = column;
        _encoding = encoding;
        _compression = compression;
    }

    public SerializedColumn Serialize(ReadOnlySpan<T> values)
    {
        return _group.Serialize(_column, values, _encoding, _compression);
    }

    public ValueTask WriteAsync(ReadOnlySpan<T> values, CancellationToken cancellationToken = default)
    {
        return Serialize(values).WriteAsync(cancellationToken);
    }
}
