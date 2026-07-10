using Plank.Reading.Logical.Internal;
using Plank.Reading.Physical;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Reading.Logical;

public readonly struct ColumnPageEnumerable<T>
{
    readonly ParquetFileReader _physicalReader;
    readonly int _rowGroupOrdinal;
    readonly int _columnOrdinal;
    readonly Column _column;
    readonly InternalColumnChunkMetadata _columnChunk;
    readonly ColumnPageReadState<T> _state;
    readonly IParquetBufferPool _bufferPool;

    readonly ulong _rowCount;

    internal ColumnPageEnumerable(ParquetFileReader physicalReader, int rowGroupOrdinal, int columnOrdinal,
        Column column, InternalColumnChunkMetadata columnChunk, ColumnPageReadState<T> state,
        IParquetBufferPool bufferPool, ulong rowCount)
    {
        ArgumentNullException.ThrowIfNull(physicalReader);
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(bufferPool);

        _physicalReader = physicalReader;
        _rowGroupOrdinal = rowGroupOrdinal;
        _columnOrdinal = columnOrdinal;
        _column = column;
        _columnChunk = columnChunk;
        _state = state;
        _bufferPool = bufferPool;
        _rowCount = rowCount;
    }

    public Enumerator GetEnumerator()
        => new(_physicalReader, _rowGroupOrdinal, _columnOrdinal, _column, _columnChunk, _state, _bufferPool,
            _rowCount);

    public struct Enumerator : IDisposable
    {
        readonly ParquetFileReader _physicalReader;
        readonly int _rowGroupOrdinal;
        readonly int _columnOrdinal;
        readonly Column _column;
        readonly InternalColumnChunkMetadata _columnChunk;
        readonly ColumnPageReadState<T> _state;
        readonly IParquetBufferPool _bufferPool;
        ParquetPageCursor _cursor;
        bool _openedCursor;

        readonly ulong _rowCount;

        internal Enumerator(ParquetFileReader physicalReader, int rowGroupOrdinal, int columnOrdinal, Column column,
            InternalColumnChunkMetadata columnChunk, ColumnPageReadState<T> state, IParquetBufferPool bufferPool,
            ulong rowCount)
        {
            ArgumentNullException.ThrowIfNull(physicalReader);
            ArgumentNullException.ThrowIfNull(column);
            ArgumentNullException.ThrowIfNull(state);
            ArgumentNullException.ThrowIfNull(bufferPool);

            _physicalReader = physicalReader;
            _rowGroupOrdinal = rowGroupOrdinal;
            _columnOrdinal = columnOrdinal;
            _column = column;
            _columnChunk = columnChunk;
            _state = state;
            _bufferPool = bufferPool;
            _rowCount = rowCount;
            _cursor = default;
            _openedCursor = false;
            _state.Dictionary = null;
            Current = default;
        }

        public ColumnPage<T> Current { get; private set; }

        public bool MoveNext()
        {
            if (!_openedCursor)
            {
                _cursor = _physicalReader.OpenPages(_rowGroupOrdinal, _columnOrdinal);
                _openedCursor = true;
            }

            while (_cursor.MoveNext())
            {
                if (!ColumnChunkReader.TryDecodePage(_cursor.CurrentHeader, _cursor.CurrentPayload, _column,
                        _columnChunk, _rowCount, ref _state.Dictionary, ref _state.DictionaryBuffer,
                        ref _state.ValuesBuffer, _bufferPool, ref _state.DeltaPrefixLengthsBuffer,
                        ref _state.DeltaSuffixLengthsBuffer, out var values, out var encoding))
                    continue;

                Current = new ColumnPage<T>(values, encoding);
                return true;
            }

            return false;
        }

        public void Dispose()
        {
            if (_openedCursor)
                _cursor.Dispose();
            _openedCursor = false;
            Current = default;
        }
    }
}
