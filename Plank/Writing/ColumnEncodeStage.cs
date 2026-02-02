namespace Plank;

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
