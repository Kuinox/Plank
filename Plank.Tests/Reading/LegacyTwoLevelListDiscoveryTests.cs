using System.Buffers.Binary;
using Plank.Reading.Logical;
using Plank.Schema;

namespace Plank.Tests.Reading;

internal sealed class LegacyTwoLevelListDiscoveryTests
{
    [Test]
    public void ValidLegacyTwoLevelListSchemaCanBeRead()
    {
        using var stream = new MemoryStream(CreateFile());
        using var reader = new ParquetReader();

        reader.Reset(stream);

        var list = reader.Schema.Definitions.Single();
        if (list.Kind != NodeKind.List ||
            list.Repetition != ParquetRepetition.Optional ||
            list.Children is not [{ Kind: NodeKind.Leaf, PhysicalType: ParquetPhysicalType.Int32 }])
            throw new InvalidOperationException("The valid legacy two-level LIST schema was not preserved.");
    }

    static byte[] CreateFile()
    {
        byte[] footer =
        [
            0x15, 0x02,
            0x19, 0x3C,
            0x48, 0x06, (byte)'s', (byte)'c', (byte)'h', (byte)'e', (byte)'m', (byte)'a',
            0x15, 0x02,
            0x00,
            0x35, 0x02,
            0x18, 0x06, (byte)'v', (byte)'a', (byte)'l', (byte)'u', (byte)'e', (byte)'s',
            0x15, 0x02,
            0x15, 0x06,
            0x4C,
            0x3C, 0x00,
            0x00,
            0x00,
            0x15, 0x02,
            0x25, 0x04,
            0x18, 0x07, (byte)'e', (byte)'l', (byte)'e', (byte)'m', (byte)'e', (byte)'n', (byte)'t',
            0x00,
            0x16, 0x00,
            0x19, 0x0C,
            0x00
        ];

        using var stream = new MemoryStream();
        stream.Write("PAR1"u8);
        stream.Write(footer);
        Span<byte> footerLength = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(footerLength, checked((uint)footer.Length));
        stream.Write(footerLength);
        stream.Write("PAR1"u8);
        return stream.ToArray();
    }
}
