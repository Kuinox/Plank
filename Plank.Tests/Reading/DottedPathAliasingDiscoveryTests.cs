using System.Buffers.Binary;
using Plank.Reading.Logical;

namespace Plank.Tests.Reading;

internal sealed class DottedPathAliasingDiscoveryTests
{
    [Test]
    public void LiteralDottedSegmentDoesNotAliasNestedColumnPath()
    {
        using var stream = new MemoryStream(CreateFile());
        using var reader = new ParquetReader();

        reader.Reset(stream);

        if (reader.Schema.LeafColumns.Length != 2)
            throw new InvalidOperationException(
                $"Expected two distinct physical columns, got {reader.Schema.LeafColumns.Length}.");
    }

    static byte[] CreateFile()
    {
        byte[] footer =
        [
            0x15, 0x02,
            0x19, 0x4C,
            0x48, 0x06, (byte)'s', (byte)'c', (byte)'h', (byte)'e', (byte)'m', (byte)'a',
            0x15, 0x04,
            0x00,
            0x15, 0x02,
            0x25, 0x00,
            0x18, 0x03, (byte)'a', (byte)'.', (byte)'b',
            0x00,
            0x35, 0x00,
            0x18, 0x01, (byte)'a',
            0x15, 0x02,
            0x00,
            0x15, 0x02,
            0x25, 0x00,
            0x18, 0x01, (byte)'b',
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
