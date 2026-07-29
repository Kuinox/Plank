using System.Buffers.Binary;
using Plank.Reading;
using Plank.Reading.Logical;

namespace Plank.Tests.Reading;

internal sealed class TemporalUnitValidationDiscoveryTests
{
    [Test]
    public void TimestampWithoutUnitIsRejectedAsCorruptData()
        => AssertRejected<CorruptParquetException>(CreateFile(UnitEncoding.Missing));

    [Test]
    public void TimestampWithEmptyUnitIsRejectedAsCorruptData()
        => AssertRejected<CorruptParquetException>(CreateFile(UnitEncoding.Empty));

    [Test]
    public void TimestampWithUnknownUnitIsRejectedAsUnsupported()
        => AssertRejected<NotSupportedException>(CreateFile(UnitEncoding.Unknown));

    [Test]
    public void TimestampWithMultipleUnitsIsRejectedAsCorruptData()
        => AssertRejected<CorruptParquetException>(CreateFile(UnitEncoding.Multiple));

    [Test]
    public void TimestampWithoutAdjustedToUtcIsRejectedAsCorruptData()
        => AssertRejected<CorruptParquetException>(
            CreateFile(UnitEncoding.Millis, AdjustedEncoding.Missing));

    [Test]
    public void TimestampWithDuplicateAdjustedToUtcIsRejectedAsCorruptData()
        => AssertRejected<CorruptParquetException>(
            CreateFile(UnitEncoding.Millis, AdjustedEncoding.Duplicate));

    static void AssertRejected<TException>(byte[] file)
        where TException : Exception
    {
        using var stream = new MemoryStream(file);
        using var reader = new ParquetReader();

        var exception = Assert.Throws<TException>(() => reader.Reset(stream));
        if (exception.GetType() != typeof(TException))
            throw new InvalidOperationException(
                $"Expected exact exception type {typeof(TException)}, got {exception.GetType()}.");
    }

    static byte[] CreateFile(UnitEncoding unitEncoding,
        AdjustedEncoding adjustedEncoding = AdjustedEncoding.Present)
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
            0x8C
        ]);
        if (adjustedEncoding != AdjustedEncoding.Missing)
            footerStream.WriteByte(0x11);
        if (adjustedEncoding == AdjustedEncoding.Duplicate)
            footerStream.Write([
                0x01, 0x02
            ]);

        if (unitEncoding != UnitEncoding.Missing)
            footerStream.WriteByte(adjustedEncoding == AdjustedEncoding.Missing ? (byte)0x2C : (byte)0x1C);
        if (unitEncoding == UnitEncoding.Empty)
            footerStream.WriteByte(0x00);
        else if (unitEncoding == UnitEncoding.Millis)
            footerStream.Write([
                0x1C, 0x00,
                0x00
            ]);
        else if (unitEncoding == UnitEncoding.Unknown)
            footerStream.Write([
                0x4C, 0x00,
                0x00
            ]);
        else if (unitEncoding == UnitEncoding.Multiple)
            footerStream.Write([
                0x1C, 0x00,
                0x1C, 0x00,
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

    enum UnitEncoding
    {
        Missing,
        Empty,
        Unknown,
        Millis,
        Multiple
    }

    enum AdjustedEncoding
    {
        Present,
        Missing,
        Duplicate
    }
}
