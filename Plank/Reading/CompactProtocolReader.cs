namespace Plank.Reading;

ref struct CompactProtocolReader
{
    readonly ReadOnlySpan<byte> _buffer;
    int _offset;

    internal CompactProtocolReader(ReadOnlySpan<byte> buffer)
    {
        _buffer = buffer;
        _offset = 0;
    }

    internal int Offset
        => _offset;

    internal bool TryReadFieldHeader(ref int previousFieldId, out int fieldId, out CompactProtocolType type,
        out bool? inlineBool)
    {
        EnsureAvailable(1);
        var header = _buffer[_offset++];
        if (header == 0)
        {
            fieldId = 0;
            type = CompactProtocolType.Stop;
            inlineBool = null;
            return false;
        }

        type = (CompactProtocolType)(header & 0x0F);
        var delta = header >> 4;
        fieldId = delta == 0 ? ReadI16() : previousFieldId + delta;
        previousFieldId = fieldId;
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
            _ => throw new InvalidDataException($"Invalid compact protocol boolean value '{value}'.")
        };
    }

    internal (int Count, CompactProtocolType ElementType) ReadListHeader()
    {
        EnsureAvailable(1);
        var header = _buffer[_offset++];
        var countNibble = header >> 4;
        var type = (CompactProtocolType)(header & 0x0F);
        var count = countNibble == 15 ? checked((int)ReadVarUInt32()) : countNibble;
        return (count, type);
    }

    internal void Skip(CompactProtocolType type, bool? inlineBool = null)
    {
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
                var length = checked((int)ReadVarUInt32());
                EnsureAvailable(length);
                _offset += length;
                return;
            }
            case CompactProtocolType.Struct:
            {
                var previousFieldId = 0;
                while (TryReadFieldHeader(ref previousFieldId, out _, out var nestedType, out var nestedInlineBool))
                    Skip(nestedType, nestedInlineBool);
                return;
            }
            case CompactProtocolType.List:
            case CompactProtocolType.Set:
            {
                var (count, elementType) = ReadListHeader();
                for (var i = 0; i < count; i++)
                    Skip(elementType);
                return;
            }
            default:
                throw new InvalidDataException($"Unsupported compact protocol type '{type}'.");
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
            value |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return value;
            shift += 7;
            if (shift >= 35)
                throw new InvalidDataException("Invalid compact protocol UInt32 varint.");
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
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return value;
            shift += 7;
            if (shift >= 70)
                throw new InvalidDataException("Invalid compact protocol UInt64 varint.");
        }
    }

    void EnsureAvailable(int length)
    {
        if ((uint)length > (uint)(_buffer.Length - _offset))
            throw new InvalidDataException("Unexpected end of compact protocol payload.");
    }

    static int DecodeZigZag32(uint value)
        => (int)(value >> 1) ^ -((int)value & 1);

    static long DecodeZigZag64(ulong value)
        => (long)(value >> 1) ^ -((long)value & 1L);
}
