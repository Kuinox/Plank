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
