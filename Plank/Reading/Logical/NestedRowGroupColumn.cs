using Plank.Reading.Logical.Internal;
using Plank.Schema;

namespace Plank.Reading.Logical;

/// <summary>
/// Reads a leaf as dense materialized values and repetition and definition level streams without jagged arrays.
/// </summary>
public readonly struct NestedRowGroupColumn<T>
{
    readonly RowGroup _rowGroup;
    readonly int _columnOrdinal;

    internal NestedRowGroupColumn(RowGroup rowGroup, LeafColumn column, int columnOrdinal)
    {
        ArgumentNullException.ThrowIfNull(column);

        _rowGroup = rowGroup;
        Definition = column;
        _columnOrdinal = columnOrdinal;
    }

    /// <summary>Gets the selected leaf definition.</summary>
    public LeafColumn Definition { get; }

    /// <summary>Gets this column chunk's logical metadata view.</summary>
    public ParquetColumnChunkMetadata Metadata
        => _rowGroup.GetColumnMetadata(_columnOrdinal);

    /// <summary>Gets a zero-allocation page enumerator.</summary>
    public Enumerator GetEnumerator()
    {
        if (typeof(T) == typeof(byte) &&
            Definition.PhysicalType is ParquetPhysicalType.ByteArray
                or ParquetPhysicalType.FixedLenByteArray
                or ParquetPhysicalType.Int96)
            return new(_rowGroup.EnumerateVariableLengthNestedBuffers<T>(Definition.Column, _columnOrdinal)
                .GetEnumerator());
        return new(_rowGroup.EnumerateNestedBuffers<T>(Definition.Column, _columnOrdinal).GetEnumerator());
    }

    public struct Enumerator : IDisposable
    {
        NestedColumnBufferEnumerable<T>.Enumerator _inner;
        VariableLengthNestedColumnBufferEnumerable<T>.Enumerator _variableLengthInner;
        readonly bool _isVariableLength;

        internal Enumerator(NestedColumnBufferEnumerable<T>.Enumerator inner)
        {
            _inner = inner;
            _variableLengthInner = default;
            _isVariableLength = false;
        }

        internal Enumerator(VariableLengthNestedColumnBufferEnumerable<T>.Enumerator inner)
        {
            _inner = default;
            _variableLengthInner = inner;
            _isVariableLength = true;
        }

        public NestedColumnBuffer<T> Current
            => _isVariableLength ? _variableLengthInner.Current : _inner.Current;

        public bool MoveNext()
            => _isVariableLength ? _variableLengthInner.MoveNext() : _inner.MoveNext();

        public void Dispose()
        {
            if (_isVariableLength)
                _variableLengthInner.Dispose();
            else
                _inner.Dispose();
        }
    }
}
