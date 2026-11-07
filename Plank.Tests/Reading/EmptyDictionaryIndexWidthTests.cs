using Plank.Reading;
using Plank.Reading.Logical;

namespace Plank.Tests.Reading;

internal sealed class EmptyDictionaryIndexWidthTests
{
    [Test]
    [Arguments("dictionary-all-null-no-width-v1.parquet")]
    [Arguments("dictionary-all-null-no-width-v2.parquet")]
    public void ReadsArrowAllNullDictionaryWithoutWidth(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Reading", "Fixtures", name);
        using var reader = new ParquetReader();
        reader.Reset(new MemoryStream(File.ReadAllBytes(path)));
        var rows = 0;
        foreach (var buffer in reader.RowGroups[0].Column<int?>(0))
            foreach (var value in buffer.Values)
            {
                if (value is not null) throw new InvalidOperationException("Expected a null value.");
                rows++;
            }
        if (rows != 12) throw new InvalidOperationException("Expected 12 null rows.");
    }

    [Test]
    [Arguments("nested-binary-width-160-a.parquet")]
    [Arguments("nested-binary-width-160-b.parquet")]
    public void RejectsInvalidWidthInAllEmptyLists(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Reading", "Fixtures", name);
        Assert.Throws<CorruptParquetException>(() =>
        {
            using var reader = new ParquetReader();
            reader.Reset(new MemoryStream(File.ReadAllBytes(path)));
            foreach (var buffer in reader.RowGroups[0].NestedColumn<byte>(0))
                _ = buffer.Values.Count;
        });
    }
}
