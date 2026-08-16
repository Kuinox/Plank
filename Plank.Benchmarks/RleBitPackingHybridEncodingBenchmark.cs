using BenchmarkDotNet.Attributes;
using Plank.Writing;
using Plank.Writing.Encoding;

namespace Plank.Benchmarks;

[Config(typeof(OptimizationBenchmarkConfig))]
public class RleBitPackingHybridEncodingBenchmark
{
    int[][] _inputs = [];
    int _inputIndex;
    int _bitWidth;
    BufferWriter _writer;

    [Params(4_096, 65_536)]
    public int Rows { get; set; }

    [Params(
        "constant-w0",
        "alternating-w1",
        "random-w8",
        "short-runs-w8",
        "threshold-runs-w8",
        "mixed-runs-w8",
        "long-runs-w8",
        "random-w11",
        "random-w16",
        "random-w24",
        "random-w32")]
    public string Distribution { get; set; } = "random-w8";

    [GlobalSetup]
    public void GlobalSetup()
    {
        _bitWidth = GetBitWidth(Distribution);
        _inputs = new int[8][];
        for (var i = 0; i < _inputs.Length; i++)
            _inputs[i] = CreateValues(Rows, Distribution, unchecked((uint)(42 + i * 7919)));

        var bufferSize = checked((uint)(Rows * sizeof(int) + 1024));
        _writer = new BufferWriter(DefaultParquetBufferPool.Shared, bufferSize, bufferSize);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
        => _writer.Dispose();

    [Benchmark]
    public int WriteDictionaryIndexes()
    {
        var values = _inputs[_inputIndex];
        _inputIndex = (_inputIndex + 1) & (_inputs.Length - 1);
        _writer.Reset();
        RleBitPackingHybridEncoding.WriteWithBitWidthPrefixUnchecked(values, _bitWidth, ref _writer);
        return _writer.WrittenLength;
    }

    static int GetBitWidth(string distribution)
        => distribution switch
        {
            "constant-w0" => 0,
            "alternating-w1" => 1,
            "random-w8" or "short-runs-w8" or "threshold-runs-w8" or "mixed-runs-w8" or "long-runs-w8" => 8,
            "random-w11" => 11,
            "random-w16" => 16,
            "random-w24" => 24,
            "random-w32" => 32,
            _ => throw new InvalidOperationException($"Unknown distribution '{distribution}'.")
        };

    static int[] CreateValues(int count, string distribution, uint seed)
    {
        var values = new int[count];
        var bitWidth = GetBitWidth(distribution);
        switch (distribution)
        {
            case "constant-w0":
                return values;
            case "alternating-w1":
                for (var i = 0; i < values.Length; i++)
                    values[i] = (i + (int)(seed & 1)) & 1;
                return values;
            case "random-w8":
            case "random-w11":
            case "random-w16":
            case "random-w24":
            case "random-w32":
                FillRandomLiterals(values, bitWidth, ref seed);
                return values;
            case "short-runs-w8":
                FillRuns(values, bitWidth, 2, 7, ref seed);
                return values;
            case "threshold-runs-w8":
                FillThresholdRuns(values, bitWidth, ref seed);
                return values;
            case "mixed-runs-w8":
                FillMixedRuns(values, bitWidth, ref seed);
                return values;
            case "long-runs-w8":
                FillRuns(values, bitWidth, 64, 512, ref seed);
                return values;
            default:
                throw new InvalidOperationException($"Unknown distribution '{distribution}'.");
        }
    }

    static void FillRandomLiterals(Span<int> values, int bitWidth, ref uint state)
    {
        var mask = GetMask(bitWidth);
        var previous = uint.MaxValue;
        for (var i = 0; i < values.Length; i++)
        {
            var value = Next(ref state) & mask;
            if (value == previous)
                value = (value + 1) & mask;
            values[i] = unchecked((int)value);
            previous = value;
        }
    }

    static void FillRuns(Span<int> values, int bitWidth, int minimumLength, int maximumLength, ref uint state)
    {
        var offset = 0;
        var previous = uint.MaxValue;
        while (offset < values.Length)
        {
            var runLength = minimumLength + (int)(Next(ref state) % (uint)(maximumLength - minimumLength + 1));
            var value = GetDifferentValue(bitWidth, previous, ref state);
            values.Slice(offset, Math.Min(runLength, values.Length - offset)).Fill(unchecked((int)value));
            offset += runLength;
            previous = value;
        }
    }

    static void FillThresholdRuns(Span<int> values, int bitWidth, ref uint state)
    {
        int[] lengths = [7, 8, 9];
        var offset = 0;
        var previous = uint.MaxValue;
        while (offset < values.Length)
        {
            var runLength = lengths[Next(ref state) % (uint)lengths.Length];
            var value = GetDifferentValue(bitWidth, previous, ref state);
            values.Slice(offset, Math.Min(runLength, values.Length - offset)).Fill(unchecked((int)value));
            offset += runLength;
            previous = value;
        }
    }

    static void FillMixedRuns(Span<int> values, int bitWidth, ref uint state)
    {
        var offset = 0;
        var previous = uint.MaxValue;
        while (offset < values.Length)
        {
            var selector = Next(ref state) % 100;
            var runLength = selector switch
            {
                < 45 => 1,
                < 70 => 2 + (int)(Next(ref state) % 6),
                < 90 => 8 + (int)(Next(ref state) % 24),
                _ => 32 + (int)(Next(ref state) % 225)
            };
            var value = GetDifferentValue(bitWidth, previous, ref state);
            values.Slice(offset, Math.Min(runLength, values.Length - offset)).Fill(unchecked((int)value));
            offset += runLength;
            previous = value;
        }
    }

    static uint GetDifferentValue(int bitWidth, uint previous, ref uint state)
    {
        var mask = GetMask(bitWidth);
        var value = Next(ref state) & mask;
        if (value == previous)
            value = (value + 1) & mask;
        return value;
    }

    static uint GetMask(int bitWidth)
        => bitWidth == 32 ? uint.MaxValue : (1u << bitWidth) - 1u;

    static uint Next(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }
}
