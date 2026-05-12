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

    internal ColumnPageEnumerable(IParquetReadSource source, Column column, InternalColumnChunkMetadata columnChunk,
        ColumnPageReadState<T> state, IParquetBufferPool bufferPool)
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
    }

    public Enumerator GetEnumerator()
        => new(_source, _column, _columnChunk, _state, _bufferPool);

    public struct Enumerator : IDisposable
    {
        readonly IParquetReadSource _source;
        readonly Column _column;
        readonly InternalColumnChunkMetadata _columnChunk;
        readonly ColumnPageReadState<T> _state;
        readonly IParquetBufferPool _bufferPool;
        int _offset;
        bool _readBuffer;

        internal Enumerator(IParquetReadSource source, Column column, InternalColumnChunkMetadata columnChunk,
            ColumnPageReadState<T> state, IParquetBufferPool bufferPool)
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
            _offset = 0;
            _readBuffer = false;
            _state.Dictionary = null;
            Current = default;
        }

        public ColumnPage<T> Current { get; private set; }

        public bool MoveNext()
        {
            if (!_readBuffer)
            {
                _state.BufferLength = ColumnChunkReader.ReadChunkBuffer(_source, _columnChunk, ref _state.Buffer, _bufferPool);
                _readBuffer = true;
            }
            if (!ColumnChunkReader.TryReadNextDataPage(_state.Buffer!, _state.BufferLength, ref _offset, _column, _columnChunk,
                    ref _state.Dictionary, ref _state.DictionaryBuffer, ref _state.ValuesBuffer, out var values, out var encoding))
                return false;

            Current = new ColumnPage<T>(values, encoding);
            return true;
        }

        public void Dispose()
        {
            _offset = 0;
            _readBuffer = false;
            _state.Release(_bufferPool);
            Current = default;
        }
    }
}
