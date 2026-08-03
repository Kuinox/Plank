using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Plank.Schema;

/// <summary>Converts an unmanaged application value to and from a built-in unmanaged Parquet storage value.</summary>
/// <typeparam name="TValue">The application value type.</typeparam>
/// <typeparam name="TPhysical">A CLR type natively supported by Plank for the column's physical type.</typeparam>
public abstract class ParquetValueConverter<TValue, TPhysical> : ParquetValueConverter
    where TValue : unmanaged
    where TPhysical : unmanaged
{
    /// <inheritdoc />
    public sealed override Type ValueType
        => typeof(TValue);

    /// <inheritdoc />
    public sealed override Type PhysicalType
        => typeof(TPhysical);

    internal sealed override bool SupportsValueType(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type == typeof(TValue) || type == typeof(TValue?);
    }

    internal sealed override bool IsNullableValueType(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type == typeof(TValue?);
    }

    /// <summary>Converts one application value to its Parquet storage value.</summary>
    public abstract TPhysical ConvertToPhysical(TValue value);

    /// <summary>Converts one Parquet storage value to its application value.</summary>
    public abstract TValue ConvertFromPhysical(TPhysical value);

    /// <summary>Converts application values into Parquet storage values.</summary>
    /// <remarks>Override this method to provide a vectorized conversion.</remarks>
    public virtual void ConvertToPhysical(ReadOnlySpan<TValue> source, Span<TPhysical> destination)
    {
        if (destination.Length < source.Length)
            throw new ArgumentException("The destination is shorter than the source.", nameof(destination));

        for (var i = 0; i < source.Length; i++)
            destination[i] = ConvertToPhysical(source[i]);
    }

    /// <summary>Converts Parquet storage values into application values.</summary>
    /// <remarks>Override this method to provide a vectorized conversion.</remarks>
    public virtual void ConvertFromPhysical(ReadOnlySpan<TPhysical> source, Span<TValue> destination)
    {
        if (destination.Length < source.Length)
            throw new ArgumentException("The destination is shorter than the source.", nameof(destination));

        for (var i = 0; i < source.Length; i++)
            destination[i] = ConvertFromPhysical(source[i]);
    }

    internal sealed override void ConvertToPhysical(ReadOnlySpan<byte> source, Span<byte> destination, int valueCount,
        bool nullable)
    {
        if (nullable)
        {
            var values = Reinterpret<TValue?>(source, valueCount);
            var physicalValues = Reinterpret<TPhysical?>(destination, valueCount);
            for (var i = 0; i < values.Length; i++)
                physicalValues[i] = values[i] is { } value ? ConvertToPhysical(value) : null;
            return;
        }

        ConvertToPhysical(MemoryMarshal.Cast<byte, TValue>(source)[..valueCount],
            MemoryMarshal.Cast<byte, TPhysical>(destination)[..valueCount]);
    }

    internal sealed override void ConvertFromPhysical(ReadOnlySpan<byte> source, Span<byte> destination,
        int valueCount)
        => ConvertFromPhysical(MemoryMarshal.Cast<byte, TPhysical>(source)[..valueCount],
            MemoryMarshal.Cast<byte, TValue>(destination)[..valueCount]);

    internal sealed override void ConvertNullableFromPhysical(ReadOnlySpan<byte> source,
        ReadOnlySpan<int> definitions, Span<byte> destination, int physicalValueCount)
    {
        var physicalValues = MemoryMarshal.Cast<byte, TPhysical>(source)[..physicalValueCount];
        var values = Reinterpret<TValue?>(destination, definitions.Length);
        var physicalIndex = 0;
        for (var i = 0; i < definitions.Length; i++)
        {
            if (definitions[i] == 0)
            {
                values[i] = null;
                continue;
            }
            if ((uint)physicalIndex >= (uint)physicalValues.Length)
                throw new CorruptParquetException(
                    $"Definition levels consume more than {physicalValueCount} physical values.");
            values[i] = ConvertFromPhysical(physicalValues[physicalIndex++]);
        }

        if (physicalIndex != physicalValueCount)
            throw new CorruptParquetException(
                $"Definition levels consumed {physicalIndex} physical values, expected {physicalValueCount}.");
    }

    static ReadOnlySpan<T> Reinterpret<T>(ReadOnlySpan<byte> source, int count)
    {
        if (count == 0)
            return [];
        if (checked(count * Unsafe.SizeOf<T>()) > source.Length)
            throw new ArgumentException("The source is shorter than the requested value span.", nameof(source));
        ref var first = ref Unsafe.As<byte, T>(ref MemoryMarshal.GetReference(source));
        return MemoryMarshal.CreateReadOnlySpan(ref first, count);
    }

    static Span<T> Reinterpret<T>(Span<byte> destination, int count)
    {
        if (count == 0)
            return [];
        if (checked(count * Unsafe.SizeOf<T>()) > destination.Length)
            throw new ArgumentException("The destination is shorter than the requested value span.",
                nameof(destination));
        ref var first = ref Unsafe.As<byte, T>(ref MemoryMarshal.GetReference(destination));
        return MemoryMarshal.CreateSpan(ref first, count);
    }
}
