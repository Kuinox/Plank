using System.Buffers.Binary;
using Plank.Reading;
using Plank.Reading.Logical;

namespace Plank.Tests.Reading;

internal sealed class TemporalUnitValidationDiscoveryTests
{
    [Test]
    public void TimestampWithoutUnitIsRejected()
        => AssertRejected(CreateFile(unknownUnit: false), "A timestamp without a unit");

    [Test]
    public void TimestampWithUnknownUnitIsRejected()
        => AssertRejected(CreateFile(unknownUnit: true), "A timestamp with an unknown unit");

    static void AssertRejected(byte[] file, string description)
    {
        using var stream = new MemoryStream(file);
        using var reader = new ParquetReader();

        try
        {
            reader.Reset(stream);
        }
        catch (CorruptParquetException)
        {
            return;
        }
        catch (NotSupportedException)
        {
            return;
        }

        var logicalType = reader.Schema.Definitions.Single().LogicalType;
        throw new InvalidOperationException($"{description} was accepted as '{logicalType}'.");
    }

    static byte[] CreateFile(bool unknownUnit)
    {
        using var footerStream = new MemoryStream();
        footerStream.Write([
            0x15, 0x02,
            0x19, 0x2C,
            0x48, 0x06, (byte)'s', (byte)'c', (byte)'h', (byte)'e', (byte)'m', (byte)'a',
            0x15, 0x02,
            0x00,
            0x15, 0x04,
            0x25, 0x00,
            0x18, 0x02, (byte)'t', (byte)'s',
            0x6C,
            0x8C,
            0x11
        ]);
        if (unknownUnit)
            footerStream.Write([
                0x1C,
                0x4C, 0x00,
                0x00
            ]);
        footerStream.Write([
            0x00,
            0x00,
            0x00,
            0x16, 0x00,
            0x19, 0x0C,
            0x00
        ]);
        var footer = footerStream.ToArray();

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
