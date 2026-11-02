namespace Plank.Reading.Physical;

/// <summary>An owned snapshot of optional per-column-chunk geospatial statistics.</summary>
public sealed class ParquetGeospatialStatistics
{
    internal ParquetGeospatialStatistics(ParquetBoundingBox? boundingBox, int[]? types)
    {
        BoundingBox = boundingBox;
        GeospatialTypes = types is null ? null : Array.AsReadOnly(types);
    }

    public ParquetBoundingBox? BoundingBox { get; }

    /// <summary>Gets WKB type codes, including dimension offsets. Null means absent; an empty list means unknown types.</summary>
    public IReadOnlyList<int>? GeospatialTypes { get; }

    internal static ParquetGeospatialStatistics Read(ReadOnlySpan<byte> bytes)
    {
        var reader = new CompactProtocolReader(bytes);
        ParquetBoundingBox? box = null;
        int[]? types = null;
        reader.BeginRootStruct();
        while (reader.TryReadFieldHeader(out var id, out var type, out var inlineBool))
        {
            if (id == 1)
            {
                box = ParquetBoundingBox.Read(ref reader, type);
            }
            else if (id == 2)
            {
                var (count, elementType) = reader.ReadListHeader(type);
                if (elementType != CompactProtocolType.I32 || count > reader.Remaining)
                    throw new CorruptParquetException("Invalid geospatial type list.");
                types = new int[checked((int)count)];
                for (var i = 0; i < types.Length; i++)
                    types[i] = reader.ReadI32(elementType);
            }
            else
                reader.Skip(type, inlineBool);
        }
        return new ParquetGeospatialStatistics(box, types);
    }
}

/// <summary>Stored geospatial bounds. Optional Z and M bounds and NaN values are preserved.</summary>
public sealed class ParquetBoundingBox
{
    internal ParquetBoundingBox(double?[] values)
    {
        XMin = values[0]!.Value; XMax = values[1]!.Value;
        YMin = values[2]!.Value; YMax = values[3]!.Value;
        ZMin = values[4]; ZMax = values[5]; MMin = values[6]; MMax = values[7];
    }

    public double XMin { get; }
    public double XMax { get; }
    public double YMin { get; }
    public double YMax { get; }
    public double? ZMin { get; }
    public double? ZMax { get; }
    public double? MMin { get; }
    public double? MMax { get; }

    internal static ParquetBoundingBox Read(ref CompactProtocolReader reader, CompactProtocolType wireType)
    {
        var values = new double?[8];
        reader.BeginStruct(wireType);
        while (reader.TryReadFieldHeader(out var id, out var type, out var inlineBool))
        {
            if (id is >= 1 and <= 8)
            {
                values[id - 1] = reader.ReadDouble(type);
            }
            else
                reader.Skip(type, inlineBool);
        }
        var required = new RequiredFields();
        for (var i = 0; i < 4; i++)
            if (values[i].HasValue) required.Add(i + 1);
        required.Validate(ParquetRequiredFields.BoundingBox);
        return new ParquetBoundingBox(values);
    }
}
