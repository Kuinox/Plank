using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Plank.Writing.Encoding;

namespace Plank.Benchmarks;

[Config(typeof(OptimizationBenchmarkConfig))]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class DictionaryImplementationBenchmark
{
    const int RowCount = 65_536;

    int[] _intValues = [];
    string[] _stringValues = [];
    byte[][] _binaryValues = [];
    ReusableDictionaryState<int> _plankIntDictionary = null!;
    ReusableDictionaryState<string> _plankStringDictionary = null!;
    ReusableDictionaryState<byte[]> _plankBinaryDictionary = null!;
    Dictionary<int, int> _dotnetIntDictionary = null!;
    Dictionary<string, int> _dotnetStringDictionary = null!;
    Dictionary<byte[], int> _dotnetBinaryDictionary = null!;
    int _uniqueCount;

    [ParamsAllValues]
    public InputScenario Scenario { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        var indexes = CreateIndexes(Scenario, out _uniqueCount);
        var uniqueInts = new int[_uniqueCount];
        var uniqueStrings = new string[_uniqueCount];
        var uniqueBinary = new byte[_uniqueCount][];
        var stringCollisionCandidate = 0;
        var binaryCollisionCandidate = 0;
        for (var i = 0; i < _uniqueCount; i++)
        {
            if (Scenario == InputScenario.ProbeCollision)
            {
                uniqueInts[i] = i << 20;
                uniqueStrings[i] = FindCollidingString(ref stringCollisionCandidate);
                uniqueBinary[i] = FindCollidingBinary(ref binaryCollisionCandidate);
            }
            else if (Scenario == InputScenario.ShortDistinctArrays)
            {
                uniqueInts[i] = i;
                uniqueStrings[i] = $"value-{i}";
                uniqueBinary[i] = System.Text.Encoding.UTF8.GetBytes(uniqueStrings[i]);
            }
            else
            {
                uniqueInts[i] = unchecked((int)(i * 2_654_435_761u));
                uniqueStrings[i] = $"value-{i:D5}-{unchecked(i * 1_103_515_245 + 12_345):X8}";
                uniqueBinary[i] = CreateBinaryValue(i);
            }
        }

        _intValues = new int[RowCount];
        _stringValues = new string[RowCount];
        _binaryValues = new byte[RowCount][];
        for (var i = 0; i < RowCount; i++)
        {
            var index = indexes[i];
            _intValues[i] = uniqueInts[index];
            _stringValues[i] = uniqueStrings[index];
            // Real byte-array string columns commonly contain a separate UTF-8 allocation per row,
            // even when dictionary equality collapses their content to a small repeated set.
            _binaryValues[i] = Scenario == InputScenario.ShortDistinctArrays
                ? uniqueBinary[index].ToArray()
                : uniqueBinary[index];
        }

        _plankIntDictionary = new ReusableDictionaryState<int>();
        _plankStringDictionary = new ReusableDictionaryState<string>();
        _plankBinaryDictionary = new ReusableDictionaryState<byte[]>();
        _dotnetIntDictionary = new Dictionary<int, int>(_uniqueCount);
        _dotnetStringDictionary = new Dictionary<string, int>(_uniqueCount, StringComparer.Ordinal);
        _dotnetBinaryDictionary = new Dictionary<byte[], int>(_uniqueCount, ByteArrayComparer.Instance);
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = RowCount)]
    [BenchmarkCategory("Int32")]
    public int DotNetInt32()
    {
        _dotnetIntDictionary.Clear();
        for (var i = 0; i < _intValues.Length; i++)
        {
            var value = _intValues[i];
            if (!_dotnetIntDictionary.TryAdd(value, _dotnetIntDictionary.Count))
                continue;
        }

        return _dotnetIntDictionary.Count;
    }

    [Benchmark(OperationsPerInvoke = RowCount)]
    [BenchmarkCategory("Int32")]
    public int PlankInt32()
    {
        _plankIntDictionary.Reset(_uniqueCount, useMap: true);
        for (var i = 0; i < _intValues.Length; i++)
            _plankIntDictionary.GetOrAddIndex(_intValues[i]);
        return _plankIntDictionary.Count;
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = RowCount)]
    [BenchmarkCategory("String")]
    public int DotNetString()
    {
        _dotnetStringDictionary.Clear();
        for (var i = 0; i < _stringValues.Length; i++)
        {
            var value = _stringValues[i];
            if (!_dotnetStringDictionary.TryAdd(value, _dotnetStringDictionary.Count))
                continue;
        }

        return _dotnetStringDictionary.Count;
    }

    [Benchmark(OperationsPerInvoke = RowCount)]
    [BenchmarkCategory("String")]
    public int PlankString()
    {
        _plankStringDictionary.Reset(_uniqueCount, useMap: true);
        for (var i = 0; i < _stringValues.Length; i++)
            _plankStringDictionary.GetOrAddIndex(_stringValues[i]);
        return _plankStringDictionary.Count;
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = RowCount)]
    [BenchmarkCategory("Binary")]
    public int DotNetBinary()
    {
        _dotnetBinaryDictionary.Clear();
        for (var i = 0; i < _binaryValues.Length; i++)
        {
            var value = _binaryValues[i];
            if (!_dotnetBinaryDictionary.TryAdd(value, _dotnetBinaryDictionary.Count))
                continue;
        }

        return _dotnetBinaryDictionary.Count;
    }

    [Benchmark(OperationsPerInvoke = RowCount)]
    [BenchmarkCategory("Binary")]
    public int PlankBinary()
    {
        _plankBinaryDictionary.Reset(_uniqueCount, useMap: true);
        for (var i = 0; i < _binaryValues.Length; i++)
            _plankBinaryDictionary.GetOrAddIndex(_binaryValues[i]);
        return _plankBinaryDictionary.Count;
    }

    static int[] CreateIndexes(InputScenario scenario, out int uniqueCount)
    {
        var random = new Random(42);
        var indexes = new int[RowCount];
        uniqueCount = scenario switch
        {
            InputScenario.HotSet => 16,
            InputScenario.Unique => RowCount,
            InputScenario.ProbeCollision => 256,
            InputScenario.ShortDistinctArrays => 2_048,
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };

        for (var i = 0; i < uniqueCount; i++)
            indexes[i] = i;
        for (var i = uniqueCount; i < indexes.Length; i++)
        {
            indexes[i] = scenario switch
            {
                InputScenario.HotSet => random.Next(uniqueCount),
                InputScenario.Unique => i,
                InputScenario.ProbeCollision => i % uniqueCount,
                InputScenario.ShortDistinctArrays => i % uniqueCount,
                _ => throw new ArgumentOutOfRangeException(nameof(scenario))
            };
        }

        Shuffle(indexes, random);
        return indexes;
    }

    static byte[] CreateBinaryValue(int value)
    {
        var bytes = new byte[12 + value % 13];
        var state = unchecked((uint)value * 747_796_405u + 2_891_336_453u);
        for (var i = 0; i < bytes.Length; i++)
        {
            state = state * 1_664_525u + 1_013_904_223u;
            bytes[i] = (byte)(state >> 24);
        }
        return bytes;
    }

    static string FindCollidingString(ref int candidate)
    {
        while (true)
        {
            var value = $"collision-{candidate}";
            candidate++;
            if ((value.GetHashCode() & 2_047) == 0)
                return value;
        }
    }

    static byte[] FindCollidingBinary(ref int candidate)
    {
        while (true)
        {
            var value = CreateBinaryValue(candidate);
            candidate++;
            if ((WyHashing.Hash(value) & 2_047) == 0)
                return value;
        }
    }

    static void Shuffle(int[] values, Random random)
    {
        for (var i = values.Length - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }
    }

    public enum InputScenario
    {
        HotSet,
        Unique,
        ProbeCollision,
        ShortDistinctArrays
    }
}
