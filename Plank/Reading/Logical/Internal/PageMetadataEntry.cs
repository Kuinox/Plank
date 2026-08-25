using Plank.Reading.Internal;

namespace Plank.Reading.Logical.Internal;

struct PageMetadataEntry
{
    internal EncodedStatistics Statistics;
    internal ulong Offset;
    internal ulong FirstRowIndex;
    internal ulong RowCount;
    internal uint CompressedSize;
    internal byte Flags;

    internal bool HasLocation
    {
        get => (Flags & 1) != 0;
        set => Flags = value ? (byte)(Flags | 1) : (byte)(Flags & ~1);
    }

    internal bool HasFirstRowIndex
    {
        get => (Flags & 2) != 0;
        set => Flags = value ? (byte)(Flags | 2) : (byte)(Flags & ~2);
    }

    internal bool HasRowCount
    {
        get => (Flags & 4) != 0;
        set => Flags = value ? (byte)(Flags | 4) : (byte)(Flags & ~4);
    }

    internal bool HasNullPage
    {
        get => (Flags & 8) != 0;
        set => Flags = value ? (byte)(Flags | 8) : (byte)(Flags & ~8);
    }

    internal bool IsNullPage
    {
        get => (Flags & 16) != 0;
        set => Flags = value ? (byte)(Flags | 16) : (byte)(Flags & ~16);
    }
}
