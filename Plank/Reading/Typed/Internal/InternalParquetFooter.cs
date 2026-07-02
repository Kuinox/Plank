namespace Plank.Reading.Typed.Internal;

readonly struct InternalParquetFooter
{
    internal static readonly InternalParquetFooter Empty = new(0, null!, 0, 0, 0, []);

    internal InternalParquetFooter(int version, Schema.ParquetSchema schema, uint rowGroupCount, int rowGroupsOffset,
        int rowGroupsEndOffset)
        : this(version, schema, rowGroupCount, rowGroupsOffset, rowGroupsEndOffset, [])
    {
    }

    internal InternalParquetFooter(int version, InternalRowGroupMetadata[] rowGroups)
        : this(version, null!, (uint)(rowGroups?.Length ?? 0), 0, 0, rowGroups ?? [])
    {
    }

    InternalParquetFooter(int version, Schema.ParquetSchema schema, uint rowGroupCount, int rowGroupsOffset,
        int rowGroupsEndOffset, InternalRowGroupMetadata[] rowGroups)
    {
        Version = version;
        Schema = schema;
        RowGroupCount = rowGroupCount;
        RowGroupsOffset = rowGroupsOffset;
        RowGroupsEndOffset = rowGroupsEndOffset;
        RowGroups = rowGroups;
    }

    internal int Version { get; }

    internal Schema.ParquetSchema Schema { get; }

    internal uint RowGroupCount { get; }

    internal int RowGroupsOffset { get; }

    internal int RowGroupsEndOffset { get; }

    internal InternalRowGroupMetadata[] RowGroups { get; }
}
