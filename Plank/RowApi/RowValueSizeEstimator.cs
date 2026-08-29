using System.Runtime.CompilerServices;
using System.Text;
using Plank.Schema;

namespace Plank.RowApi;

static class RowValueSizeEstimator
{
    internal static bool TryGetFixedSize<T>(Column column, out ulong size)
    {
        if (typeof(T) == typeof(ReadOnlyMemory<byte>) ||
            typeof(T) == typeof(ReadOnlyMemory<byte>?) ||
            typeof(T) == typeof(Memory<byte>) ||
            typeof(T) == typeof(Memory<byte>?) ||
            typeof(T) == typeof(byte[]) ||
            typeof(T) == typeof(string) ||
            typeof(T).IsArray ||
            !typeof(T).IsValueType)
        {
            size = 0;
            return false;
        }

        size = GetScalarSize<T>(column);
        return true;
    }

    internal static ulong Estimate<T>(T value, Column column)
    {
        if (typeof(T) == typeof(ReadOnlyMemory<byte>))
            return AtLeastOne(Unsafe.As<T, ReadOnlyMemory<byte>>(ref value).Length);
        if (typeof(T) == typeof(ReadOnlyMemory<byte>?))
            return Estimate(Unsafe.As<T, ReadOnlyMemory<byte>?>(ref value));
        if (typeof(T) == typeof(Memory<byte>))
            return AtLeastOne(Unsafe.As<T, Memory<byte>>(ref value).Length);
        if (typeof(T) == typeof(Memory<byte>?))
            return Estimate(Unsafe.As<T, Memory<byte>?>(ref value));
        if (typeof(T) == typeof(byte[]))
            return AtLeastOne(Unsafe.As<T, byte[]?>(ref value)?.Length ?? 0);
        if (typeof(T) == typeof(string))
        {
            var text = Unsafe.As<T, string?>(ref value);
            return text is null ? 1UL : AtLeastOne(Encoding.UTF8.GetByteCount(text));
        }
        if (typeof(T).IsArray)
            return EstimateArray(Unsafe.As<T, Array?>(ref value), column);
        if (!typeof(T).IsValueType)
            return Unsafe.As<T, object?>(ref value) is null ? 1UL : GetScalarSize<T>(column);

        return GetScalarSize<T>(column);
    }

    static ulong Estimate(ReadOnlyMemory<byte>? value)
        => AtLeastOne(value?.Length ?? 0);

    static ulong Estimate(Memory<byte>? value)
        => AtLeastOne(value?.Length ?? 0);

    static ulong EstimateArray(Array? values, Column column)
    {
        if (values is null)
            return 1;
        if (values.Length == 0)
            return 1;

        var elementType = values.GetType().GetElementType()!;
        if (elementType.IsValueType && elementType != typeof(ReadOnlyMemory<byte>) &&
            elementType != typeof(Memory<byte>))
            return checked((ulong)values.Length * (GetPhysicalScalarSize(column) + 1));

        ulong size = checked((ulong)values.Length);
        for (var i = 0; i < values.Length; i++)
            size = checked(size + EstimateObject(values.GetValue(i), column));
        return size;
    }

    static ulong EstimateObject(object? value, Column column)
        => value switch
        {
            null => 1,
            string text => AtLeastOne(Encoding.UTF8.GetByteCount(text)),
            byte[] bytes => AtLeastOne(bytes.Length),
            ReadOnlyMemory<byte> memory => AtLeastOne(memory.Length),
            Memory<byte> memory => AtLeastOne(memory.Length),
            Array array => EstimateArray(array, column),
            _ => GetPhysicalScalarSize(column)
        };

    static ulong GetScalarSize<T>(Column column)
    {
        var physicalSize = GetPhysicalScalarSize(column);
        if (column.PhysicalType is ParquetPhysicalType.ByteArray && typeof(T).IsValueType)
            return checked((ulong)Unsafe.SizeOf<T>());
        return physicalSize;
    }

    static ulong AtLeastOne(int size)
        => checked((ulong)Math.Max(size, 1));

    static ulong GetPhysicalScalarSize(Column column)
        => column.PhysicalType switch
        {
            ParquetPhysicalType.Boolean => 1,
            ParquetPhysicalType.Int32 => 4,
            ParquetPhysicalType.Int64 => 8,
            ParquetPhysicalType.Int96 => 12,
            ParquetPhysicalType.Float => 4,
            ParquetPhysicalType.Double => 8,
            ParquetPhysicalType.FixedLenByteArray => column.Options.TypeLength,
            ParquetPhysicalType.ByteArray => 1,
            _ => throw new InvalidOperationException($"Unknown Parquet physical type '{column.PhysicalType}'.")
        };
}
