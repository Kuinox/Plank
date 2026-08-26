namespace Plank.Sample;

static class RowApiSample
{
    public static string Run()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-sample-row-{Guid.NewGuid():N}.parquet");

        using var stream = File.Create(path);
        using var rowWriter = EventSchema.CreateRowWriter(stream);

        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 3; i++)
        {
            var row = rowWriter.GetRow();
            row.Id = i + 1;
            row.Name = i switch
            {
                0 => "created"u8.ToArray(),
                1 => "updated"u8.ToArray(),
                _ => "deleted"u8.ToArray()
            };
            row.OccurredAt = now.AddMinutes(i);
            rowWriter.Next();
        }

        rowWriter.Complete();
        return path;
    }
}
