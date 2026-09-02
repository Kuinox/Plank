namespace Plank.Tests.E2E;

using Plank.Schema;
using Plank.Writing;

internal sealed class GeneratedRowReaderE2ETests
{
    static readonly Plank.Reading.ParquetSchemaEvolutionOptions MissingColumnEvolution = new()
    {
        MissingColumns = Plank.Reading.MissingColumnEvolutionBehavior.MaterializeDefault,
        Repetition = Plank.Reading.RepetitionEvolutionBehavior.AllowRequiredToOptional,
        LogicalTypes = Plank.Reading.SchemaTypeEvolutionBehavior.AllowCompatible
    };

    [Test]
    public async Task GeneratedRowReaderReadsProjectedColumnsAcrossRowGroups()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-generated-row-reader-{Guid.NewGuid():N}.parquet");

        try
        {
            WriteEncodedRows(path);

            using var stream = File.OpenRead(path);
            using var reader = EncodedRowSchema.CreateRowReader(stream,
                EncodedRowSchema.Projection.Id | EncodedRowSchema.Projection.Tag);
            var ids = new List<ulong>();
            var tags = new List<byte[]?>();

            while (reader.MoveNext())
            {
                var row = reader.Current;
                ids.Add(row.Id);
                var tag = row.Tag;
                tags.Add(tag.IsNull ? null : tag.Span.ToArray());
                AssertUnprojectedDefaultValueThrows(row);
                AssertUnprojectedPayloadThrows(row);
            }

            await Assert.That(ids).IsEquivalentTo([10UL, 20UL, 30UL, 40UL]);
            AssertNullableByteArrays(tags, ["a"u8.ToArray(), null, "c"u8.ToArray(), "d"u8.ToArray()]);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public async Task GeneratedColumnReaderExposesTypedColumnsOnRowGroups()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-generated-column-reader-{Guid.NewGuid():N}.parquet");

        try
        {
            WriteEncodedRows(path);

            using var stream = File.OpenRead(path);
            using var reader = EncodedRowSchema.CreateReader(stream);
            var ids = new List<ulong>();
            var tags = new List<byte[]?>();

            foreach (var rowGroup in reader.RowGroups)
            {
                foreach (var buffer in rowGroup.IdColumn)
                    foreach (var id in buffer.Values)
                        ids.Add(id);

                foreach (var buffer in rowGroup.TagColumn)
                    for (var i = 0; i < buffer.Count; i++)
                        tags.Add(buffer.IsNull(i) ? null : buffer.GetValue(i).ToArray());
            }

            await Assert.That(reader.RowGroups.Count).IsEqualTo(2);
            await Assert.That(reader.RowGroups[0].RowCount).IsEqualTo(2UL);
            await Assert.That(reader.RowGroups[0].IdColumn.Metadata.ValueCount).IsEqualTo(2UL);
            await Assert.That(ids).IsEquivalentTo([10UL, 20UL, 30UL, 40UL]);
            AssertNullableByteArrays(tags, ["a"u8.ToArray(), null, "c"u8.ToArray(), "d"u8.ToArray()]);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public async Task GeneratedRowReaderMaterializesAddedLaterColumn()
    {
        using var stream = CreateEvolvingFile(includeAdded: false, addedOptional: false, idPhysicalType: Plank.Schema.ParquetPhysicalType.Int32,
            maybeOptional: true);
        using var reader = EvolvingRowSchema.CreateRowReader(stream,
            EvolvingRowSchema.Projection.Id | EvolvingRowSchema.Projection.Added, schemaEvolution: MissingColumnEvolution);

        var ids = new List<int>();
        var added = new List<int>();

        while (reader.MoveNext())
        {
            var row = reader.Current;
            ids.Add(row.Id);
            added.Add(row.Added);
        }

        await Assert.That(ids).IsEquivalentTo([1, 2, 3]);
        await Assert.That(added).IsEquivalentTo([0, 0, 0]);
    }

    [Test]
    public void GeneratedRowReaderBatchesPresentAndMissingColumnsAcrossEmptyRowGroupsAndBufferBoundaries()
    {
        const int rowCount = 4_097;
        using var stream = CreateBatchedEvolvingFile(rowCount, prependEmptyRowGroup: true);
        AssertBatchedEvolvingFileShape(stream.ToArray());
        using var reader = EvolvingRowSchema.CreateRowReader(stream,
            EvolvingRowSchema.Projection.Id | EvolvingRowSchema.Projection.Added,
            schemaEvolution: MissingColumnEvolution);

        var rowIndex = 0;
        while (reader.MoveNext())
        {
            var row = reader.Current;
            var expectedId = CreateBatchedId(rowIndex);
            if (row.Id != expectedId)
                throw new InvalidOperationException($"Expected id {expectedId}, got {row.Id}.");
            if (row.Added != 0)
                throw new InvalidOperationException(
                    $"Expected the missing column default at row {rowIndex + 1}, got {row.Added}.");
            rowIndex++;
        }

        if (rowIndex != rowCount)
            throw new InvalidOperationException($"Expected {rowCount} rows, got {rowIndex}.");
    }

    [Test]
    public void GeneratedRowReaderResetsBetweenBatchedAndMissingOnlyProjections()
    {
        const int rowCount = 513;
        using var source = CreateBatchedEvolvingFile(rowCount, prependEmptyRowGroup: true);
        var file = source.ToArray();
        using var reader = EvolvingRowSchema.CreateRowReader(source,
            EvolvingRowSchema.Projection.Id | EvolvingRowSchema.Projection.Maybe,
            schemaEvolution: MissingColumnEvolution);

        AssertBatchedEvolvingRows(reader, rowCount, includeId: true, includeMaybe: true, includeAdded: false);

        using var missingOnlySource = new MemoryStream(file, writable: false);
        reader.Reset(missingOnlySource, EvolvingRowSchema.Projection.Added);
        AssertBatchedEvolvingRows(reader, rowCount, includeId: false, includeMaybe: false, includeAdded: true);

        using var mixedSource = new MemoryStream(file, writable: false);
        reader.Reset(mixedSource, EvolvingRowSchema.Projection.Id | EvolvingRowSchema.Projection.Added);
        AssertBatchedEvolvingRows(reader, rowCount, includeId: true, includeMaybe: false, includeAdded: true);
    }

    [Test]
    public async Task GeneratedRowReaderAllowsRequiredFileColumnForOptionalGeneratedColumn()
    {
        using var stream = CreateEvolvingFile(includeAdded: true, addedOptional: false, idPhysicalType: Plank.Schema.ParquetPhysicalType.Int32,
            maybeOptional: false);
        using var reader = EvolvingRowSchema.CreateRowReader(stream,
            EvolvingRowSchema.Projection.Maybe, schemaEvolution: MissingColumnEvolution);

        var values = new List<int?>();
        while (reader.MoveNext())
            values.Add(reader.Current.Maybe);

        await Assert.That(values).IsEquivalentTo(new int?[] { 10, 20, 30 });
    }

    [Test]
    public async Task GeneratedRowReaderRejectsUnsafeOptionalToRequiredChange()
    {
        using var stream = CreateEvolvingFile(includeAdded: true, addedOptional: true, idPhysicalType: Plank.Schema.ParquetPhysicalType.Int32,
            maybeOptional: true);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => EvolvingRowSchema.CreateRowReader(stream,
                EvolvingRowSchema.Projection.Added,
                schemaEvolution: new Plank.Reading.ParquetSchemaEvolutionOptions
                {
                    Repetition = Plank.Reading.RepetitionEvolutionBehavior.AllowRequiredToOptionalAndOptionalToRequired
                })).ConfigureAwait(false));
    }

