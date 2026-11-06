using System.Buffers.Binary;
using Plank.Reading;
using Plank.Reading.Logical;
using Plank.Reading.Logical.Internal;
using Plank.Reading.Physical;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Reading;

internal sealed class EmptyV1LevelsTests
{
    // One optional Boolean column, five values, and a zero-byte definition stream.
    const string EmptyDefinitions = "UEFSMRUAFR4VHiwVChUAFQYVBhwYAQEYAQAWBCgBARgBABERAAAAAAAAAAIBAgACAQIAAgEFGRECGRgBABkYAQEVABkWBAAZHBYIFWQWAAAAFQIZLEgGc2NoZW1hFQIAFQAlAhgBYwAWChkcGRwmABwVABklAAYZGAFjFQAWChZkFmQmCDwYAQEYAQAWBCgBARgBABERAAAWjgEVFBZsFSIAFmQWCiYIFmQAORwcAAAAYAAAAFBBUjE=";

    [Test]
    public void SampleRejectsEmptyDefinitions()
    {
        var error = Assert.Throws<CorruptParquetException>(() => ReadBoolean(Convert.FromBase64String(EmptyDefinitions)));
        if (!error.Message.Contains("definition", StringComparison.Ordinal))
            throw new InvalidOperationException("Exception must identify definition levels.");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public void NestedV1RejectsEmptyLevelStream(bool repetition)
    {
        var schema = new ParquetSchema([
            ColumnDefinition.List("numbers", ColumnDefinition.RequiredLeaf("element", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: [EncodingKind.Plain])), repetition: ParquetRepetition.Required)
        ]);
        using var stream = new MemoryStream();
        using (var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None, DataPageVersion = ParquetDataPageVersion.V1
        }))
        {
            var column = writer.CreateSerializedColumn<int[]>(schema.LeafColumns[0]);
            column.Serialize([[1, 2], [], [3]]);
            writer.StartRowGroup().Write(column);
            writer.CloseFile();
        }
        var file = stream.ToArray();
        ReadNested(file); // Valid repetition and definition streams still decode.
        int offset;
        using (var physical = new ParquetFileReader())
        {
            physical.Reset(new MemoryStream(file, writable: false));
            offset = checked((int)physical.Metadata.ColumnChunk(0, 0).DataPageOffset);
        }
        var header = PageHeaderReader.Read(file.AsSpan(offset));
        var payload = offset + header.HeaderLength;
        var repetitionLength = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(payload));
        if (repetitionLength <= 0)
            throw new InvalidOperationException("Expected nonempty repetition levels.");
        var prefix = repetition ? payload : payload + sizeof(int) + repetitionLength;
        if (BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(prefix)) <= 0)
            throw new InvalidOperationException("Expected nonempty level stream.");
        file.AsSpan(prefix, sizeof(int)).Clear();
        var error = Assert.Throws<CorruptParquetException>(() => ReadNested(file));
        if (!error.Message.Contains(repetition ? "repetition" : "definition", StringComparison.Ordinal))
            throw new InvalidOperationException("Exception identified the wrong levels.");
    }

    [Test]
    public void ValidOptionalAndRequiredV1Read()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.OptionalLeaf("optional", ParquetPhysicalType.Int32),
            ColumnDefinition.RequiredLeaf("required", ParquetPhysicalType.Int32)
        ]);
        using var stream = new MemoryStream();
        using (var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None, DataPageVersion = ParquetDataPageVersion.V1
        }))
        {
            var group = writer.StartRowGroup();
            var optional = group.CreateSerializedColumn<int?>(schema.LeafColumns[0]);
            optional.Serialize([1, null, 3]);
            group.Write(optional);
            var required = group.CreateSerializedColumn<int>(schema.LeafColumns[1]);
            required.Serialize([4, 5, 6]);
            group.Write(required);
            writer.CloseFile();
        }
        using var reader = new ParquetReader();
        reader.Reset(new MemoryStream(stream.ToArray()));
        var optionalValues = new List<int?>();
        foreach (var buffer in reader.RowGroups[0].Column<int?>(0))
            optionalValues.AddRange(buffer.Values);
        var requiredValues = new List<int>();
        foreach (var buffer in reader.RowGroups[0].Column<int>(1))
            requiredValues.AddRange(buffer.Values);
        if (!optionalValues.SequenceEqual(new int?[] { 1, null, 3 }) || !requiredValues.SequenceEqual([4, 5, 6]))
            throw new InvalidOperationException("Valid V1 values changed.");
    }

    [Test]
    public void EmptyPageWithEmptyDefinitionsIsAccepted()
    {
        var schema = new ParquetSchema([ColumnDefinition.OptionalLeaf("value", ParquetPhysicalType.Int32)]);
        var header = new PageHeader(PageHeaderType.DataPage, 4, 4, 0, EncodingKind.Plain,
            HeaderLength: 1, RepetitionLevelsByteLength: 0, DefinitionLevelsByteLength: 0, NullCount: 0,
            IsCompressed: false, RepetitionLevelEncoding: EncodingKind.Rle,
            DefinitionLevelEncoding: EncodingKind.Rle, RowCount: 0);
        var buffers = default(ColumnReadBuffers<int?>);
        try
        {
            if (!ColumnChunkReader.TryDecodeNullablePageIntoNative(header, new byte[4],
                    schema.LeafColumns[0].Column, 0, ref buffers, DefaultParquetBufferPool.Shared, out var result) ||
                result.Values.Length != 0)
                throw new InvalidOperationException("Empty V1 page did not decode as empty.");
        }
        finally
        {
            buffers.Dispose();
        }
    }

    static void ReadBoolean(byte[] file)
    {
        using var reader = new ParquetReader();
        reader.Reset(new MemoryStream(file, writable: false));
        foreach (var buffer in reader.RowGroups[0].Column<bool?>(0))
            _ = buffer.Values.Length;
    }

    static void ReadNested(byte[] file)
    {
        using var reader = new ParquetReader();
        reader.Reset(new MemoryStream(file, writable: false));
        var values = new List<int>();
        var definitions = new List<int>();
        var repetitions = new List<int>();
        foreach (var buffer in reader.RowGroups[0].NestedColumn<int>(0))
        {
            values.AddRange(buffer.Values.Values);
            definitions.AddRange(buffer.DefinitionLevels);
            repetitions.AddRange(buffer.RepetitionLevels);
        }
        if (!values.SequenceEqual([1, 2, 3]) || !definitions.SequenceEqual([1, 1, 0, 1]) ||
            !repetitions.SequenceEqual([0, 1, 0, 0]))
            throw new InvalidOperationException("Nested V1 values or levels changed.");
    }
}
