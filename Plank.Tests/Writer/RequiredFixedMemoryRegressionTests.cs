using Plank.Reading;
using Plank.Reading.Logical.Internal;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Writer;

internal sealed class RequiredFixedMemoryRegressionTests
{
    [Test]
    [Arguments(ParquetPhysicalType.FixedLenByteArray, ParquetDataPageVersion.V1, EncodingKind.Plain)]
    [Arguments(ParquetPhysicalType.FixedLenByteArray, ParquetDataPageVersion.V2, EncodingKind.Plain)]
    [Arguments(ParquetPhysicalType.Int96, ParquetDataPageVersion.V1, EncodingKind.Plain)]
    [Arguments(ParquetPhysicalType.Int96, ParquetDataPageVersion.V2, EncodingKind.Plain)]
    [Arguments(ParquetPhysicalType.FixedLenByteArray, ParquetDataPageVersion.V1, null)]
    [Arguments(ParquetPhysicalType.FixedLenByteArray, ParquetDataPageVersion.V2, null)]
    [Arguments(ParquetPhysicalType.Int96, ParquetDataPageVersion.V1, null)]
    [Arguments(ParquetPhysicalType.Int96, ParquetDataPageVersion.V2, null)]
    [Arguments(ParquetPhysicalType.FixedLenByteArray, ParquetDataPageVersion.V1, EncodingKind.ByteStreamSplit)]
    [Arguments(ParquetPhysicalType.FixedLenByteArray, ParquetDataPageVersion.V2, EncodingKind.ByteStreamSplit)]
    public void RequiredFixedMemoryRoundTripsWithoutLengthPrefixes(ParquetPhysicalType physicalType,
        ParquetDataPageVersion pageVersion, EncodingKind? encoding)
    {
        // A null encoding keeps the writer default, which dictionary-encodes the values.
        var schema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("value", physicalType,
                new ColumnOptions(typeLength: physicalType == ParquetPhysicalType.FixedLenByteArray ? 4u : 0u,
                    encodings: encoding is { } kind ? [kind] : default))
        ]);
        // Two slices of one backing buffer also exercise nonzero ReadOnlyMemory offsets.
        byte[] payloads = [0, 0, 0, 0, 0, 0, 0, 0, 140, 61, 37, 0, 1, 0, 0, 0, 0, 0, 0, 0, 140, 61, 37, 0];
        var width = physicalType == ParquetPhysicalType.Int96 ? 12 : 4;
        ReadOnlyMemory<byte>[] expected = [payloads.AsMemory(0, width), payloads.AsMemory(12, width)];
        using var output = new MemoryStream();
        using (var writer = schema.CreateWriter(output, new ParquetWriterOptions
               { Compression = CompressionKind.None, DataPageVersion = pageVersion }))
        {
            var column = writer.CreateSerializedColumn<ReadOnlyMemory<byte>>(schema.LeafColumns[0]);
            column.Serialize(expected);
            writer.StartRowGroup().Write(column);
            writer.CloseFile();
        }

        using var reader = schema.CreateReader(new MemoryStream(output.ToArray()));
        var values = new List<byte[]>();
        foreach (var buffer in reader.RowGroups[0].Column<byte>(0))
            for (var i = 0; i < buffer.Count; i++)
                values.Add(buffer.GetValue(i).ToArray());
        if (values.Count != 2 || !values[0].AsSpan().SequenceEqual(expected[0].Span) || !values[1].AsSpan().SequenceEqual(expected[1].Span))
            throw new InvalidOperationException("Fixed-length memory values did not round trip.");
    }

    [Test]
    public void WrongLengthInt96MemoryReportsSameErrorAsByteArray()
    {
        var arrayError = WriteInt96([new byte[3]]);
        var memoryError = WriteInt96([new ReadOnlyMemory<byte>(new byte[3])]);
        if (arrayError != memoryError)
            throw new InvalidOperationException($"Expected '{arrayError}', got '{memoryError}'.");
    }

    static string WriteInt96<T>(T[] values) where T : notnull
    {
        var schema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int96,
                new ColumnOptions(encodings: [EncodingKind.Plain]))
        ]);
        try
        {
            using var writer = schema.CreateWriter(new MemoryStream());
            var column = writer.CreateSerializedColumn<T>(schema.LeafColumns[0]);
            column.Serialize(values);
            writer.StartRowGroup().Write(column);
            writer.CloseFile();
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }

        throw new InvalidOperationException("Writing a 3-byte INT96 value did not fail.");
    }
}
