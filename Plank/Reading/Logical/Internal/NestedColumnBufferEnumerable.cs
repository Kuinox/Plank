using Plank.Reading.Physical;
using Plank.Schema;

namespace Plank.Reading.Logical.Internal;

readonly struct NestedColumnBufferEnumerable<T>
{
    readonly ParquetFileReader _physicalReader;
    readonly int _rowGroupOrdinal;
    readonly int _columnOrdinal;
    readonly LeafColumn _definition;
    readonly IParquetBufferPool _bufferPool;
    readonly ulong _rowCount;
    readonly ParquetPagePruner? _pruner;

    internal NestedColumnBufferEnumerable(ParquetFileReader physicalReader, int rowGroupOrdinal, int columnOrdinal,
        LeafColumn definition, IParquetBufferPool bufferPool, ulong rowCount, ParquetPagePruner? pruner)
    {
        ArgumentNullException.ThrowIfNull(physicalReader);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(bufferPool);

        _physicalReader = physicalReader;
        _rowGroupOrdinal = rowGroupOrdinal;
        _columnOrdinal = columnOrdinal;
        _definition = definition;
        _bufferPool = bufferPool;
        _rowCount = rowCount;
        _pruner = pruner;
    }

    internal Enumerator GetEnumerator()
        => new(_physicalReader, _rowGroupOrdinal, _columnOrdinal, _definition, _bufferPool, _rowCount, _pruner);

    internal struct Enumerator : IDisposable
    {
        readonly ParquetFileReader _physicalReader;
        readonly int _rowGroupOrdinal;
        readonly int _columnOrdinal;
        readonly LeafColumn _definition;
        readonly IParquetBufferPool _bufferPool;
        readonly ulong _rowCount;
        readonly ParquetPagePruner? _pruner;
        ParquetPageCursor _cursor;
        PageMetadataHandle _pageMetadata;
        ColumnReadBuffers<T> _buffers;
        bool _openedCursor;

        internal Enumerator(ParquetFileReader physicalReader, int rowGroupOrdinal, int columnOrdinal,
            LeafColumn definition, IParquetBufferPool bufferPool, ulong rowCount, ParquetPagePruner? pruner)
        {
            ArgumentNullException.ThrowIfNull(physicalReader);
            ArgumentNullException.ThrowIfNull(definition);
            ArgumentNullException.ThrowIfNull(bufferPool);

            _physicalReader = physicalReader;
            _rowGroupOrdinal = rowGroupOrdinal;
            _columnOrdinal = columnOrdinal;
            _definition = definition;
            _bufferPool = bufferPool;
            _rowCount = rowCount;
            _pruner = pruner;
            _cursor = default;
            _pageMetadata = default;
            _buffers = default;
            _openedCursor = false;
            Current = default;
        }

        internal NestedColumnBuffer<T> Current { get; private set; }

        internal bool MoveNext()
        {
            if (!_openedCursor)
            {
                if (_pruner is null)
                    _cursor = _physicalReader.OpenPages(_rowGroupOrdinal, _columnOrdinal);
                else
                {
                    _pageMetadata = PageMetadataReader.OpenHandle(_physicalReader, _rowGroupOrdinal, _columnOrdinal,
                        _definition, _rowCount);
                    _cursor = _physicalReader.OpenPages(_rowGroupOrdinal, _columnOrdinal, _pageMetadata, _pruner);
                }
                _openedCursor = true;
            }

            while (_cursor.MoveNext())
            {
                if (ColumnChunkReader.TryDecodeDictionaryPageIntoNative<T>(_cursor.CurrentHeader,
                        _cursor.CurrentPayload, _definition.Column, ref _buffers, _bufferPool))
                    continue;

                if (ColumnChunkReader.TryDecodeNestedPageIntoNative(_cursor.CurrentHeader,
                        _cursor.CurrentPayload, _definition, ref _buffers, _bufferPool, out var buffer))
                {
                    Current = buffer;
                    return true;
                }

                throw new NotSupportedException(
                    $"Page type '{_cursor.CurrentHeader.Type}' with encoding '{_cursor.CurrentHeader.Encoding}' " +
                    $"for nested column '{_definition.Path}' cannot be decoded into pooled values of type '{typeof(T)}'.");
            }

            return false;
        }

        public void Dispose()
        {
            if (_openedCursor)
                _cursor.Dispose();
            _pageMetadata.Dispose();
            _pageMetadata = default;
            _buffers.Dispose();
            _openedCursor = false;
            Current = default;
        }
    }
}
