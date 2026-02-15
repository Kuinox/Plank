namespace Plank2.Writing;

public sealed class RowGroupWriter
{
    readonly ParquetWriter _writer;
    int _nextColumnOrdinal;
    int _rowCount;
    bool _metadataStarted;

    internal RowGroupWriter(ParquetWriter writer)
    {
        _writer = writer;
        _nextColumnOrdinal = 0;
        _rowCount = -1;
        _metadataStarted = false;
    }

    public void Write(SerializedColumn serialized)
    {
        ArgumentNullException.ThrowIfNull(serialized);
        _writer.EnsureRowGroupOpen();
        serialized.EnsureOwnedBy(_writer);

        var ordinal = serialized.ColumnOrdinal;
        var expectedOrdinal = _nextColumnOrdinal;
        if (ordinal != expectedOrdinal)
            throw new InvalidOperationException(
                $"Column order mismatch. Expected ordinal {expectedOrdinal}, got {ordinal}.");

        if (!_metadataStarted)
        {
            _rowCount = serialized._rowCount;
            _writer.BeginOpenRowGroupMetadata(_rowCount);
            _metadataStarted = true;
        }
        else if (serialized._rowCount != _rowCount)
            throw new InvalidOperationException(
                $"Row count mismatch for row group. Expected {_rowCount}, got {serialized._rowCount}.");

        var pages = serialized.Pages;
        long totalUncompressedSize = 0;
        long totalCompressedSize = 0;
        for (var i = 0; i < pages.Count; i++)
        {
            ref var page = ref pages[i];
            var headerSize = page.Header.WrittenSpan.Length;
            var contentSize = page.Content.WrittenSpan.Length;
            _writer.WriteBuffer(page.Header);
            _writer.WriteBuffer(page.Content);
            var pageSize = checked((long)headerSize + contentSize);
            totalUncompressedSize += pageSize;
            totalCompressedSize += pageSize;
        }

        _writer.AppendOpenRowGroupColumnMetadata(
            serialized._rowCount,
            serialized._rowCount,
            totalUncompressedSize,
            totalCompressedSize);

        serialized.Consume(_writer);
        _nextColumnOrdinal++;
        if (_nextColumnOrdinal == _writer.ColumnCount)
            _writer.CompleteOpenRowGroup();
    }
}
