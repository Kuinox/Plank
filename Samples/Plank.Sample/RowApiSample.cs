namespace Plank.Sample;

static class RowApiSample
{
    internal static readonly DateTimeOffset ExampleTime =
        new(2026, 1, 2, 12, 30, 0, TimeSpan.FromHours(2));

    public static string Run()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-sample-row-{Guid.NewGuid():N}.parquet");
        WriteRows(path);
        ReadRows(path);
        ReadExplicitly(path);
        ReadSelectedRows(path);
        VerifyRows(path);
        return path;
    }

    static void WriteRows(string path)
    {
        #region WriteRows
        using var stream = File.Create(path);
        using var writer = EventSchema.CreateRowWriter(stream);

        var occurredAt = new DateTimeOffset(2026, 1, 2, 12, 30, 0, TimeSpan.FromHours(2));
        for (var id = 1; id <= 3; id++)
        {
            var row = writer.GetRow();
            row.Id = id;
            row.Name = id switch
            {
                1 => "created"u8.ToArray(),
                2 => null,
                _ => []
            };
            row.OccurredAt = occurredAt;
        }

        writer.Complete();
        #endregion
    }

    static void ReadRows(string path)
    {
        #region ReadRows
        using var stream = File.OpenRead(path);
        using EventSchema.RowReader reader = EventSchema.CreateRowReader(stream);

        foreach (EventSchema.ReadRow row in reader)
        {
            string? name = row.Name.IsNull ? null : Encoding.UTF8.GetString(row.Name.Value);
            Console.WriteLine($"{row.Id}: {name ?? "<null>"} at {row.OccurredAt:O}");
        }
        #endregion
    }

    static void ReadExplicitly(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = EventSchema.CreateRowReader(stream);
        #region ReadExplicitly
        while (reader.MoveNext())
        {
            EventSchema.ReadRow row = reader.Current;
            Console.WriteLine(row.Id);
        }
        #endregion
    }

    static void ReadSelectedRows(string path)
    {
        #region ReadSelectedRows
        EventSchema.Projection projection = EventSchema.Projection.Id |
            EventSchema.Projection.Name;

        using var stream = File.OpenRead(path);
        using EventSchema.RowReader reader = EventSchema.CreateRowReader(stream, projection);

        foreach (EventSchema.ReadRow row in reader)
        {
            string? name = row.Name.IsNull ? null : Encoding.UTF8.GetString(row.Name.Value);
            Console.WriteLine($"{row.Id}: {name ?? "<null>"}");
        }
        #endregion
    }

    static void VerifyRows(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = EventSchema.CreateRowReader(stream);
        var count = 0;
        while (reader.MoveNext())
        {
            var row = reader.Current;
            count++;
            if (row.Id != count || row.OccurredAt != ExampleTime || row.OccurredAt.Offset != TimeSpan.Zero)
                throw new InvalidOperationException("The row sample did not preserve IDs and timestamp instants.");
            var nameMatches = count switch
            {
                1 => !row.Name.IsNull && row.Name.Value.SequenceEqual("created"u8),
                2 => row.Name.IsNull,
                3 => !row.Name.IsNull && row.Name.Value.IsEmpty,
                _ => false
            };
            if (!nameMatches)
                throw new InvalidOperationException("The row sample did not preserve null, empty, and populated binary values.");
        }
        if (count != 3)
            throw new InvalidOperationException($"Expected 3 sample rows, got {count}.");
    }
}
