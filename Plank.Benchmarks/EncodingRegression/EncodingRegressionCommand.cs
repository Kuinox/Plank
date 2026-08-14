using System.Globalization;
using Plank.Benchmarks.Published;

namespace Plank.Benchmarks.EncodingRegression;

/// <summary>
/// <c>--encoding-regression</c> measures every encoder path and records a hash of each encoded file.
/// <c>--encoding-regression-compare baseline.json current.json</c> reports throughput deltas and,
/// more importantly, any case whose encoded bytes changed. Deltas compare the fastest observed
/// iteration, which is far more stable than the median on a shared runner.
/// </summary>
public static class EncodingRegressionCommand
{
    const double DefaultToleranceRatio = 1.10;

    // Cases this cheap are dominated by scheduler noise on a shared runner: bool/plain/required
    // encodes 200k values in ~30 us, where a few microseconds of jitter reads as tens of percent.
    // They are still printed and still gated on encoded bytes, just not failed on time.
    const double DefaultSignificanceFloorMicroseconds = 100;

    public static async Task RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        var root = PublishedBenchmarkCommand.FindRepositoryRoot();
        var label = PublishedBenchmarkCommand.ReadValue(args, "--label") ?? "current";
        var output = PublishedBenchmarkCommand.ReadValue(args, "--output")
                     ?? Path.Combine(root, "artifacts", "benchmarks", $"encoding-regression-{label}.json");
        var options = CreateOptions(args);
        var report = EncodingRegressionRunner.Run(options, label, ReadCommit(root));

        Directory.CreateDirectory(Path.GetDirectoryName(output)
                                  ?? throw new InvalidOperationException("The output path has no directory."));
        await File.WriteAllTextAsync(output, EncodingRegressionJson.Serialize(report), cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"Encoding regression snapshot: {output}");
    }

    public static async Task<int> CompareAsync(string[] args, CancellationToken cancellationToken = default)
    {
        if (args.Length < 2)
            throw new ArgumentException(
                "--encoding-regression-compare requires a baseline path and a current path.", nameof(args));

        var baseline = EncodingRegressionJson.Deserialize(
            await File.ReadAllTextAsync(args[0], cancellationToken).ConfigureAwait(false));
        var current = EncodingRegressionJson.Deserialize(
            await File.ReadAllTextAsync(args[1], cancellationToken).ConfigureAwait(false));
        var tolerance = PublishedBenchmarkCommand.ReadValue(args, "--tolerance") is { } raw
            ? double.Parse(raw, CultureInfo.InvariantCulture)
            : DefaultToleranceRatio;
        var floor = PublishedBenchmarkCommand.ReadValue(args, "--significance-floor") is { } rawFloor
            ? double.Parse(rawFloor, CultureInfo.InvariantCulture)
            : DefaultSignificanceFloorMicroseconds;

        return Compare(baseline, current, tolerance, floor);
    }

    static int Compare(EncodingRegressionReport baseline, EncodingRegressionReport current, double tolerance,
        double significanceFloorMicroseconds)
    {
        var baselineCases = baseline.Cases.ToDictionary(static result => result.Id, StringComparer.Ordinal);
        var outputChanges = new List<string>();
        var statusChanges = new List<string>();
        var slower = new List<string>();

        Console.WriteLine($"baseline: {baseline.Label} ({baseline.Commit})");
        Console.WriteLine($"current:  {current.Label} ({current.Commit})");
        Console.WriteLine();
        Console.WriteLine($"{"case",-46} {"baseline min",13} {"current min",13} {"delta",9}  output");
        Console.WriteLine(new string('-', 96));

        foreach (var currentCase in current.Cases)
        {
            if (!baselineCases.TryGetValue(currentCase.Id, out var baselineCase))
            {
                Console.WriteLine($"{currentCase.Id,-46} {"(new case)",51}");
                continue;
            }

            if (baselineCase.Status != currentCase.Status)
                statusChanges.Add($"{currentCase.Id}: {baselineCase.Status} -> {currentCase.Status}"
                                  + (currentCase.Error is { } error ? $" ({error})" : ""));

            var outputState = "same";
            if (baselineCase.OutputSha256 != currentCase.OutputSha256)
            {
                outputState = "CHANGED";
                outputChanges.Add(
                    $"{currentCase.Id}: {baselineCase.OutputBytes} bytes -> {currentCase.OutputBytes} bytes");
            }

            if (baselineCase.MinMicroseconds is not { } baselineMin
                || currentCase.MinMicroseconds is not { } currentMin)
            {
                Console.WriteLine($"{currentCase.Id,-46} {"-",13} {"-",13} {"-",9}  {outputState}");
                continue;
            }

            var ratio = currentMin / baselineMin;
            var delta = (ratio - 1) * 100;
            var significant = baselineMin >= significanceFloorMicroseconds;
            var deltaText = FormatDelta(delta);
            if (ratio > tolerance && significant)
                slower.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{currentCase.Id}: {baselineMin:N1} us -> {currentMin:N1} us ({deltaText})"));

            var note = significant ? "" : "  (below noise floor)";
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{currentCase.Id,-46} {baselineMin,13:N1} {currentMin,13:N1} {deltaText,9}  {outputState}{note}"));
        }

        Console.WriteLine();
        var failed = false;

        if (outputChanges.Count > 0)
        {
            failed = true;
            Console.WriteLine($"Encoded output changed in {outputChanges.Count} case(s):");
            foreach (var change in outputChanges)
                Console.WriteLine($"  {change}");
        }

        if (statusChanges.Count > 0)
        {
            failed = true;
            Console.WriteLine($"Case status changed in {statusChanges.Count} case(s):");
            foreach (var change in statusChanges)
                Console.WriteLine($"  {change}");
        }

        if (slower.Count > 0)
        {
            failed = true;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Slower than the {(tolerance - 1) * 100:N0}% tolerance in {slower.Count} case(s):"));
            foreach (var regression in slower)
                Console.WriteLine($"  {regression}");
        }

        if (!failed)
            Console.WriteLine("No regressions: encoded output identical and no case beyond tolerance.");
        return failed ? 1 : 0;
    }

    static string FormatDelta(double deltaPercent)
        => string.Create(CultureInfo.InvariantCulture,
            $"{(deltaPercent < 0 ? '-' : '+')}{Math.Abs(deltaPercent):N1}%");

    static EncodingRegressionOptions CreateOptions(string[] args)
        => new()
        {
            Rows = ReadInt(args, "--rows") ?? 200_000,
            Warmups = ReadInt(args, "--warmups") ?? 3,
            Iterations = ReadInt(args, "--iterations") ?? 30
        };

    static int? ReadInt(string[] args, string name)
        => PublishedBenchmarkCommand.ReadValue(args, name) is { } value
            ? int.Parse(value, CultureInfo.InvariantCulture)
            : null;

    static string ReadCommit(string root)
    {
        var head = Path.Combine(root, ".git", "HEAD");
        if (!File.Exists(head))
            return "unknown";
        var contents = File.ReadAllText(head).Trim();
        if (!contents.StartsWith("ref:", StringComparison.Ordinal))
            return contents;
        var referencePath = Path.Combine(root, ".git", contents[4..].Trim());
        return File.Exists(referencePath) ? File.ReadAllText(referencePath).Trim() : contents;
    }
}
