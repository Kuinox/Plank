namespace Plank.Reading.Logical.Internal;

ref struct DeltaBinaryPackedReader
{
    readonly ReadOnlySpan<byte> _buffer;
    int _offset;
    ulong _bitBuffer;
    int _bufferedBits;

    internal DeltaBinaryPackedReader(ReadOnlySpan<byte> buffer)
    {
        _buffer = buffer;
        _offset = 0;
        _bitBuffer = 0;
        _bufferedBits = 0;
    }

    internal int Offset
        => _offset;

    internal ulong ReadUnsignedVarInt()
    {
        ulong value = 0;
        var shift = 0;
        while (true)
        {
            var b = ReadByte();
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return value;
            shift += 7;
            if (shift >= 70)
                throw new CorruptParquetException("Invalid delta-binary-packed UInt64 varint.");
        }
    }

    internal long ReadZigZagInt64()
    {
        var value = ReadUnsignedVarInt();
        return (long)(value >> 1) ^ -((long)value & 1L);
    }

    internal byte ReadByte()
    {
        if ((uint)_offset >= (uint)_buffer.Length)
            throw new CorruptParquetException("Unexpected end of delta-binary-packed payload.");

        _bitBuffer = 0;
        _bufferedBits = 0;
        return _buffer[_offset++];
    }

    internal ulong ReadPackedUnsigned(int bitWidth)
    {
        ulong value = 0;
        var writtenBits = 0;
        while (writtenBits < bitWidth)
        {
            if (_bufferedBits == 0)
            {
                if ((uint)_offset >= (uint)_buffer.Length)
                    throw new CorruptParquetException("Unexpected end of delta-binary-packed mini-block.");

                _bitBuffer = _buffer[_offset++];
                _bufferedBits = 8;
            }

            var bitsToRead = Math.Min(bitWidth - writtenBits, _bufferedBits);
            var chunkMask = bitsToRead == 64 ? ulong.MaxValue : (1UL << bitsToRead) - 1UL;
            value |= (_bitBuffer & chunkMask) << writtenBits;
            _bitBuffer >>= bitsToRead;
            _bufferedBits -= bitsToRead;
            writtenBits += bitsToRead;
        }

        return value;
    }
}
