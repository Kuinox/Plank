using System.Diagnostics;
using System.Text.Json;
using Plank.DictionaryLab;

namespace Plank.DictionaryLab.Benchmarks;

public static class WeightedExplorer
{
    static readonly int[] UniquePercents = [10, 20, 30, 40, 50, 60, 70, 80, 90];

    public static int Run(string[] args)
    {
        var rounds = ParseInt(args, "--rounds", 40);
        var rows = ParseInt(args, "--rows", 250_000);
        var exploreWeight = ParseDouble(args, "--explore-weight", 0.7);
        var minRootBranchSamples = ParseInt(args, "--min-root-branch-samples", 12);
        var path = ParsePath(args, "--state", "/home/kuinox/dev/Plank/BenchmarkDotNet.Artifacts/dictionary-lab-explorer.json");

        var state = LoadState(path);
        var random = new Random(42);
        var nodeById = DictionaryNodeCatalog.Nodes.ToDictionary(static x => x.Id, StringComparer.Ordinal);
        var rootByNodeId = BuildRootByNodeId(nodeById);
        var stringBaselinePool = new Dictionary<string, int>(256, StringComparer.Ordinal);
        var utf8BaselinePool = new Dictionary<ReadOnlyMemory<byte>, int>(256, ReadOnlyMemoryByteComparer.Instance);

        for (var round = 0; round < rounds; round++)
        {
            var selected = PickCandidate(state, exploreWeight, minRootBranchSamples, random, rootByNodeId);
            var uniquePercent = UniquePercents[random.Next(UniquePercents.Length)];
            var uniqueCount = Math.Max(1, checked((int)Math.Ceiling(rows * (uniquePercent / 100d))));
            var initialCapacity = Math.Max(256, uniqueCount);
            var stringValues = TestDataGenerator.CreateShuffledValues(rows, uniqueCount);
            var utf8Values = TestDataGenerator.CreateShuffledUtf8Values(rows, uniqueCount);

            var stringBaselineMs = RunMicro(stringValues, stringBaselinePool, initialCapacity);
            var stringCandidateMs = RunMicro(stringValues, selected.Create(), initialCapacity);
            var stringSpeedup = stringBaselineMs / stringCandidateMs;

            var utf8Node = Utf8DictionaryNodeCatalog.Get(selected.Id);
            var utf8BaselineMs = RunMicro(utf8Values, utf8BaselinePool, initialCapacity);
            var utf8CandidateMs = RunMicro(utf8Values, utf8Node.Create(), initialCapacity);
            var utf8Speedup = utf8BaselineMs / utf8CandidateMs;
            var mergedSpeedup = Math.Sqrt(stringSpeedup * utf8Speedup);

            AddSample(state, selected.Id, stringSpeedup, utf8Speedup, mergedSpeedup);
            Console.WriteLine(
                $"round={round + 1}/{rounds} node={selected.Id} unique={uniquePercent}% string={stringSpeedup:F3}x utf8={utf8Speedup:F3}x merged={mergedSpeedup:F3}x");
        }

        SaveState(path, state);
        ExplorationReportGenerator.Write(path, "/home/kuinox/dev/Plank/benchmarks/report");
        PrintLeaderboard(state);
        return 0;
    }

    static double RunMicro(string[] values, Dictionary<string, int> dictionary, int initialCapacity)
    {
        if (dictionary.Comparer != StringComparer.Ordinal || dictionary.EnsureCapacity(0) < initialCapacity)
            dictionary = new Dictionary<string, int>(initialCapacity, StringComparer.Ordinal);

        static void Populate(Dictionary<string, int> map, string[] source)
        {
            map.Clear();
            if (source.Length == 0)
                return;
            if (!map.TryGetValue(source[0], out _))
                map.Add(source[0], 0);
            for (var i = 1; i < source.Length; i++)
                if (!map.TryGetValue(source[i], out _))
                    map.Add(source[i], map.Count);
        }

        Populate(dictionary, values);

        var ticks = 0L;
        for (var i = 0; i < 5; i++)
        {
            var start = Stopwatch.GetTimestamp();
            Populate(dictionary, values);
            ticks += Stopwatch.GetTimestamp() - start;
        }

        return ticks * 1_000d / Stopwatch.Frequency / 5d;
    }

    static double RunMicro(string[] values, IIndexDictionary<string> dictionary, int initialCapacity)
    {
        static void Populate(IIndexDictionary<string> map, string[] source, int capacity)
        {
            map.Reset(capacity);
            if (source.Length == 0)
                return;
            map.GetOrAddIndex(source[0]);
            for (var i = 1; i < source.Length; i++)
                map.GetOrAddIndex(source[i]);
        }

        Populate(dictionary, values, initialCapacity);

        var ticks = 0L;
        for (var i = 0; i < 5; i++)
        {
            var start = Stopwatch.GetTimestamp();
            Populate(dictionary, values, initialCapacity);
            ticks += Stopwatch.GetTimestamp() - start;
        }

        return ticks * 1_000d / Stopwatch.Frequency / 5d;
    }

