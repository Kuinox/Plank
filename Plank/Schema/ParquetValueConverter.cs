namespace Plank.Schema;

/// <summary>Describes a custom mapping between a CLR value and a supported Parquet storage value.</summary>
/// <remarks>
/// Converter instances are attached to schema leaves. Plank preserves nullable values itself and only invokes the
/// converter for non-null values. A converter can be used concurrently by pipeline workers and must be thread-safe.
/// </remarks>
public abstract class ParquetValueConverter
{
    /// <summary>Initializes a custom Parquet value converter.</summary>
    protected ParquetValueConverter() { }

    /// <summary>Gets the application value type handled by this converter.</summary>
    public abstract Type ValueType { get; }

    /// <summary>Gets the built-in CLR storage type handled by this converter.</summary>
    public abstract Type PhysicalType { get; }

    internal abstract bool SupportsValueType(Type type);

    internal abstract bool IsNullableValueType(Type type);

    internal abstract void ConvertToPhysical(ReadOnlySpan<byte> source, Span<byte> destination, int valueCount,
        bool nullable);

    internal abstract void ConvertFromPhysical(ReadOnlySpan<byte> source, Span<byte> destination, int valueCount);

    internal abstract void ConvertNullableFromPhysical(ReadOnlySpan<byte> source, ReadOnlySpan<int> definitions,
        Span<byte> destination, int physicalValueCount);
}
