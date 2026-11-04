using Plank.Reading;
using Plank.Reading.Physical;

namespace Plank.Tests.Reading;

internal sealed class ColumnChunkConsistencyTests
{
    const string TypeMismatch = "UEFSMRUGFVAVNFwVChUAFQoVABUAFQAxHBgIAAAAAAAA+D8YCAAAAAAAAPD/FgAoCAAAAAAAAPg/GAgAAAAAAADw/xERFgIAAAAoAAAyAQAE+D8JDwT4vw0IIP8AAAAAAADw/xkRAhkYCAAAAAAAAPD/GRgIAAAAAAAAAD8VABkWAAAZHBYIFcABFgAAABUCGSxIBnNjaGVtYRUCABUIJQAYAWMAFgoZHBkcJgAcFQoZFQAZGAFjFQIWChbcARbAASYIPBgIAAAAAAAA+D8YCAAAAAAAAPD/FgAoCAAAAAAAAPg/GAgAAAAAAADw/xERFgIAABaGAhUWFsgBFT4AFtwBFgomCBbAAQA5HBwAAACCAAAAUEFSMQ==";
    const string RepeatedZero = "UEFSMRUGFTgVOFwVBhUAFQQVABUIFQwSHBgDAwMDGAEBFgAoAwMDAxgBARERAAAAAgACAQIABAECAQEAAAABAgAAAAICAwAAAAMDAxkRAhkYAQEZGAMDAwMVABkWAAAZHBYIFZABFgAAABUCGSxIBnNjaGVtYRUCABUMJQQYAWMAFgQZHBkcJgAcFQwZJQAGGRgBYxUAFgAWkAEWkAEmCDwYAwMDAxgBARYAKAMDAwMYAQEREQAAFr4BFRYWmAEVJgAWkAEWBCYIFpABADkcHAAAAGkAAABQQVIx";

    [Test]
    public void RejectsPhysicalTypeMismatch()
    {
        using var reader = new ParquetFileReader();
        var error = Assert.Throws<CorruptParquetException>(() => reader.Reset(new MemoryStream(Convert.FromBase64String(TypeMismatch))));
        if (!error.Message.Contains("physical type", StringComparison.Ordinal))
            throw new InvalidOperationException(error.Message);
    }

    [Test]
    public void RejectsRepeatedCountBelowRowCount()
    {
        using var reader = new ParquetFileReader();
        var error = Assert.Throws<CorruptParquetException>(() => reader.Reset(new MemoryStream(Convert.FromBase64String(RepeatedZero))));
        if (!error.Message.Contains("Repeated column", StringComparison.Ordinal))
            throw new InvalidOperationException(error.Message);
    }

    [Test]
    [Arguments(2)]
    [Arguments(4)]
    public void RejectsPageCountDifferentFromRepeatedChunkCount(int count)
    {
        using var reader = OpenRepeated(count);
        using var cursor = reader.OpenPages(0, 0);
        var error = Assert.Throws<CorruptParquetException>(() => { while (cursor.MoveNext()) { } });
        if (!error.Message.Contains("Data pages", StringComparison.Ordinal))
            throw new InvalidOperationException(error.Message);
    }

    [Test]
    public void AcceptsMatchingRepeatedCount()
    {
        using var reader = OpenRepeated(3);
        using var cursor = reader.OpenPages(0, 0);
        var pages = 0;
        while (cursor.MoveNext())
            pages++;
        if (pages != 1)
            throw new InvalidOperationException($"Expected one page, got {pages}.");
    }

    static ParquetFileReader OpenRepeated(int count)
    {
        var file = Convert.FromBase64String(RepeatedZero);
        // ColumnMetaData.num_values: change the one-byte zigzag value, leaving two rows and three page entries.
        if (file[0x96] != 0x16 || file[0x97] != 0)
            throw new InvalidOperationException("Unexpected fixture layout.");
        file[0x97] = checked((byte)(count * 2));
        var reader = new ParquetFileReader();
        reader.Reset(new MemoryStream(file));
        return reader;
    }
}
