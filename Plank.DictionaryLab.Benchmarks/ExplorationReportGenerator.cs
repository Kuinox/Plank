using System.Text;
using System.Text.Json;
using Plank.DictionaryLab;

namespace Plank.DictionaryLab.Benchmarks;

public static class ExplorationReportGenerator
{
    public static int Run(string[] args)
    {
        var statePath = ParsePath(args, "--state", "/home/kuinox/dev/Plank/BenchmarkDotNet.Artifacts/dictionary-lab-explorer.json");
        var reportDir = ParsePath(args, "--report-dir", "/home/kuinox/dev/Plank/benchmarks/report");
        Write(statePath, reportDir);
        Console.WriteLine($"Wrote report files to {reportDir}");
        return 0;
    }

    public static void Write(string statePath, string reportDir)
    {
        var state = LoadState(statePath);
        Directory.CreateDirectory(reportDir);

        var dotPath = Path.Combine(reportDir, "DICTIONARY_LAB_EXPLORATION.dot");
        var mdPath = Path.Combine(reportDir, "DICTIONARY_LAB_EXPLORATION.md");

        File.WriteAllText(dotPath, BuildDot(state));
        File.WriteAllText(mdPath, BuildMarkdown(state, statePath, dotPath));
    }

    static ExplorerState LoadState(string path)
    {
        var state = new ExplorerState();
        if (!File.Exists(path))
            return EnsureAllNodesPresent(state);

        var json = File.ReadAllText(path);
        state = JsonSerializer.Deserialize<ExplorerState>(json) ?? new ExplorerState();
        return EnsureAllNodesPresent(state);
    }

    static ExplorerState EnsureAllNodesPresent(ExplorerState state)
    {
        for (var i = 0; i < DictionaryNodeCatalog.Nodes.Count; i++)
        {
            var id = DictionaryNodeCatalog.Nodes[i].Id;
            if (state.Candidates.Any(x => x.NodeId == id))
                continue;
            state.Candidates.Add(new ExplorerCandidateState { NodeId = id });
        }

        state.TotalSamples = 0;
        for (var i = 0; i < state.Candidates.Count; i++)
            state.TotalSamples += state.Candidates[i].Samples;
        return state;
    }

    static string BuildDot(ExplorerState state)
    {
        var statsById = state.Candidates.ToDictionary(static x => x.NodeId, StringComparer.Ordinal);
        var nodes = DictionaryNodeCatalog.Nodes;
        var sb = new StringBuilder();
        sb.AppendLine("digraph DictionaryLabExploration {");
        sb.AppendLine("  rankdir=LR;");
        sb.AppendLine("  labelloc=\"t\";");
        sb.AppendLine("  label=\"Dictionary Lab Exploration (Merged + String + Utf8)\";");
        sb.AppendLine("  node [shape=box];");
        sb.AppendLine();
        sb.AppendLine("  base_hashtable [label=\"base.hashtable\"];");
        sb.AppendLine("  base_btree [label=\"base.btree\"];");
        sb.AppendLine();

        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            statsById.TryGetValue(node.Id, out var stat);
            var safeName = ToDotId(node.Id);
            var label = stat is null || stat.Samples == 0
                ? $"{node.Id}\nunscored"
                : $"{node.Id}\n{stat.MeanMergedSpeedup:F3}x merged\n{stat.MeanStringSpeedup:F3}x string\n{stat.MeanUtf8Speedup:F3}x utf8\n{stat.Samples} samples";
            sb.Append("  ").Append(safeName).Append(" [label=\"").Append(Escape(label)).AppendLine("\"];");
        }

