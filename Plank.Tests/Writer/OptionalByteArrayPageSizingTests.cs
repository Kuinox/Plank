using Plank.Schema;
using Plank.Writing;
using PlankParquetSchema = Plank.Schema.ParquetSchema;

namespace Plank.Tests.Writer;

/// <summary>
/// The optional byte-array page sizer hands the payload size it measured to the page writer instead of
/// letting it walk the rows again. That only holds for variable-length rows, whose measurement includes
/// the plain length prefix the writer goes on to emit - a fixed-length column measures rows without one,
/// so it has to keep counting for itself. Both shapes are round-tripped here across several pages.
/// </summary>
internal sealed class OptionalByteArrayPageSizingTests
{
    [Test]
    public void VariableLengthValuesRoundTripAcrossPages()
    {
        var values = new byte[400][];
        for (var i = 0; i < values.Length; i++)
            values[i] = i % 5 == 0 ? null! : System.Text.Encoding.UTF8.GetBytes($"value-{i:D4}-{new string('x', i % 23)}");

        var schema = new PlankParquetSchema([
            Plank.Schema.ColumnDefinition.Leaf("value", ParquetPhysicalType.ByteArray,
                new ColumnOptions(ParquetRepetition.Optional))
        ]);

        AssertRoundTrip(schema, values, 128);
    }

    [Test]
    public void FixedLengthValuesRoundTripAcrossPages()
    {
        var values = new byte[400][];
        for (var i = 0; i < values.Length; i++)
        {
            if (i % 5 == 0)
                continue;
            var value = new byte[16];
            for (var b = 0; b < value.Length; b++)
                value[b] = (byte)(i + b);
            values[i] = value;
        }

        var schema = new PlankParquetSchema([
            Plank.Schema.ColumnDefinition.Leaf("value", ParquetPhysicalType.FixedLenByteArray,
                new ColumnOptions(ParquetRepetition.Optional, typeLength: 16), new LogicalType.Uuid())
        ]);

        AssertRoundTrip(schema, values, 128);
    }

    static void AssertRoundTrip(PlankParquetSchema schema, byte[]?[] values, uint targetPageBytes)
    {
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            TargetDataPageSizeBytes = targetPageBytes
        });
        var column = writer.CreateSerializedColumn<byte[]?>(schema.LeafColumns[0]);
        column.Serialize(values);
        if (column.Pages.Count < 4)
            throw new InvalidOperationException(
                $"Expected the column to split into several pages, got {column.Pages.Count}.");

        writer.StartRowGroup().Write(column);
        writer.CloseFile();

        using var readStream = new MemoryStream(stream.ToArray(), writable: false);
        using var reader = new ParquetSharp.ParquetFileReader(readStream, leaveOpen: false);
        using var rowGroup = reader.RowGroup(0);
        using var logicalReader = rowGroup.Column(0).LogicalReader();
        var actual = (Array)logicalReader.Apply(new ReadAllVisitor(values.Length));
        for (var i = 0; i < values.Length; i++)
        {
            var read = ToBytes(actual.GetValue(i));
            if (values[i] is null)
            {
                if (read is not null)
                    throw new InvalidOperationException($"Row {i} came back non-null.");
                continue;
            }

            if (read is null || !read.AsSpan().SequenceEqual(values[i]))
                throw new InvalidOperationException($"Row {i} round-tripped wrong.");
        }
    }

    static byte[]? ToBytes(object? value)
        => value switch
        {
            null => null,
            byte[] bytes => bytes,
            Guid guid => guid.ToByteArray(bigEndian: true),
            _ => throw new InvalidOperationException($"Unexpected read value type '{value.GetType()}'.")
        };

    sealed class ReadAllVisitor(int count) : ParquetSharp.ILogicalColumnReaderVisitor<object>
    {
        public object OnLogicalColumnReader<TValue>(ParquetSharp.LogicalColumnReader<TValue> reader)
            => reader.ReadAll(count);
    }
}
