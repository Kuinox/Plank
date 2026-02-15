using Plank.Schema;

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
        {
            var column = serialized.GetPreparedColumn(_writer);
            ThrowColumnOrderMismatch(_writer.GetColumnByOrdinal(expectedOrdinal), expectedOrdinal, column, ordinal);
        }

        _writer.WriteSerializedColumnToOpenRowGroup(serialized);
        _nextColumnOrdinal++;
        if (_nextColumnOrdinal == _writer.ColumnCount)
            _writer.CompleteOpenRowGroup();
    }

    static void ThrowColumnOrderMismatch(Column expectedColumn, int expectedOrdinal, Column actualColumn, int actualOrdinal)
        => throw new InvalidOperationException(
            $"Column order mismatch. Expected '{expectedColumn.Name}' at ordinal {expectedOrdinal}, but received '{actualColumn.Name}' at ordinal {actualOrdinal}.");
}