        sb.AppendLine();
        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            var from = node.ParentId is null
                ? node.BaseId == "btree" ? "base_btree" : "base_hashtable"
                : ToDotId(node.ParentId);
            var to = ToDotId(node.Id);
            var edgeLabel = node.ParentId is null ? "root branch" : "experiment branch";
            sb.Append("  ").Append(from).Append(" -> ").Append(to).Append(" [label=\"").Append(edgeLabel).AppendLine("\"];");
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    static string BuildMarkdown(ExplorerState state, string statePath, string dotPath)
    {
        var statsById = state.Candidates.ToDictionary(static x => x.NodeId, StringComparer.Ordinal);
        var nodes = DictionaryNodeCatalog.Nodes;
        var nodeById = nodes.ToDictionary(static x => x.Id, StringComparer.Ordinal);
        var rootByNodeId = BuildRootByNodeId(nodeById);
        var ranked = nodes
            .Select(node =>
            {
                statsById.TryGetValue(node.Id, out var stat);
                return (node, stat);
            })
            .OrderByDescending(static x => x.stat?.MeanMergedSpeedup ?? 0d)
            .ThenByDescending(static x => x.stat?.Samples ?? 0)
            .ToArray();

        var sb = new StringBuilder();
        sb.AppendLine("# Dictionary Lab Exploration");
        sb.AppendLine();
        sb.Append("State source: `").Append(statePath).AppendLine("`  ");
        sb.Append("Last update: ").AppendLine(DateTime.UtcNow.ToString("yyyy-MM-dd"));
        sb.AppendLine();
        sb.AppendLine("## Current Leaderboard");
        sb.AppendLine();
        sb.AppendLine("| Rank | Node | Mean merged speedup vs .NET | Mean string speedup | Mean utf8 speedup | Samples |");
        sb.AppendLine("|---|---|---:|---:|---:|---:|");
        for (var i = 0; i < ranked.Length; i++)
        {
            var (node, stat) = ranked[i];
            var merged = stat?.MeanMergedSpeedup ?? 0;
            var str = stat?.MeanStringSpeedup ?? 0;
            var utf8 = stat?.MeanUtf8Speedup ?? 0;
            var samples = stat?.Samples ?? 0;
            sb.Append("| ").Append(i + 1).Append(" | `").Append(node.Id).Append("` | ")
                .Append(merged.ToString("F3")).Append("x | ")
                .Append(str.ToString("F3")).Append("x | ")
                .Append(utf8.ToString("F3")).Append("x | ")
                .Append(samples).AppendLine(" |");
        }

        var rankedString = nodes
            .Select(node =>
            {
                statsById.TryGetValue(node.Id, out var stat);
                return (node, stat);
            })
            .OrderByDescending(static x => x.stat?.MeanStringSpeedup ?? 0d)
            .ThenByDescending(static x => x.stat?.Samples ?? 0)
            .ToArray();
        sb.AppendLine();
        sb.AppendLine("## String Leaderboard");
        sb.AppendLine();
        sb.AppendLine("| Rank | Node | Mean string speedup vs .NET | Mean utf8 speedup | Mean merged speedup | Samples |");
        sb.AppendLine("|---|---|---:|---:|---:|---:|");
        for (var i = 0; i < rankedString.Length; i++)
        {
            var (node, stat) = rankedString[i];
            var str = stat?.MeanStringSpeedup ?? 0;
            var utf8 = stat?.MeanUtf8Speedup ?? 0;
            var merged = stat?.MeanMergedSpeedup ?? 0;
            var samples = stat?.Samples ?? 0;
            sb.Append("| ").Append(i + 1).Append(" | `").Append(node.Id).Append("` | ")
                .Append(str.ToString("F3")).Append("x | ")
                .Append(utf8.ToString("F3")).Append("x | ")
                .Append(merged.ToString("F3")).Append("x | ")
                .Append(samples).AppendLine(" |");
        }

        var rankedUtf8 = nodes
            .Select(node =>
            {
                statsById.TryGetValue(node.Id, out var stat);
                return (node, stat);
            })
            .OrderByDescending(static x => x.stat?.MeanUtf8Speedup ?? 0d)
            .ThenByDescending(static x => x.stat?.Samples ?? 0)
            .ToArray();
        sb.AppendLine();
        sb.AppendLine("## Utf8 Leaderboard");
        sb.AppendLine();
        sb.AppendLine("| Rank | Node | Mean utf8 speedup vs .NET | Mean string speedup | Mean merged speedup | Samples |");
        sb.AppendLine("|---|---|---:|---:|---:|---:|");
        for (var i = 0; i < rankedUtf8.Length; i++)
        {
            var (node, stat) = rankedUtf8[i];
            var utf8 = stat?.MeanUtf8Speedup ?? 0;
            var str = stat?.MeanStringSpeedup ?? 0;
            var merged = stat?.MeanMergedSpeedup ?? 0;
            var samples = stat?.Samples ?? 0;
            sb.Append("| ").Append(i + 1).Append(" | `").Append(node.Id).Append("` | ")
                .Append(utf8.ToString("F3")).Append("x | ")
                .Append(str.ToString("F3")).Append("x | ")
                .Append(merged.ToString("F3")).Append("x | ")
                .Append(samples).AppendLine(" |");
        }

        sb.AppendLine();
        sb.AppendLine("## Root Branch Coverage");
        sb.AppendLine();
        sb.AppendLine("| Root node | Base | Samples |");
        sb.AppendLine("|---|---|---:|");
        foreach (var root in nodes.Where(static x => x.ParentId is null).OrderBy(static x => x.Id))
        {
            var samples = nodes
                .Where(node => rootByNodeId[node.Id] == root.Id)
                .Sum(node => statsById.TryGetValue(node.Id, out var stat) ? stat.Samples : 0);
            sb.Append("| `").Append(root.Id).Append("` | `base.").Append(root.BaseId).Append("` | ")
                .Append(samples).AppendLine(" |");
        }

        sb.AppendLine();
        sb.AppendLine("## Node Approach Summary");
        sb.AppendLine();
        sb.AppendLine("| Node | Parent | Approach |");
        sb.AppendLine("|---|---|---|");
        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            var parent = node.ParentId ?? $"base.{node.BaseId}";
            sb.Append("| `").Append(node.Id).Append("` | `").Append(parent).Append("` | ")
                .Append(node.Notes).AppendLine(" |");
        }

        sb.AppendLine();
        sb.AppendLine("## Exploration Graph (Graphviz DOT)");
        sb.AppendLine();
        sb.Append("Diagram source: [DICTIONARY_LAB_EXPLORATION.dot](").Append(dotPath).AppendLine(")");
        return sb.ToString();
    }

    static string ParsePath(string[] args, string key, string fallback)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(key, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return fallback;
    }

    static string ToDotId(string id)
        => id.Replace('.', '_').Replace('-', '_');

    static string Escape(string value)
        => value
            .Replace("\"", "\\\"")
            .Replace("\r\n", "\\n")
            .Replace("\n", "\\n");

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
}
