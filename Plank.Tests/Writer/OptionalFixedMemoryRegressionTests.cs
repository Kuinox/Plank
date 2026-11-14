using Plank.BloomFilters;
using Plank.Reading;
using Plank.Reading.Logical.Internal;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Writer;

internal sealed class OptionalFixedMemoryRegressionTests
{
    [Test]
    [Arguments(ParquetDataPageVersion.V1)]
    [Arguments(ParquetDataPageVersion.V2)]
    public void OptionalFixedMemoryRoundTripsWithoutLengthPrefixes(ParquetDataPageVersion pageVersion)
    {
        var schema = new ParquetSchema([
            ColumnDefinition.OptionalLeaf("value", ParquetPhysicalType.FixedLenByteArray,
                new ColumnOptions(typeLength: 4, encodings: [EncodingKind.Plain]))
        ]);
        using var output = new MemoryStream();
        using (var writer = schema.CreateWriter(output, new ParquetWriterOptions
               { Compression = CompressionKind.None, DataPageVersion = pageVersion }))
        {
            var column = writer.CreateSerializedColumn<ReadOnlyMemory<byte>?>(schema.LeafColumns[0]);
            column.Serialize([new byte[] { 0x10, 0x20, 0x30, 0x40 }, null, new byte[] { 1, 2, 3, 4 }]);
            writer.StartRowGroup().Write(column);
            writer.CloseFile();
        }
        using var reader = schema.CreateReader(new MemoryStream(output.ToArray()));
        var values = new List<byte[]?>();
        foreach (var buffer in reader.RowGroups[0].Column<byte>(0))
            for (var i = 0; i < buffer.Count; i++)
                values.Add(buffer.IsNull(i) ? null : buffer.GetValue(i).ToArray());
        if (values.Count != 3 || values[1] is not null ||
            !values[0]!.AsSpan().SequenceEqual(new byte[] { 0x10, 0x20, 0x30, 0x40 }) ||
            !values[2]!.AsSpan().SequenceEqual(new byte[] { 1, 2, 3, 4 }))
            throw new InvalidOperationException("Fixed-length memory values did not round trip.");
    }
}
