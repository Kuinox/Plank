using Plank.Reading;

namespace Plank.Tests.Reading;

internal sealed class CompactProtocolVarintDiscoveryTests
{
    [Test]
    public void UInt32VarintRejectsBitsBeyondItsWidth()
        => Assert.Throws<CorruptParquetException>(ReadOverflowingUInt32);

    [Test]
    public void UInt64VarintRejectsBitsBeyondItsWidth()
        => Assert.Throws<CorruptParquetException>(ReadOverflowingUInt64);

    [Test]
    public void UInt32VarintAcceptsMaximumValue()
    {
        var reader = new CompactProtocolReader([0xff, 0xff, 0xff, 0xff, 0x0f]);

        if (reader.ReadVarU32() != uint.MaxValue)
            throw new InvalidOperationException("The maximum UInt32 varint did not decode correctly.");
    }

    [Test]
    public void UInt64VarintAcceptsMaximumValue()
    {
        var reader = new CompactProtocolReader(
            [0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x01]);

        if (reader.ReadI64() != long.MinValue)
            throw new InvalidOperationException("The maximum UInt64 varint did not decode correctly.");
    }

    static void ReadOverflowingUInt32()
    {
        var reader = new CompactProtocolReader([0xff, 0xff, 0xff, 0xff, 0x7f]);
        _ = reader.ReadVarU32();
    }

    static void ReadOverflowingUInt64()
    {
        var reader = new CompactProtocolReader(
            [0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x7f]);
        _ = reader.ReadI64();
    }
}
