using Plank.Schema;

namespace Plank.Tests.E2E;

[ParquetSchema(AllowAllocatingValues = true)]
internal sealed partial class GeneratedNullableIntegerSchema
{
    public byte?[]? ByteArray { get; set; }
    public List<byte?>? ByteList { get; set; }
    public Dictionary<uint, byte?>? ByteMap { get; set; }
    public byte?[][]? ByteMatrix { get; set; }
    public List<byte> RequiredBytes { get; set; } = [];

    public ushort?[]? UInt16Array { get; set; }
    public List<ushort?>? UInt16List { get; set; }
    public Dictionary<uint, ushort?>? UInt16Map { get; set; }
    public ushort?[][]? UInt16Matrix { get; set; }
    public List<ushort> RequiredUInt16s { get; set; } = [];

    public uint?[]? UInt32Array { get; set; }
    public List<uint?>? UInt32List { get; set; }
    public Dictionary<uint, uint?>? UInt32Map { get; set; }
    public uint?[][]? UInt32Matrix { get; set; }
    public List<uint> RequiredUInt32s { get; set; } = [];

    public ulong?[]? UInt64Array { get; set; }
    public List<ulong?>? UInt64List { get; set; }
    public Dictionary<uint, ulong?>? UInt64Map { get; set; }
    public ulong?[][]? UInt64Matrix { get; set; }
    public List<ulong> RequiredUInt64s { get; set; } = [];

    public int?[]? Int32Array { get; set; }
    public long?[]? Int64Array { get; set; }
}
