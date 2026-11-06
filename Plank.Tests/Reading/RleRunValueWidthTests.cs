using Plank.Reading;
using Plank.Reading.Logical;
using Plank.Reading.Physical;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.PageStrategy;

namespace Plank.Tests.Reading;

internal sealed class RleRunValueWidthTests
{
    // Optional BOOLEAN, V2: definitions 0a 01, values length 2 and run 26 fc.
    const string Sample = "UEFSMRUGFRAVEFwVChUAFQoVBhUEFQASPBgBARgBABYAKAEBGAEAEREAAAAKAQIAAAAm/BkRAhkYAQAZGAEBFQAZFgAAGRwWCBVgFgAAABUCGSxIBnNjaGVtYRUCABUAJQIYAWMAFgoZHBkcJgAcFQAZFQYZGAFjFQAWChZgFmAmCDwYAQEYAQAWACgBARgBABERAAAWigEVFBZoFSIAFmAWCiYIFmAAORwcAAAAXwAAAFBBUjE=";

    // Optional INT64 dictionary page: 127 literal groups are declared with only two index bytes left.
    const string ShortInt64IndexRun = "UEFSMRUEFUAVQEwVBBUAAAAAAAAAAAAAAAEAAAAAAAAAAgAAAAAAAAADAAAAAAAAABUAFRwVHCwVEBUQFQYVBhwYCAMAAAAAAAD/GAgAAAAAAAAAABYCKAgDAAAAAAAAABgIAAAAAAAAAAAREQAAAAYAAAAmAQIACAIB/0Q5GRECGRgIAAAAAAAAAAAZGAgDAAAAAAAAABUAGRYCABkcFmIVmgEWAAAAFQIZLEgGc2NoZW1hFQIAFQQlAhgBYwAWEBkcGRwmABwVBBk1ABAGGRgBYxUAFhAW9AEW9AEmYiYIHBgIAwAAAAAAAAAYCAAAAAAAAAAAFgIoCAMAAAAAAAAAGAgAAAAAAAAAABERAAAWugIVFhb8ARU+ABb0ARYQJggW9AEAORwcAAAAhAAAAFBBUjE=";

