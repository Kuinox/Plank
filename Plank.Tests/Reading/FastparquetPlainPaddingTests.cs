using Plank.Reading;
using Plank.Reading.Logical;

namespace Plank.Tests.Reading;

internal sealed class FastparquetPlainPaddingTests
{
    // fastparquet 2026.9.0, native DataPageV1, uncompressed; var=[b'x', None, b'', b'az'],
    // fixed=[b'xy', b'12', b'zz', b'ab'] with fixed_text={"fixed": 2} and has_nulls=["var"].
    const string Fixture =
        "UEFSMRUAFToVOiwVCBUAFQYVCAAAAgAAAAMNAQAAAHgAAAAAAgAAAGF6AAAAAAAAAAAVABUgFSAsFQgVABUGFQgAAHh5MTJ6emFi" +
        "AAAAAAAAAAAVAhk8SAZzY2hlbWEVBAAVDCUCGAN2YXIAFQ4VBBUAGAVmaXhlZAAWCBkcGSwmCBwVDBkVABkYA3ZhchUAFggWXBZc" +
        "GQAWCDw2AgAZHBUAFQAVAgAAACZkHBUOGRUAGRgFZml4ZWQVABYIFkIWQhkAFmQ8NgAAGRwVABUAFQIAAAAWngEWCAAZHBgGcGFu" +
        "ZGFzGOcDeyJjb2x1bW5faW5kZXhlcyI6IFt7ImZpZWxkX25hbWUiOiBudWxsLCAibWV0YWRhdGEiOiBudWxsLCAibmFtZSI6IG51" +
        "bGwsICJudW1weV90eXBlIjogInN0ciIsICJwYW5kYXNfdHlwZSI6ICJtaXhlZC1pbnRlZ2VyIn1dLCAiY29sdW1ucyI6IFt7ImZp" +
        "ZWxkX25hbWUiOiAidmFyIiwgIm1ldGFkYXRhIjogbnVsbCwgIm5hbWUiOiAidmFyIiwgIm51bXB5X3R5cGUiOiAib2JqZWN0Iiwg" +
        "InBhbmRhc190eXBlIjogImJ5dGVzIn0sIHsiZmllbGRfbmFtZSI6ICJmaXhlZCIsICJtZXRhZGF0YSI6IG51bGwsICJuYW1lIjog" +
        "ImZpeGVkIiwgIm51bXB5X3R5cGUiOiAib2JqZWN0IiwgInBhbmRhc190eXBlIjogImJ5dGVzIn1dLCAiY3JlYXRvciI6IHsibGli" +
        "cmFyeSI6ICJmYXN0cGFycXVldCIsICJ2ZXJzaW9uIjogIjIwMjYuOS4wIn0sICJpbmRleF9jb2x1bW5zIjogW10sICJwYW5kYXNf" +
        "dmVyc2lvbiI6ICIzLjEuMSIsICJwYXJ0aXRpb25fY29sdW1ucyI6IFtdfQAYLWZhc3RwYXJxdWV0LXB5dGhvbiB2ZXJzaW9uIDIw" +
        "MjYuOS4wIChidWlsZCAwKQCsAgAAUEFSMQ==";

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public void ReadsFastparquetV1PlainBinaryWithZeroPadding(bool borrow)
    {
        using var source = new MemoryReadSource(Convert.FromBase64String(Fixture));
        using var reader = new ParquetReader(new ParquetReaderOptions { BorrowBinaryValues = borrow });
        reader.Reset(source);
        if (!Read(reader.RowGroups[0], 0).SequenceEqual(["78", null, "", "617A"]) ||
            !Read(reader.RowGroups[0], 1).SequenceEqual(["7879", "3132", "7A7A", "6162"]))
            throw new InvalidOperationException("Decoded values do not match the Fastparquet fixture.");
    }

    [Test]
    [Arguments(49, (byte)1)] // Last padding byte; a nonzero suffix must still fail.
    [Arguments(12, (byte)6)] // Page declares three values, but its chunk declares four.
    [Arguments(27, (byte)99)] // First length prefix; cannot be satisfied by available data.
    [Arguments(-1, (byte)'X')] // A different producer cannot use the compatibility exception.
    public void RejectsOtherPlainBinaryTrailingData(int offset, byte replacement)
    {
        var file = Convert.FromBase64String(Fixture);
        if (offset < 0)
            offset = file.AsSpan().IndexOf("fastparquet-python version "u8);
        file[offset] = replacement;
        using var source = new MemoryReadSource(file);
        using var reader = new ParquetReader();
        reader.Reset(source);
        Assert.Throws<CorruptParquetException>(() => Read(reader.RowGroups[0], 0));
    }

    static List<string?> Read(RowGroup group, int ordinal)
    {
        var result = new List<string?>();
        foreach (var buffer in group.Column<byte>(ordinal))
            for (var i = 0; i < buffer.Count; i++)
                result.Add(buffer.IsNull(i) ? null : Convert.ToHexString(buffer.GetValue(i)));
        return result;
    }
}
