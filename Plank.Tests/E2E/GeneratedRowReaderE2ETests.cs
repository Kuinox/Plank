namespace Plank.Tests.E2E;

using Plank.Schema;

internal sealed class GeneratedRowReaderE2ETests
{
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
