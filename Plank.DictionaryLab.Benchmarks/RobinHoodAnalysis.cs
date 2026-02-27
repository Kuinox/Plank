using System.Diagnostics;
using Plank.DictionaryLab;

namespace Plank.DictionaryLab.Benchmarks;

public static class RobinHoodAnalysis
{
    public static int Run(string[] args)
    {
        var rows = ParseInt(args, "--rows", 200_000);
        var uniquePercent = ParseInt(args, "--unique", 50);
        var uniqueCount = Math.Max(1, checked((int)Math.Ceiling(rows * (uniquePercent / 100d))));
        var capacity = Math.Max(256, uniqueCount);

        var stringValues = TestDataGenerator.CreateShuffledValues(rows, uniqueCount);
        var utf8Values = TestDataGenerator.CreateShuffledUtf8Values(rows, uniqueCount);

        Console.WriteLine($"RobinHood analysis: rows={rows}, uniquePercent={uniquePercent}, uniqueCount={uniqueCount}, capacity={capacity}");
        Console.WriteLine();
        Console.WriteLine("String keys:");
        AnalyzeString("hash.linear.v1", stringValues, capacity);
        AnalyzeString("hash.linear.half-load.v1", stringValues, capacity);
        AnalyzeString("hash.robinhood.v1", stringValues, capacity);

        Console.WriteLine();
        Console.WriteLine("UTF-8 keys:");
        AnalyzeUtf8("hash.linear.v1", utf8Values, capacity);
        AnalyzeUtf8("hash.linear.half-load.v1", utf8Values, capacity);
        AnalyzeUtf8("hash.robinhood.v1", utf8Values, capacity);
        return 0;
    }

    static void AnalyzeString(string nodeId, string[] values, int capacity)
    {
        var node = DictionaryNodeCatalog.Get(nodeId);
        var coldMs = MeasureCold(() => node.Create(), values, capacity);
        var steadyMs = MeasureSteady(node.Create(), values, capacity);
        Console.WriteLine($"  {nodeId,-36} cold={coldMs,7:F3} ms  steady={steadyMs,7:F3} ms");
    }

    static void AnalyzeUtf8(string nodeId, ReadOnlyMemory<byte>[] values, int capacity)
    {
        var node = Utf8DictionaryNodeCatalog.Get(nodeId);
        var coldMs = MeasureCold(() => node.Create(), values, capacity);
        var steadyMs = MeasureSteady(node.Create(), values, capacity);
        Console.WriteLine($"  {nodeId,-36} cold={coldMs,7:F3} ms  steady={steadyMs,7:F3} ms");
    }

    static double MeasureCold<T>(Func<IIndexDictionary<T>> factory, T[] values, int capacity)
    {
        var ticks = 0L;
        for (var i = 0; i < 6; i++)
        {
            var dictionary = factory();
            var start = Stopwatch.GetTimestamp();
            Populate(dictionary, values, capacity);
            ticks += Stopwatch.GetTimestamp() - start;
        }

        return ticks * 1_000d / Stopwatch.Frequency / 6d;
    }

    static double MeasureSteady<T>(IIndexDictionary<T> dictionary, T[] values, int capacity)
    {
        Populate(dictionary, values, capacity);
        var ticks = 0L;
        for (var i = 0; i < 20; i++)
        {
            var start = Stopwatch.GetTimestamp();
            Populate(dictionary, values, capacity);
            ticks += Stopwatch.GetTimestamp() - start;
        }

        return ticks * 1_000d / Stopwatch.Frequency / 20d;
    }

    static void Populate<T>(IIndexDictionary<T> dictionary, T[] values, int capacity)
    {
        dictionary.Reset(capacity);
        for (var i = 0; i < values.Length; i++)
            dictionary.GetOrAddIndex(values[i]);
    }

    static int ParseInt(string[] args, string key, int fallback)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (!args[i].Equals(key, StringComparison.OrdinalIgnoreCase))
                continue;
            if (int.TryParse(args[i + 1], out var parsed))
                return parsed;
        }

        return fallback;
    }
}
