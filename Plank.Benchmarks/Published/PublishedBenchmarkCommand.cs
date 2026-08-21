namespace Plank.Benchmarks.Published;

public static class PublishedBenchmarkCommand
{
    public static async Task RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        var root = FindRepositoryRoot();
        var options = CreateOptions(args);
        var dataDirectory = ReadValue(args, "--data-dir") ?? Path.Combine(root, "Plank.Benchmarks", "nyc-data");
        var output = ReadValue(args, "--output") ?? GetDefaultOutputPath(root, read: false, options);
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
        var report = await PublishedBenchmarkRunner.RunAsync(realWorld, synthetic, options, cancellationToken)
            .ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(output)
            ?? throw new InvalidOperationException("The output path has no directory."));
        await File.WriteAllTextAsync(output, PublishedBenchmarkJson.Serialize(report), cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"Published benchmark snapshot: {output}");
    }

    internal static string GetDefaultOutputPath(string root, bool read, PublishedBenchmarkOptions options)
    {
        var prefix = read ? "read" : "write";
        if (options.CaseId is not null)
            return Path.Combine(root, "artifacts", "benchmarks", $"{prefix}-case-v1.json");
        return Path.Combine(root, options.Quick
            ? $"artifacts/benchmarks/{prefix}-quick-v1.json"
            : $"docs/benchmarks/{prefix}-v1.json");
    }

    internal static PublishedBenchmarkOptions CreateOptions(string[] args)
    {
        var quick = args.Contains("--quick", StringComparer.Ordinal);
        return new PublishedBenchmarkOptions
        {
            Quick = quick,
            Warmups = ReadInt(args, "--warmups") ?? (quick ? 1 : 8),
            Iterations = ReadInt(args, "--iterations") ?? (quick ? 1 : 7),
            WorkerCount = ReadInt(args, "--workers") ?? Environment.ProcessorCount,
            SyntheticRows = ReadInt(args, "--synthetic-rows") ?? 1_000_000,
            SyntheticWidth = ReadInt(args, "--synthetic-width") ?? Environment.ProcessorCount,
            QuickRows = ReadInt(args, "--quick-rows") ?? 4_096,
            QuickWidth = ReadInt(args, "--quick-width") ?? Math.Min(4, Environment.ProcessorCount),
            CaseId = ReadValue(args, "--case")
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
