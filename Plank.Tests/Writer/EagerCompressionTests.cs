using Plank.Reading.Logical;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Writer;

internal sealed class EagerCompressionTests
{
    static readonly CompressionKind[] CompressionKinds =
    [
        CompressionKind.None,
        CompressionKind.Snappy,
        CompressionKind.Gzip,
        CompressionKind.Zstd,
        CompressionKind.Lz4,
        CompressionKind.Brotli
    ];

    static readonly ParquetDataPageVersion[] DataPageVersions =
    [
        ParquetDataPageVersion.V1,
        ParquetDataPageVersion.V2
    ];

    [Test]
    public void PreparedPagesRoundTripAcrossCodecsAndDataPageVersions()
    {
        foreach (var dataPageVersion in DataPageVersions)
            foreach (var compression in CompressionKinds)
                RoundTrip(dataPageVersion, compression);
    }

    [Test]
    public async Task ParallelPreparationUsesIndependentCompressionBuffers()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: [EncodingKind.Plain]))
        ]);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.Gzip,
            CompressionLevel = 1
        });
        var first = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);
        var second = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);
        var firstValues = CreateRequiredValues(4096, 3);
        var secondValues = CreateRequiredValues(4096, 7);

        await Task.WhenAll(
            Task.Run(() => first.Serialize(firstValues)),
            Task.Run(() => second.Serialize(secondValues))).ConfigureAwait(false);

        writer.StartRowGroup().Write(first);
        writer.StartRowGroup().Write(second);
        writer.CloseFile();

        using var reader = schema.CreateReader(new MemoryStream(stream.ToArray()), new ParquetReaderOptions
        {
            VerifyPageCrc = true
        });
        AssertColumn(reader.RowGroups[0].Column<int>(schema.LeafColumns[0]), firstValues,
            "first parallel row group");
        AssertColumn(reader.RowGroups[1].Column<int>(schema.LeafColumns[0]), secondValues,
            "second parallel row group");
    }

    static void RoundTrip(ParquetDataPageVersion dataPageVersion, CompressionKind compression)
    {
        var schema = new ParquetSchema([
            ColumnDefinition.OptionalLeaf("optional", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: [EncodingKind.Plain])),
            ColumnDefinition.RequiredLeaf("dictionary", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: [EncodingKind.RleDictionary]))
        ]);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = compression,
            DataPageVersion = dataPageVersion,
            TargetDataPageSizeBytes = 256,
            WritePageCrc = true,
            WritePageIndexes = true
        });
        var optional = writer.CreateSerializedColumn<int?>(schema.LeafColumns[0]);
        var dictionary = writer.CreateSerializedColumn<int>(schema.LeafColumns[1]);
        var optionalValues = CreateOptionalValues(4096);
        var dictionaryValues = CreateRequiredValues(4096, 11);

        optional.Serialize(optionalValues);
        dictionary.Serialize(dictionaryValues);
        var rowGroup = writer.StartRowGroup();
        rowGroup.Write(optional);
        rowGroup.Write(dictionary);
        writer.CloseFile();

        using var reader = schema.CreateReader(new MemoryStream(stream.ToArray()), new ParquetReaderOptions
        {
            VerifyPageCrc = true
        });
        var context = $"{dataPageVersion} with {compression}";
        AssertColumn(reader.RowGroups[0].Column<int?>(schema.LeafColumns[0]), optionalValues, context);
        AssertColumn(reader.RowGroups[0].Column<int>(schema.LeafColumns[1]), dictionaryValues, context);
    }

    static int?[] CreateOptionalValues(int count)
    {
        var values = new int?[count];
        for (var i = 0; i < values.Length; i++)
            values[i] = i % 7 == 0 ? null : i % 31;
        return values;
    }

    static int[] CreateRequiredValues(int count, int modulus)
    {
        var values = new int[count];
        for (var i = 0; i < values.Length; i++)
            values[i] = i % modulus;
        return values;
    }

    static void AssertColumn<T>(RowGroupColumn<T> column, T[] expected, string context)
    {
        var index = 0;
        foreach (var buffer in column)
            foreach (var value in buffer.Values)
            {
                if (index >= expected.Length || !EqualityComparer<T>.Default.Equals(value, expected[index]))
                    throw new InvalidOperationException($"Value mismatch at {index} for {context}.");
                index++;
            }

        if (index != expected.Length)
            throw new InvalidOperationException($"Expected {expected.Length} values, got {index} for {context}.");
    }
}
