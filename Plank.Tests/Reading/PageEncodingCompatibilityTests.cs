using Plank.Reading;
using Plank.Reading.Logical.Internal;
using Plank.Reading.Physical;
using Plank.RowApi;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.PageStrategy;

namespace Plank.Tests.Reading;

internal sealed class PageEncodingCompatibilityTests
{
    [Test]
    [Arguments(ParquetPhysicalType.Float)]
    [Arguments(ParquetPhysicalType.Double)]
    [Arguments(ParquetPhysicalType.Boolean)]
    public void DeltaBinaryPackedRejectsNonIntegerPhysicalTypes(ParquetPhysicalType type)
    {
        // PR #174's payload and physical types, exercised at the shared page dispatch.
        byte[] payload = [0x80, 0x01, 0x04, 0x01, 0x00];
        AssertRejected(PageHeaderType.DataPage, type, EncodingKind.DeltaBinaryPacked, payload);
    }

    [Test]
    public void InvalidValueEncodingsAreRejectedForEveryPageKind()
    {
        foreach (var pageType in new[] { PageHeaderType.DataPage, PageHeaderType.DataPageV2, PageHeaderType.DictionaryPage })
        foreach (var (type, encoding) in new[] {
            (ParquetPhysicalType.Float, EncodingKind.DeltaBinaryPacked),
            (ParquetPhysicalType.Int32, EncodingKind.DeltaLengthByteArray),
            (ParquetPhysicalType.Int32, EncodingKind.DeltaByteArray),
            (ParquetPhysicalType.Int32, EncodingKind.Rle),
            (ParquetPhysicalType.Boolean, EncodingKind.ByteStreamSplit),
            (ParquetPhysicalType.ByteArray, EncodingKind.ByteStreamSplit),
            (ParquetPhysicalType.Int64, EncodingKind.Alp),
            (ParquetPhysicalType.Int32, EncodingKind.BitPacked) })
            AssertRejected(pageType, type, encoding, []);
    }

    [Test]
    public void DictionaryPagesRejectNonPlainEncodingsEvenWhenCompatible()
    {
        foreach (var encoding in new[] { EncodingKind.RleDictionary, EncodingKind.DeltaBinaryPacked, EncodingKind.ByteStreamSplit })
            AssertRejected(PageHeaderType.DictionaryPage, ParquetPhysicalType.Int32, encoding, []);
    }

    [Test]
    public void OldDictionaryPageEncodingIsDecodedAsPlain()
    {
        var column = new ParquetSchema([ColumnDefinition.RequiredLeaf("Value", ParquetPhysicalType.Int32)]).LeafColumns[0].Column;
        var buffers = default(ColumnReadBuffers<int>);
        try
        {
            var header = Header(PageHeaderType.DictionaryPage, EncodingKind.PlainDictionary);
            if (!ColumnChunkReader.TryDecodeDictionaryPageIntoNative(header, [42, 0, 0, 0], column, ref buffers, DefaultParquetBufferPool.Shared))
                throw new InvalidOperationException("Legacy dictionary page was not decoded.");
        }
        finally { buffers.Dispose(); }
    }

    [Test]
    public void DictionaryPageEncodingIsReadFromFileAndValidated()
    {
        var schema = new ParquetSchema([ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int32,
            new ColumnOptions(encodings: [EncodingKind.RleDictionary]), pageStrategy: new ForcedDictionary())]);
        int[] expected = [42, 17, 42, 17];
        using var output = new MemoryStream();
        using (var writer = schema.CreateWriter(output, new ParquetWriterOptions { Compression = CompressionKind.None }))
        {
            var group = writer.StartRowGroup();
            var column = group.CreateSerializedColumn<int>(schema.LeafColumns[0]);
            column.Serialize(expected);
            group.Write(column);
            writer.CloseFile();
        }
        var file = output.ToArray();
        int offset;
        using (var physical = new ParquetFileReader())
        {
            physical.Reset(new MemoryStream(file, writable: false));
            var chunk = physical.Metadata.ColumnChunk(0, 0);
            if (chunk.DictionaryPageOffset == 0 || chunk.DictionaryPageOffset >= chunk.DataPageOffset)
                throw new InvalidOperationException("Writer did not produce a dictionary page.");
            offset = checked((int)chunk.DictionaryPageOffset);
        }
        var original = PageHeaderReader.Read(file.AsSpan(offset));
        if (original.Type != PageHeaderType.DictionaryPage || original.Encoding != EncodingKind.Plain)
            throw new InvalidOperationException("Expected a plain dictionary page.");
        AssertFileValues(file, schema, expected);

