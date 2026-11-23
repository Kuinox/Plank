using System.Buffers.Binary;
using Plank.Reading.Logical;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Writer;

internal sealed class Float16StatisticsTests
{
    [Test]
    public void FiniteFloat16BoundsUseNumericOrderAndIgnoreNan()
    {
        var schema = CreateSchema();
        var file = Write(schema, [[0x00, 0xc0], [0x00, 0x3c], [0x00, 0x7e]]); // -2, +1, NaN
        using var reader = schema.CreateReader(new MemoryStream(file, writable: false));
        var metadata = reader.RowGroups[0].GetColumnMetadata(0);
        AssertBounds(metadata.Statistics, 0xc000, 0x3c00);
        if (WriterFormatBugReproductionsTests.ReadFirstColumnNanCount(file) != 1)
            throw new InvalidOperationException("Expected nan_count=1 in the chunk statistics.");
        if (!metadata.HasColumnIndex)
            throw new InvalidOperationException("A page with finite bounds should have a column index.");
        using var pages = metadata.OpenPages();
        AssertBounds(pages[0].Statistics, 0xc000, 0x3c00);
    }

    [Test]
    public void AllNanFloat16PageOmitsBoundsAndColumnIndex()
    {
        var schema = CreateSchema();
        var file = Write(schema, [[0x00, 0x7e]]);
        using var reader = schema.CreateReader(new MemoryStream(file, writable: false));
        var metadata = reader.RowGroups[0].GetColumnMetadata(0);
        if (metadata.HasColumnIndex || !metadata.HasOffsetIndex)
            throw new InvalidOperationException("All-NaN page must have an offset index but not a column index.");
        if (!metadata.Statistics.Minimum.IsEmpty || !metadata.Statistics.Maximum.IsEmpty)
            throw new InvalidOperationException("All-NaN chunk must not advertise bounds.");
    }

    [Test]
    public void Float16ZeroBoundsUseSignedZeroConvention()
    {
        var schema = CreateSchema();
        var file = Write(schema, [[0x00, 0x00]]);
        using var reader = schema.CreateReader(new MemoryStream(file, writable: false));
        AssertBounds(reader.RowGroups[0].GetColumnMetadata(0).Statistics, 0x8000, 0x0000);
    }

    static void AssertBounds(ParquetStatistics statistics, ushort min, ushort max)
    {
        var actualMin = BinaryPrimitives.ReadUInt16LittleEndian(statistics.Minimum);
        var actualMax = BinaryPrimitives.ReadUInt16LittleEndian(statistics.Maximum);
        if (actualMin != min || actualMax != max)
            throw new InvalidOperationException($"Expected FLOAT16 bounds {min:x4}/{max:x4}, got {actualMin:x4}/{actualMax:x4}.");
    }

    static ParquetSchema CreateSchema()
        => new([ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.FixedLenByteArray,
            new ColumnOptions(typeLength: 2), new LogicalType.Float16())]);

    static byte[] Write(ParquetSchema schema, byte[][] values)
    {
        using var stream = new MemoryStream();
        using var writer = schema.CreateWriter(stream, new ParquetWriterOptions { WritePageIndexes = true });
        var column = writer.CreateSerializedColumn<byte[]>(schema.LeafColumns[0]);
        column.Serialize(values);
        writer.StartRowGroup().Write(column);
        writer.CloseFile();
        return stream.ToArray();
    }
}
