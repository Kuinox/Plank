using Plank.Schema;

namespace Plank.Reading;

public readonly struct ColumnPageEnumerable<T>
{
    readonly Stream _stream;
    readonly Column _column;
    readonly InternalColumnChunkMetadata _columnChunk;

    internal ColumnPageEnumerable(Stream stream, Column column, InternalColumnChunkMetadata columnChunk)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(column);

        _stream = stream;
        _column = column;
        _columnChunk = columnChunk;
    }

    public Enumerator GetEnumerator()
        => new(_stream, _column, _columnChunk);

    public struct Enumerator : IDisposable
    {
        readonly Stream _stream;
        readonly Column _column;
        readonly InternalColumnChunkMetadata _columnChunk;
        byte[]? _buffer;
        int _bufferLength;
        int _offset;
        Array? _dictionary;
        T[]? _valuesBuffer;

        internal Enumerator(Stream stream, Column column, InternalColumnChunkMetadata columnChunk)
        {
            ArgumentNullException.ThrowIfNull(stream);
            ArgumentNullException.ThrowIfNull(column);

            _stream = stream;
            _column = column;
            _columnChunk = columnChunk;
            _buffer = null;
            _bufferLength = 0;
            _offset = 0;
            _dictionary = null;
            _valuesBuffer = null;
            Current = default;
        }

        public ColumnPage<T> Current { get; private set; }

        public bool MoveNext()
        {
            if (_buffer is null)
                _bufferLength = ColumnChunkReader.ReadChunkBuffer(_stream, _columnChunk, ref _buffer);
            if (!ColumnChunkReader.TryReadNextDataPage(_buffer!, _bufferLength, ref _offset, _column, _columnChunk,
                    ref _dictionary, ref _valuesBuffer, out var values, out var encoding))
                return false;

            Current = new ColumnPage<T>(values, encoding);
            return true;
        }

        public void Dispose()
        {
            _buffer = null;
            _dictionary = null;
            _valuesBuffer = null;
            _bufferLength = 0;
            _offset = 0;
            Current = default;
        }
    }
}