    static double RunMicro(ReadOnlyMemory<byte>[] values, Dictionary<ReadOnlyMemory<byte>, int> dictionary, int initialCapacity)
    {
        if (dictionary.EnsureCapacity(0) < initialCapacity)
            dictionary = new Dictionary<ReadOnlyMemory<byte>, int>(initialCapacity, ReadOnlyMemoryByteComparer.Instance);

        static void Populate(Dictionary<ReadOnlyMemory<byte>, int> map, ReadOnlyMemory<byte>[] source)
        {
            map.Clear();
            if (source.Length == 0)
                return;
            if (!map.TryGetValue(source[0], out _))
                map.Add(source[0], 0);
            for (var i = 1; i < source.Length; i++)
                if (!map.TryGetValue(source[i], out _))
                    map.Add(source[i], map.Count);
        }

        Populate(dictionary, values);

        var ticks = 0L;
        for (var i = 0; i < 5; i++)
        {
            var start = Stopwatch.GetTimestamp();
            Populate(dictionary, values);
            ticks += Stopwatch.GetTimestamp() - start;
        }

        return ticks * 1_000d / Stopwatch.Frequency / 5d;
    }

    static double RunMicro(ReadOnlyMemory<byte>[] values, IIndexDictionary<ReadOnlyMemory<byte>> dictionary, int initialCapacity)
    {
        static void Populate(IIndexDictionary<ReadOnlyMemory<byte>> map, ReadOnlyMemory<byte>[] source, int capacity)
        {
            map.Reset(capacity);
            if (source.Length == 0)
                return;
            map.GetOrAddIndex(source[0]);
            for (var i = 1; i < source.Length; i++)
                map.GetOrAddIndex(source[i]);
        }

        Populate(dictionary, values, initialCapacity);

        var ticks = 0L;
        for (var i = 0; i < 5; i++)
        {
            var start = Stopwatch.GetTimestamp();
            Populate(dictionary, values, initialCapacity);
            ticks += Stopwatch.GetTimestamp() - start;
        }

        return ticks * 1_000d / Stopwatch.Frequency / 5d;
    }

    static DictionaryNode<string> PickCandidate(
        ExplorerState state,
        double exploreWeight,
        int minRootBranchSamples,
        Random random,
        Dictionary<string, string> rootByNodeId)
    {
        var nodes = DictionaryNodeCatalog.Nodes;
        var nodesByRoot = BuildNodesByRoot(nodes, rootByNodeId);
        var underSampledRoots = nodesByRoot
            .Select(x => (RootId: x.Key, Samples: CountRootSamples(state, x.Value)))
            .Where(x => x.Samples < minRootBranchSamples)
            .OrderBy(x => x.Samples)
            .ToArray();
        if (underSampledRoots.Length > 0)
        {
            var leastSampledRoot = underSampledRoots[0].RootId;
            return PickBest(nodesByRoot[leastSampledRoot], state, exploreWeight, random);
        }

        var unsampled = nodes.Where(x => FindOrCreate(state, x.Id).Samples == 0).ToArray();
        if (unsampled.Length > 0)
            return unsampled[random.Next(unsampled.Length)];

        var logTotal = Math.Log(Math.Max(1, state.TotalSamples));
        var bestScore = double.NegativeInfinity;
        var bestNode = nodes[0];

        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            var candidate = FindOrCreate(state, node.Id);
            var exploration = Math.Sqrt(logTotal / candidate.Samples);
            var score = candidate.MeanMergedSpeedup + exploreWeight * exploration;
            if (score <= bestScore)
                continue;

            bestScore = score;
            bestNode = node;
        }

