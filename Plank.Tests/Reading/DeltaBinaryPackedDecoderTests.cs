using Plank.Reading;
using Plank.Writing;
using Plank.Writing.Encoding;

namespace Plank.Tests.Reading;

internal sealed class DeltaBinaryPackedDecoderTests
{
    [Test]
    public void ReadConsumedByteCountIncludesPaddedMiniBlockBytes()
    {
        var values = new[] { 4, 11, 3, 18, 2 };
        var writer = new BufferWriter(DefaultParquetBufferPool.Shared, 1024, 1024);
        DeltaBinaryPackedEncoding.WriteInt32(values, ref writer);

        var payload = new byte[writer.WrittenLength + 3];
        writer.CopyTo(payload);
        payload[^3] = 0xAA;
        payload[^2] = 0xBB;
        payload[^1] = 0xCC;

        var decoded = DeltaBinaryPackedDecoder.ReadInt32(payload);
        var consumed = DeltaBinaryPackedDecoder.ReadConsumedByteCount(payload);

        if (!decoded.SequenceEqual(values))
            throw new InvalidOperationException("Delta binary packed values did not round-trip.");
        if (consumed != writer.WrittenLength)
            throw new InvalidOperationException(
                $"Expected consumed byte count {writer.WrittenLength}, got {consumed}.");
    }
}
