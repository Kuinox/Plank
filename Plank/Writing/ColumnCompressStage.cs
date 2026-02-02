namespace Plank;

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
