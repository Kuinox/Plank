using BenchmarkDotNet.Attributes;
using Plank.Writing;
using Plank.Writing.Encoding;

namespace Plank.Benchmarks;

[Config(typeof(OptimizationBenchmarkConfig))]
public class BooleanRleBitPackingHybridEncodingBenchmark
{
    bool[][] _inputs = [];
    int _inputIndex;
    BufferWriter _writer;

    [Params(65_536, 1_000_000)]
    public int Rows { get; set; }

    [Params("alternating", "threshold-runs", "long-runs", "constant")]
    public string Distribution { get; set; } = "long-runs";

    [GlobalSetup]
    public void GlobalSetup()
    {
        _inputs = new bool[8][];
        for (var i = 0; i < _inputs.Length; i++)
            _inputs[i] = CreateValues(Rows, Distribution, i);

        var bufferSize = checked((uint)(Rows + 1_024));
        _writer = new BufferWriter(DefaultParquetBufferPool.Shared, bufferSize, bufferSize);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
        => _writer.Dispose();

    [Benchmark]
    public int WriteBooleans()
    {
        var values = _inputs[_inputIndex];
        _inputIndex = (_inputIndex + 1) & (_inputs.Length - 1);
        _writer.Reset();
        RleBitPackingHybridEncoding.WriteBooleans(values, ref _writer);
        return _writer.WrittenLength;
    }

    static bool[] CreateValues(int count, string distribution, int inputIndex)
    {
        var values = new bool[count];
        switch (distribution)
        {
            case "alternating":
                for (var i = 0; i < values.Length; i++)
                    values[i] = ((i + inputIndex) & 1) != 0;
                break;
            case "threshold-runs":
                FillRuns(values, [7, 8, 9], inputIndex);
                break;
            case "long-runs":
                FillRuns(values, [128], inputIndex);
                break;
            case "constant":
                values.AsSpan().Fill((inputIndex & 1) != 0);
                break;
            default:
                throw new InvalidOperationException($"Unknown distribution '{distribution}'.");
        }

        return values;
    }

    static void FillRuns(Span<bool> values, ReadOnlySpan<int> runLengths, int inputIndex)
    {
        var offset = 0;
        var runIndex = inputIndex;
        while (offset < values.Length)
        {
            var runLength = runLengths[runIndex % runLengths.Length];
            values.Slice(offset, Math.Min(runLength, values.Length - offset)).Fill((runIndex & 1) != 0);
            offset += runLength;
            runIndex++;
        }
    }
}
