using Plank.Reading;
using Plank.Reading.Logical;
using Plank.Reading.Logical.Internal;

namespace Plank.Tests.Reading;

internal sealed class LegacyBitPackedReaderTests
{
    const string BinaryFixture =
        "UEFSMRUAFYQBFYQBLBUYFQAVBhUIHDYAKAELGAEAAAAAAgAAABgBAQAAAAABAAAAAQEAAAACAQAAAAMBAAAA" +
        "BAEAAAAFAQAAAAYBAAAABwEAAAAIAQAAAAkBAAAACgEAAAALFQIZLEgJZm9vLkV2ZW50FQIAFQwlAhgDZm9v" +
        "VQIAFhgZHBkcJggcFQwZNQYIABkYA2ZvbxUAFhgWvgEWvgEmCDw2ACgBCxgBAAAZHBUAFQAVAgAAABa+ARYY" +
        "ABk8GBhwYXJxdWV0LnByb3RvLmRlc2NyaXB0b3IYXW5hbWU6ICJFdmVudCIKZmllbGQgewogIG5hbWU6ICJm" +
        "b28iCiAgbnVtYmVyOiAxCiAgbGFiZWw6IExBQkVMX09QVElPTkFMCiAgdHlwZTogVFlQRV9CWVRFUwp9CgAY" +
        "EXdyaXRlci5tb2RlbC5uYW1lGAhwcm90b2J1ZgAYE3BhcnF1ZXQucHJvdG8uY2xhc3MYFGZvby5iYXouRm9v" +
        "YmF6JEV2ZW50ABhKcGFycXVldC1tciB2ZXJzaW9uIDEuMTAuMCAoYnVpbGQgMDMxYTY2NTQwMDllM2I4MjAy" +
        "MDAxMmExODQzNGM1ODJiZDc0YzczYSkZHBwAAABzAQAAUEFSMQ==";

    [Test]
    public async Task ReadsLegacyBitPackedDefinitionLevels()
    {
        using var stream = new MemoryStream(Convert.FromBase64String(BinaryFixture));
        using var reader = new ParquetReader();
        reader.Reset(stream);

        var values = ReadAll(reader.RowGroups[0].Column<byte>(reader.Schema.LeafColumns[0]));

        await Assert.That(values)
            .IsEquivalentTo(Enumerable.Range(0, 12).Select(static value => (byte)value).ToArray());
    }

    [Test]
    public async Task DecodesValuesMostSignificantBitFirst()
    {
        int[] values = new int[8];

        LegacyBitPackedDecoder.Decode([0x05, 0x39, 0x77], bitWidth: 3, values);

        await Assert.That(values).IsEquivalentTo(Enumerable.Range(0, 8).ToArray());
    }

    [Test]
    public async Task DecodesDefinitionLevelPattern()
    {
        int[] values = new int[10];

        LegacyBitPackedDecoder.Decode([0b1011_0010, 0b1100_0000], bitWidth: 1, values);

        await Assert.That(values).IsEquivalentTo([1, 0, 1, 1, 0, 0, 1, 0, 1, 1]);
    }

    [Test]
    public async Task RejectsTruncatedPayload()
    {
        var exception = Assert.Throws<CorruptParquetException>(() =>
            LegacyBitPackedDecoder.Decode([0xFF], bitWidth: 3, new int[3]));

        await Assert.That(exception.Message).Contains("too short");
    }

    static byte[] ReadAll(RowGroupColumn<byte> column)
    {
        var values = new List<byte>();
        foreach (var buffer in column)
            values.AddRange(buffer.Values);
        return values.ToArray();
    }
}
