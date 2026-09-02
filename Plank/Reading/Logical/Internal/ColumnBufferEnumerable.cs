using Plank.Reading.Physical;
using Plank.Schema;

namespace Plank.Reading.Logical.Internal;

readonly struct ColumnBufferEnumerable<T>
{
    readonly ParquetFileReader _physicalReader;
    readonly int _rowGroupOrdinal;
    readonly int _columnOrdinal;
    readonly Column _column;
    readonly LeafColumn _definition;
    readonly IParquetBufferPool _bufferPool;
    readonly ulong _rowCount;
    readonly ParquetPagePruner? _pruner;
    readonly bool _borrowRequiredPlainValues;

    internal ColumnBufferEnumerable(ParquetFileReader physicalReader, int rowGroupOrdinal, int columnOrdinal,
        LeafColumn definition, IParquetBufferPool bufferPool, ulong rowCount, ParquetPagePruner? pruner,
        bool borrowRequiredPlainValues)
    {
        ArgumentNullException.ThrowIfNull(physicalReader);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(bufferPool);

        _physicalReader = physicalReader;
        _rowGroupOrdinal = rowGroupOrdinal;
        _columnOrdinal = columnOrdinal;
        _column = definition.Column;
        _definition = definition;
        _bufferPool = bufferPool;
        _rowCount = rowCount;
        _pruner = pruner;
        _borrowRequiredPlainValues = borrowRequiredPlainValues;
    }

    internal Enumerator GetEnumerator()
        => new(_physicalReader, _rowGroupOrdinal, _columnOrdinal, _definition, _bufferPool, _rowCount, _pruner,
            _borrowRequiredPlainValues);

    internal struct Enumerator : IDisposable
    {
        readonly ParquetFileReader _physicalReader;
        readonly int _rowGroupOrdinal;
        readonly int _columnOrdinal;
        readonly Column _column;
        readonly LeafColumn _definition;
        readonly IParquetBufferPool _bufferPool;
        readonly ulong _rowCount;
        readonly ParquetPagePruner? _pruner;
        readonly bool _borrowRequiredPlainValues;
        ParquetPageCursor _cursor;
        PageMetadataHandle _pageMetadata;
        ColumnReadBuffers<T> _buffers;
        ColumnChunkReader.FixedWidthPageState _fixedWidthPage;
        bool _openedCursor;

        internal Enumerator(ParquetFileReader physicalReader, int rowGroupOrdinal, int columnOrdinal,
            LeafColumn definition,
            IParquetBufferPool bufferPool, ulong rowCount, ParquetPagePruner? pruner,
            bool borrowRequiredPlainValues)
        {
            ArgumentNullException.ThrowIfNull(physicalReader);
            ArgumentNullException.ThrowIfNull(definition);
            ArgumentNullException.ThrowIfNull(bufferPool);

            _physicalReader = physicalReader;
            _rowGroupOrdinal = rowGroupOrdinal;
            _columnOrdinal = columnOrdinal;
            _column = definition.Column;
            _definition = definition;
            _bufferPool = bufferPool;
            _rowCount = rowCount;
            _pruner = pruner;
            _borrowRequiredPlainValues = borrowRequiredPlainValues;
            _cursor = default;
            _pageMetadata = default;
            _buffers = default;
            _fixedWidthPage = default;
            _openedCursor = false;
            Current = default;
        }

        internal ColumnBuffer<T> Current { get; private set; }

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

            if (_fixedWidthPage.Active)
            {
                Current = ColumnChunkReader.DecodeNextFixedWidthBatch(
                    _cursor.CurrentPayloadUnchecked, _column, ref _buffers, _bufferPool,
                    ref _fixedWidthPage);
                return true;
            }

            while (_cursor.MoveNext())
            {
                var borrowedBinaryPayload = typeof(T) == typeof(BinaryValueDescriptor) &&
                    !_borrowRequiredPlainValues
                        ? _cursor.CurrentBorrowedPayloadUnchecked
                        : default;
                if (ColumnChunkReader.TryDecodeDictionaryPageIntoNative<T>(_cursor.CurrentHeader,
                        _cursor.CurrentPayload, borrowedBinaryPayload, _column,
                        ref _buffers, _bufferPool))
                    continue;

                if (ColumnChunkReader.TryStartFixedWidthPageBatches(_cursor.CurrentHeader,
                        _cursor.CurrentPayload,
                        _borrowRequiredPlainValues ? _cursor.CurrentBorrowedPayloadUnchecked : default,
                        _column, _rowCount, ref _buffers, _bufferPool,
                        ref _fixedWidthPage, out var batchedFixedWidthBuffer))
                {
                    Current = batchedFixedWidthBuffer;
                    return true;
                }

                if (ColumnChunkReader.TryDecodeNullablePageIntoNative<T>(_cursor.CurrentHeader,
                        _cursor.CurrentPayload, borrowedBinaryPayload, _column, _rowCount,
                        ref _buffers, _bufferPool, out var nullableBuffer))
                {
                    Current = nullableBuffer;
                    return true;
                }

                if (ColumnChunkReader.TryDecodeRequiredPageIntoNative<T>(_cursor.CurrentHeader,
                        _cursor.CurrentPayload, borrowedBinaryPayload, _column, _rowCount,
                        ref _buffers, _bufferPool, out var nativeBuffer))
                {
                    Current = nativeBuffer;
                    return true;
                }

                throw new NotSupportedException(
                    $"Page type '{_cursor.CurrentHeader.Type}' with encoding '{_cursor.CurrentHeader.Encoding}' " +
                    $"for column '{_column.Name}' cannot be decoded into pooled values of type '{typeof(T)}'.");
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
            _fixedWidthPage = default;
            _openedCursor = false;
            Current = default;
        }
    }
}
