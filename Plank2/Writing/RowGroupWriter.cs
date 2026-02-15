namespace Plank2.Writing;

public sealed class RowGroupWriter
{
    readonly ParquetWriter _writer;
    int _nextColumnOrdinal;

    internal RowGroupWriter(ParquetWriter writer)
    {
        _writer = writer;
        _nextColumnOrdinal = 0;
    }

    public void Write(SerializedColumn serialized)
    {
        ArgumentNullException.ThrowIfNull(serialized);

        var ordinal = serialized.GetPreparedColumnOrdinal(_writer);
        var expectedOrdinal = _nextColumnOrdinal;
        if (expectedOrdinal >= _writer.ColumnCount)
            throw new InvalidOperationException("The current row group already contains all schema columns.");
        if (ordinal != expectedOrdinal)
            throw new InvalidOperationException(
                $"Column order mismatch. Expected ordinal {expectedOrdinal}, got {ordinal}.");

        _writer.WriteSerializedColumnToOpenRowGroup(serialized);
        _nextColumnOrdinal++;
        if (_nextColumnOrdinal == _writer.ColumnCount)
            _writer.CompleteOpenRowGroup();
    }
}
