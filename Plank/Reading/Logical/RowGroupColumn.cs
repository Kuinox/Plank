using Plank.Schema;
using Plank.Reading.Logical.Internal;

namespace Plank.Reading.Logical;

public readonly struct RowGroupColumn<T>
{
    readonly RowGroup _rowGroup;
    readonly int _columnOrdinal;

    internal RowGroupColumn(RowGroup rowGroup, Column column, int columnOrdinal)
    {
        ArgumentNullException.ThrowIfNull(column);

        _rowGroup = rowGroup;
        Definition = column;
        _columnOrdinal = columnOrdinal;
    }

    public Column Definition { get; }

    public Enumerator GetEnumerator()
        => new(_rowGroup.EnumerateBuffers<T>(Definition, _columnOrdinal).GetEnumerator());

    public struct Enumerator : IDisposable
    {
        ColumnBufferEnumerable<T>.Enumerator _inner;

        internal Enumerator(ColumnBufferEnumerable<T>.Enumerator inner)
            => _inner = inner;

        public ColumnBuffer<T> Current
            => _inner.Current;

        public bool MoveNext()
            => _inner.MoveNext();

        public void Dispose()
            => _inner.Dispose();
    }
}
