namespace Plank.Tests.E2E;

internal sealed class WideRowProjectionTests
{
    [Test]
    public void GeneratedProjectionDistinguishesColumnsBeyondUlongWidth()
    {
        var file = CreateFile();

        using (var stream = new MemoryStream(file))
        using (var reader = WideRowSchema.CreateRowReader(stream, WideRowSchema.Projection.Column64))
        {
            AssertSingleRow(reader);
            if (reader.Current.Column64 != 64)
                throw new InvalidOperationException($"Expected column 64 to contain 64, got {reader.Current.Column64}.");
            AssertColumn0WasNotSelected(reader.Current);
        }

        using (var stream = new MemoryStream(file))
        using (var reader = WideRowSchema.CreateRowReader(stream, WideRowSchema.Projection.Column0))
        {
            AssertSingleRow(reader);
            if (reader.Current.Column0 != 0)
                throw new InvalidOperationException($"Expected column 0 to contain 0, got {reader.Current.Column0}.");
            AssertColumn64WasNotSelected(reader.Current);
        }

        using (var stream = new MemoryStream(file))
        using (var reader = WideRowSchema.CreateRowReader(stream,
                   WideRowSchema.Projection.Column0 | WideRowSchema.Projection.Column64))
        {
            AssertSingleRow(reader);
            if (reader.Current.Column0 != 0 || reader.Current.Column64 != 64)
                throw new InvalidOperationException("Combined projection did not materialize both selected columns.");
        }
    }

    static byte[] CreateFile()
    {
        using var stream = new MemoryStream();
        var writer = WideRowSchema.Schema.CreateWriter(stream);
        var rowGroup = writer.StartRowGroup();
        for (var i = 0; i < WideRowSchema.Schema.LeafColumns.Length; i++)
        {
            var column = rowGroup.CreateSerializedColumn<int>(WideRowSchema.Schema.LeafColumns[i]);
            column.Serialize([i]);
            rowGroup.Write(column);
        }

        writer.CloseFile();
        return stream.ToArray();
    }

    static void AssertSingleRow(WideRowSchema.RowReader reader)
    {
        if (!reader.MoveNext())
            throw new InvalidOperationException("Expected one generated row.");
    }

    static void AssertColumn0WasNotSelected(WideRowSchema.Row row)
    {
        try
        {
            _ = row.Column0;
            throw new InvalidOperationException("Expected column 0 access to throw.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("was not selected", StringComparison.Ordinal))
        {
        }
    }

    static void AssertColumn64WasNotSelected(WideRowSchema.Row row)
    {
        try
        {
            _ = row.Column64;
            throw new InvalidOperationException("Expected column 64 access to throw.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("was not selected", StringComparison.Ordinal))
        {
        }
    }
}
