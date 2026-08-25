using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Plank.Schema;

namespace Plank.Reading.Logical.Internal;

static class AlpDecoder
{
    const int HeaderSize = 7;

    internal static bool TryDecode<T>(ReadOnlySpan<byte> payload, Column column, uint valueCount,
        Span<T> destination)
    {
        if (column.PhysicalType == ParquetPhysicalType.Float && typeof(T) == typeof(float))
        {
            DecodeFloatPage(payload, valueCount, Unsafe.As<Span<T>, Span<float>>(ref destination));
            return true;
        }
        if (column.PhysicalType == ParquetPhysicalType.Double && typeof(T) == typeof(double))
        {
            DecodeDoublePage(payload, valueCount, Unsafe.As<Span<T>, Span<double>>(ref destination));
            return true;
        }
        return false;
    }

    static void DecodeFloatPage(ReadOnlySpan<byte> payload, uint valueCount, Span<float> destination)
    {
        var header = ReadPageHeader(payload, valueCount, destination.Length);
        var destinationOffset = 0;
        for (var vectorIndex = 0; vectorIndex < header.VectorCount; vectorIndex++)
        {
            var vector = GetVector(payload, header.VectorCount, vectorIndex);
            var vectorCount = Math.Min(header.VectorSize, destination.Length - destinationOffset);
            DecodeFloatVector(vector, destination.Slice(destinationOffset, vectorCount));
            destinationOffset += vectorCount;
        }
    }

    static void DecodeDoublePage(ReadOnlySpan<byte> payload, uint valueCount, Span<double> destination)
    {
        var header = ReadPageHeader(payload, valueCount, destination.Length);
        var destinationOffset = 0;
        for (var vectorIndex = 0; vectorIndex < header.VectorCount; vectorIndex++)
        {
            var vector = GetVector(payload, header.VectorCount, vectorIndex);
            var vectorCount = Math.Min(header.VectorSize, destination.Length - destinationOffset);
            DecodeDoubleVector(vector, destination.Slice(destinationOffset, vectorCount));
            destinationOffset += vectorCount;
        }
    }

    static PageInfo ReadPageHeader(ReadOnlySpan<byte> payload, uint valueCount, int destinationLength)
    {
        if (payload.Length < HeaderSize)
            throw new CorruptParquetException(
                $"ALP payload ({payload.Length} bytes) is shorter than its {HeaderSize}-byte header.");
        if (payload[0] != 0)
            throw new CorruptParquetException(
                $"ALP compression mode {payload[0]} is not supported.");
        if (payload[1] != 0)
            throw new CorruptParquetException(
                $"ALP integer encoding {payload[1]} is not supported.");

        var logVectorSize = payload[2];
        if (logVectorSize is < 3 or > 15)
            throw new CorruptParquetException(
                $"ALP log vector size {logVectorSize} is outside the supported range [3, 15].");
        var elementCount = BinaryPrimitives.ReadInt32LittleEndian(payload[3..]);
        if (elementCount < 0)
            throw new CorruptParquetException($"ALP element count {elementCount} is negative.");
        if ((uint)elementCount != valueCount || elementCount != destinationLength)
            throw new CorruptParquetException(
                $"ALP element count {elementCount} does not match the page's {valueCount} encoded values.");

        var vectorSize = 1 << logVectorSize;
        var vectorCount = checked((int)(((long)elementCount + vectorSize - 1) / vectorSize));
        var offsetByteCount = checked(vectorCount * sizeof(uint));
        if (payload.Length < HeaderSize + offsetByteCount)
            throw new CorruptParquetException(
                $"ALP payload ({payload.Length} bytes) is too short for {vectorCount} vector offsets.");
        if (vectorCount == 0)
        {
            if (payload.Length != HeaderSize)
                throw new CorruptParquetException(
                    $"Empty ALP payload has {payload.Length - HeaderSize} trailing bytes.");
            return new PageInfo(vectorSize, 0);
        }

        var firstOffset = BinaryPrimitives.ReadUInt32LittleEndian(payload[HeaderSize..]);
        if (firstOffset != (uint)offsetByteCount)
            throw new CorruptParquetException(
                $"ALP first vector offset is {firstOffset}, expected {offsetByteCount}.");
        var relativePayloadLength = checked((uint)(payload.Length - HeaderSize));
        var previousOffset = firstOffset;
        for (var i = 1; i < vectorCount; i++)
        {
            var offset = ReadOffset(payload, i);
            if (offset < previousOffset)
                throw new CorruptParquetException(
                    $"ALP vector offset {i} ({offset}) precedes offset {i - 1} ({previousOffset}).");
            if (offset > relativePayloadLength)
                throw new CorruptParquetException(
                    $"ALP vector offset {i} ({offset}) exceeds payload length {relativePayloadLength}.");
            previousOffset = offset;
        }
        if (previousOffset > relativePayloadLength)
            throw new CorruptParquetException(
                $"ALP vector offset {previousOffset} exceeds payload length {relativePayloadLength}.");

        return new PageInfo(vectorSize, vectorCount);
    }

