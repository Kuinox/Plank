using BenchmarkDotNet.Attributes;
using Plank.Writing;
using Plank.Writing.Encoding;

namespace Plank.Benchmarks;

[Config(typeof(OptimizationBenchmarkConfig))]
public class DeltaBinaryPackedEncodingBenchmark
{
    int[] _values = [];
    long[] _longValues = [];
    BufferWriter _writer;

    [Params(4_096, 65_536)]
    public int Rows { get; set; }

    [Params("constant-delta", "narrow-delta", "small-random-delta", "small-domain-random",
        "timestamp-like-13-bit", "random")]
    public string Distribution { get; set; } = "constant-delta";

    [GlobalSetup]
    public void GlobalSetup()
    {
        _values = CreateValues(Rows, Distribution);
        _longValues = CreateLongValues(Rows, Distribution);
        _writer = new BufferWriter(
            DefaultParquetBufferPool.Shared,
            chunkSizeBytes: checked((uint)(Rows * sizeof(long) + 1024)),
            initialBufferBytes: checked((uint)(Rows * sizeof(long) + 1024)));
    }

    [GlobalCleanup]
    public void GlobalCleanup()
        => _writer.Dispose();

    [Benchmark]
    public int WriteInt32()
    {
        _writer.Reset();
        DeltaBinaryPackedEncoding.WriteInt32(_values, ref _writer);
        return _writer.WrittenLength;
    }

    [Benchmark]
    public int WriteInt64()
    {
        _writer.Reset();
        DeltaBinaryPackedEncoding.WriteInt64(_longValues, ref _writer);
        return _writer.WrittenLength;
    }

    static int[] CreateValues(int count, string distribution)
    {
        var values = new int[count];
        var random = new Random(42);

        switch (distribution)
        {
            case "constant-delta":
                for (var i = 0; i < values.Length; i++)
                    values[i] = i * 7;
                return values;
            case "narrow-delta":
                for (var i = 0; i < values.Length; i++)
                    values[i] = i * 3 + i % 7;
                return values;
            case "small-random-delta":
            {
                var current = 0;
                for (var i = 0; i < values.Length; i++)
                {
                    current = unchecked(current + random.Next(-16, 17));
                    values[i] = current;
                }

                return values;
            }
            case "small-domain-random":
                for (var i = 0; i < values.Length; i++)
                    values[i] = random.Next(1, 266);
                return values;
            case "timestamp-like-13-bit":
                for (var i = 0; i < values.Length; i++)
                    values[i] = checked(i * 3_000 + i % 7 * 1_000);
                return values;
            case "random":
                random.NextBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan()));
                return values;
            default:
                throw new InvalidOperationException($"Unknown distribution '{distribution}'.");
        }
    }

    static long[] CreateLongValues(int count, string distribution)
    {
        var values = new long[count];
        var random = new Random(42);

        switch (distribution)
        {
            case "constant-delta":
                for (var i = 0; i < values.Length; i++)
                    values[i] = i * 7L;
                return values;
            case "narrow-delta":
                for (var i = 0; i < values.Length; i++)
                    values[i] = i * 3L + i % 7;
                return values;
            case "small-random-delta":
            {
                long current = 0;
                for (var i = 0; i < values.Length; i++)
                {
                    current = unchecked(current + random.Next(-16, 17));
                    values[i] = current;
                }

                return values;
            }
            case "small-domain-random":
                for (var i = 0; i < values.Length; i++)
                    values[i] = random.Next(1, 266);
                return values;
            case "timestamp-like-13-bit":
                for (var i = 0; i < values.Length; i++)
                    values[i] = i * 3_000L + i % 7 * 1_000L;
                return values;
            case "random":
                random.NextBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan()));
                return values;
            default:
                throw new InvalidOperationException($"Unknown distribution '{distribution}'.");
        }
    }
}
