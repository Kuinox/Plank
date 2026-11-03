using Plank.Reading;

namespace Plank.Tests.Reading;

internal sealed class PageHeaderValidationTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public void MissingOrWrongTypeRequiredFieldIsRejected(bool v2, bool wrongType)
    {
        byte[] header = v2
            ? [0x15, 0x06, 0x15, 0x02, 0x15, 0x02, 0x5c,
                0x15, 0x02, 0x15, 0x00, 0x15, 0x02, 0x15, 0x00, 0x15, 0x00, 0x15, 0x00, 0x00, 0x00]
            : [0x15, 0x00, 0x15, 0x02, 0x15, 0x02, 0x2c,
                0x15, 0x02, 0x15, 0x00, 0x15, 0x06, 0x15, 0x06, 0x00, 0x00];
        var fieldOffset = header.Length - 4;
        header[fieldOffset] = wrongType ? (byte)0x11 : (byte)0x35;
        if (wrongType)
            header = [.. header.AsSpan(0, fieldOffset + 1), 0x00, 0x00];
        Assert.Throws<CorruptParquetException>(() => PageHeaderReader.Read(header));
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public void RejectsMissingValueEncoding(bool v2)
    {
        byte[] header = v2
            ? [0x15, 0x06, 0x15, 0x02, 0x15, 0x02, 0x5c,
                0x15, 0x0a, 0x15, 0x00, 0x15, 0x0a, 0x25, 0x00, 0x15, 0x00, 0x00, 0x00]
            : [0x15, 0x00, 0x15, 0x02, 0x15, 0x02, 0x2c,
                0x15, 0x0a, 0x25, 0x06, 0x15, 0x06, 0x00, 0x00];
        Assert.Throws<CorruptParquetException>(() => PageHeaderReader.Read(header));
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public void RejectsStatisticsWithWrongCompactType(bool exactness)
    {
        byte[] header = [0x15, 0x00, 0x15, 0x02, 0x15, 0x02, 0x2c,
            0x15, 0x02, 0x15, 0x00, 0x15, 0x06, 0x15, 0x06, 0x1c,
            exactness ? (byte)0x77 : (byte)0x57, exactness ? (byte)0x01 : (byte)0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        Assert.Throws<CorruptParquetException>(() => PageHeaderReader.Read(header));
    }

    [Test]
    public void RejectsEmptyPageHeader()
        => Assert.Throws<CorruptParquetException>(() => PageHeaderReader.Read([0x00]));

    [Test]
    public void RejectsDictionaryHeaderWithWrongCompactType()
    {
        byte[] header = [0x15, 0x00, 0x15, 0x02, 0x15, 0x02, 0x47, 0x11, 0x06, 0x00, 0x00];
        Assert.Throws<CorruptParquetException>(() => PageHeaderReader.Read(header));
    }

    [Test]
    public void RejectsWrongDictionaryEncodingWireType()
    {
        byte[] header = [0x15, 0x04, 0x15, 0x18, 0x15, 0x18, 0x4c, 0x15, 0x06, 0x11, 0x00, 0x00];
        Assert.Throws<CorruptParquetException>(() => PageHeaderReader.Read(header));
    }

    [Test]
    public void DictionaryEncodingPolicyIsNotEnforcedByHeaderParser()
    {
        byte[] header = [0x15, 0x04, 0x15, 0x18, 0x15, 0x18, 0x4c, 0x15, 0x06, 0x15, 0x10, 0x00, 0x00];
        if (PageHeaderReader.Read(header).ValueCount != 3)
            throw new InvalidOperationException("Dictionary value count was not read.");
    }

    [Test]
    public void RejectsMissingDictionaryEncoding()
    {
        byte[] header = [0x15, 0x04, 0x15, 0x18, 0x15, 0x18, 0x4c, 0x15, 0x06, 0x00, 0x00];
        Assert.Throws<CorruptParquetException>(() => PageHeaderReader.Read(header));
    }

    [Test]
    public void RejectsWrongDictionaryCountType()
    {
        byte[] header = [0x15, 0x04, 0x15, 0x18, 0x15, 0x18, 0x4c, 0x11, 0x15, 0x00, 0x00, 0x00];
        Assert.Throws<CorruptParquetException>(() => PageHeaderReader.Read(header));
    }
}
