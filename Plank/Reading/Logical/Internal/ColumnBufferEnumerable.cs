using Plank.Reading.Physical;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Reading.Logical.Internal;

readonly struct ColumnBufferEnumerable<T>
{
    readonly ParquetFileReader _physicalReader;
    readonly int _rowGroupOrdinal;
    readonly int _columnOrdinal;
    readonly Column _column;
    readonly InternalColumnChunkMetadata _columnChunk;
    readonly IParquetBufferPool _bufferPool;
    readonly ulong _rowCount;

    internal ColumnBufferEnumerable(ParquetFileReader physicalReader, int rowGroupOrdinal, int columnOrdinal,
        Column column, InternalColumnChunkMetadata columnChunk, IParquetBufferPool bufferPool, ulong rowCount)
    {
        ArgumentNullException.ThrowIfNull(physicalReader);
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(bufferPool);

        _physicalReader = physicalReader;
        _rowGroupOrdinal = rowGroupOrdinal;
        _columnOrdinal = columnOrdinal;
        _column = column;
        _columnChunk = columnChunk;
        _bufferPool = bufferPool;
        _rowCount = rowCount;
    }

    internal Enumerator GetEnumerator()
        => new(_physicalReader, _rowGroupOrdinal, _columnOrdinal, _column, _columnChunk, _bufferPool, _rowCount);

    internal struct Enumerator : IDisposable
    {
        readonly ParquetFileReader _physicalReader;
        readonly int _rowGroupOrdinal;
        readonly int _columnOrdinal;
        readonly Column _column;
        readonly InternalColumnChunkMetadata _columnChunk;
        readonly IParquetBufferPool _bufferPool;
        readonly ulong _rowCount;
        ParquetPageCursor _cursor;
        ColumnReadBuffers<T> _buffers;
        bool _openedCursor;

        internal Enumerator(ParquetFileReader physicalReader, int rowGroupOrdinal, int columnOrdinal, Column column,
            InternalColumnChunkMetadata columnChunk, IParquetBufferPool bufferPool, ulong rowCount)
        {
            ArgumentNullException.ThrowIfNull(physicalReader);
            ArgumentNullException.ThrowIfNull(column);
            ArgumentNullException.ThrowIfNull(bufferPool);

            _physicalReader = physicalReader;
            _rowGroupOrdinal = rowGroupOrdinal;
            _columnOrdinal = columnOrdinal;
            _column = column;
            _columnChunk = columnChunk;
            _bufferPool = bufferPool;
            _rowCount = rowCount;
            _cursor = default;
            _buffers = default;
            _openedCursor = false;
            Current = default;
        }

        internal ColumnBuffer<T> Current { get; private set; }

        internal bool MoveNext()
        {
            if (!_openedCursor)
            {
                _cursor = _physicalReader.OpenPages(_rowGroupOrdinal, _columnOrdinal);
                _openedCursor = true;
            }

            while (_cursor.MoveNext())
            {
                if (ColumnChunkReader.TryDecodeDictionaryPageIntoNative<T>(_cursor.CurrentHeader,
                        _cursor.CurrentPayload, _column, ref _buffers, _bufferPool))
                    continue;

                if (ColumnChunkReader.TryDecodeNullablePageIntoNative<T>(_cursor.CurrentHeader,
                        _cursor.CurrentPayload, _column, _rowCount, ref _buffers, _bufferPool,
                        out var nullableBuffer))
                {
                    Current = nullableBuffer;
                    return true;
                }

                if (ColumnChunkReader.TryDecodeRequiredPageIntoNative<T>(_cursor.CurrentHeader,
                        _cursor.CurrentPayload, _column, _rowCount, ref _buffers, _bufferPool,
                        out var nativeBuffer))
                {
                    Current = nativeBuffer;
                    return true;
                }

                if (!ColumnChunkReader.TryDecodePage(_cursor.CurrentHeader, _cursor.CurrentPayload, _column,
                        _columnChunk, _rowCount, ref _buffers.ManagedDictionary,
                        ref _buffers.ManagedDictionaryBuffer, ref _buffers.ManagedValuesBuffer,
                        _bufferPool, ref _buffers.Scratch, out var values, out _))
                    continue;

                Current = _buffers.CreateBuffer(values, _bufferPool);
                return true;
            }

            return false;
        }

        public void Dispose()
        {
            if (_openedCursor)
                _cursor.Dispose();
            _buffers.Dispose(_bufferPool);
            _openedCursor = false;
            Current = default;
        }
    }
}
