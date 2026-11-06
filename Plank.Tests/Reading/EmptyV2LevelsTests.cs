using Plank.Reading;
using Plank.Reading.Logical;
using Plank.Reading.Physical;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Reading;

internal sealed class EmptyV2LevelsTests
{
    static readonly string[] Samples = [
        "UEFSMRUGFTgVOBWw5/uyAkwVChUAFQoVABUAFQARHBgD/wD/GAAWACgD/wD/GAAREQAAABwQAAAAAAEBBAADAQU0AgMDAAAA/wD/AQAAAAcZEQIZGAAZGAP/AP8VABkWAAAZHBYIFZgBFgAAABUCGSxIBnNjaGVtYRUCABUMJQIYAWMAFgoZHBkcJgAcFQwZFQAZGAFjFQIWChaYARaYASYIPBgD/wD/GAAWACgD/wD/GAAREQAAFsQBFRYWoAEVJAAWmAEWCiYIFpgBADkcHAAAAGYAAABQQVIx",
        "UEFSMRUGFVAVUFwVChUAFQoVEhUAFQASHBgI//9jp7O24A0YCMfP////////FgAoCP//Y6eztuANGAjHz////////xERAAAAADnH/wAQMM//AAAA/2MAAAD/pwAAAP+zAAAA/7YAAAD/4AAAAP8NABkRAhkYCMfP////////GRgI//9jp7O24A0VABkWAAAZHBYIFdgBFgAAABUCGSxIBnNjaGVtYRUCABUEJQIYAWMlChUIFSQsXBUIFSQAAAAWChkcGRwmABwVBBkVEhkYAWMVABYKFtgBFtgBJgg8GAj//2Ons7bgDRgIx8////////8WACgI//9jp7O24A0YCMfP////////EREAABaeAhUWFuABFT4AFtgBFgomCBbYAQA5HBwAAACOAAAAUEFSMQ==",
        "UEFSMRUGFTIVMlwVChUAFQoVABUAFQASHBgBBRgBARYAKAEFGAEBEREAAAABAAAAAQEAAAACAQAAAAMBAAAABAEAAAAFFUAcHAAAHBwAABwcAAAAgTwI/wIkIEAACMQIAggOACAQYCAQUAABABhASAAMgwAZEQIZGAEBGRgBBRUAGRYAABkcFggVggEWAAAAFQIZLEgGc2NoZW1hFQIAFQwlAhgBYwAWChkcGRwmABwVDBkVABkYAWMVABYKFoIBFoIBJgg8GAEFGAEBFgAoAQUYAQEREQAmigEVXgAWigIVFhboARUiABaCARYKJggWggEAORwcAAAAaQAAAFBBUjE="
    ];

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public void SamplesRejectEmptyDefinitions(bool strict)
    {
        for (var i = 0; i < Samples.Length; i++)
        {
            var file = Convert.FromBase64String(Samples[i]);
            using var reader = new ParquetReader(new ParquetReaderOptions { Strict = strict });
            reader.Reset(new MemoryStream(file, writable: false));
            var error = Assert.Throws<CorruptParquetException>(() =>
            {
                using var cursor = reader.PhysicalReader.OpenPages(0, 0);
                while (cursor.MoveNext()) { }
            });
            if (!error.Message.Contains("definition", StringComparison.Ordinal))
                throw new InvalidOperationException("Expected definition-level error.");
            var sampleIndex = i;
            error = Assert.Throws<CorruptParquetException>(() =>
            {
                if (sampleIndex == 1)
                {
                    foreach (var buffer in reader.RowGroups[0].Column<long?>(0))
                        _ = buffer.Values.Length;
                }
                else
                {
                    foreach (var buffer in reader.RowGroups[0].Column<byte>(0))
                        _ = buffer.Values.Length;
                }
            });
            if (!error.Message.Contains("definition", StringComparison.Ordinal))
                throw new InvalidOperationException("Expected definition-level error.");
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public void NestedRejectsEmptyStreams(bool repetition, bool strict)
    {
        var schema = new ParquetSchema([
            ColumnDefinition.List("numbers", ColumnDefinition.RequiredLeaf("element", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: [EncodingKind.Plain])), repetition: ParquetRepetition.Required)
        ]);
        using var stream = new MemoryStream();
        using (var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None, DataPageVersion = ParquetDataPageVersion.V2
        }))
        {
            var column = writer.CreateSerializedColumn<int[]>(schema.LeafColumns[0]);
            column.Serialize([[1, 2], [], [3]]);
            writer.StartRowGroup().Write(column);
            writer.CloseFile();
        }
        var file = stream.ToArray();
        using (var physical = new ParquetFileReader())
        {
            physical.Reset(new MemoryStream(file, writable: false));
            var column = physical.Metadata.ColumnSchema(0);
            if (!column.IsRepeated || !column.HasDefinitionLevels)
                throw new InvalidOperationException("Repeated ancestor must produce both level streams.");
        }
        ReadNested(file, strict);
        int offset;
        using (var physical = new ParquetFileReader())
        {
            physical.Reset(new MemoryStream(file, writable: false));
            offset = checked((int)physical.Metadata.ColumnChunk(0, 0).DataPageOffset);
        }
        var header = PageHeaderReader.Read(file.AsSpan(offset));
        // The writer emits compact i32 fields 5 and 6 consecutively; these small lengths
        // use single-byte zigzag varints. Change only the selected header value.
        var def = checked((byte)(header.DefinitionLevelsByteLength * 2));
        var rep = checked((byte)(header.RepetitionLevelsByteLength * 2));
        if (def == 0 || rep == 0 || def >= 128 || rep >= 128)
            throw new InvalidOperationException("Expected small nonempty level streams.");
        var pattern = new byte[] { 0x15, def, 0x15, rep };
        var headerBytes = file.AsSpan(offset, header.HeaderLength);
        var index = headerBytes.IndexOf(pattern);
        if (index < 0 || headerBytes[(index + 1)..].IndexOf(pattern) >= 0)
            throw new InvalidOperationException("Could not uniquely locate level lengths.");
        headerBytes[index + (repetition ? 3 : 1)] = 0;
        var error = Assert.Throws<CorruptParquetException>(() => ReadNested(file, strict));
        if (!error.Message.Contains(repetition ? "repetition" : "definition", StringComparison.Ordinal))
            throw new InvalidOperationException("Exception identified the wrong levels.");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public void OptionalAndRequiredV2ReadAndEmptyDefinitionsReject(bool strict)
    {
        var schema = new ParquetSchema([
            ColumnDefinition.OptionalLeaf("optional", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: [EncodingKind.Plain])),
            ColumnDefinition.RequiredLeaf("required", ParquetPhysicalType.Int32)
        ]);
        using var stream = new MemoryStream();
        using (var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None, DataPageVersion = ParquetDataPageVersion.V2
        }))
        {
            var group = writer.StartRowGroup();
            var optional = group.CreateSerializedColumn<int?>(schema.LeafColumns[0]);
            optional.Serialize([1, 2, 3]);
            group.Write(optional);
            var required = group.CreateSerializedColumn<int>(schema.LeafColumns[1]);
            required.Serialize([4, 5, 6]);
            group.Write(required);
            writer.CloseFile();
        }
        var file = stream.ToArray();
        using (var physical = new ParquetFileReader())
        {
            physical.Reset(new MemoryStream(file, writable: false));
            var optionalSchema = physical.Metadata.ColumnSchema(0);
            var requiredSchema = physical.Metadata.ColumnSchema(1);
            if (optionalSchema.IsRepeated || !optionalSchema.HasDefinitionLevels ||
                requiredSchema.IsRepeated || requiredSchema.HasDefinitionLevels)
                throw new InvalidOperationException("Incorrect level flags for optional or required leaf.");
        }
        void ReadValues()
        {
            using var reader = new ParquetReader(new ParquetReaderOptions { Strict = strict });
            reader.Reset(new MemoryStream(file, writable: false));
            var optional = new List<int?>();
            foreach (var buffer in reader.RowGroups[0].Column<int?>(0))
                optional.AddRange(buffer.Values);
            var required = new List<int>();
            foreach (var buffer in reader.RowGroups[0].Column<int>(1))
                required.AddRange(buffer.Values);
            if (!optional.SequenceEqual(new int?[] { 1, 2, 3 }) || !required.SequenceEqual([4, 5, 6]))
                throw new InvalidOperationException("Valid V2 values changed.");
        }
        ReadValues();
        int offset;
        using (var physical = new ParquetFileReader())
        {
            physical.Reset(new MemoryStream(file, writable: false));
            offset = checked((int)physical.Metadata.ColumnChunk(0, 0).DataPageOffset);
        }
        var header = PageHeaderReader.Read(file.AsSpan(offset));
        var length = checked((byte)(header.DefinitionLevelsByteLength * 2));
        if (length == 0 || length >= 128 || header.RepetitionLevelsByteLength != 0 || header.NullCount != 0)
            throw new InvalidOperationException("Unexpected optional page header.");
        var bytes = file.AsSpan(offset, header.HeaderLength);
        var pattern = new byte[] { 0x15, length, 0x15, 0 };
        var index = bytes.IndexOf(pattern);
        if (index < 0 || bytes[(index + 1)..].IndexOf(pattern) >= 0)
            throw new InvalidOperationException("Could not uniquely locate level lengths.");
        bytes[index + 1] = 0;
        var error = Assert.Throws<CorruptParquetException>(ReadValues);
        if (!error.Message.Contains("definition", StringComparison.Ordinal))
            throw new InvalidOperationException("Expected definition-level error.");
    }

    [Test]
    public void EmptyPageAndZeroMaximumLevelsAreAccepted()
    {
        var header = new PageHeader(PageHeaderType.DataPageV2, 0, 0, 0, EncodingKind.Plain,
            HeaderLength: 1, RepetitionLevelsByteLength: 0, DefinitionLevelsByteLength: 0, NullCount: 0,
            IsCompressed: false, RepetitionLevelEncoding: EncodingKind.Rle,
            DefinitionLevelEncoding: EncodingKind.Rle, RowCount: 0);
        var repeated = new ParquetColumnSchemaInfo(0, 0, 1, ParquetPhysicalType.Int32, 0, default,
            isRepeated: true, hasDefinitionLevels: true);
        ParquetPageCursor.ValidateV2Levels(header, repeated);
        ParquetPageCursor.ValidateV2Levels(header with { ValueCount = 1 }, default);
    }

    static void ReadNested(byte[] file, bool strict)
    {
        using var reader = new ParquetReader(new ParquetReaderOptions { Strict = strict });
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
            throw new InvalidOperationException("Nested V2 values or levels changed.");
    }
}
