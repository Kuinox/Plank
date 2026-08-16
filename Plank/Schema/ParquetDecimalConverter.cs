using System.Buffers.Binary;

namespace Plank.Schema;

static class ParquetDecimalConverter
{
    const int MaximumClrPrecision = 29;
    const int MaximumClrScale = 28;

    internal static LogicalType.Decimal RequireLogicalType(Column column)
    {
        if (column.LogicalType is not LogicalType.Decimal decimalType)
            throw new InvalidOperationException(
                $"Column '{column.Name}' must declare a decimal logical type to use System.Decimal values.");
        if (decimalType.Precision > MaximumClrPrecision)
            throw new NotSupportedException(
                $"Column '{column.Name}' has decimal precision {decimalType.Precision}; System.Decimal supports at most {MaximumClrPrecision} digits.");
        if (decimalType.Scale > MaximumClrScale)
            throw new NotSupportedException(
                $"Column '{column.Name}' has decimal scale {decimalType.Scale}; System.Decimal supports at most scale {MaximumClrScale}.");
        return decimalType;
    }

    internal static int ToInt32(decimal value, Column column)
        => checked((int)ToUnscaled(value, column));

    internal static long ToInt64(decimal value, Column column)
        => checked((long)ToUnscaled(value, column));

    internal static decimal FromInt32(int value, Column column)
        => FromUnscaled(value, column);

    internal static decimal FromInt64(long value, Column column)
        => FromUnscaled(value, column);

    internal static int GetByteCount(decimal value, Column column)
        => GetSignedByteCount(ToUnscaled(value, column));

    internal static int WriteBigEndian(decimal value, Column column, Span<byte> destination)
    {
        var unscaled = ToUnscaled(value, column);
        Span<byte> encoded = stackalloc byte[sizeof(ulong) * 2];
        BinaryPrimitives.WriteInt128BigEndian(encoded, unscaled);
        var offset = GetSignedByteOffset(encoded);
        var length = encoded.Length - offset;
        if (destination.Length < length)
            throw new ArgumentException("Destination is too small for the encoded decimal value.", nameof(destination));
        encoded[offset..].CopyTo(destination);
        return length;
    }

    internal static void WriteFixedBigEndian(decimal value, Column column, Span<byte> destination)
    {
        var unscaled = ToUnscaled(value, column);
        Span<byte> encoded = stackalloc byte[sizeof(ulong) * 2];
        BinaryPrimitives.WriteInt128BigEndian(encoded, unscaled);
        var offset = GetSignedByteOffset(encoded);
        var length = encoded.Length - offset;
        if (length > destination.Length)
            throw new OverflowException(
                $"Decimal value '{value}' does not fit the {destination.Length}-byte physical representation for column '{column.Name}'.");

        destination.Fill(unscaled < 0 ? byte.MaxValue : (byte)0);
        encoded[offset..].CopyTo(destination[^length..]);
    }

    internal static decimal ReadBigEndian(ReadOnlySpan<byte> value, Column column)
    {
        if (value.IsEmpty)
            throw new CorruptParquetException($"Column '{column.Name}' contains an empty decimal payload.");

        var negative = (value[0] & 0x80) != 0;
        var extension = negative ? byte.MaxValue : (byte)0;
        while (value.Length > sizeof(ulong) * 2 && value[0] == extension &&
               ((value[1] & 0x80) != 0) == negative)
            value = value[1..];
        if (value.Length > sizeof(ulong) * 2)
            throw new CorruptParquetException(
                $"Decimal payload for column '{column.Name}' exceeds the supported System.Decimal range.");

        Span<byte> encoded = stackalloc byte[sizeof(ulong) * 2];
        encoded.Fill(extension);
        value.CopyTo(encoded[^value.Length..]);
        return FromUnscaled(BinaryPrimitives.ReadInt128BigEndian(encoded), column);
    }

    static Int128 ToUnscaled(decimal value, Column column)
    {
        var decimalType = RequireLogicalType(column);
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(value, bits);

        var magnitude = (UInt128)(uint)bits[0] |
                        ((UInt128)(uint)bits[1] << 32) |
                        ((UInt128)(uint)bits[2] << 64);
        var valueScale = (bits[3] >> 16) & 0x7f;
        if (valueScale > decimalType.Scale)
        {
            for (var i = decimalType.Scale; i < valueScale; i++)
            {
                var quotient = magnitude / 10;
                if (quotient * 10 != magnitude)
                    throw new InvalidOperationException(
                        $"Decimal value '{value}' has more fractional digits than scale {decimalType.Scale} permits for column '{column.Name}'.");
                magnitude = quotient;
            }
        }
        else
        {
            try
            {
                checked
                {
                    for (var i = valueScale; i < decimalType.Scale; i++)
                        magnitude *= 10;
                }
            }
            catch (OverflowException)
            {
                throw new OverflowException(
                    $"Decimal value '{value}' exceeds precision {decimalType.Precision} for column '{column.Name}'.");
            }
        }

        if (magnitude >= PowerOfTen(decimalType.Precision))
            throw new OverflowException(
                $"Decimal value '{value}' exceeds precision {decimalType.Precision} for column '{column.Name}'.");

        var signed = checked((Int128)magnitude);
        return bits[3] < 0 && magnitude != 0 ? -signed : signed;
    }

    static decimal FromUnscaled(Int128 value, Column column)
    {
        var decimalType = RequireLogicalType(column);
        if (value == Int128.MinValue)
            throw new CorruptParquetException(
                $"Decimal value for column '{column.Name}' exceeds the supported System.Decimal range.");

        var negative = value < 0;
        var magnitude = (UInt128)(negative ? -value : value);
        if (magnitude >= PowerOfTen(decimalType.Precision) || magnitude > (UInt128)decimal.MaxValue)
            throw new CorruptParquetException(
                $"Decimal value for column '{column.Name}' exceeds the supported System.Decimal range.");

        return new decimal(
            unchecked((int)(uint)magnitude),
            unchecked((int)(uint)(magnitude >> 32)),
            unchecked((int)(uint)(magnitude >> 64)),
            negative,
            checked((byte)decimalType.Scale));
    }

    static UInt128 PowerOfTen(int exponent)
    {
        UInt128 result = 1;
        for (var i = 0; i < exponent; i++)
            result *= 10;
        return result;
    }

    static int GetSignedByteCount(Int128 value)
    {
        Span<byte> encoded = stackalloc byte[sizeof(ulong) * 2];
        BinaryPrimitives.WriteInt128BigEndian(encoded, value);
        return encoded.Length - GetSignedByteOffset(encoded);
    }

    static int GetSignedByteOffset(ReadOnlySpan<byte> encoded)
    {
        var negative = (encoded[0] & 0x80) != 0;
        var extension = negative ? byte.MaxValue : (byte)0;
        var offset = 0;
        while (offset < encoded.Length - 1 && encoded[offset] == extension &&
               ((encoded[offset + 1] & 0x80) != 0) == negative)
            offset++;
        return offset;
    }
}
