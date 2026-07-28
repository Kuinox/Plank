namespace Plank.Reading.Physical;

using Plank.Reading.Logical;
using Plank.Reading.Logical.Internal;
using Plank.Schema;

public struct ParquetPageCursor : IDisposable
{
    const int MaxPageHeaderLength = 64 * 1024;

    ParquetFileReader? _owner;
    readonly int _generation;
    readonly ParquetColumnChunkInfo _chunk;
    readonly PageMetadataHandle _pageMetadata;
    readonly ParquetPagePruner? _pruner;
    ParquetBuffer _payloadBuffer;
    int _chunkLength;
    int _offset;
    int _payloadLength;
    int _nextCandidatePageOrdinal;
    int _pendingPageOrdinal;
    bool _dictionaryPending;

    internal ParquetPageCursor(ParquetFileReader owner, int generation, int rowGroupOrdinal, int columnOrdinal)
    {
        _owner = owner;
        _generation = generation;
        _chunk = owner.Metadata.ColumnChunk(rowGroupOrdinal, columnOrdinal);
        _pageMetadata = default;
        _pruner = null;
        _payloadBuffer = default;
        _chunkLength = 0;
        _offset = 0;
        _payloadLength = 0;
        _nextCandidatePageOrdinal = 0;
        _pendingPageOrdinal = -1;
        _dictionaryPending = false;
        CurrentHeader = default;

        if (_chunk.TotalCompressedSize > int.MaxValue)
            throw new NotSupportedException("Column chunks larger than Int32.MaxValue are not supported.");
        if (_chunk.ChunkOffset > owner.Source.Length ||
            _chunk.TotalCompressedSize > owner.Source.Length - _chunk.ChunkOffset)
            throw new CorruptParquetException(
                $"Column chunk at offset {_chunk.ChunkOffset} with size {_chunk.TotalCompressedSize} exceeds source length ({owner.Source.Length}).");

        _chunkLength = checked((int)_chunk.TotalCompressedSize);
    }

    internal ParquetPageCursor(ParquetFileReader owner, int generation, int rowGroupOrdinal, int columnOrdinal,
        PageMetadataHandle pageMetadata, ParquetPagePruner pruner)
        : this(owner, generation, rowGroupOrdinal, columnOrdinal)
    {
        _pageMetadata = pageMetadata;
        _pruner = pruner;
        _dictionaryPending = _chunk.DictionaryPageOffset != 0 &&
            _chunk.DictionaryPageOffset < _chunk.DataPageOffset;
    }

    public PageHeader CurrentHeader { get; private set; }

    public ParquetPage Current
        => new(CurrentHeader, CurrentPayload);

    public ReadOnlySpan<byte> CurrentPayload
    {
        get
        {
            ValidateCurrent();
            return _payloadBuffer.Span[.._payloadLength];
        }
    }

    public ParquetPageCursor GetEnumerator()
        => this;

    public bool MoveNext()
    {
        var owner = GetOwner();
        if (_pruner is not null)
            return MoveNextPruned(owner);
        if (_offset >= _chunkLength)
        {
            CurrentHeader = default;
            _payloadLength = 0;
            ReturnPayloadBuffer();
            return false;
        }

        return ReadPage(owner, _offset);
    }

    bool MoveNextPruned(ParquetFileReader owner)
    {
        var metadata = _pageMetadata;
        if (_pendingPageOrdinal < 0)
        {
            while (_nextCandidatePageOrdinal < metadata.Count)
            {
                var ordinal = _nextCandidatePageOrdinal++;
                var page = metadata.GetMetadata(ordinal);
                var accepted = _pruner!(in page);
                owner.ValidateGeneration(_generation);
                if (!accepted)
                    continue;
                _pendingPageOrdinal = ordinal;
                break;
            }
        }

        if (_pendingPageOrdinal < 0)
        {
            CurrentHeader = default;
            _payloadLength = 0;
            ReturnPayloadBuffer();
            return false;
        }

        if (_dictionaryPending)
        {
            _dictionaryPending = false;
            var dictionaryOffset = checked((int)(_chunk.DictionaryPageOffset - _chunk.ChunkOffset));
            var dictionaryLength = checked((int)(_chunk.DataPageOffset - _chunk.DictionaryPageOffset));
            return ReadPage(owner, dictionaryOffset, dictionaryLength);
        }

        var selected = metadata.GetMetadata(_pendingPageOrdinal);
        _pendingPageOrdinal = -1;
        if (selected.Offset < _chunk.ChunkOffset ||
            selected.Offset - _chunk.ChunkOffset > (ulong)_chunkLength)
            throw new CorruptParquetException(
                $"Selected page offset {selected.Offset} is outside its column chunk.");
        if (selected.CompressedSize > int.MaxValue)
            throw new NotSupportedException("Pages larger than Int32.MaxValue are not supported.");
        return ReadPage(owner, checked((int)(selected.Offset - _chunk.ChunkOffset)),
            checked((int)selected.CompressedSize));
    }

