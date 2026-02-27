using BenchmarkDotNet.Running;

namespace Plank.DictionaryLab.Benchmarks;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Any(static x => x.Equals("--write-report", StringComparison.OrdinalIgnoreCase)))
            return ExplorationReportGenerator.Run(args);

        if (args.Any(static x => x.Equals("--analyze-robinhood", StringComparison.OrdinalIgnoreCase)))
            return RobinHoodAnalysis.Run(args);

        if (args.Any(static x => x.Equals("--explore", StringComparison.OrdinalIgnoreCase)))
            return WeightedExplorer.Run(args);

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        return 0;
    }
}
