using Plank.Schema;

namespace Plank.Reading;

public readonly struct ColumnPageEnumerable<T>
{
    readonly Stream _stream;
    readonly Column _column;
    readonly InternalColumnChunkMetadata _columnChunk;
    readonly ColumnPageReadState<T> _state;

    internal ColumnPageEnumerable(Stream stream, Column column, InternalColumnChunkMetadata columnChunk,
        ColumnPageReadState<T> state)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(state);

        _stream = stream;
        _column = column;
        _columnChunk = columnChunk;
        _state = state;
    }

    public Enumerator GetEnumerator()
        => new(_stream, _column, _columnChunk, _state);

    public struct Enumerator : IDisposable
    {
        readonly Stream _stream;
        readonly Column _column;
        readonly InternalColumnChunkMetadata _columnChunk;
        readonly ColumnPageReadState<T> _state;
        int _offset;
        bool _readBuffer;

        internal Enumerator(Stream stream, Column column, InternalColumnChunkMetadata columnChunk,
            ColumnPageReadState<T> state)
        {
            ArgumentNullException.ThrowIfNull(stream);
            ArgumentNullException.ThrowIfNull(column);
            ArgumentNullException.ThrowIfNull(state);

            _stream = stream;
            _column = column;
            _columnChunk = columnChunk;
            _state = state;
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
                _state.BufferLength = ColumnChunkReader.ReadChunkBuffer(_stream, _columnChunk, ref _state.Buffer);
                _readBuffer = true;
            }
            if (!ColumnChunkReader.TryReadNextDataPage(_state.Buffer!, _state.BufferLength, ref _offset, _column, _columnChunk,
                    ref _state.Dictionary, ref _state.ValuesBuffer, out var values, out var encoding))
                return false;

            Current = new ColumnPage<T>(values, encoding);
            return true;
        }

        public void Dispose()
        {
            _offset = 0;
            _readBuffer = false;
            _state.Dictionary = null;
            Current = default;
        }
    }
}
