namespace Plank.Tests.E2E;

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
