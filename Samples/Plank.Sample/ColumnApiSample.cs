namespace Plank.Sample;

static class ColumnApiSample
{
    public static string Run()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-sample-column-{Guid.NewGuid():N}.parquet");

        using var stream = File.Create(path);
        var writer = EventSchema.CreateWriter(stream);
        var rowGroup = writer.StartRowGroup();

        var ids = rowGroup.Id;
        ids.Serialize([1, 2, 3]);
        rowGroup.Write(ids);

        var names = rowGroup.Name;
        names.Serialize(["created", "updated", "deleted"]);
        rowGroup.Write(names);

        var times = rowGroup.OccurredAt;
        times.Serialize([
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(1),
            DateTimeOffset.UtcNow.AddMinutes(2)
        ]);
        rowGroup.Write(times);

        writer.CloseFile();
        return path;
    }
}
