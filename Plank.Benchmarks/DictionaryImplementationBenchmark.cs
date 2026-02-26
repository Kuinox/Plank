using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Plank.Writing.Encoding;

namespace Plank.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class DictionaryImplementationBenchmark
{
    string[] _values = [];
    ReusableDictionaryState<string> _plankDictionary = null!;
    Dictionary<string, int> _dotnetDictionary = null!;
    int _initialUniqueCapacity;

    [Params(500_000)]
    public int Rows { get; set; }

    [Params(10, 30, 50, 70, 90)]
    public int UniquePercent { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        var uniqueCount = Math.Max(1, checked((int)Math.Ceiling(Rows * (UniquePercent / 100d))));
        _initialUniqueCapacity = Math.Max(256, uniqueCount);

        var uniques = new string[uniqueCount];
        for (var i = 0; i < uniqueCount; i++)
            uniques[i] = $"value-{i}";

        _values = new string[Rows];
        for (var i = 0; i < Rows; i++)
            _values[i] = uniques[i % uniqueCount];

        Shuffle(_values);

        _plankDictionary = new ReusableDictionaryState<string>();
        _dotnetDictionary = new Dictionary<string, int>(_initialUniqueCapacity, StringComparer.Ordinal);
    }

    [Benchmark(Baseline = true)]
    public int DotNetDictionary()
    {
        _dotnetDictionary.Clear();
        for (var i = 0; i < _values.Length; i++)
        {
            var value = _values[i];
            if (_dotnetDictionary.TryGetValue(value, out _))
                continue;
            _dotnetDictionary.Add(value, _dotnetDictionary.Count);
        }

        return _dotnetDictionary.Count;
    }

    [Benchmark]
    public int PlankReusableDictionary()
    {
        _plankDictionary.Reset(_initialUniqueCapacity, useMap: true, StringComparer.Ordinal);
        for (var i = 0; i < _values.Length; i++)
            _plankDictionary.GetOrAddIndex(_values[i]);
        return _plankDictionary.Count;
    }

    static void Shuffle(string[] values)
    {
        var random = new Random(42);
        for (var i = values.Length - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }
    }
}