    [Test]
    public async Task GeneratedRowReaderRejectsPhysicalShapeChange()
    {
        using var stream = CreateEvolvingFile(includeAdded: true, addedOptional: false, idPhysicalType: Plank.Schema.ParquetPhysicalType.Int64,
            maybeOptional: true);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => EvolvingRowSchema.CreateRowReader(stream,
                EvolvingRowSchema.Projection.Id,
                schemaEvolution: new Plank.Reading.ParquetSchemaEvolutionOptions
                {
                    PhysicalTypes = Plank.Reading.SchemaTypeEvolutionBehavior.AllowCompatible,
                    MaterializedTypes = Plank.Reading.SchemaTypeEvolutionBehavior.AllowCompatible
                })).ConfigureAwait(false));
    }

    [Test]
    public async Task GeneratedRowReaderResetsAcrossMixedSchemaFiles()
    {
        using var oldFile = CreateEvolvingFile(includeAdded: false, addedOptional: false, idPhysicalType: Plank.Schema.ParquetPhysicalType.Int32,
            maybeOptional: true);
        using var newFile = CreateEvolvingFile(includeAdded: true, addedOptional: false, idPhysicalType: Plank.Schema.ParquetPhysicalType.Int32,
            maybeOptional: true);
        using var reader = EvolvingRowSchema.CreateRowReader(oldFile,
            EvolvingRowSchema.Projection.Id | EvolvingRowSchema.Projection.Added, schemaEvolution: MissingColumnEvolution);

        var first = ReadEvolvingRows(reader);
        reader.Reset(newFile, EvolvingRowSchema.Projection.Id | EvolvingRowSchema.Projection.Added);
        var second = ReadEvolvingRows(reader);

        await Assert.That(first).IsEquivalentTo([(1, 0), (2, 0), (3, 0)]);
        await Assert.That(second).IsEquivalentTo([(1, 100), (2, 200), (3, 300)]);
    }

    [Test]
    public async Task GeneratedRowReaderReadsAllColumns()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-generated-row-reader-all-{Guid.NewGuid():N}.parquet");

        try
        {
            WriteEncodedRows(path);

            using var stream = File.OpenRead(path);
            using var reader = EncodedRowSchema.CreateRowReader(stream);
            var payloads = new List<byte[]>();
            var defaultValues = new List<uint>();

            while (reader.MoveNext())
            {
                var row = reader.Current;
                payloads.Add(row.Payload.Span.ToArray());
                defaultValues.Add(row.DefaultValue);
            }

            await Assert.That(defaultValues).IsEquivalentTo([1U, 2U, 3U, 4U]);
            AssertByteArrays(payloads, [[1, 2], [3], [4, 5, 6], [7]]);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public void GeneratedRowReaderResolvesReorderedFileColumns()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-generated-row-reader-reordered-{Guid.NewGuid():N}.parquet");

        try
        {
            WriteReorderedRows(path);

            using var stream = File.OpenRead(path);
            using var reader = EncodedRowSchema.CreateRowReader(stream);
            if (!reader.MoveNext())
                throw new InvalidOperationException("Expected one generated row.");

            var row = reader.Current;
            if (row.Id != 42UL)
                throw new InvalidOperationException($"Expected id 42, got {row.Id}.");
            var tag = row.Tag;
            if (tag.IsNull || !tag.Span.SequenceEqual("tag"u8))
                throw new InvalidOperationException("Expected tag 'tag'.");
            if (row.DefaultValue != 9U)
                throw new InvalidOperationException($"Expected default value 9, got {row.DefaultValue}.");
            if (!row.Payload.Span.SequenceEqual(new byte[] { 8, 7 }))
                throw new InvalidOperationException("Payload was not read from the reordered file column.");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public async Task GeneratedRowReaderValidatesExpectedSchema()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-generated-row-reader-invalid-{Guid.NewGuid():N}.parquet");

        try
        {
            var schema = new ParquetSchema([
                ColumnDefinition.Leaf("id", ParquetPhysicalType.Int32),
                EncodedRowSchema.Schema.Definitions[1],
                EncodedRowSchema.Schema.Definitions[2],
                EncodedRowSchema.Schema.Definitions[3]
            ]);
            using (var stream = File.Create(path))
            {
                var writer = schema.CreateWriter(stream);
                var rowGroup = writer.StartRowGroup();
                var id = rowGroup.CreateSerializedColumn<int>(schema.LeafColumns[0]);
                id.Serialize([42]);
                rowGroup.Write(id);
                var tag = rowGroup.CreateSerializedColumn<byte[]>(schema.LeafColumns[1]);
                tag.Serialize(["tag"u8.ToArray()]);
                rowGroup.Write(tag);
                var payload = rowGroup.CreateSerializedColumn<byte[]>(schema.LeafColumns[2]);
                payload.Serialize([new byte[] { 8, 7 }]);
                rowGroup.Write(payload);
                var defaultValue = rowGroup.CreateSerializedColumn<uint>(schema.LeafColumns[3]);
                defaultValue.Serialize([9U]);
                rowGroup.Write(defaultValue);
                writer.CloseFile();
            }

            using var stream2 = File.OpenRead(path);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Task.Run(() => EncodedRowSchema.CreateRowReader(stream2)));
            await Assert.That(ex.Message).Contains("physical type");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    static List<(int Id, int Added)> ReadEvolvingRows(EvolvingRowSchema.RowReader reader)
    {
        var rows = new List<(int Id, int Added)>();
        while (reader.MoveNext())
        {
            var row = reader.Current;
            rows.Add((row.Id, row.Added));
        }

        return rows;
    }

    static MemoryStream CreateEvolvingFile(bool includeAdded, bool addedOptional, Plank.Schema.ParquetPhysicalType idPhysicalType,
        bool maybeOptional)
    {
        var columns = new List<Plank.Schema.ColumnDefinition>
        {
            Plank.Schema.ColumnDefinition.Leaf("id", idPhysicalType)
        };
        if (includeAdded)
        {
            var repetition = addedOptional ? Plank.Schema.ParquetRepetition.Optional : Plank.Schema.ParquetRepetition.Required;
            columns.Add(Plank.Schema.ColumnDefinition.Leaf("added", Plank.Schema.ParquetPhysicalType.Int32,
                new Plank.Schema.ColumnOptions(repetition)));
        }

        columns.Add(Plank.Schema.ColumnDefinition.Leaf("maybe", Plank.Schema.ParquetPhysicalType.Int32,
            new Plank.Schema.ColumnOptions(maybeOptional ? Plank.Schema.ParquetRepetition.Optional : Plank.Schema.ParquetRepetition.Required)));

        var schema = new Plank.Schema.ParquetSchema([.. columns]);
        var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream);
        var rowGroup = writer.StartRowGroup();

        if (idPhysicalType == Plank.Schema.ParquetPhysicalType.Int64)
        {
            var id = rowGroup.CreateSerializedColumn<long>(schema.LeafColumns[0]);
            id.Serialize([1L, 2L, 3L]);
            rowGroup.Write(id);
        }
        else
        {
            var id = rowGroup.CreateSerializedColumn<int>(schema.LeafColumns[0]);
            id.Serialize([1, 2, 3]);
            rowGroup.Write(id);
        }

        var maybeOrdinal = 1;
        if (includeAdded)
        {
            if (addedOptional)
            {
                var added = rowGroup.CreateSerializedColumn<int?>(schema.LeafColumns[1]);
                added.Serialize([100, null, 300]);
                rowGroup.Write(added);
            }
            else
            {
                var added = rowGroup.CreateSerializedColumn<int>(schema.LeafColumns[1]);
                added.Serialize([100, 200, 300]);
                rowGroup.Write(added);
            }

            maybeOrdinal = 2;
        }

        if (maybeOptional)
        {
            var maybe = rowGroup.CreateSerializedColumn<int?>(schema.LeafColumns[maybeOrdinal]);
            maybe.Serialize([10, null, 30]);
            rowGroup.Write(maybe);
        }
        else
        {
            var maybe = rowGroup.CreateSerializedColumn<int>(schema.LeafColumns[maybeOrdinal]);
            maybe.Serialize([10, 20, 30]);
            rowGroup.Write(maybe);
        }

        writer.CloseFile();
        return new MemoryStream(stream.ToArray());
    }

    static MemoryStream CreateBatchedEvolvingFile(int rowCount, bool prependEmptyRowGroup)
    {
        var schema = CreateBatchedEvolvingSchema();
        var stream = new MemoryStream();
        using var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            TargetDataPageSizeBytes = 63
        });

        if (prependEmptyRowGroup)
            WriteBatchedEvolvingRowGroup(writer, schema, [], []);

        var ids = new int[rowCount];
        var maybe = new int?[rowCount];
        for (var i = 0; i < rowCount; i++)
        {
            ids[i] = CreateBatchedId(i);
            maybe[i] = i % 5 == 1 ? null : (i + 1) * 10;
        }
        WriteBatchedEvolvingRowGroup(writer, schema, ids, maybe);

        writer.CloseFile();
        return new MemoryStream(stream.ToArray(), writable: false);
    }

    static ParquetSchema CreateBatchedEvolvingSchema()
        => new([
            ColumnDefinition.Leaf("id", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: [EncodingKind.DeltaBinaryPacked])),
            ColumnDefinition.Leaf("maybe", ParquetPhysicalType.Int32,
                new ColumnOptions(ParquetRepetition.Optional, [EncodingKind.Plain]))
        ]);

    static void AssertBatchedEvolvingFileShape(byte[] file)
    {
        var schema = CreateBatchedEvolvingSchema();
        using var stream = new MemoryStream(file, writable: false);
        using var reader = schema.CreateReader(stream);
        if (reader.RowGroups.Count != 2 || reader.RowGroups[0].RowCount != 0)
            throw new InvalidOperationException("Expected an empty row group before the populated row group.");

        var rowGroup = reader.RowGroups[1];
        var idBuffers = new List<int>();
        foreach (var buffer in rowGroup.Column<int>(schema.LeafColumns[0]))
            idBuffers.Add(buffer.Count);
        var maybeBuffers = new List<int>();
        foreach (var buffer in rowGroup.Column<int?>(schema.LeafColumns[1]))
            maybeBuffers.Add(buffer.Count);

        if (idBuffers.Count != 1 || maybeBuffers.Count <= 1)
            throw new InvalidOperationException(
                $"Expected one id buffer and multiple optional buffers; got {idBuffers.Count} and {maybeBuffers.Count}.");
    }

    static void WriteBatchedEvolvingRowGroup(ParquetWriter writer, ParquetSchema schema,
        ReadOnlySpan<int> ids, ReadOnlySpan<int?> maybe)
    {
        var rowGroup = writer.StartRowGroup();
        var id = rowGroup.CreateSerializedColumn<int>(schema.LeafColumns[0]);
        id.Serialize(ids);
        rowGroup.Write(id);
        var optional = rowGroup.CreateSerializedColumn<int?>(schema.LeafColumns[1]);
        optional.Serialize(maybe);
        rowGroup.Write(optional);
    }

    static void AssertBatchedEvolvingRows(EvolvingRowSchema.RowReader reader, int rowCount,
        bool includeId, bool includeMaybe, bool includeAdded)
    {
        var index = 0;
        while (reader.MoveNext())
        {
            var row = reader.Current;
            var expectedId = CreateBatchedId(index);
            if (includeId && row.Id != expectedId)
                throw new InvalidOperationException($"Expected id {expectedId}, got {row.Id}.");
            if (includeMaybe)
            {
                int? expectedMaybe = index % 5 == 1 ? null : (index + 1) * 10;
                if (row.Maybe != expectedMaybe)
                    throw new InvalidOperationException(
                        $"Expected optional value {expectedMaybe} at row {index + 1}, got {row.Maybe}.");
            }
            if (includeAdded && row.Added != 0)
                throw new InvalidOperationException(
                    $"Expected the missing column default at row {index + 1}, got {row.Added}.");
            index++;
        }

        if (index != rowCount)
            throw new InvalidOperationException($"Expected {rowCount} rows, got {index}.");
    }

    static int CreateBatchedId(int index)
        => unchecked((index * 1_103_515_245 + 12_345) ^ (index << 16));

    static void WriteEncodedRows(string path)
    {
        using var stream = File.Create(path);
        var writer = EncodedRowSchema.CreateWriter(stream);

        var first = writer.StartRowGroup();
        first.Id.Serialize([10UL, 20UL]);
        first.Write(first.Id);
        first.Tag.Serialize(["a"u8.ToArray(), null]);
        first.Write(first.Tag);
        first.Payload.Serialize([new byte[] { 1, 2 }, new byte[] { 3 }]);
        first.Write(first.Payload);
        first.DefaultValue.Serialize([1U, 2U]);
        first.Write(first.DefaultValue);

        var second = writer.StartRowGroup();
        second.Id.Serialize([30UL, 40UL]);
        second.Write(second.Id);
        second.Tag.Serialize(["c"u8.ToArray(), "d"u8.ToArray()]);
        second.Write(second.Tag);
        second.Payload.Serialize([new byte[] { 4, 5, 6 }, new byte[] { 7 }]);
        second.Write(second.Payload);
        second.DefaultValue.Serialize([3U, 4U]);
        second.Write(second.DefaultValue);

        writer.CloseFile();
    }

    static void WriteReorderedRows(string path)
    {
        var schema = new ParquetSchema([
            EncodedRowSchema.Schema.Definitions[2],
            EncodedRowSchema.Schema.Definitions[0],
            EncodedRowSchema.Schema.Definitions[3],
            EncodedRowSchema.Schema.Definitions[1]
        ]);

        using var stream = File.Create(path);
        var writer = schema.CreateWriter(stream);
        var rowGroup = writer.StartRowGroup();

        var payload = rowGroup.CreateSerializedColumn<byte[]>(schema.LeafColumns[0]);
        payload.Serialize([new byte[] { 8, 7 }]);
        rowGroup.Write(payload);

        var id = rowGroup.CreateSerializedColumn<ulong>(schema.LeafColumns[1]);
        id.Serialize([42UL]);
        rowGroup.Write(id);

        var defaultValue = rowGroup.CreateSerializedColumn<uint>(schema.LeafColumns[2]);
        defaultValue.Serialize([9U]);
        rowGroup.Write(defaultValue);

        var tag = rowGroup.CreateSerializedColumn<byte[]>(schema.LeafColumns[3]);
        tag.Serialize(["tag"u8.ToArray()]);
        rowGroup.Write(tag);

        writer.CloseFile();
    }

    static void AssertUnprojectedDefaultValueThrows(EncodedRowSchema.ReadRow row)
    {
        try
        {
            _ = row.DefaultValue;
            throw new InvalidOperationException("Expected skipped column access to throw.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("was not selected", StringComparison.Ordinal))
        {
        }
    }

    static void AssertUnprojectedPayloadThrows(EncodedRowSchema.ReadRow row)
    {
        try
        {
            _ = row.Payload;
            throw new InvalidOperationException("Expected skipped binary column access to throw.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("was not selected", StringComparison.Ordinal))
        {
        }
    }

    static void AssertByteArrays(IReadOnlyList<byte[]> actual, IReadOnlyList<byte[]> expected)
    {
        if (actual.Count != expected.Count)
            throw new InvalidOperationException($"Expected {expected.Count} byte arrays, got {actual.Count}.");

        for (var i = 0; i < actual.Count; i++)
            if (!actual[i].AsSpan().SequenceEqual(expected[i]))
                throw new InvalidOperationException($"Byte array at index {i} did not match.");
    }

    static void AssertNullableByteArrays(IReadOnlyList<byte[]?> actual, IReadOnlyList<byte[]?> expected)
    {
        if (actual.Count != expected.Count)
            throw new InvalidOperationException($"Expected {expected.Count} byte arrays, got {actual.Count}.");

        for (var i = 0; i < actual.Count; i++)
        {
            if (actual[i] is null || expected[i] is null)
            {
                if (actual[i] is not null || expected[i] is not null)
                    throw new InvalidOperationException($"Byte array nullability at index {i} did not match.");
                continue;
            }

            if (!actual[i]!.AsSpan().SequenceEqual(expected[i]))
                throw new InvalidOperationException($"Byte array at index {i} did not match.");
        }
    }
}
