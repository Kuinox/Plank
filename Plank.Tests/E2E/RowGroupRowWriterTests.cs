using Plank.Reading;
using Plank.Writing;

namespace Plank.Tests.E2E;

internal sealed class RowGroupRowWriterTests
{
    [Test]
    [Arguments(1, 1)]
    [Arguments(4, 4)]
    [Arguments(4, 5)]
    [Arguments(1, 100)]
    public void RowGroupWriterGrowsPastInitialCapacity(int capacity, int rowCount)
    {
        var options = new ParquetWriterOptions { RowApiInitialRowCapacity = capacity };
        using var stream = new MemoryStream();
        using (var parquet = DatasetRowSchema.Schema.CreateWriter(stream, options))
        {
            var writer = DatasetRowSchema.CreateRowWriter(parquet.StartRowGroup(), options);
            for (var i = 0; i < rowCount; i++)
            {
                var row = writer.GetRow();
                row.Value = i;
                row.Path = new byte[i];
            }
            writer.Write();
            parquet.CloseFile();
        }

        using var source = new MemoryReadSource(stream.ToArray());
        using var reader = DatasetRowSchema.CreateRowReader(source);
        var index = 0;
        while (reader.MoveNext())
        {
            if (reader.Current.Value != index || reader.Current.Path.Value.Length != index)
                throw new InvalidOperationException($"Row {index} was corrupted.");
            index++;
        }
        if (index != rowCount)
            throw new InvalidOperationException($"Expected {rowCount} rows, got {index}.");
    }
}
