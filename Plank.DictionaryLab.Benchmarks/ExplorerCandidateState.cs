namespace Plank.DictionaryLab.Benchmarks;

public sealed class ExplorerCandidateState
{
    public string NodeId { get; set; } = string.Empty;

    public int Samples { get; set; }

    public double MeanStringSpeedup { get; set; }

    public double MeanUtf8Speedup { get; set; }

    public double MeanMergedSpeedup { get; set; }
}
