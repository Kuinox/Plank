namespace Plank.Reading.Physical;

public struct ParquetPageCursor : IDisposable
{
    ParquetFileReader? _owner;
    readonly int _version;
    readonly ParquetColumnChunkInfo _chunk;
    byte[]? _chunkBuffer;
    byte[]? _decompressionBuffer;
    int _chunkLength;
    int _offset;
    int _payloadOffset;
    int _payloadLength;
    int _uncompressedLength;
    bool _payloadIsDecompressed;

    internal ParquetPageCursor(ParquetFileReader owner, int version, int rowGroupOrdinal, int columnOrdinal)
    {
        _owner = owner;
        _version = version;
        _chunk = owner.GetColumnChunk(version, rowGroupOrdinal, columnOrdinal);
        _chunkBuffer = null;
        _decompressionBuffer = null;
        _chunkLength = 0;
        _offset = 0;
        _payloadOffset = 0;
        _payloadLength = 0;
        _uncompressedLength = 0;
        _payloadIsDecompressed = false;
        CurrentHeader = default;

        if (_chunk.TotalCompressedSize > int.MaxValue)
            throw new NotSupportedException("Column chunks larger than Int32.MaxValue are not supported.");
        if (_chunk.TotalCompressedSize > owner.Source.Length)
            throw new CorruptParquetException(
                $"Column chunk size ({_chunk.TotalCompressedSize}) exceeds source length ({owner.Source.Length}).");

        _chunkLength = checked((int)_chunk.TotalCompressedSize);
        _chunkBuffer = owner.BufferPool.Rent<byte>(checked((uint)_chunkLength));
        owner.Source.ReadExactly(_chunk.ChunkOffset, _chunkBuffer.AsSpan(0, _chunkLength));
    }

    public PageHeader CurrentHeader { get; private set; }

    public ReadOnlySpan<byte> CurrentCompressedPayload
    {
        get
        {
            ValidateCurrent();
            return _chunkBuffer.AsSpan(_payloadOffset, _payloadLength);
        }
    }

    public ReadOnlySpan<byte> CurrentPayload
    {
        get
        {
            ValidateCurrent();
            return _payloadIsDecompressed
                ? _decompressionBuffer.AsSpan(0, _uncompressedLength)
                : _chunkBuffer.AsSpan(_payloadOffset, _payloadLength);
        }
    }

    public bool MoveNext()
    {
        var owner = GetOwner();
        if (_offset >= _chunkLength)
        {
            CurrentHeader = default;
            return false;
        }

        var remaining = _chunkBuffer!.AsSpan(_offset, _chunkLength - _offset);
        var maxUncompressedPageSize = (uint)Math.Min(_chunk.TotalUncompressedSize, uint.MaxValue);
        var header = PageHeaderReader.Read(remaining, maxUncompressedPageSize);
        _offset += header.HeaderLength;
        if (header.CompressedPageSize > (uint)(_chunkLength - _offset))
            throw new CorruptParquetException(
                $"Page compressed size ({header.CompressedPageSize}) exceeds remaining column chunk buffer ({_chunkLength - _offset}).");

        _payloadOffset = _offset;
        _payloadLength = checked((int)header.CompressedPageSize);
        _offset += _payloadLength;
        _payloadIsDecompressed = false;
        _uncompressedLength = _payloadLength;

        var compressed = _chunkBuffer.AsSpan(_payloadOffset, _payloadLength);
        if (RequiresDecompression(header, compressed.Length))
        {
            if (header.UncompressedPageSize > int.MaxValue)
                throw new NotSupportedException("Page payloads larger than Int32.MaxValue are not supported.");
            var uncompressedLength = checked((int)header.UncompressedPageSize);
            EnsureDecompressionBuffer(owner, uncompressedLength);
            var destination = _decompressionBuffer.AsSpan(0, uncompressedLength);

            if (header.Type == PageHeaderType.DataPageV2)
            {
                var levelLength = checked((int)(header.RepetitionLevelsByteLength +
                    header.DefinitionLevelsByteLength));
                if (levelLength > compressed.Length || levelLength > destination.Length)
                    throw new CorruptParquetException("DataPageV2 level bytes exceed the page payload.");
                compressed[..levelLength].CopyTo(destination);
                ParquetDecompressor.DecompressInto(compressed[levelLength..], _chunk.Compression,
                    destination[levelLength..]);
            }
            else
            {
                ParquetDecompressor.DecompressInto(compressed, _chunk.Compression, destination);
            }

            _payloadIsDecompressed = true;
            _uncompressedLength = uncompressedLength;
        }

        CurrentHeader = header;
        return true;
    }

    public void Dispose()
    {
        var owner = _owner;
        if (owner is null)
            return;

        if (_chunkBuffer is not null)
            owner.BufferPool.Return(_chunkBuffer);
        if (_decompressionBuffer is not null)
            owner.BufferPool.Return(_decompressionBuffer);

        _owner = null;
        _chunkBuffer = null;
        _decompressionBuffer = null;
        CurrentHeader = default;
    }

    bool RequiresDecompression(PageHeader header, int payloadLength)
    {
        if (_chunk.Compression == Writing.CompressionKind.None || payloadLength == 0)
            return false;
        return header.Type != PageHeaderType.DataPageV2 || header.IsCompressed;
    }

    void EnsureDecompressionBuffer(ParquetFileReader owner, int length)
    {
        if (_decompressionBuffer is not null && _decompressionBuffer.Length >= length)
            return;
        if (_decompressionBuffer is not null)
            owner.BufferPool.Return(_decompressionBuffer);
        _decompressionBuffer = owner.BufferPool.Rent<byte>(checked((uint)length));
    }

    ParquetFileReader GetOwner()
    {
        var owner = _owner ?? throw new ObjectDisposedException(nameof(ParquetPageCursor));
        owner.ValidateHandle(_version);
        return owner;
    }

    void ValidateCurrent()
    {
        _ = GetOwner();
        if (CurrentHeader.HeaderLength == 0)
            throw new InvalidOperationException("The cursor is not positioned on a page.");
    }
}
