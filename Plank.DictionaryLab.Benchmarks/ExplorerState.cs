namespace Plank.DictionaryLab.Benchmarks;

public sealed class ExplorerState
{
    public int TotalSamples { get; set; }

    public List<ExplorerCandidateState> Candidates { get; set; } = [];
}