    bool ReadPage(ParquetFileReader owner, int pageOffset, int? boundedPageLength = null)
    {
        if ((uint)pageOffset >= (uint)_chunkLength)
        {
            CurrentHeader = default;
            _payloadLength = 0;
            ReturnPayloadBuffer();
            return false;
        }

        _offset = pageOffset;
        var remainingChunkLength = _chunkLength - _offset;
        var headerProbeLength = Math.Min(remainingChunkLength, MaxPageHeaderLength);
        if (boundedPageLength.HasValue)
        {
            if (boundedPageLength.Value <= 0 || boundedPageLength.Value > remainingChunkLength)
                throw new CorruptParquetException(
                    $"Indexed page size {boundedPageLength.Value} is outside its column chunk.");
            headerProbeLength = Math.Min(headerProbeLength, boundedPageLength.Value);
        }
        EnsurePayloadBuffer(owner, headerProbeLength);
        var headerBytes = _payloadBuffer.Span[..headerProbeLength];
        owner.Source.ReadExactly(_chunk.ChunkOffset + (ulong)_offset, headerBytes);
        var maxUncompressedPageSize = (uint)Math.Min(_chunk.TotalUncompressedSize, uint.MaxValue);
        var header = PageHeaderReader.Read(headerBytes, maxUncompressedPageSize);
        var totalPageLength = checked(header.HeaderLength + (int)header.CompressedPageSize);
        if (boundedPageLength.HasValue && totalPageLength > boundedPageLength.Value)
            throw new CorruptParquetException(
                $"Page size {totalPageLength} exceeds its indexed compressed size {boundedPageLength.Value}.");
        _offset += header.HeaderLength;
        if (header.CompressedPageSize > (uint)(_chunkLength - _offset))
            throw new CorruptParquetException(
                $"Page compressed size ({header.CompressedPageSize}) exceeds remaining column chunk buffer ({_chunkLength - _offset}).");

        var compressedLength = checked((int)header.CompressedPageSize);
        var sourceOffset = _chunk.ChunkOffset + (ulong)_offset;
        _offset += compressedLength;
        if (!RequiresDecompression(header, compressedLength))
        {
            EnsurePayloadBuffer(owner, compressedLength);
            if (compressedLength > 0)
                owner.Source.ReadExactly(sourceOffset, _payloadBuffer.Span[..compressedLength]);
            _payloadLength = compressedLength;
            CurrentHeader = header;
            return true;
        }

        if (header.UncompressedPageSize > int.MaxValue)
            throw new NotSupportedException("Page payloads larger than Int32.MaxValue are not supported.");

        var uncompressedLength = checked((int)header.UncompressedPageSize);
        EnsurePayloadBuffer(owner, uncompressedLength);
        var destination = _payloadBuffer.Span[..uncompressedLength];

        if (header.Type == PageHeaderType.DataPageV2)
        {
            var levelLength = checked((int)(header.RepetitionLevelsByteLength +
                header.DefinitionLevelsByteLength));
            if (levelLength > compressedLength || levelLength > destination.Length)
                throw new CorruptParquetException("DataPageV2 level bytes exceed the page payload.");

            if (levelLength > 0)
                owner.Source.ReadExactly(sourceOffset, destination[..levelLength]);

            sourceOffset += (ulong)levelLength;
            compressedLength -= levelLength;
            destination = destination[levelLength..];
        }

        if (compressedLength > 0)
        {
            using var compressed = owner.BufferPool.Rent(checked((uint)compressedLength));
            owner.Source.ReadExactly(sourceOffset, compressed.Span[..compressedLength]);
            ParquetDecompressor.DecompressInto(compressed.Span[..compressedLength], _chunk.Compression,
                destination);
        }

        _payloadLength = uncompressedLength;
        CurrentHeader = header;
        return true;
    }

    public void Dispose()
    {
        var owner = _owner;
        if (owner is null)
            return;

        ReturnPayloadBuffer();

        _owner = null;
        CurrentHeader = default;
    }

    bool RequiresDecompression(PageHeader header, int payloadLength)
    {
        if (_chunk.Compression == CompressionKind.None || payloadLength == 0)
            return false;
        return header.Type != PageHeaderType.DataPageV2 || header.IsCompressed;
    }

    void EnsurePayloadBuffer(ParquetFileReader owner, int length)
    {
        if (_payloadBuffer.Length >= length)
            return;

        ReturnPayloadBuffer();
        _payloadBuffer = owner.BufferPool.Rent(checked((uint)length));
    }

    void ReturnPayloadBuffer()
    {
        _payloadBuffer.Dispose();
    }

    ParquetFileReader GetOwner()
    {
        var owner = _owner ?? throw new ObjectDisposedException(nameof(ParquetPageCursor));
        owner.ValidateGeneration(_generation);
        return owner;
    }

    void ValidateCurrent()
    {
        _ = GetOwner();
        if (CurrentHeader.HeaderLength == 0)
            throw new InvalidOperationException("The cursor is not positioned on a page.");
    }
}
