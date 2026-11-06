using Plank.Reading;
using Plank.Reading.Logical;
using Plank.Reading.Logical.Internal;

namespace Plank.Tests.Reading;

internal sealed class DeltaTruncatedBlockHeaderTests
{
    const string Fixture = "UEFSMRUGFfYBFfYBXBUKFQAVChUKFQQVABIcGAT/WyYFGAQAAAAAFgAoBP9bJgUYBAAAAAAREQAAAAoBgAEEBQCt//////////8AAAAAAAAAAAAA/////////////+CyUhscAAEbHAAAAIE8CP///wAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABkRAhkYBAAAAAAZGAT/WyYFFQAZFgAAGRwWCBXiAhYAAAAVAhksSAZzY2hlbWEVAgAVAiUCGAFjJQ5MfBIcHAAAAAAAFgoZHBkcJgAcFQIZJQoGGRgBYxUAFgoW4gIW4gImCDwYBP9bJgUYBAAAAAAWACgE/1smBRgEAAAAABERAAAWmAMVFhbqAhUuABbiAhYKJggW4gIAORwcAAAAfAAAAFBBUjE=";

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