        return bestNode;
    }

    static Dictionary<string, List<DictionaryNode<string>>> BuildNodesByRoot(
        IReadOnlyList<DictionaryNode<string>> nodes,
        Dictionary<string, string> rootByNodeId)
    {
        var result = new Dictionary<string, List<DictionaryNode<string>>>(StringComparer.Ordinal);
        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            var rootId = rootByNodeId[node.Id];
            if (!result.TryGetValue(rootId, out var list))
            {
                list = [];
                result.Add(rootId, list);
            }

            list.Add(node);
        }

        return result;
    }

    static int CountRootSamples(ExplorerState state, IReadOnlyList<DictionaryNode<string>> nodes)
    {
        var total = 0;
        for (var i = 0; i < nodes.Count; i++)
            total += FindOrCreate(state, nodes[i].Id).Samples;
        return total;
    }

    static DictionaryNode<string> PickBest(
        IReadOnlyList<DictionaryNode<string>> nodes,
        ExplorerState state,
        double exploreWeight,
        Random random)
    {
        var unsampled = nodes.Where(x => FindOrCreate(state, x.Id).Samples == 0).ToArray();
        if (unsampled.Length > 0)
            return unsampled[random.Next(unsampled.Length)];

        var logTotal = Math.Log(Math.Max(1, state.TotalSamples));
        var bestScore = double.NegativeInfinity;
        var bestNode = nodes[0];
        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            var candidate = FindOrCreate(state, node.Id);
            var exploration = Math.Sqrt(logTotal / candidate.Samples);
            var score = candidate.MeanMergedSpeedup + exploreWeight * exploration;
            if (score <= bestScore)
                continue;

            bestScore = score;
            bestNode = node;
        }

        return bestNode;
    }

    static Dictionary<string, string> BuildRootByNodeId(Dictionary<string, DictionaryNode<string>> nodeById)
    {
        var result = new Dictionary<string, string>(nodeById.Count, StringComparer.Ordinal);
        foreach (var pair in nodeById)
            result[pair.Key] = FindRootId(pair.Value, nodeById);
        return result;
    }

    static string FindRootId(DictionaryNode<string> node, Dictionary<string, DictionaryNode<string>> nodeById)
    {
        var current = node;
        while (current.ParentId is not null)
            current = nodeById[current.ParentId];
        return current.Id;
    }

    static ExplorerState LoadState(string path)
    {
        if (!File.Exists(path))
            return CreateEmptyState();

        var json = File.ReadAllText(path);
        var state = JsonSerializer.Deserialize<ExplorerState>(json);
        if (state is null)
            return CreateEmptyState();

        for (var i = 0; i < state.Candidates.Count; i++)
        {
            var candidate = state.Candidates[i];
            if (candidate.Samples <= 0)
                continue;
            if (candidate.MeanMergedSpeedup != 0 || candidate.MeanStringSpeedup != 0 || candidate.MeanUtf8Speedup != 0)
                continue;
            candidate.Samples = 0;
        }

        state.TotalSamples = 0;
        for (var i = 0; i < state.Candidates.Count; i++)
            state.TotalSamples += state.Candidates[i].Samples;

        for (var i = 0; i < DictionaryNodeCatalog.Nodes.Count; i++)
            FindOrCreate(state, DictionaryNodeCatalog.Nodes[i].Id);
        return state;
    }

    static ExplorerState CreateEmptyState()
    {
        var state = new ExplorerState();
        for (var i = 0; i < DictionaryNodeCatalog.Nodes.Count; i++)
            state.Candidates.Add(new ExplorerCandidateState { NodeId = DictionaryNodeCatalog.Nodes[i].Id });
        return state;
    }

    static void SaveState(string path, ExplorerState state)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(state, options);
        File.WriteAllText(path, json);
    }

    static ExplorerCandidateState FindOrCreate(ExplorerState state, string nodeId)
    {
        for (var i = 0; i < state.Candidates.Count; i++)
        {
            var candidate = state.Candidates[i];
            if (candidate.NodeId == nodeId)
                return candidate;
        }

        var created = new ExplorerCandidateState { NodeId = nodeId };
        state.Candidates.Add(created);
        return created;
    }

    static void AddSample(ExplorerState state, string nodeId, double stringSpeedup, double utf8Speedup, double mergedSpeedup)
    {
        var candidate = FindOrCreate(state, nodeId);
        candidate.Samples++;
        state.TotalSamples++;
        candidate.MeanStringSpeedup += (stringSpeedup - candidate.MeanStringSpeedup) / candidate.Samples;
        candidate.MeanUtf8Speedup += (utf8Speedup - candidate.MeanUtf8Speedup) / candidate.Samples;
        candidate.MeanMergedSpeedup += (mergedSpeedup - candidate.MeanMergedSpeedup) / candidate.Samples;
    }

    static void PrintLeaderboard(ExplorerState state)
    {
        Console.WriteLine();
        Console.WriteLine("Leaderboard (higher speedup is better; >1 means faster than .NET):");
        var ranked = state.Candidates
            .OrderByDescending(static x => x.MeanMergedSpeedup)
            .ThenByDescending(static x => x.Samples)
            .ToArray();

        for (var i = 0; i < ranked.Length; i++)
        {
            var entry = ranked[i];
            Console.WriteLine(
                $"{i + 1,2}. {entry.NodeId,-26} merged={entry.MeanMergedSpeedup:F3}x string={entry.MeanStringSpeedup:F3}x utf8={entry.MeanUtf8Speedup:F3}x samples={entry.Samples}");
        }
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

    static double ParseDouble(string[] args, string key, double fallback)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (!args[i].Equals(key, StringComparison.OrdinalIgnoreCase))
                continue;
            if (double.TryParse(args[i + 1], out var parsed))
                return parsed;
        }

        return fallback;
    }

    static string ParsePath(string[] args, string key, string fallback)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(key, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return fallback;
    }
}
