namespace Plank.Reading.Physical;

using Plank.Reading.Logical;
using Plank.Reading.Logical.Internal;
using Plank.Schema;

public struct ParquetPageCursor : IDisposable
{
    const int InitialPageHeaderLength = 64 * 1024;

    ParquetFileReader? _owner;
    readonly int _generation;
    readonly ParquetColumnChunkInfo _chunk;
    readonly PageMetadataHandle _pageMetadata;
    readonly ParquetPagePruner? _pruner;
    ParquetBuffer _payloadBuffer;
    ReadOnlyMemory<byte> _borrowedPayload;
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
        _borrowedPayload = default;
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
            return _borrowedPayload.IsEmpty
                ? _payloadBuffer.Span[.._payloadLength]
                : _borrowedPayload.Span;
        }
    }

    // The logical continuation path already owns an active page and avoids repeating cursor validation per batch.
    internal ReadOnlySpan<byte> CurrentPayloadUnchecked
        => _borrowedPayload.IsEmpty
            ? _payloadBuffer.Span[.._payloadLength]
            : _borrowedPayload.Span;

    internal ReadOnlyMemory<byte> CurrentBorrowedPayloadUnchecked
        => _borrowedPayload;

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
            _borrowedPayload = default;
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
            _borrowedPayload = default;
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
            _borrowedPayload = default;
            ReturnPayloadBuffer();
            return false;
        }

        _payloadLength = 0;
        _borrowedPayload = default;
        _offset = pageOffset;
        var pageFileOffset = _chunk.ChunkOffset + (ulong)pageOffset;
        var remainingChunkLength = _chunkLength - _offset;
        var maxHeaderLength = remainingChunkLength;
        if (boundedPageLength.HasValue)
        {
            if (boundedPageLength.Value <= 0 || boundedPageLength.Value > remainingChunkLength)
                throw new CorruptParquetException(
                    $"Indexed page size {boundedPageLength.Value} is outside its column chunk.");
            maxHeaderLength = boundedPageLength.Value;
        }
        var headerProbeLength = Math.Min(maxHeaderLength, InitialPageHeaderLength);
        var maxUncompressedPageSize = (uint)Math.Min(_chunk.TotalUncompressedSize, uint.MaxValue);
        PageHeader header;
        while (true)
        {
            ReadOnlySpan<byte> headerBytes;
            if (owner.TryBorrowSource(pageFileOffset, headerProbeLength, out var borrowedHeader))
                headerBytes = borrowedHeader.Span;
            else
            {
                EnsurePayloadBuffer(owner, headerProbeLength);
                var headerDestination = _payloadBuffer.Span[..headerProbeLength];
                owner.Source.ReadExactly(pageFileOffset, headerDestination);
                headerBytes = headerDestination;
            }
            if (PageHeaderReader.TryRead(headerBytes, maxUncompressedPageSize, out header, out var missingBytes))
                break;

            var requiredLength = PageHeaderReader.GetRequiredBufferLength(headerProbeLength, missingBytes,
                maxHeaderLength);
            headerProbeLength = (int)Math.Min(maxHeaderLength,
                Math.Max((long)headerProbeLength * 2, requiredLength));
        }
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
        if (!RequiresDecompression(header))
        {
            if (header.CompressedPageSize != header.UncompressedPageSize)
                throw new CorruptParquetException(
                    $"Uncompressed page size ({header.UncompressedPageSize}) does not match its payload size ({header.CompressedPageSize}).");

            ReadOnlySpan<byte> payload;
            if (owner.TryBorrowSource(sourceOffset, compressedLength, out var borrowedPayload))
            {
                _borrowedPayload = borrowedPayload;
                payload = borrowedPayload.Span;
            }
            else
            {
                EnsurePayloadBuffer(owner, compressedLength);
                if (compressedLength > 0)
                    owner.Source.ReadExactly(sourceOffset, _payloadBuffer.Span[..compressedLength]);
                payload = _payloadBuffer.Span[..compressedLength];
            }
            if (owner.VerifyPageCrc && header.Crc.HasValue)
                VerifyPageCrc(header, ParquetCrc32.Compute(payload), pageFileOffset);
            _payloadLength = compressedLength;
            CurrentHeader = header;
            return true;
        }

        if (header.UncompressedPageSize > int.MaxValue)
            throw new NotSupportedException("Page payloads larger than Int32.MaxValue are not supported.");

        var uncompressedLength = checked((int)header.UncompressedPageSize);
        EnsurePayloadBuffer(owner, uncompressedLength);
        var destination = _payloadBuffer.Span[..uncompressedLength];
        var verifyPageCrc = owner.VerifyPageCrc && header.Crc.HasValue;
        var crcState = ParquetCrc32.InitialState;

        if (header.Type == PageHeaderType.DataPageV2)
        {
            var levelLength = checked((int)(header.RepetitionLevelsByteLength +
                header.DefinitionLevelsByteLength));
            if (levelLength > compressedLength || levelLength > destination.Length)
                throw new CorruptParquetException("DataPageV2 level bytes exceed the page payload.");

            if (levelLength > 0)
            {
                owner.Source.ReadExactly(sourceOffset, destination[..levelLength]);
                if (verifyPageCrc)
                    crcState = ParquetCrc32.Append(crcState, destination[..levelLength]);
            }

            sourceOffset += (ulong)levelLength;
            compressedLength -= levelLength;
            destination = destination[levelLength..];
        }

        if (compressedLength > 0)
        {
            using var compressed = owner.BufferPool.Rent(checked((uint)compressedLength));
            var compressedPayload = compressed.Span[..compressedLength];
            owner.Source.ReadExactly(sourceOffset, compressedPayload);
            if (verifyPageCrc)
            {
                crcState = ParquetCrc32.Append(crcState, compressedPayload);
                VerifyPageCrc(header, ParquetCrc32.Complete(crcState), pageFileOffset);
            }
            ParquetDecompressor.DecompressInto(compressedPayload, _chunk.Compression, destination);
        }
        else
        {
            if (verifyPageCrc)
                VerifyPageCrc(header, ParquetCrc32.Complete(crcState), pageFileOffset);
            if (!destination.IsEmpty)
                throw new CorruptParquetException("Compressed page payload is empty but its uncompressed payload is not.");
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
        _borrowedPayload = default;
        _payloadLength = 0;
        CurrentHeader = default;
    }

    bool RequiresDecompression(PageHeader header)
    {
        if (_chunk.Compression == CompressionKind.None ||
            header.CompressedPageSize == 0 && header.UncompressedPageSize == 0)
            return false;
        return header.Type != PageHeaderType.DataPageV2 || header.IsCompressed;
    }

    void VerifyPageCrc(PageHeader header, uint actual, ulong pageFileOffset)
    {
        var expected = header.Crc!.Value;
        if (actual != expected)
            throw new CorruptParquetException(
                $"Page CRC mismatch at offset {pageFileOffset} for row group {_chunk.RowGroupOrdinal}, column {_chunk.ColumnOrdinal}: expected 0x{expected:X8}, computed 0x{actual:X8}.");
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