    static ReadOnlySpan<byte> GetVector(ReadOnlySpan<byte> payload, int vectorCount, int vectorIndex)
    {
        var start = ReadOffset(payload, vectorIndex);
        var end = vectorIndex + 1 == vectorCount
            ? checked((uint)(payload.Length - HeaderSize))
            : ReadOffset(payload, vectorIndex + 1);
        if (end < start)
            throw new CorruptParquetException(
                $"ALP vector {vectorIndex} ends at {end}, before its start at {start}.");
        return payload.Slice(checked(HeaderSize + (int)start), checked((int)(end - start)));
    }

    static uint ReadOffset(ReadOnlySpan<byte> payload, int vectorIndex)
        => BinaryPrimitives.ReadUInt32LittleEndian(
            payload[(HeaderSize + vectorIndex * sizeof(uint))..]);

    static void DecodeFloatVector(ReadOnlySpan<byte> vector, Span<float> destination)
    {
        const int vectorHeaderSize = 9;
        if (vector.Length < vectorHeaderSize)
            throw new CorruptParquetException(
                $"ALP FLOAT vector ({vector.Length} bytes) is shorter than its {vectorHeaderSize}-byte header.");
        var exponent = vector[0];
        var factor = vector[1];
        ValidateParameters(exponent, factor, AlpEncodingPrimitives.FloatMaxExponent, "FLOAT");
        var exceptionCount = BinaryPrimitives.ReadUInt16LittleEndian(vector[2..]);
        if (exceptionCount > destination.Length)
            throw new CorruptParquetException(
                $"ALP FLOAT vector has {exceptionCount} exceptions for {destination.Length} elements.");
        var frameOfReference = BinaryPrimitives.ReadInt32LittleEndian(vector[4..]);
        var bitWidth = vector[8];
        if (bitWidth > 32)
            throw new CorruptParquetException($"ALP FLOAT bit width {bitWidth} exceeds 32.");

        var packedByteCount = GetPackedByteCount(destination.Length, bitWidth);
        var expectedLength = checked(vectorHeaderSize + packedByteCount + exceptionCount * 6);
        if (vector.Length != expectedLength)
            throw new CorruptParquetException(
                $"ALP FLOAT vector length is {vector.Length}, expected {expectedLength}.");
        var packed = vector.Slice(vectorHeaderSize, packedByteCount);
        ValidatePadding(packed, destination.Length, bitWidth);
        var reader = new PackedValueReader(packed, bitWidth);
        for (var i = 0; i < destination.Length; i++)
        {
            var delta = checked((uint)reader.Read());
            var encoded = unchecked((int)((uint)frameOfReference + delta));
            destination[i] = AlpEncodingPrimitives.Decode(encoded, exponent, factor);
        }

        var positionOffset = vectorHeaderSize + packedByteCount;
        var valueOffset = positionOffset + exceptionCount * sizeof(ushort);
        for (var i = 0; i < exceptionCount; i++)
        {
            var position = BinaryPrimitives.ReadUInt16LittleEndian(
                vector[(positionOffset + i * sizeof(ushort))..]);
            if (position >= destination.Length)
                throw new CorruptParquetException(
                    $"ALP FLOAT exception position {position} exceeds vector length {destination.Length}.");
            var bits = BinaryPrimitives.ReadInt32LittleEndian(
                vector[(valueOffset + i * sizeof(float))..]);
            destination[position] = BitConverter.Int32BitsToSingle(bits);
        }
    }

