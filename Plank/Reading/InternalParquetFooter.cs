namespace Plank.Reading;

readonly struct InternalParquetFooter
{
    internal static readonly InternalParquetFooter Empty = new(0, null!, 0, 0, 0);

    internal InternalParquetFooter(int version, Schema.ParquetSchema schema, uint rowGroupCount, int rowGroupsOffset,
        int rowGroupsEndOffset)
    {
        Version = version;
        Schema = schema;
        RowGroupCount = rowGroupCount;
        RowGroupsOffset = rowGroupsOffset;
        RowGroupsEndOffset = rowGroupsEndOffset;
    }

    internal int Version { get; }

    internal Schema.ParquetSchema Schema { get; }

    internal uint RowGroupCount { get; }

    internal int RowGroupsOffset { get; }

    internal int RowGroupsEndOffset { get; }
}
