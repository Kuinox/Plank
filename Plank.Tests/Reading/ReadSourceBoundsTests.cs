using Plank.Reading;

namespace Plank.Tests.Reading;

internal sealed class ReadSourceBoundsTests
{
    [Test]
    public void FileReadSourceRejectsDestinationLongerThanSource()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, [42]);
            using var source = new FileReadSource(path);

            Assert.Throws<CorruptParquetException>(() => source.ReadExactly(0, new byte[2]));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void StreamReadSourceRejectsDestinationLongerThanSource()
    {
        using var stream = new MemoryStream([42], writable: false);
        var source = new StreamReadSource(stream);

        Assert.Throws<CorruptParquetException>(() => source.ReadExactly(0, new byte[2]));
    }
}
