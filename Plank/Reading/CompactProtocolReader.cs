using System.Runtime.CompilerServices;

namespace Plank.Reading;

ref struct CompactProtocolReader
{
    const int MaxStructDepth = 64;

    readonly ReadOnlySpan<byte> _buffer;

    // Set when the buffer is known to be a prefix of the real payload rather than
    // the whole of it. In that mode running out of bytes is not corruption but a
    // request for more, and it is raised as CompactProtocolTruncatedException
    // carrying the shortfall. Only the page-header probe reads that way; see the
    // exception's remarks for why the distinction has to be structural.
    readonly bool _bufferMayBeTruncated;

    FieldIdStack _fieldIdStack;
    int _offset;
    int _lastFieldId;
    int _depth;

    internal CompactProtocolReader(ReadOnlySpan<byte> buffer) : this(buffer, bufferMayBeTruncated: false)
    {
    }

    internal CompactProtocolReader(ReadOnlySpan<byte> buffer, bool bufferMayBeTruncated)
    {
        _buffer = buffer;
        _bufferMayBeTruncated = bufferMayBeTruncated;
        _offset = 0;
        _lastFieldId = 0;
        _depth = 0;
    }

    internal int Offset
        => _offset;

    internal uint Remaining
        => (uint)(_buffer.Length - _offset);

    internal void BeginStruct()
    {
        PushStruct();
    }

    internal bool TryReadFieldHeader(out int fieldId, out CompactProtocolType type, out bool? inlineBool)
    {
        EnsureAvailable(1);
        var header = _buffer[_offset++];
        if (header == 0)
        {
            PopStruct();
            fieldId = 0;
            type = CompactProtocolType.Stop;
            inlineBool = null;
            return false;
        }

        type = (CompactProtocolType)(header & 0x0F);
        var delta = header >> 4;
        fieldId = delta == 0 ? ReadI16() : _lastFieldId + delta;
        _lastFieldId = fieldId;
        inlineBool = type switch
        {
            CompactProtocolType.BooleanTrue => true,
            CompactProtocolType.BooleanFalse => false,
            _ => null
        };
        return true;
    }

    internal int ReadI32()
        => DecodeZigZag32(ReadVarUInt32());

    internal int ReadI16()
        => DecodeZigZag32(ReadVarUInt32());

    internal long ReadI64()
        => DecodeZigZag64(ReadVarUInt64());

    internal byte ReadByte()
    {
        EnsureAvailable(1);
        return _buffer[_offset++];
    }

    internal uint ReadVarU32(uint max = uint.MaxValue)
    {
        var value = ReadVarUInt32();
        if (value > max)
            throw new CorruptParquetException($"Expected a varint value no greater than {max} but got {value}.");
        return value;
    }

    internal uint ReadI32AsU32(uint max = uint.MaxValue)
    {
        var value = DecodeZigZag32(ReadVarUInt32());
        if (value < 0 || (uint)value > max)
            throw new CorruptParquetException($"Expected a non-negative i32 value no greater than {max} but got {value}.");
        return (uint)value;
    }

    internal ulong ReadI64AsU64(ulong max = ulong.MaxValue)
    {
        var value = DecodeZigZag64(ReadVarUInt64());
        if (value < 0 || (ulong)value > max)
            throw new CorruptParquetException($"Expected a non-negative i64 value no greater than {max} but got {value}.");
        return (ulong)value;
    }

    internal bool ReadBool(bool? inlineBool)
    {
        if (inlineBool.HasValue)
            return inlineBool.Value;

        EnsureAvailable(1);
        var value = _buffer[_offset++];
        return value switch
        {
            (byte)CompactProtocolType.BooleanTrue => true,
            (byte)CompactProtocolType.BooleanFalse => false,
            _ => throw new CorruptParquetException($"Invalid compact protocol boolean value '{value}'.")
        };
    }

    internal ReadOnlySpan<byte> ReadBinary()
    {
        var length = ReadBinaryLength();
        var value = _buffer.Slice(_offset, length);
        _offset += length;
        return value;
    }

    /// <summary>Reads a binary field's length prefix and checks it against what is left.</summary>
    /// <remarks>
    /// A length longer than the bytes remaining is the same condition
    /// EnsureAvailable reports, found one field earlier, so it has to be reported
    /// the same way — as a shortfall when the buffer is a known prefix, and as
    /// corruption otherwise. Bounding it separately, as a varint range check, is
    /// what made a truncated statistics min/max look malformed to the page-header
    /// probe rather than incomplete.
    /// </remarks>
    int ReadBinaryLength()
    {
        var length = ReadVarUInt32();
        var available = Remaining;
        if (length <= available)
            return (int)length;
        if (length > int.MaxValue)
            throw new CorruptParquetException($"Compact protocol binary length {length} exceeds Int32.MaxValue.");
        if (_bufferMayBeTruncated)
            throw new CompactProtocolTruncatedException(checked((int)(length - available)));
        throw new CorruptParquetException(
            $"Expected a varint value no greater than {available} but got {length}.");
    }

    internal (uint Count, CompactProtocolType ElementType) ReadListHeader()
    {
        EnsureAvailable(1);
        var header = _buffer[_offset++];
        var countNibble = header >> 4;
        var type = (CompactProtocolType)(header & 0x0F);
        var count = countNibble == 15 ? ReadVarU32() : (uint)countNibble;
        return (count, type);
    }

    internal void Skip(CompactProtocolType type, bool? inlineBool = null, int remainingDepth = 64)
    {
        if (remainingDepth <= 0)
            throw new CorruptParquetException("Compact protocol nesting depth exceeds maximum.");

        switch (type)
        {
            case CompactProtocolType.BooleanTrue:
            case CompactProtocolType.BooleanFalse:
                _ = ReadBool(inlineBool);
                return;
            case CompactProtocolType.Byte:
                EnsureAvailable(1);
                _offset++;
                return;
            case CompactProtocolType.I16:
                _ = ReadI16();
                return;
            case CompactProtocolType.I32:
                _ = ReadI32();
                return;
            case CompactProtocolType.I64:
                _ = ReadI64();
                return;
            case CompactProtocolType.Binary:
            {
                // The length has to land in a local first: ReadBinaryLength
                // advances _offset past the prefix, and a compound assignment
                // would have read _offset before that happened.
                var length = ReadBinaryLength();
                _offset += length;
                return;
            }
            case CompactProtocolType.Struct:
            {
                BeginStruct();
                while (TryReadFieldHeader(out _, out var nestedType, out var nestedInlineBool))
                    Skip(nestedType, nestedInlineBool, remainingDepth - 1);
                return;
            }
            case CompactProtocolType.List:
            case CompactProtocolType.Set:
            {
                var (count, elementType) = ReadListHeader();
                for (var i = 0U; i < count; i++)
                    Skip(elementType, remainingDepth: remainingDepth - 1);
                return;
            }
            default:
                throw new CorruptParquetException($"Unsupported compact protocol type '{type}'.");
        }
    }

    uint ReadVarUInt32()
    {
        uint value = 0;
        var shift = 0;
        while (true)
        {
            EnsureAvailable(1);
            var b = _buffer[_offset++];
            if (shift == 28 && (b & 0xF0) != 0)
                throw new CorruptParquetException("Invalid compact protocol UInt32 varint.");
            value |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return value;
            shift += 7;
            if (shift > 28)
                throw new CorruptParquetException("Invalid compact protocol UInt32 varint.");
        }
    }

    ulong ReadVarUInt64()
    {
        ulong value = 0;
        var shift = 0;
        while (true)
        {
            EnsureAvailable(1);
            var b = _buffer[_offset++];
            if (shift == 63 && (b & 0xFE) != 0)
                throw new CorruptParquetException("Invalid compact protocol UInt64 varint.");
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return value;
            shift += 7;
            if (shift > 63)
                throw new CorruptParquetException("Invalid compact protocol UInt64 varint.");
        }
    }

    void EnsureAvailable(int length)
    {
        var available = _buffer.Length - _offset;
        if ((uint)length <= (uint)available)
            return;
        if (_bufferMayBeTruncated)
            throw new CompactProtocolTruncatedException(length - available);
        throw new CorruptParquetException("Unexpected end of compact protocol payload.");
    }

    void PushStruct()
    {
        if (_depth >= MaxStructDepth)
            throw new CorruptParquetException("Compact protocol nesting depth exceeds maximum.");

        _fieldIdStack[_depth++] = _lastFieldId;
        _lastFieldId = 0;
    }

    void PopStruct()
    {
        if (_depth > 0)
            _lastFieldId = _fieldIdStack[--_depth];
    }

    static int DecodeZigZag32(uint value)
        => (int)(value >> 1) ^ -((int)value & 1);

    static long DecodeZigZag64(ulong value)
        => (long)(value >> 1) ^ -((long)value & 1L);

    [InlineArray(MaxStructDepth)]
    struct FieldIdStack
    {
        int _element0;
    }
}
