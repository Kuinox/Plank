namespace Plank;

public readonly struct ColumnWriter
{
    readonly RowGroupWriter _group;
    readonly ParquetSchema.Column _column;

    internal ColumnWriter(RowGroupWriter group, ParquetSchema.Column column)
    {
        _group = group;
        _column = column;
    }

    public SerializedColumn Serialize<T>(ReadOnlySpan<T> values)
    {
        return _group.Serialize(_column, values, null, null);
    }

    public ColumnEncodeStage Encode(EncodingKind encoding)
    {
        return new ColumnEncodeStage(_group, _column, encoding);
    }

    public ValueTask WriteAsync<T>(ReadOnlySpan<T> values, CancellationToken cancellationToken = default)
    {
        return Serialize(values).WriteAsync(cancellationToken);
    }
}

public readonly struct ColumnEncodeStage
{
    readonly RowGroupWriter _group;
    readonly ParquetSchema.Column _column;
    readonly EncodingKind _encoding;

    internal ColumnEncodeStage(RowGroupWriter group, ParquetSchema.Column column, EncodingKind encoding)
    {
        _group = group;
        _column = column;
        _encoding = encoding;
    }

    public ColumnCompressStage Compress(CompressionKind compression)
    {
        return new ColumnCompressStage(_group, _column, _encoding, compression);
    }

    public SerializedColumn Serialize<T>(ReadOnlySpan<T> values)
    {
        return _group.Serialize(_column, values, _encoding, null);
    }

    public ValueTask WriteAsync<T>(ReadOnlySpan<T> values, CancellationToken cancellationToken = default)
    {
        return Serialize(values).WriteAsync(cancellationToken);
    }
}

public readonly struct ColumnCompressStage
{
    readonly RowGroupWriter _group;
    readonly ParquetSchema.Column _column;
    readonly EncodingKind _encoding;
    readonly CompressionKind _compression;

    internal ColumnCompressStage(RowGroupWriter group, ParquetSchema.Column column, EncodingKind encoding, CompressionKind compression)
    {
        _group = group;
        _column = column;
        _encoding = encoding;
        _compression = compression;
    }

    public SerializedColumn Serialize<T>(ReadOnlySpan<T> values)
    {
        return _group.Serialize(_column, values, _encoding, _compression);
    }

    public ValueTask WriteAsync<T>(ReadOnlySpan<T> values, CancellationToken cancellationToken = default)
    {
        return Serialize(values).WriteAsync(cancellationToken);
    }
}