    [Test]
    public void NullableInt64DictionaryRejectsTruncatedLiteralRun()
    {
        var file = Convert.FromBase64String(ShortInt64IndexRun);
        var error = Assert.Throws<CorruptParquetException>(() =>
        {
            using var reader = new ParquetReader();
            reader.Reset(new MemoryStream(file, writable: false));
            foreach (var buffer in reader.RowGroups[0].Column<long?>(0))
                _ = buffer.Values.Length;
        });
        if (!error.Message.Contains("RLE literal run exceeds the remaining payload", StringComparison.Ordinal))
            throw new InvalidOperationException("Expected the shared run parser to reject the complete literal run.");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public void BooleanRunMustFit(bool nested)
    {
        var file = Convert.FromBase64String(Sample);
        Assert.Throws<CorruptParquetException>(() => ReadBoolean(file, nested));
        file[51] = 1;
        ReadBoolean(file, nested);
        file[51] = 2;
        Assert.Throws<CorruptParquetException>(() => ReadBoolean(file, nested));
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public void DefinitionRunMustFit(bool nested)
    {
        var file = Convert.FromBase64String(Sample);
        file[51] = 1;
        ReadBoolean(file, nested);
        file[45] = 2;
        Assert.Throws<CorruptParquetException>(() => ReadBoolean(file, nested));
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public void DictionaryRunMustFitEvenWhenIndexExists(bool nested)
    {
        var schema = new ParquetSchema([ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int32,
            new ColumnOptions(encodings: [EncodingKind.RleDictionary]), pageStrategy: new DictionaryPage())]);
        using var stream = new MemoryStream();
        using (var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        { Compression = CompressionKind.None, DataPageVersion = ParquetDataPageVersion.V2 }))
        {
            var column = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);
            column.Serialize(Enumerable.Range(0, 32).Select(i => i % 8).ToArray());
            writer.StartRowGroup().Write(column);
            writer.CloseFile();
        }
        var file = stream.ToArray();
        int offset;
        using (var physical = new ParquetFileReader())
        {
            physical.Reset(new MemoryStream(file, writable: false));
            offset = checked((int)physical.Metadata.ColumnChunk(0, 0).DataPageOffset);
        }
        var header = PageHeaderReader.Read(file.AsSpan(offset));
        var payload = offset + header.HeaderLength;
        file[payload] = 2; // bit width, despite eight dictionary entries
        file[payload + 1] = 64; // repeat 32 times
        file[payload + 2] = 3; // maximum for width 2
        ReadDictionary(file, nested);
        file[payload + 2] = 4; // valid dictionary index, invalid width
        Assert.Throws<CorruptParquetException>(() => ReadDictionary(file, nested));

        // A literal run must be complete, even when the page needs only 32 values.
        file[payload + 1] = 127; // 63 groups at width 2 claim 126 bytes
        Assert.Throws<CorruptParquetException>(() => ReadDictionary(file, nested));
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public void NestedLevelsRespectWidthAndSchemaMaximum(bool repetition)
    {
        // An optional list with required elements has max definition 2 (width 2)
        // and max repetition 1. Repeated runs allow testing both constraints.
        var schema = new ParquetSchema([ColumnDefinition.List("values",
            ColumnDefinition.RequiredLeaf("element", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: [EncodingKind.Plain])), ParquetRepetition.Optional)]);
        using var stream = new MemoryStream();
        using (var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        { Compression = CompressionKind.None, DataPageVersion = ParquetDataPageVersion.V2 }))
        {
            var column = writer.CreateSerializedColumn<int[]>(schema.LeafColumns[0]);
            column.Serialize(Enumerable.Range(0, 16).Select(_ => new[] { 42 }).ToArray());
            writer.StartRowGroup().Write(column);
            writer.CloseFile();
        }
        var file = stream.ToArray();
        int offset;
        using (var physical = new ParquetFileReader())
        {
            physical.Reset(new MemoryStream(file, writable: false));
            offset = checked((int)physical.Metadata.ColumnChunk(0, 0).DataPageOffset);
        }
        var header = PageHeaderReader.Read(file.AsSpan(offset));
        var payload = offset + header.HeaderLength;
        var run = repetition ? payload : payload + checked((int)header.RepetitionLevelsByteLength);
        if (file[run] != 2)
            throw new InvalidOperationException("Expected a one-value repeated level run.");
        ReadDictionary(file, nested: true);
        file[run + 1] = repetition ? (byte)2 : (byte)3;
        Assert.Throws<CorruptParquetException>(() => ReadDictionary(file, nested: true));
        if (!repetition)
        {
            file[run + 1] = 4; // exceeds bit width 2
            Assert.Throws<CorruptParquetException>(() => ReadDictionary(file, nested: true));
        }
    }

    static void ReadBoolean(byte[] file, bool nested)
    {
        using var reader = new ParquetReader();
        reader.Reset(new MemoryStream(file, writable: false));
        if (nested)
            foreach (var buffer in reader.RowGroups[0].NestedColumn<bool>(0))
                _ = buffer.Values.Values.Length;
        else
            foreach (var buffer in reader.RowGroups[0].Column<bool?>(0))
                _ = buffer.Values.Length;
    }

    static void ReadDictionary(byte[] file, bool nested)
    {
        using var reader = new ParquetReader();
        reader.Reset(new MemoryStream(file, writable: false));
        if (nested)
            foreach (var buffer in reader.RowGroups[0].NestedColumn<int>(0))
                _ = buffer.Values.Values.Length;
        else
            foreach (var buffer in reader.RowGroups[0].Column<int>(0))
                _ = buffer.Values.Length;
    }

    sealed class DictionaryPage : IPageStrategy
    {
        public DictionaryMode GetDictionaryMode() => DictionaryMode.Forced;
        public bool ShouldDropDictionary(uint uniqueCount, uint totalRowCount, uint rowsSeen) => false;
        public uint GetNextDataPageRowCount(uint totalRowCount, uint rowsWritten) => totalRowCount - rowsWritten;
    }
}
