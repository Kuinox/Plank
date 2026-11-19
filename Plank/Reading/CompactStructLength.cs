namespace Plank.Reading;

/// <summary>Measures a compact-protocol struct held in a buffer that may be a prefix of it, without throwing.</summary>
/// <remarks>
/// The page-header probe grows its window to exactly the bytes a header needs, so it parses many prefixes per
/// header. Asking <see cref="PageHeaderReader.TryRead"/> about each one costs a thrown
/// <see cref="CompactProtocolTruncatedException"/>; this walks only the struct's framing instead and reports the
/// same lower bound on the shortfall. Validation stays with the real parse, which runs once the struct is complete
/// and on anything this walk considers malformed, so errors are reported exactly as before.
/// </remarks>
static class CompactStructLength
{
    internal enum Result
    {
        Complete,
        NeedMore,
        Malformed
    }

    const int MaxStructDepth = 64;

    /// <summary>Measures the struct starting at <paramref name="buffer"/>[0].</summary>
    /// <param name="length">The struct's length when <see cref="Result.Complete"/>.</param>
    /// <param name="missingBytes">A lower bound on the bytes still needed when <see cref="Result.NeedMore"/>.</param>
    internal static Result Measure(ReadOnlySpan<byte> buffer, out int length, out int missingBytes)
    {
        var offset = 0;
        var result = SkipStruct(buffer, ref offset, 0, out missingBytes);
        length = result == Result.Complete ? offset : 0;
        return result;
    }

    static Result SkipStruct(ReadOnlySpan<byte> buffer, ref int offset, int depth, out int missingBytes)
    {
        missingBytes = 0;
        if (depth >= MaxStructDepth)
            return Result.Malformed;
        var lastFieldId = 0;
        while (true)
        {
            if (offset >= buffer.Length)
                return NeedMore(1, out missingBytes);
            var header = buffer[offset++];
            if (header == 0)
                return Result.Complete;
            var type = header & 0x0F;
            if (type > 13)
                return Result.Malformed;
            var delta = header >> 4;
            if (delta == 0)
            {
                // Long-form field id: a zigzag varint i16 follows the type byte.
                var idResult = ReadVarint(buffer, ref offset, maxBytes: 3, out var zigzag, out missingBytes);
                if (idResult != Result.Complete)
                    return idResult;
                var fieldId = (long)(zigzag >> 1) ^ -(long)(zigzag & 1);
                if (fieldId is < short.MinValue or > short.MaxValue)
                    return Result.Malformed;
                lastFieldId = (int)fieldId;
            }
            else
            {
                // The parser rejects ids that delta encoding pushes past i16; stop where it would.
                lastFieldId += delta;
                if (lastFieldId > short.MaxValue)
                    return Result.Malformed;
            }
            if (type is 1 or 2)
                continue; // booleans live in the field header
            var valueResult = SkipValue(buffer, ref offset, type, depth, out missingBytes);
            if (valueResult != Result.Complete)
                return valueResult;
        }
    }

    static Result SkipValue(ReadOnlySpan<byte> buffer, ref int offset, int type, int depth, out int missingBytes)
    {
        missingBytes = 0;
        switch (type)
        {
            case 1:
            case 2:
            case 3:
                return SkipBytes(buffer, ref offset, 1, out missingBytes);
            case 4:
                return SkipVarint(buffer, ref offset, maxBytes: 3, out missingBytes);
            case 5:
                return SkipVarint(buffer, ref offset, maxBytes: 5, out missingBytes);
            case 6:
                return SkipVarint(buffer, ref offset, maxBytes: 10, out missingBytes);
            case 7:
                return SkipBytes(buffer, ref offset, 8, out missingBytes);
            case 8:
            {
                var lengthResult = ReadVarint(buffer, ref offset, maxBytes: 5, out var binaryLength, out missingBytes);
                if (lengthResult != Result.Complete)
                    return lengthResult;
                return binaryLength > int.MaxValue
                    ? Result.Malformed
                    : SkipBytes(buffer, ref offset, (int)binaryLength, out missingBytes);
            }
            case 9:
            case 10:
            {
                if (offset >= buffer.Length)
                    return NeedMore(1, out missingBytes);
                var listHeader = buffer[offset++];
                var elementType = listHeader & 0x0F;
                ulong count = (uint)(listHeader >> 4);
                if (count == 15)
                {
                    var countResult = ReadVarint(buffer, ref offset, maxBytes: 5, out count, out missingBytes);
                    if (countResult != Result.Complete)
                        return countResult;
                }
                return SkipElements(buffer, ref offset, elementType, count, depth, out missingBytes);
            }
            case 11:
            {
                var countResult = ReadVarint(buffer, ref offset, maxBytes: 5, out var count, out missingBytes);
                if (countResult != Result.Complete || count == 0)
                    return countResult;
                if (offset >= buffer.Length)
                    return NeedMore(1, out missingBytes);
                var types = buffer[offset++];
                for (ulong i = 0; i < count; i++)
                {
                    var keyResult = SkipElements(buffer, ref offset, types >> 4, 1, depth, out missingBytes);
                    if (keyResult != Result.Complete)
                        return keyResult;
                    var valueResult = SkipElements(buffer, ref offset, types & 0x0F, 1, depth, out missingBytes);
                    if (valueResult != Result.Complete)
                        return valueResult;
                }
                return Result.Complete;
            }
            case 12:
                return SkipStruct(buffer, ref offset, depth + 1, out missingBytes);
            case 13:
                return SkipBytes(buffer, ref offset, 16, out missingBytes);
            default:
                return Result.Malformed;
        }
    }

    // Every element takes at least one byte, so a corrupt count runs out of buffer rather than looping long.
    static Result SkipElements(ReadOnlySpan<byte> buffer, ref int offset, int elementType, ulong count, int depth,
        out int missingBytes)
    {
        missingBytes = 0;
        if (elementType is 0 or > 13)
            return count == 0 ? Result.Complete : Result.Malformed;
        for (ulong i = 0; i < count; i++)
        {
            // Inside containers booleans take a byte of their own.
            var result = SkipValue(buffer, ref offset, elementType is 2 ? 1 : elementType, depth, out missingBytes);
            if (result != Result.Complete)
                return result;
        }
        return Result.Complete;
    }

    static Result SkipVarint(ReadOnlySpan<byte> buffer, ref int offset, int maxBytes, out int missingBytes)
        => ReadVarint(buffer, ref offset, maxBytes, out _, out missingBytes);

    static Result ReadVarint(ReadOnlySpan<byte> buffer, ref int offset, int maxBytes, out ulong value,
        out int missingBytes)
    {
        value = 0;
        missingBytes = 0;
        for (var i = 0; i < maxBytes; i++)
        {
            if (offset >= buffer.Length)
                return NeedMore(1, out missingBytes);
            var b = buffer[offset++];
            value |= (ulong)(b & 0x7F) << (7 * i);
            if ((b & 0x80) == 0)
                return Result.Complete;
        }
        return Result.Malformed;
    }

    static Result SkipBytes(ReadOnlySpan<byte> buffer, ref int offset, int count, out int missingBytes)
    {
        var available = buffer.Length - offset;
        if (count <= available)
        {
            offset += count;
            missingBytes = 0;
            return Result.Complete;
        }
        return NeedMore(count - available, out missingBytes);
    }

    static Result NeedMore(int bytes, out int missingBytes)
    {
        missingBytes = bytes;
        return Result.NeedMore;
    }
}
