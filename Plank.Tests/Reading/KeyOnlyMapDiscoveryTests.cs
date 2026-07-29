using System.Buffers.Binary;
using Plank.Reading.Logical;
using Plank.Schema;

namespace Plank.Tests.Reading;

internal sealed class KeyOnlyMapDiscoveryTests
{
    [Test]
    public void ValidKeyOnlyMapSchemaCanBeReadAsASet()
    {
        using var stream = new MemoryStream(CreateFile());
        using var reader = new ParquetReader();

        reader.Reset(stream);

        var map = reader.Schema.Definitions.Single();
        if (map.Kind != NodeKind.Map ||
            map.Repetition != ParquetRepetition.Required ||
            map.Children is not
            [
                {
                    Name: "key",
                    Kind: NodeKind.Leaf,
                    Repetition: ParquetRepetition.Required,
                    PhysicalType: ParquetPhysicalType.Int32
                }
            ])
            throw new InvalidOperationException("The valid key-only MAP schema was not preserved as a set.");
    }

    static byte[] CreateFile()
    {
        byte[] footer =
        [
            0x15, 0x02,
            0x19, 0x4C,
            0x48, 0x06, (byte)'s', (byte)'c', (byte)'h', (byte)'e', (byte)'m', (byte)'a',
            0x15, 0x02,
            0x00,
            0x35, 0x00,
            0x18, 0x04, (byte)'t', (byte)'a', (byte)'g', (byte)'s',
            0x15, 0x02,
            0x15, 0x02,
            0x4C,
            0x4C, 0x00,
            0x00,
            0x00,
            0x35, 0x04,
            0x18, 0x09, (byte)'k', (byte)'e', (byte)'y', (byte)'_', (byte)'v', (byte)'a', (byte)'l', (byte)'u',
            (byte)'e',
            0x15, 0x02,
            0x00,
            0x15, 0x02,
            0x25, 0x00,
            0x18, 0x03, (byte)'k', (byte)'e', (byte)'y',
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
