namespace Plank.Reading;

readonly struct InternalParquetFooter
{
    internal static readonly InternalParquetFooter Empty = new(0, []);

    internal InternalParquetFooter(int version, InternalRowGroupMetadata[] rowGroups)
    {
        Version = version;
        RowGroups = rowGroups ?? [];
    }

    internal int Version { get; }

    internal InternalRowGroupMetadata[] RowGroups { get; }
}
