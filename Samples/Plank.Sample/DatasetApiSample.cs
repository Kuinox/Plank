namespace Plank.Sample;

static class DatasetApiSample
{
    public static void Run()
    {
        // Keep the example's relative output paths in a fresh directory.
        var originalDirectory = Environment.CurrentDirectory;
        var directory = Path.Combine(Path.GetTempPath(), $"plank-sample-dataset-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            Environment.CurrentDirectory = directory;
            WriteStaticPaths();
            Verify("events/even.parquet", [0, 2, 4]);
            Verify("events/odd.parquet", [1, 3, 5]);
            // Reopening the same paths must append, including when sources are recycled.
            WriteStaticPaths();
            Verify("events/even.parquet", [0, 2, 4, 0, 2, 4]);
            Verify("events/odd.parquet", [1, 3, 5, 1, 3, 5]);
            WriteAllocatedPaths();
            for (var id = 0; id < 6; id++)
                Verify($"events/bucket={id}.parquet", [id]);
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Directory.Delete(directory, recursive: true);
        }
    }

    static void WriteStaticPaths()
    {
        #region StaticDataset
        using var file = new FileParquetSource();
        IParquetReadWriteSource[] files = [file];

        using var writer = EventSchema.CreateDatasetWriter(
            static (EventSchema row, IParquetBufferPool pool, out ParquetBuffer? allocation) =>
            {
                allocation = null;
                return row.Id % 2 == 0
                    ? "events/even.parquet"u8
                    : "events/odd.parquet"u8;
            },
            files);

        for (var id = 0; id < 6; id++)
            writer.Queue(new EventSchema { Id = id, Name = "event"u8.ToArray(), OccurredAt = DateTimeOffset.UtcNow });
        // Disposing the writer flushes all remaining rows and closes the output files.
        #endregion
    }

    static void WriteAllocatedPaths()
    {
        #region AllocatedDataset
        using var file = new FileParquetSource();
        IParquetReadWriteSource[] files = [file];

        using var writer = EventSchema.CreateDatasetWriter(
            static (EventSchema row, IParquetBufferPool pool, out ParquetBuffer? allocation) =>
            {
                var path = $"events/bucket={row.Id % 16}.parquet";
                var buffer = pool.Rent(checked((uint)Encoding.UTF8.GetByteCount(path)));
                var length = Encoding.UTF8.GetBytes(path, buffer.Span);
                allocation = buffer;
                return buffer.Span[..length];
            },
            files);

        for (var id = 0; id < 6; id++)
            writer.Queue(new EventSchema { Id = id, OccurredAt = DateTimeOffset.UtcNow });
        #endregion
    }

    static void Verify(string path, int[] expectedIds)
    {
        using var stream = File.OpenRead(path);
        using var reader = EventSchema.CreateRowReader(stream);
        var ids = new List<int>();
        while (reader.MoveNext())
            ids.Add(reader.Current.Id);
        // Dataset output ordering is not part of this example's contract.
        if (!ids.Order().SequenceEqual(expectedIds.Order()))
            throw new InvalidOperationException($"Dataset sample wrote unexpected rows to {path}.");
    }
}
