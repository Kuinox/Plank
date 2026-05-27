using Plank.Schema;
using Plank.Writing;

namespace Plank.Reading;

public readonly struct ColumnPageEnumerable<T>
{
    readonly IParquetReadSource _source;
    readonly Column _column;
    readonly InternalColumnChunkMetadata _columnChunk;
    readonly ColumnPageReadState<T> _state;
    readonly IParquetBufferPool _bufferPool;

    readonly ulong _rowCount;

    internal ColumnPageEnumerable(IParquetReadSource source, Column column, InternalColumnChunkMetadata columnChunk,
        ColumnPageReadState<T> state, IParquetBufferPool bufferPool, ulong rowCount)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(bufferPool);

        _source = source;
        _column = column;
        _columnChunk = columnChunk;
        _state = state;
        _bufferPool = bufferPool;
        _rowCount = rowCount;
    }

    public Enumerator GetEnumerator()
        => new(_source, _column, _columnChunk, _state, _bufferPool, _rowCount);

    public struct Enumerator : IDisposable
    {
        readonly IParquetReadSource _source;
        readonly Column _column;
        readonly InternalColumnChunkMetadata _columnChunk;
        readonly ColumnPageReadState<T> _state;
        readonly IParquetBufferPool _bufferPool;
        int _offset;
        bool _readBuffer;
        bool _missingPageEmitted;

        readonly ulong _rowCount;

        internal Enumerator(IParquetReadSource source, Column column, InternalColumnChunkMetadata columnChunk,
            ColumnPageReadState<T> state, IParquetBufferPool bufferPool, ulong rowCount)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(column);
            ArgumentNullException.ThrowIfNull(state);
            ArgumentNullException.ThrowIfNull(bufferPool);

            _source = source;
            _column = column;
            _columnChunk = columnChunk;
            _state = state;
            _bufferPool = bufferPool;
            _rowCount = rowCount;
            _offset = 0;
            _readBuffer = false;
            _missingPageEmitted = false;
            _state.Dictionary = null;
            Current = default;
        }

        public ColumnPage<T> Current { get; private set; }

        public bool MoveNext()
        {
            if (_columnChunk.IsMissing)
            {
                if (_missingPageEmitted || _rowCount == 0)
                    return false;
                if (_rowCount > int.MaxValue)
                    throw new NotSupportedException("Synthetic missing-column pages larger than Int32.MaxValue are not supported.");

                Current = new ColumnPage<T>(new T[(int)_rowCount], EncodingKind.Plain);
                _missingPageEmitted = true;
                return true;
            }

            if (!_readBuffer)
            {
                _state.BufferLength = ColumnChunkReader.ReadChunkBuffer(_source, _columnChunk, ref _state.Buffer, _bufferPool);
                _readBuffer = true;
            }
            if (!ColumnChunkReader.TryReadNextDataPage(_state.Buffer!, _state.BufferLength, ref _offset, _column, _columnChunk,
                    _rowCount, ref _state.Dictionary, ref _state.DictionaryBuffer, ref _state.ValuesBuffer, _bufferPool,
                    ref _state.DeltaPrefixLengthsBuffer, ref _state.DeltaSuffixLengthsBuffer,
                    ref _state.DecompressionBuffer, out var values, out var encoding))
                return false;

            Current = new ColumnPage<T>(values, encoding);
            return true;
        }

        public void Dispose()
        {
            _offset = 0;
            _readBuffer = false;
            _missingPageEmitted = false;
            _state.ReleasePageBuffer(_bufferPool);
            Current = default;
        }
    }
}
