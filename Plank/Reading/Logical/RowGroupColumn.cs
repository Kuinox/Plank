using Plank.Schema;
using Plank.Reading.Logical.Internal;

namespace Plank.Reading.Logical;

/// <summary>Exposes a row group's column as temporary decoded buffers.</summary>
/// <remarks>Decoded buffer boundaries are not physical page boundaries and may change between versions.</remarks>
public readonly struct RowGroupColumn<T>
{
    readonly RowGroup _rowGroup;
    readonly int _columnOrdinal;

    internal RowGroupColumn(RowGroup rowGroup, LeafColumn column, int columnOrdinal)
    {
        ArgumentNullException.ThrowIfNull(column);

        _rowGroup = rowGroup;
        Definition = column;
        _columnOrdinal = columnOrdinal;
    }

    public LeafColumn Definition { get; }

    /// <summary>Gets this column chunk's logical metadata view.</summary>
    public ParquetColumnChunkMetadata Metadata
        => _rowGroup.GetColumnMetadata(_columnOrdinal);

    public Enumerator GetEnumerator()
    {
        if (typeof(T) == typeof(byte) &&
            Definition.PhysicalType is ParquetPhysicalType.ByteArray
                or ParquetPhysicalType.FixedLenByteArray
                or ParquetPhysicalType.Int96)
            return new(_rowGroup.EnumerateVariableLengthBuffers<T>(Definition, _columnOrdinal)
                .GetEnumerator(), _rowGroup);
        return new(_rowGroup.EnumerateBuffers<T>(Definition, _columnOrdinal).GetEnumerator(), _rowGroup);
    }

    public struct Enumerator : IDisposable
    {
        ColumnBufferEnumerable<T>.Enumerator _inner;
        VariableLengthColumnBufferEnumerable<T>.Enumerator _variableLengthInner;
        readonly bool _isVariableLength;
        readonly ParquetReader _reader;
        readonly int _footerVersion;

        internal Enumerator(ColumnBufferEnumerable<T>.Enumerator inner, RowGroup rowGroup)
        {
            _reader = rowGroup.GetReader();
            _footerVersion = rowGroup.Metadata.FooterVersion;
            _inner = inner;
            _variableLengthInner = default;
            _isVariableLength = false;
        }

        internal Enumerator(VariableLengthColumnBufferEnumerable<T>.Enumerator inner, RowGroup rowGroup)
        {
            _reader = rowGroup.GetReader();
            _footerVersion = rowGroup.Metadata.FooterVersion;
            _inner = default;
            _variableLengthInner = inner;
            _isVariableLength = true;
        }

        public ColumnBuffer<T> Current
            => _isVariableLength ? _variableLengthInner.Current : _inner.Current;

        public bool MoveNext()
        {
            _reader.ValidateFooterVersion(_footerVersion);
            return _isVariableLength ? _variableLengthInner.MoveNext() : _inner.MoveNext();
        }

        public void Dispose()
        {
            if (_isVariableLength)
                _variableLengthInner.Dispose();
            else
                _inner.Dispose();
        }
    }
}
