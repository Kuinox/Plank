using Plank.Reading;
using Plank.Reading.Logical;

namespace Plank.Tests.Reading;

internal sealed class MissingDictionaryPageTests
{
    // All definition levels are null, but the data page still declares RLE_DICTIONARY.
    const string Fixture = "UEFSMRUAFTQVMCwVChUQFQYVBhw2BCgE/////xgEAAAAABERAAAAGhgKAAAAAgACDQQsAAAAAP////85MAAAGRECGRgEAAAAABkYBP////8VABkWBAAZHBYIFXYWAAAAFQIZLEgGc2NoZW1hFQIAFQIlAhgBYyUaTKwTIBEAAAAWChkcGRwmABwVAhklAAYZGAFjFQIWChZ6FnYmCDw2BCgE/////xgEAAAAABERAAAWrAEVFBZ+FS4AFnoWCiYUFnYAORwcAAAAaQAAAFBBUjE=";

    [Test]
    public void DictionaryEncodedAllNullPageRequiresDictionary()
    {
        using var reader = new ParquetReader();
        reader.Reset(new MemoryStream(Convert.FromBase64String(Fixture)));
        Assert.Throws<CorruptParquetException>(() =>
        {
            foreach (var buffer in reader.RowGroups[0].Column<int?>(0))
                _ = buffer.Count;
        });
    }
}