    static void DecodeDoubleVector(ReadOnlySpan<byte> vector, Span<double> destination)
    {
        const int vectorHeaderSize = 13;
        if (vector.Length < vectorHeaderSize)
            throw new CorruptParquetException(
                $"ALP DOUBLE vector ({vector.Length} bytes) is shorter than its {vectorHeaderSize}-byte header.");
        var exponent = vector[0];
        var factor = vector[1];
        ValidateParameters(exponent, factor, AlpEncodingPrimitives.DoubleMaxExponent, "DOUBLE");
        var exceptionCount = BinaryPrimitives.ReadUInt16LittleEndian(vector[2..]);
        if (exceptionCount > destination.Length)
            throw new CorruptParquetException(
                $"ALP DOUBLE vector has {exceptionCount} exceptions for {destination.Length} elements.");
        var frameOfReference = BinaryPrimitives.ReadInt64LittleEndian(vector[4..]);
        var bitWidth = vector[12];
        if (bitWidth > 64)
            throw new CorruptParquetException($"ALP DOUBLE bit width {bitWidth} exceeds 64.");

        var packedByteCount = GetPackedByteCount(destination.Length, bitWidth);
        var expectedLength = checked(vectorHeaderSize + packedByteCount + exceptionCount * 10);
        if (vector.Length != expectedLength)
            throw new CorruptParquetException(
                $"ALP DOUBLE vector length is {vector.Length}, expected {expectedLength}.");
        var packed = vector.Slice(vectorHeaderSize, packedByteCount);
        ValidatePadding(packed, destination.Length, bitWidth);
        var reader = new PackedValueReader(packed, bitWidth);
        for (var i = 0; i < destination.Length; i++)
        {
            var delta = reader.Read();
            var encoded = unchecked((long)((ulong)frameOfReference + delta));
            destination[i] = AlpEncodingPrimitives.Decode(encoded, exponent, factor);
        }

        var positionOffset = vectorHeaderSize + packedByteCount;
        var valueOffset = positionOffset + exceptionCount * sizeof(ushort);
        for (var i = 0; i < exceptionCount; i++)
        {
            var position = BinaryPrimitives.ReadUInt16LittleEndian(
                vector[(positionOffset + i * sizeof(ushort))..]);
            if (position >= destination.Length)
                throw new CorruptParquetException(
                    $"ALP DOUBLE exception position {position} exceeds vector length {destination.Length}.");
            var bits = BinaryPrimitives.ReadInt64LittleEndian(
                vector[(valueOffset + i * sizeof(double))..]);
            destination[position] = BitConverter.Int64BitsToDouble(bits);
        }
    }

    static void ValidateParameters(byte exponent, byte factor, int maximumExponent, string type)
    {
        if (exponent > maximumExponent)
            throw new CorruptParquetException(
                $"ALP {type} exponent {exponent} exceeds {maximumExponent}.");
        if (factor > exponent)
            throw new CorruptParquetException(
                $"ALP {type} factor {factor} exceeds exponent {exponent}.");
    }

    static int GetPackedByteCount(int valueCount, int bitWidth)
        => checked((int)(((long)valueCount * bitWidth + 7) / 8));

    static void ValidatePadding(ReadOnlySpan<byte> packed, int valueCount, int bitWidth)
    {
        var remainder = (int)((long)valueCount * bitWidth & 7);
        if (remainder != 0 && (packed[^1] >> remainder) != 0)
            throw new CorruptParquetException("ALP packed values have non-zero padding bits.");
    }

    readonly record struct PageInfo(int VectorSize, int VectorCount);

    ref struct PackedValueReader
    {
        readonly ReadOnlySpan<byte> _source;
        readonly int _bitWidth;
        UInt128 _accumulator;
        int _bitsInAccumulator;
        int _offset;

        internal PackedValueReader(ReadOnlySpan<byte> source, int bitWidth)
        {
            _source = source;
            _bitWidth = bitWidth;
        }

        internal ulong Read()
        {
            if (_bitWidth == 0)
                return 0;
            while (_bitsInAccumulator < _bitWidth)
            {
                _accumulator |= (UInt128)_source[_offset++] << _bitsInAccumulator;
                _bitsInAccumulator += 8;
            }
            var value = _bitWidth == 64
                ? (ulong)_accumulator
                : (ulong)_accumulator & ((1UL << _bitWidth) - 1);
            _accumulator >>= _bitWidth;
            _bitsInAccumulator -= _bitWidth;
            return value;
        }
    }
}
