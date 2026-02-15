using Plank.Schema;
using Plank2;

namespace Plank2.Writing;

public sealed class SerializedColumn
{
    readonly PageList _pages;
    readonly ParquetWriter _owner;
    int _columnOrdinal;
    int _rowCount;
    bool _isPrepared;
    bool _isWritten;

    internal SerializedColumn(ParquetWriter owner, int initialPageCapacity, IParquetLog log)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _pages = new PageList(initialPageCapacity, log);
        _owner = owner;
        _columnOrdinal = -1;
        _rowCount = 0;
        _isPrepared = false;
        _isWritten = false;
    }

    public void Serialize<T>(Column column, ReadOnlySpan<T> values)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (_isPrepared)
            throw new InvalidOperationException("SerializedColumn is already prepared and not yet consumed.");

        var columnOrdinal = _owner.GetColumnOrdinal(column);
        _pages.Clear();
        _columnOrdinal = columnOrdinal;
        _rowCount = values.Length;
        _isPrepared = true;
        _isWritten = false;

        TODO;
    }

    public bool IsWritten
        => _isWritten;

    internal PageList Pages
        => _pages;

    internal void EnsureOwnedBy(ParquetWriter owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!_isPrepared)
            throw new InvalidOperationException("SerializedColumn is not prepared.");
        if (!ReferenceEquals(_owner, owner))
            throw new InvalidOperationException("SerializedColumn is owned by a different ParquetWriter.");
    }

    internal void Consume(ParquetWriter owner)
    {
        EnsureOwnedBy(owner);
        _isPrepared = false;
        _isWritten = true;
    }

    internal int GetPreparedColumnOrdinal(ParquetWriter owner)
    {
        EnsureOwnedBy(owner);
        return _columnOrdinal;
    }
}
