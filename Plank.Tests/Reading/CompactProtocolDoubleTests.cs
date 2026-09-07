using Plank.Reading;

namespace Plank.Tests.Reading;

internal sealed class CompactProtocolDoubleTests
{
    [Test]
    public void UnknownDoubleFieldDoesNotConsumeTheFollowingField()
    {
        // Unknown field 1: DOUBLE (NaN), followed by field 2: I32 = 42.
        var reader = new CompactProtocolReader([0x17, 0, 0, 0, 0, 0, 0, 0xf8, 0x7f, 0x15, 84, 0]);
        reader.BeginStruct();
        if (!reader.TryReadFieldHeader(out _, out var type, out var inlineBool))
            throw new InvalidOperationException("Missing unknown field.");
        reader.Skip(type, inlineBool);
        if (!reader.TryReadFieldHeader(out var id, out type, out _) || id != 2 ||
            type != CompactProtocolType.I32 || reader.ReadI32() != 42)
            throw new InvalidOperationException("Skipping a double lost the following field.");
        if (reader.TryReadFieldHeader(out _, out _, out _))
            throw new InvalidOperationException("Expected the end of the struct.");
    }

    [Test]
    public void TruncatedDoubleIsRejected()
        => Assert.Throws<CorruptParquetException>(SkipTruncatedDouble);

    [Test]
    public void TruncatedProbeRequestsMoreBytes()
        => Assert.Throws<CompactProtocolTruncatedException>(SkipPartialDouble);

    static void SkipTruncatedDouble()
    {
        var reader = new CompactProtocolReader(new byte[7]);
        reader.Skip(CompactProtocolType.Double);
    }

    static void SkipPartialDouble()
    {
        var reader = new CompactProtocolReader(new byte[7], bufferMayBeTruncated: true);
        reader.Skip(CompactProtocolType.Double);
    }
}
