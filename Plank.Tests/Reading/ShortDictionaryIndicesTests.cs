using Plank.Reading;
using Plank.Reading.Logical;

namespace Plank.Tests.Reading;

internal sealed class ShortDictionaryIndicesTests
{
    const string Fixture = "UEFSMRUEFUAVQEwVCBUAAAAEAAAAAAAAAAQAAAABAAAABAAAAAIAAAAEAAAAAwAAABUAFRwVHCwVEBUQFQYVBhwYBAMAAAAYBAAAAAAWAigEAwAAABgEAAAAABERAAAABgAAAAUCAgAIAQIjJDkZEQIZGAQAAAAAGRgEAwAAABUAGRYCABkcFmIVehYAAAAVAhksSAZzY2hlbWEVAgAVDCUCGAFjABYQGRwZHCYAHBUMGTUAEAYZGAFjFQAWEBbUARbUASZiJggcGAQDAAAAGAQAAAAAFgIoBAMAAAAYBAAAAAAREQAAFooCFRQW3AEVLgAW1AEWECYIFtQBADkcHAAAAHQAAABQQVIx";

    [Test]
    public void DictionaryLiteralRunMustFitEvenWhenFewValuesAreNeeded()
    {
        using var reader = new ParquetReader();
        reader.Reset(new MemoryStream(Convert.FromBase64String(Fixture)));
        Assert.Throws<CorruptParquetException>(() =>
        {
            foreach (var buffer in reader.RowGroups[0].Column<byte>(0))
                _ = buffer.Count;
        });
    }
}
