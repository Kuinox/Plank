using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Writer;

internal sealed class ConverterFailureTests
{
    [Test]
    public async Task RetryingAfterConverterFailurePreservesLatestRowGroup()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int32,
                converter: new ThrowingConverter())
        ]);
        using var initial = new MemoryStream();
        using (var writer = schema.CreateWriter(initial))
        {
            var column = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);
            column.Serialize([7]);
            writer.StartRowGroup().Write(column);
            writer.CloseFile();
        }

        using var output = new MemoryStream();
        output.Write(initial.ToArray());
        output.Position = 0;
        using (var appender = schema.CreateAppender(output, new ParquetAppendOptions { AppendToLatestRowGroup = true }))
        {
            var column = appender.CreateSerializedColumn<int>(schema.LeafColumns[0]);
            Assert.Throws<InvalidDataException>(() => column.Serialize([-1]));
            column.Serialize([42]);
            appender.StartRowGroup().Write(column);
            appender.CloseFile();
        }

        using var reader = schema.CreateReader(new MemoryStream(output.ToArray()));
        await Assert.That(reader.RowGroups[0].RowCount).IsEqualTo(2UL);
        var values = new List<int>();
        foreach (var buffer in reader.RowGroups[0].Column<int>(0))
            values.AddRange(buffer.Values);
        await Assert.That(values.SequenceEqual([7, 42])).IsTrue();
    }

    sealed class ThrowingConverter : ParquetValueConverter<int, int>
    {
        public override int ConvertToPhysical(int value)
            => value < 0 ? throw new InvalidDataException("Rejected application value.") : value;

        public override int ConvertFromPhysical(int value) => value;
    }
}
