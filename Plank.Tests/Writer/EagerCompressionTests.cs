using System.Buffers.Binary;
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
    public void V1OptionalLevelLengthIsWrittenInPlace()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.OptionalLeaf("value", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: [EncodingKind.Plain]))
        ]);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None,
            DataPageVersion = ParquetDataPageVersion.V1
        });
        var serialized = writer.CreateSerializedColumn<int?>(schema.LeafColumns[0]);

        serialized.Serialize([1, null, 2, null, 3]);

        ref var page = ref serialized.Pages[0];
        var content = new byte[page.Content.WrittenLength];
        page.Content.CopyTo(content);
        var definitionLength = BinaryPrimitives.ReadUInt32LittleEndian(content);
        if (definitionLength != page.DefinitionLevelsByteLength)
            throw new InvalidOperationException(
                $"Expected definition-level prefix {page.DefinitionLevelsByteLength}, got {definitionLength}.");
        if (page.RepetitionLevelsByteLength != 0)
            throw new InvalidOperationException("Flat optional data unexpectedly contained repetition levels.");
        if (content.Length < sizeof(uint) + definitionLength)
            throw new InvalidOperationException("Definition-level prefix exceeds the V1 page payload.");

        writer.StartRowGroup().Write(serialized);
        writer.CloseFile();
    }

    [Test]
    public void V1RepeatedLevelLengthsAreWrittenInPlace()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.List("numbers",
                ColumnDefinition.RequiredLeaf("element", ParquetPhysicalType.Int32,
                    new ColumnOptions(encodings: [EncodingKind.Plain])),
                repetition: ParquetRepetition.Required)
        ]);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None,
            DataPageVersion = ParquetDataPageVersion.V1
        });
        var serialized = writer.CreateSerializedColumn<int[]>(schema.LeafColumns[0]);
        int[][] rows = [[1, 2], [], [3]];

        serialized.Serialize(rows);

        ref var page = ref serialized.Pages[0];
        var content = new byte[page.Content.WrittenLength];
        page.Content.CopyTo(content);
        var repetitionLength = BinaryPrimitives.ReadUInt32LittleEndian(content);
        if (repetitionLength != page.RepetitionLevelsByteLength)
            throw new InvalidOperationException(
                $"Expected repetition-level prefix {page.RepetitionLevelsByteLength}, got {repetitionLength}.");

        var definitionPrefixOffset = checked(sizeof(uint) + (int)repetitionLength);
        var definitionLength = BinaryPrimitives.ReadUInt32LittleEndian(content.AsSpan(definitionPrefixOffset));
        if (definitionLength != page.DefinitionLevelsByteLength)
            throw new InvalidOperationException(
                $"Expected definition-level prefix {page.DefinitionLevelsByteLength}, got {definitionLength}.");
        if (content.Length < definitionPrefixOffset + sizeof(uint) + definitionLength)
            throw new InvalidOperationException("Level prefixes exceed the V1 page payload.");

        writer.StartRowGroup().Write(serialized);
        writer.CloseFile();
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
