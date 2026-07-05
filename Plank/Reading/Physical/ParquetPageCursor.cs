namespace Plank.Reading.Physical;

public struct ParquetPageCursor : IDisposable
{
    const int MaxPageHeaderLength = 64 * 1024;

    ParquetFileReader? _owner;
    readonly int _generation;
    readonly ParquetColumnChunkInfo _chunk;
    byte[]? _payloadBuffer;
    int _chunkLength;
    int _offset;
    int _payloadLength;

    internal ParquetPageCursor(ParquetFileReader owner, int generation, int rowGroupOrdinal, int columnOrdinal)
    {
        _owner = owner;
        _generation = generation;
        _chunk = owner.Metadata.ColumnChunk(rowGroupOrdinal, columnOrdinal);
        _payloadBuffer = null;
        _chunkLength = 0;
        _offset = 0;
        _payloadLength = 0;
        CurrentHeader = default;

        if (_chunk.TotalCompressedSize > int.MaxValue)
            throw new NotSupportedException("Column chunks larger than Int32.MaxValue are not supported.");
        if (_chunk.ChunkOffset > owner.Source.Length ||
            _chunk.TotalCompressedSize > owner.Source.Length - _chunk.ChunkOffset)
            throw new CorruptParquetException(
                $"Column chunk at offset {_chunk.ChunkOffset} with size {_chunk.TotalCompressedSize} exceeds source length ({owner.Source.Length}).");

        _chunkLength = checked((int)_chunk.TotalCompressedSize);
    }

    public PageHeader CurrentHeader { get; private set; }

    public ParquetPage Current
        => new(CurrentHeader, CurrentPayload);

    public ReadOnlySpan<byte> CurrentPayload
    {
        get
        {
            ValidateCurrent();
            return _payloadBuffer!.AsSpan(0, _payloadLength);
        }
    }

    public ParquetPageCursor GetEnumerator()
        => this;

    public bool MoveNext()
    {
        var owner = GetOwner();
        if (_offset >= _chunkLength)
        {
            CurrentHeader = default;
            _payloadLength = 0;
            ReturnPayloadBuffer(owner);
            return false;
        }

        var headerProbeLength = Math.Min(_chunkLength - _offset, MaxPageHeaderLength);
        EnsurePayloadBuffer(owner, headerProbeLength);
        var headerBytes = _payloadBuffer!.AsSpan(0, headerProbeLength);
        owner.Source.ReadExactly(_chunk.ChunkOffset + (ulong)_offset, headerBytes);
        var maxUncompressedPageSize = (uint)Math.Min(_chunk.TotalUncompressedSize, uint.MaxValue);
        var header = PageHeaderReader.Read(headerBytes, maxUncompressedPageSize);
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
                owner.Source.ReadExactly(sourceOffset, _payloadBuffer!.AsSpan(0, compressedLength));
            _payloadLength = compressedLength;
            CurrentHeader = header;
            return true;
        }

        if (header.UncompressedPageSize > int.MaxValue)
            throw new NotSupportedException("Page payloads larger than Int32.MaxValue are not supported.");

        var uncompressedLength = checked((int)header.UncompressedPageSize);
        EnsurePayloadBuffer(owner, uncompressedLength);
        var destination = _payloadBuffer!.AsSpan(0, uncompressedLength);

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
            var compressed = owner.BufferPool.Rent<byte>(checked((uint)compressedLength));
            try
            {
                owner.Source.ReadExactly(sourceOffset, compressed.AsSpan(0, compressedLength));
                ParquetDecompressor.DecompressInto(compressed.AsSpan(0, compressedLength), _chunk.Compression,
                    destination);
            }
            finally
            {
                owner.BufferPool.Return(compressed);
            }
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

        ReturnPayloadBuffer(owner);

        _owner = null;
        CurrentHeader = default;
    }

    bool RequiresDecompression(PageHeader header, int payloadLength)
    {
        if (_chunk.Compression == Writing.CompressionKind.None || payloadLength == 0)
            return false;
        return header.Type != PageHeaderType.DataPageV2 || header.IsCompressed;
    }

    void EnsurePayloadBuffer(ParquetFileReader owner, int length)
    {
        if (_payloadBuffer is not null && _payloadBuffer.Length >= length)
            return;

        ReturnPayloadBuffer(owner);
        _payloadBuffer = owner.BufferPool.Rent<byte>(checked((uint)length));
    }

    void ReturnPayloadBuffer(ParquetFileReader owner)
    {
        if (_payloadBuffer is null)
            return;

        owner.BufferPool.Return(_payloadBuffer);
        _payloadBuffer = null;
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
