namespace Plank.Tests.E2E;

using Plank.Schema;

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
            var tags = new List<string?>();

            while (reader.MoveNext())
            {
                var row = reader.Current;
                ids.Add(row.Id);
                tags.Add(row.Tag);
                AssertUnprojectedDefaultValueThrows(row);
            }

            await Assert.That(ids).IsEquivalentTo([10UL, 20UL, 30UL, 40UL]);
            await Assert.That(tags).IsEquivalentTo(["a", null, "c", "d"]);
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
                payloads.Add(row.Payload);
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
            if (row.Tag != "tag")
                throw new InvalidOperationException($"Expected tag 'tag', got '{row.Tag}'.");
            if (row.DefaultValue != 9U)
                throw new InvalidOperationException($"Expected default value 9, got {row.DefaultValue}.");
            if (!row.Payload.AsSpan().SequenceEqual(new byte[] { 8, 7 }))
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
                new Column("id", ParquetPhysicalType.Int32),
                EncodedRowSchema.Schema.Columns[1],
                EncodedRowSchema.Schema.Columns[2],
                EncodedRowSchema.Schema.Columns[3]
            ]);
            using (var stream = File.Create(path))
            {
                var writer = schema.CreateWriter(stream);
                var rowGroup = writer.StartRowGroup();
                var id = rowGroup.CreateSerializedColumn<int>(schema.Columns[0]);
                id.Serialize([42]);
                rowGroup.Write(id);
                var tag = rowGroup.CreateSerializedColumn<string>(schema.Columns[1]);
                tag.Serialize(["tag"]);
                rowGroup.Write(tag);
                var payload = rowGroup.CreateSerializedColumn<byte[]>(schema.Columns[2]);
                payload.Serialize([new byte[] { 8, 7 }]);
                rowGroup.Write(payload);
                var defaultValue = rowGroup.CreateSerializedColumn<uint>(schema.Columns[3]);
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
        var columns = new List<Plank.Schema.Column>
        {
            new("id", idPhysicalType)
        };
        if (includeAdded)
        {
            var repetition = addedOptional ? Plank.Schema.ParquetRepetition.Optional : Plank.Schema.ParquetRepetition.Required;
            columns.Add(new Plank.Schema.Column("added", Plank.Schema.ParquetPhysicalType.Int32,
                new Plank.Schema.ColumnOptions(repetition)));
        }

        columns.Add(new Plank.Schema.Column("maybe", Plank.Schema.ParquetPhysicalType.Int32,
            new Plank.Schema.ColumnOptions(maybeOptional ? Plank.Schema.ParquetRepetition.Optional : Plank.Schema.ParquetRepetition.Required)));

        var schema = new Plank.Schema.ParquetSchema([.. columns]);
        var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream);
        var rowGroup = writer.StartRowGroup();

        if (idPhysicalType == Plank.Schema.ParquetPhysicalType.Int64)
        {
            var id = rowGroup.CreateSerializedColumn<long>(schema.Columns[0]);
            id.Serialize([1L, 2L, 3L]);
            rowGroup.Write(id);
        }
        else
        {
            var id = rowGroup.CreateSerializedColumn<int>(schema.Columns[0]);
            id.Serialize([1, 2, 3]);
            rowGroup.Write(id);
        }

        var maybeOrdinal = 1;
        if (includeAdded)
        {
            if (addedOptional)
            {
                var added = rowGroup.CreateSerializedColumn<int?>(schema.Columns[1]);
                added.Serialize([100, null, 300]);
                rowGroup.Write(added);
            }
            else
            {
                var added = rowGroup.CreateSerializedColumn<int>(schema.Columns[1]);
                added.Serialize([100, 200, 300]);
                rowGroup.Write(added);
            }

            maybeOrdinal = 2;
        }

        if (maybeOptional)
        {
            var maybe = rowGroup.CreateSerializedColumn<int?>(schema.Columns[maybeOrdinal]);
            maybe.Serialize([10, null, 30]);
            rowGroup.Write(maybe);
        }
        else
        {
            var maybe = rowGroup.CreateSerializedColumn<int>(schema.Columns[maybeOrdinal]);
            maybe.Serialize([10, 20, 30]);
            rowGroup.Write(maybe);
        }

        writer.CloseFile();
        return new MemoryStream(stream.ToArray());
    }

    static void WriteEncodedRows(string path)
    {
        using var stream = File.Create(path);
        var writer = EncodedRowSchema.CreateWriter(stream);

        var first = writer.StartRowGroup();
        first.Id.Serialize([10UL, 20UL]);
        first.Write(first.Id);
        first.Tag.Serialize(["a", null]);
        first.Write(first.Tag);
        first.Payload.Serialize([new byte[] { 1, 2 }, new byte[] { 3 }]);
        first.Write(first.Payload);
        first.DefaultValue.Serialize([1U, 2U]);
        first.Write(first.DefaultValue);

        var second = writer.StartRowGroup();
        second.Id.Serialize([30UL, 40UL]);
        second.Write(second.Id);
        second.Tag.Serialize(["c", "d"]);
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
            EncodedRowSchema.Schema.Columns[2],
            EncodedRowSchema.Schema.Columns[0],
            EncodedRowSchema.Schema.Columns[3],
            EncodedRowSchema.Schema.Columns[1]
        ]);

        using var stream = File.Create(path);
        var writer = schema.CreateWriter(stream);
        var rowGroup = writer.StartRowGroup();

        var payload = rowGroup.CreateSerializedColumn<byte[]>(schema.Columns[0]);
        payload.Serialize([new byte[] { 8, 7 }]);
        rowGroup.Write(payload);

        var id = rowGroup.CreateSerializedColumn<ulong>(schema.Columns[1]);
        id.Serialize([42UL]);
        rowGroup.Write(id);

        var defaultValue = rowGroup.CreateSerializedColumn<uint>(schema.Columns[2]);
        defaultValue.Serialize([9U]);
        rowGroup.Write(defaultValue);

        var tag = rowGroup.CreateSerializedColumn<string>(schema.Columns[3]);
        tag.Serialize(["tag"]);
        rowGroup.Write(tag);

        writer.CloseFile();
    }

    static void AssertUnprojectedDefaultValueThrows(EncodedRowSchema.Row row)
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

    static void AssertByteArrays(IReadOnlyList<byte[]> actual, IReadOnlyList<byte[]> expected)
    {
        if (actual.Count != expected.Count)
            throw new InvalidOperationException($"Expected {expected.Count} byte arrays, got {actual.Count}.");

        for (var i = 0; i < actual.Count; i++)
            if (!actual[i].AsSpan().SequenceEqual(expected[i]))
                throw new InvalidOperationException($"Byte array at index {i} did not match.");
    }
}