        // Locate the one-byte encoding field by parsing the changed header and requiring
        // every other header field to remain unchanged; the payload and offsets stay intact.
        var encodingOffset = -1;
        for (var i = offset; i < offset + original.HeaderLength; i++)
        {
            if (file[i] != 0) continue;
            file[i] = 4; // compact protocol zigzag encoding of PLAIN_DICTIONARY (2)
            try
            {
                if (PageHeaderReader.Read(file.AsSpan(offset)) == original with { Encoding = EncodingKind.PlainDictionary })
                    encodingOffset = i;
            }
            catch (Exception) { /* Not the encoding field. */ }
            file[i] = 0;
            if (encodingOffset >= 0) break;
        }
        if (encodingOffset < 0) throw new InvalidOperationException("Could not locate the dictionary encoding field.");

        file[encodingOffset] = 4;
        AssertFileValues(file, schema, expected);
        file[encodingOffset] = 16; // RLE_DICTIONARY (8)
        if (PageHeaderReader.Read(file.AsSpan(offset)).Encoding != EncodingKind.RleDictionary)
            throw new InvalidOperationException("Dictionary encoding was not parsed.");
        Assert.Throws<CorruptParquetException>(() => AssertFileValues(file, schema, expected));
    }

    static void AssertFileValues(byte[] file, ParquetSchema schema, int[] expected)
    {
        using var source = new MemoryReadSource(file);
        using var reader = schema.CreateReader(source);
        var actual = new List<int>();
        foreach (var buffer in reader.RowGroups[0].Column<int>(0))
            actual.AddRange(buffer.Values.ToArray());
        if (!actual.SequenceEqual(expected))
            throw new InvalidOperationException("Dictionary values did not round-trip.");
    }

    sealed class ForcedDictionary : IPageStrategy
    {
        public DictionaryMode GetDictionaryMode() => DictionaryMode.Forced;
        public bool ShouldDropDictionary(uint uniqueCount, uint totalRowCount, uint rowsSeen) => false;
        public uint GetNextDataPageRowCount(uint totalRowCount, uint rowsWritten) => totalRowCount - rowsWritten;
    }

    [Test]
    public void DataPageLegacyDictionaryAndUnusedLevelEncodingsPassValidation()
    {
        var leaf = new ParquetSchema([ColumnDefinition.RequiredLeaf("Value", ParquetPhysicalType.Int32)]).LeafColumns[0];
        var column = leaf.Column;
        var buffers = new ColumnReadBuffers<int> { HasDictionary = true };
        try
        {
            foreach (var pageType in new[] { PageHeaderType.DataPage, PageHeaderType.DataPageV2 })
            {
                var header = Header(pageType, EncodingKind.PlainDictionary) with { RepetitionLevelEncoding = EncodingKind.BitPacked, DefinitionLevelEncoding = EncodingKind.BitPacked };
                if (ColumnChunkReader.TryDecodeDictionaryPageIntoNative(header, [], column, ref buffers, DefaultParquetBufferPool.Shared))
                    throw new InvalidOperationException("Data page treated as dictionary page.");
            }
        }
        finally { buffers.Dispose(); }
    }

    [Test]
    [Arguments(ParquetRepetition.Required, false)]
    [Arguments(ParquetRepetition.Optional, false)]
    [Arguments(ParquetRepetition.Repeated, true)]
    [Arguments(ParquetRepetition.Repeated, false)]
    public void DataPageV1DeprecatedLevelsRespectSchemaMaximum(ParquetRepetition repetition, bool repetitionLevel)
    {
        var schema = new ParquetSchema([ColumnDefinition.Leaf("value", ParquetPhysicalType.Int32,
            new ColumnOptions(repetition, [EncodingKind.Plain]))]);
        using var output = new MemoryStream();
        using (var writer = schema.CreateWriter(output, new ParquetWriterOptions { Compression = CompressionKind.None, DataPageVersion = ParquetDataPageVersion.V1 }))
        {
            var group = writer.StartRowGroup();
            if (repetition == ParquetRepetition.Repeated)
            {
                var column = group.CreateSerializedColumn<int[]>(schema.LeafColumns[0]);
                column.Serialize([[1, 2], [3]]);
                group.Write(column);
            }
            else if (repetition == ParquetRepetition.Optional)
            {
                var column = group.CreateSerializedColumn<int?>(schema.LeafColumns[0]);
                column.Serialize([1, null, 3]);
                group.Write(column);
            }
            else
            {
                var column = group.CreateSerializedColumn<int>(schema.LeafColumns[0]);
                column.Serialize([1, 2, 3]);
                group.Write(column);
            }
            writer.CloseFile();
        }
        var file = output.ToArray();
        int offset;
        using (var physical = new ParquetFileReader())
        {
            physical.Reset(new MemoryStream(file, writable: false));
            offset = checked((int)physical.Metadata.ColumnChunk(0, 0).DataPageOffset);
        }
        var original = PageHeaderReader.Read(file.AsSpan(offset));
        if (original.Type != PageHeaderType.DataPage)
            throw new InvalidOperationException("Expected a v1 data page.");
        // Flat columns also declare unused repetition levels as BIT_PACKED.
        foreach (var changeRepetition in repetition == ParquetRepetition.Repeated ? new[] { repetitionLevel } : [true, false])
        {
            original = PageHeaderReader.Read(file.AsSpan(offset));
            var expected = changeRepetition
                ? original with { RepetitionLevelEncoding = EncodingKind.BitPacked }
                : original with { DefinitionLevelEncoding = EncodingKind.BitPacked };
            var changed = false;
            for (var i = offset; i < offset + original.HeaderLength; i++)
            {
                if (file[i] != 6) continue; // zigzag RLE (3)
                file[i] = 8; // zigzag BIT_PACKED (4)
                try { if (PageHeaderReader.Read(file.AsSpan(offset)) == expected) { changed = true; break; } }
                catch (Exception) { }
                file[i] = 6;
            }
            if (!changed) throw new InvalidOperationException("Could not locate the level encoding.");
        }
        using var source = new MemoryReadSource(file);
        using var reader = schema.CreateReader(source);
        if (repetition == ParquetRepetition.Required)
        {
            var values = new List<int>();
            foreach (var buffer in reader.RowGroups[0].Column<int>(0)) values.AddRange(buffer.Values.ToArray());
            if (!values.SequenceEqual([1, 2, 3])) throw new InvalidOperationException("Values changed.");
        }
        else
        {
            var exception = Assert.Throws<NotSupportedException>(() =>
            {
                if (repetition == ParquetRepetition.Repeated)
                    foreach (var buffer in reader.RowGroups[0].NestedColumn<int>(0)) { _ = buffer.Count; }
                else
                    foreach (var buffer in reader.RowGroups[0].Column<int?>(0)) { _ = buffer.Count; }
            });
            var level = repetitionLevel ? "repetition" : "definition";
            AssertLevelEncodingError(exception, level);

            var rowException = Assert.Throws<NotSupportedException>(() =>
            {
                using var stream = new MemoryStream(file, writable: false);
                RowApiColumnDescriptor descriptor = repetition == ParquetRepetition.Repeated
                    ? new RowApiNestedColumnDescriptor<int[], int>("value", schema.LeafColumns[0], new RowApiCollectionLevel(1, 0, 1))
                    : new RowApiColumnDescriptor<int?>("value", schema.LeafColumns[0]);
                using var rows = new RowReaderCore(stream, schema, [descriptor], null,
                    new RowReaderOptions { Execution = new() { WorkerCount = 1 } }, null);
                while (rows.MoveNext()) { }
            });
            AssertLevelEncodingError(rowException, level);
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public void GeneratedRowReaderRejectsDeprecatedLevels(bool nested, bool repetitionLevel)
    {
        using var output = new MemoryStream();
        using (var writer = LevelEncodingRow.CreateRowWriter(output, maxParallelism: 1,
                   new ParquetWriterOptions { Compression = CompressionKind.None, DataPageVersion = ParquetDataPageVersion.V1 }))
        {
            var row = writer.GetRow();
            row.Flat = 1;
            row.Nested = [1, 2];
            writer.Complete();
        }
        var file = output.ToArray();
        int offset;
        using (var physical = new ParquetFileReader())
        {
            physical.Reset(new MemoryStream(file, writable: false));
            var column = nested ? LevelEncodingRow.Schema.LeafColumns.Single(c => c.Path.Contains("Nested", StringComparison.Ordinal))
                : LevelEncodingRow.Schema.LeafColumns.Single(c => c.Path == "Flat");
            var ordinal = Array.IndexOf(LevelEncodingRow.Schema.LeafColumns.ToArray(), column);
            offset = checked((int)physical.Metadata.ColumnChunk(0, ordinal).DataPageOffset);
        }
        var original = PageHeaderReader.Read(file.AsSpan(offset));
        var expected = repetitionLevel ? original with { RepetitionLevelEncoding = EncodingKind.BitPacked }
            : original with { DefinitionLevelEncoding = EncodingKind.BitPacked };
        var changed = false;
        for (var i = offset; i < offset + original.HeaderLength; i++)
        {
            if (file[i] != 6) continue;
            file[i] = 8;
            try { if (PageHeaderReader.Read(file.AsSpan(offset)) == expected) { changed = true; break; } }
            catch (Exception) { }
            file[i] = 6;
        }
        if (!changed) throw new InvalidOperationException("Could not locate the level encoding.");
        var exception = Assert.Throws<NotSupportedException>(() =>
        {
            using var stream = new MemoryStream(file, writable: false);
            using var reader = LevelEncodingRow.CreateRowReader(stream,
                nested ? LevelEncodingRow.Projection.Nested : LevelEncodingRow.Projection.Flat);
            foreach (var row in reader) { _ = row; }
        });
        AssertLevelEncodingError(exception, repetitionLevel ? "repetition" : "definition");
    }

    static void AssertLevelEncodingError(NotSupportedException exception, string level)
    {
        if (!exception.Message.Contains("BIT_PACKED", StringComparison.Ordinal) ||
            !exception.Message.Contains(level, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Missing encoding or level information in error.", exception);
    }

    [Test]
    [Arguments(ParquetDataPageVersion.V1, false)]
    [Arguments(ParquetDataPageVersion.V2, false)]
    [Arguments(ParquetDataPageVersion.V1, true)]
    [Arguments(ParquetDataPageVersion.V2, true)]
    public void FlatAndNestedReadersRejectMalformedPageEncoding(ParquetDataPageVersion version, bool nested)
    {
        var schema = new ParquetSchema([ColumnDefinition.Leaf("value", ParquetPhysicalType.Int32,
            new ColumnOptions(nested ? ParquetRepetition.Repeated : ParquetRepetition.Required, [EncodingKind.Plain]))]);
        using var output = new MemoryStream();
        using (var writer = schema.CreateWriter(output, new ParquetWriterOptions { Compression = CompressionKind.None, DataPageVersion = version }))
        {
            var group = writer.StartRowGroup();
            if (nested)
            {
                var column = group.CreateSerializedColumn<int[]>(schema.LeafColumns[0]);
                column.Serialize([[1, 2], [3]]);
                group.Write(column);
            }
            else
            {
                var column = group.CreateSerializedColumn<int>(schema.LeafColumns[0]);
                column.Serialize([1, 2]);
                group.Write(column);
            }
            writer.CloseFile();
        }
        var file = output.ToArray();
        int offset;
        using (var physical = new ParquetFileReader())
        {
            physical.Reset(new MemoryStream(file, writable: false));
            offset = checked((int)physical.Metadata.ColumnChunk(0, 0).DataPageOffset);
        }
        var original = PageHeaderReader.Read(file.AsSpan(offset));
        var changed = false;
        for (var i = offset; i < offset + original.HeaderLength; i++)
        {
            if (file[i] != 0) continue;
            file[i] = 20; // compact protocol zigzag encoding of ALP (10)
            try
            {
                var candidate = PageHeaderReader.Read(file.AsSpan(offset));
                if (candidate == original with { Encoding = EncodingKind.Alp }) { changed = true; break; }
            }
            catch (Exception) { /* Not the encoding field. */ }
            file[i] = 0;
        }
        if (!changed) throw new InvalidOperationException("Could not locate the page encoding.");
        using var source = new MemoryReadSource(file);
        using var reader = schema.CreateReader(source);
        Assert.Throws<CorruptParquetException>(() =>
        {
            if (nested)
                foreach (var buffer in reader.RowGroups[0].NestedColumn<int>(0)) { _ = buffer.Count; }
            else
                foreach (var buffer in reader.RowGroups[0].Column<int>(0)) { _ = buffer.Count; }
        });
    }

    static void AssertRejected(PageHeaderType pageType, ParquetPhysicalType type, EncodingKind encoding, byte[] payload)
    {
        var column = new ParquetSchema([ColumnDefinition.RequiredLeaf("Value", type)]).LeafColumns[0].Column;
        var buffers = default(ColumnReadBuffers<int>);
        try
        {
            var header = Header(pageType, encoding);
            Assert.Throws<CorruptParquetException>(() => ColumnChunkReader.TryDecodeDictionaryPageIntoNative(
                header, payload, column, ref buffers, DefaultParquetBufferPool.Shared));
        }
        finally { buffers.Dispose(); }
    }

    static PageHeader Header(PageHeaderType type, EncodingKind encoding)
        => new(type, 5, 5, 1, encoding, HeaderLength: 1, RepetitionLevelsByteLength: 0,
            DefinitionLevelsByteLength: 0, NullCount: 0, IsCompressed: false,
            RepetitionLevelEncoding: EncodingKind.Rle, DefinitionLevelEncoding: EncodingKind.Rle, RowCount: 1);
}
