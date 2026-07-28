namespace Plank.Tests.E2E;

internal sealed class GuidByteStreamSplitTests
{
    [Test]
    public void GeneratedGuidColumnSupportsDeclaredByteStreamSplitEncoding()
    {
        var expected = new[]
        {
            Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
            Guid.Parse("ffeeddcc-bbaa-9988-7766-554433221100")
        };

        using var stream = new MemoryStream();
        var writer = GuidByteStreamSplitRowSchema.CreateWriter(stream);
        var rowGroup = writer.StartRowGroup();
        rowGroup.Id.Serialize(expected);
        rowGroup.Write(rowGroup.Id);
        writer.CloseFile();

        stream.Position = 0;
        using var reader = GuidByteStreamSplitRowSchema.CreateRowReader(stream);
        var actual = new List<Guid>();
        while (reader.MoveNext())
            actual.Add(reader.Current.Id);

        if (!actual.SequenceEqual(expected))
            throw new InvalidOperationException("Guid values encoded with BYTE_STREAM_SPLIT did not round-trip.");
    }
}
