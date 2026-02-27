using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Plank.DictionaryLab;

namespace Plank.DictionaryLab.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 2, iterationCount: 8)]
public class DictionaryNodeBenchmark
{
    string[] _values = [];
    int _initialCapacity;
    Dictionary<string, int> _dotnetDictionary = null!;
    IIndexDictionary<string> _candidate = null!;

    [Params(500_000)]
    public int Rows { get; set; }

    [Params(10, 30, 50, 70, 90)]
    public int UniquePercent { get; set; }

    [ParamsSource(nameof(NodeIds))]
    public string NodeId { get; set; } = string.Empty;

    public IEnumerable<string> NodeIds() => DictionaryNodeCatalog.Nodes.Select(static x => x.Id);

    [GlobalSetup]
    public void GlobalSetup()
    {
        var uniqueCount = Math.Max(1, checked((int)Math.Ceiling(Rows * (UniquePercent / 100d))));
        _initialCapacity = Math.Max(256, uniqueCount);
        _values = TestDataGenerator.CreateShuffledValues(Rows, uniqueCount);
        _dotnetDictionary = new Dictionary<string, int>(_initialCapacity, StringComparer.Ordinal);
        _candidate = DictionaryNodeCatalog.Get(NodeId).Create();
    }

    [Benchmark(Baseline = true)]
    public int DotNetDictionary()
    {
        _dotnetDictionary.Clear();
        if (_values.Length == 0)
            return 0;

        if (!_dotnetDictionary.TryGetValue(_values[0], out _))
            _dotnetDictionary.Add(_values[0], 0);

        for (var i = 1; i < _values.Length; i++)
            if (!_dotnetDictionary.TryGetValue(_values[i], out _))
                _dotnetDictionary.Add(_values[i], _dotnetDictionary.Count);

        return _dotnetDictionary.Count;
    }

    [Benchmark]
    public int CandidateDictionary()
    {
        _candidate.Reset(_initialCapacity);
        if (_values.Length == 0)
            return 0;

        _candidate.GetOrAddIndex(_values[0]);
        for (var i = 1; i < _values.Length; i++)
            _candidate.GetOrAddIndex(_values[i]);
        return _candidate.Count;
    }
}
