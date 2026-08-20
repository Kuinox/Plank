namespace Plank.Benchmarks.Published;

public static class PublishedReadBenchmarkCommand
{
    public static async Task RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        var root = PublishedBenchmarkCommand.FindRepositoryRoot();
        var options = PublishedBenchmarkCommand.CreateOptions(args);
        var dataDirectory = PublishedBenchmarkCommand.ReadValue(args, "--data-dir")
            ?? Path.Combine(root, "Plank.Benchmarks", "nyc-data");
        var output = PublishedBenchmarkCommand.ReadValue(args, "--output")
            ?? PublishedBenchmarkCommand.GetDefaultOutputPath(root, read: true, options);
        var synthetic = SyntheticBenchmarkData.Create(
            options.Quick ? options.QuickRows : options.SyntheticRows,
            options.Quick ? options.QuickWidth : options.SyntheticWidth,
            options.CaseId);
        IReadOnlyList<PublishedBenchmarkDataSet> realWorld = [];
        if (options.CaseId is null || TaxiBenchmarkData.IsCaseId(options.CaseId))
        {
            var taxiPath = await TaxiBenchmarkData.EnsureJanuary2024Async(dataDirectory, cancellationToken)
                .ConfigureAwait(false);
            Console.WriteLine("Preloading and converting January 2024 NYC Yellow Taxi data (not timed).");
            realWorld = TaxiBenchmarkData.Load(taxiPath, options.Quick ? options.QuickRows : null, options.CaseId);
        }
        var report = await PublishedReadBenchmarkRunner.RunAsync(realWorld, synthetic, options, cancellationToken)
            .ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(output)
            ?? throw new InvalidOperationException("The output path has no directory."));
        await File.WriteAllTextAsync(output, PublishedBenchmarkJson.Serialize(report), cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"Published benchmark snapshot: {output}");
    }
}
