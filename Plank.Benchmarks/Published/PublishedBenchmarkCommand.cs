namespace Plank.Benchmarks.Published;

public static class PublishedBenchmarkCommand
{
    public static async Task RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        var root = FindRepositoryRoot();
        var quick = args.Contains("--quick", StringComparer.Ordinal);
        var dataDirectory = ReadValue(args, "--data-dir") ?? Path.Combine(root, "Plank.Benchmarks", "nyc-data");
        var output = ReadValue(args, "--output") ?? Path.Combine(root,
            quick ? "artifacts/benchmarks/write-quick-v1.json" : "docs/benchmarks/write-v1.json");
        var options = CreateOptions(args);
        var taxiPath = await TaxiBenchmarkData.EnsureJanuary2024Async(dataDirectory, cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine("Preloading and converting January 2024 NYC Yellow Taxi data (not timed).");
        var realWorld = TaxiBenchmarkData.Load(taxiPath, quick ? options.QuickRows : null);
        var synthetic = SyntheticBenchmarkData.Create(
            quick ? options.QuickRows : options.SyntheticRows,
            quick ? options.QuickWidth : options.SyntheticWidth);
        var report = await PublishedBenchmarkRunner.RunAsync(realWorld, synthetic, options, cancellationToken)
            .ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(output)
            ?? throw new InvalidOperationException("The output path has no directory."));
        await File.WriteAllTextAsync(output, PublishedBenchmarkJson.Serialize(report), cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"Published benchmark snapshot: {output}");
    }

    internal static PublishedBenchmarkOptions CreateOptions(string[] args)
    {
        var quick = args.Contains("--quick", StringComparer.Ordinal);
        return new PublishedBenchmarkOptions
        {
            Quick = quick,
            Warmups = ReadInt(args, "--warmups") ?? (quick ? 1 : 2),
            Iterations = ReadInt(args, "--iterations") ?? (quick ? 1 : 7),
            WorkerCount = ReadInt(args, "--workers") ?? Environment.ProcessorCount,
            SyntheticRows = ReadInt(args, "--synthetic-rows") ?? 1_000_000,
            SyntheticWidth = ReadInt(args, "--synthetic-width") ?? Environment.ProcessorCount,
            QuickRows = ReadInt(args, "--quick-rows") ?? 4_096,
            QuickWidth = ReadInt(args, "--quick-width") ?? Math.Min(4, Environment.ProcessorCount)
        };
    }

    internal static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null;
             directory = directory.Parent)
        {
            // A linked worktree has a .git *file* holding a gitdir pointer, not a
            // directory, so testing only for a directory made the published
            // benchmarks refuse to run from any worktree — which is exactly where
            // you run them when the primary checkout is busy doing something else.
            var marker = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(marker) || File.Exists(marker))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not find the Plank repository root.");
    }

    internal static string? ReadValue(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0)
            return null;
        if (index == args.Length - 1)
            throw new ArgumentException($"{name} requires a value.");
        return args[index + 1];
    }

    static int? ReadInt(string[] args, string name)
        => ReadValue(args, name) is { } value ? int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) : null;
}
