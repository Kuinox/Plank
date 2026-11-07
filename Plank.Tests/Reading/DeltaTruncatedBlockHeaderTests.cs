using Plank.Reading;
using Plank.Reading.Logical;
using Plank.Reading.Logical.Internal;

namespace Plank.Tests.Reading;

internal sealed class DeltaTruncatedBlockHeaderTests
{
    const string Fixture = "UEFSMRUGFfYBFfYBXBUKFQAVChUKFQQVABIcGAT/WyYFGAQAAAAAFgAoBP9bJgUYBAAAAAAREQAAAAoBgAEEBQCt//////////8AAAAAAAAAAAAA/////////////+CyUhscAAEbHAAAAIE8CP///wAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABkRAhkYBAAAAAAZGAT/WyYFFQAZFgAAGRwWCBXiAhYAAAAVAhksSAZzY2hlbWEVAgAVAiUCGAFjJQ5MfBIcHAAAAAAAFgoZHBkcJgAcFQIZJQoGGRgBYxUAFgoW4gIW4gImCDwYBP9bJgUYBAAAAAAWACgE/1smBRgEAAAAABERAAAWmAMVFhbqAhUuABbiAhYKJggW4gIAORwcAAAAfAAAAFBBUjE=";

    [Test]
    public void Int32MinimumDeltaVarintChecksWidthAndTruncation()
    {
        foreach (var (encoded, expected) in new (byte[], uint)[]
        {
            ([0], 0), ([0x80, 1], 128), ([0xff, 0xff, 0xff, 0xff, 0x0f], uint.MaxValue),
            ([0x80, 0x80, 0x80, 0x80, 0], 0)
        })
        {
            byte[] payload = [0x80, 1, 4, 2, 0, .. encoded, 0, 0, 0, 0];
            var values = new int[2];
            DeltaBinaryPackedDecoder.ReadInt32(payload, values);
            var delta = (int)(expected >> 1) ^ -(int)(expected & 1);
            if (values[0] != 0 || values[1] != delta ||
                DeltaBinaryPackedDecoder.GetEncodedLength(payload, 2) != payload.Length)
                throw new InvalidOperationException("Incorrect INT32 varint decoding.");
        }

        foreach (var encoded in new byte[][]
        {
            [], [0x80], [0x80, 0x80, 0x80, 0x80, 0x10],
            [0x80, 0x80, 0x80, 0x80, 0x80, 0]
        })
        {
            byte[] payload = [0x80, 1, 4, 2, 0, .. encoded];
            Assert.Throws<CorruptParquetException>(() => DeltaBinaryPackedDecoder.ReadInt32(payload, new int[2]));
            Assert.Throws<CorruptParquetException>(() => DeltaBinaryPackedDecoder.GetEncodedLength(payload, 2));
        }
    }

    [Test]
    public void Int32MinimumDeltaCannotExceedItsPhysicalWidth()
    {
        using var reader = new ParquetReader();
        reader.Reset(new MemoryStream(Convert.FromBase64String(Fixture)));
        Assert.Throws<CorruptParquetException>(() =>
        {
            foreach (var buffer in reader.RowGroups[0].Column<int?>(0))
                _ = buffer.Count;
        });
        byte[] encoded = [0x80, 1, 4, 5, 0, 0xad, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0, 0, 0, 0, 0];
        Assert.Throws<CorruptParquetException>(() => DeltaBinaryPackedDecoder.ReadInt32(encoded, new int[5]));
        Assert.Throws<CorruptParquetException>(() => DeltaBinaryPackedDecoder.ReadNullableInt32(encoded, new int?[5], true));
        Assert.Throws<CorruptParquetException>(() => DeltaBinaryPackedDecoder.ReadNarrowInt32<byte>(encoded, new byte[5]));
    }
}
