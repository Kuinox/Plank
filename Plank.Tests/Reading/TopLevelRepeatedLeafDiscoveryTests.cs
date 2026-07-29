using System.Buffers.Binary;
using Plank.Reading.Logical;
using Plank.Schema;

namespace Plank.Tests.Reading;

internal sealed class TopLevelRepeatedLeafDiscoveryTests
{
    [Test]
    public void TopLevelRepeatedLeafRemainsRepeatedAfterSchemaDiscovery()
    {
        using var stream = new MemoryStream(CreateFile());
        using var reader = new ParquetReader();

        reader.Reset(stream);

        var definition = reader.Schema.Definitions.Single();
        var leaf = reader.Schema.LeafColumns.Single();
        if (definition.Repetition != ParquetRepetition.Repeated)
            throw new InvalidOperationException("The physical repeated definition was not preserved.");
        if (leaf.Options.Repetition != ParquetRepetition.Repeated)
            throw new InvalidOperationException(
                $"Expected the flattened leaf to remain Repeated, got {leaf.Options.Repetition}.");
    }

    static byte[] CreateFile()
    {
        byte[] footer =
        [
            0x15, 0x02,
            0x19, 0x2C,
            0x48, 0x06, (byte)'s', (byte)'c', (byte)'h', (byte)'e', (byte)'m', (byte)'a',
            0x15, 0x02,
            0x00,
            0x15, 0x02,
            0x25, 0x04,
            0x18, 0x06, (byte)'v', (byte)'a', (byte)'l', (byte)'u', (byte)'e', (byte)'s',
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
