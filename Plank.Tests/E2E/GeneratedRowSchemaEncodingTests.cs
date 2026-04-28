using ParquetSharp;
using Plank.Schema;

namespace Plank.Tests.E2E;

[ParquetSchema]
public sealed partial class EncodedRowSchema
{
    [ParquetColumn("id", Encodings = [EncodingKind.DeltaBinaryPacked])]
    public ulong Id { get; set; }

    [ParquetColumn("tag", Encodings = [EncodingKind.RleDictionary])]
    public string? Tag { get; set; }

    [ParquetColumn("payload", Encodings = [EncodingKind.Plain])]
    public byte[] Payload { get; set; } = [];

    [ParquetColumn("defaultValue")]
    public uint DefaultValue { get; set; }
}

internal sealed class GeneratedRowSchemaEncodingTests
{
    [Test]
    public void GeneratedSchemaCarriesRequestedEncodings()
    {
        var columns = EncodedRowSchema.Schema.Columns;

        AssertEncodingOptions(columns[0].Options.Encodings, [EncodingKind.DeltaBinaryPacked]);
        AssertEncodingOptions(columns[1].Options.Encodings, [EncodingKind.RleDictionary]);
        AssertEncodingOptions(columns[2].Options.Encodings, [EncodingKind.Plain]);
        AssertEncodingOptions(columns[3].Options.Encodings, []);
    }

    [Test]
    public void GeneratedRowWriterUsesRequestedEncodings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-generated-schema-encoding-{Guid.NewGuid():N}.parquet");

        try
        {
            using (var stream = File.Create(path))
            {
                var writer = EncodedRowSchema.CreateWriter(stream);
                var rowGroup = writer.StartRowGroup();

                var ids = rowGroup.Id;
                ids.Serialize([10UL, 20UL, 30UL]);
                rowGroup.Write(ids);

                var tags = rowGroup.Tag;
                tags.Serialize(["a", "b", "a"]);
                rowGroup.Write(tags);

                var payloads = rowGroup.Payload;
                payloads.Serialize([new byte[] { 1, 2 }, new byte[] { 3 }, new byte[] { 4, 5, 6 }]);
                rowGroup.Write(payloads);

                var defaultValues = rowGroup.DefaultValue;
                defaultValues.Serialize([1U, 2U, 3U]);
                rowGroup.Write(defaultValues);

                writer.CloseFile();
            }

            using var reader = new ParquetFileReader(path);
            using var rg = reader.RowGroup(0);

            AssertEncodings(rg, 0, Encoding.DeltaBinaryPacked);
            AssertEncodings(rg, 1, Encoding.RleDictionary);
            AssertEncodings(rg, 2, Encoding.Plain);
            AssertEncodings(rg, 3, Encoding.Plain);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    static void AssertEncodings(RowGroupReader rowGroup, int columnIndex, Encoding expected)
    {
        using var column = rowGroup.MetaData.GetColumnChunkMetaData(columnIndex);
        var encodings = column.Encodings;
        if (!encodings.Contains(expected))
            throw new InvalidOperationException(
                $"Column {columnIndex} did not contain expected encoding '{expected}'. Actual: {string.Join(", ", encodings)}");
    }

    static void AssertEncodingOptions(IReadOnlyList<EncodingKind> actual, IReadOnlyList<EncodingKind> expected)
    {
        if (actual.Count != expected.Count)
            throw new InvalidOperationException($"Expected {expected.Count} encodings but found {actual.Count}.");

        for (var i = 0; i < expected.Count; i++)
            if (actual[i] != expected[i])
                throw new InvalidOperationException($"Expected encoding '{expected[i]}' at index {i} but found '{actual[i]}'.");
    }
}
