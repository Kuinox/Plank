using BenchmarkDotNet.Attributes;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.Encoding;

namespace Plank.Benchmarks;

[Config(typeof(OptimizationBenchmarkConfig))]
public class DeltaByteArrayEncodingBenchmark
{
    static readonly Column DeltaByteArrayColumn = new("value", ParquetPhysicalType.ByteArray,
        new ColumnOptions(encodings: [EncodingKind.DeltaByteArray]));
    static readonly Column DeltaLengthByteArrayColumn = new("value", ParquetPhysicalType.ByteArray,
        new ColumnOptions(encodings: [EncodingKind.DeltaLengthByteArray]));

    byte[][] _byteArrayValues = [];
    ReadOnlyMemory<byte>[] _memoryValues = [];
    byte[][] _optionalByteArrayValues = [];
    ReadOnlyMemory<byte>?[] _optionalMemoryValues = [];
    BufferWriterFactory _bufferWriters;
    BufferWriter _writer;

    [Params(31, 4_096)]
    public int Rows { get; set; }

    [Params("none-mixed", "short-fixed", "long-mixed")]
    public string Distribution { get; set; } = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _byteArrayValues = CreateValues(Rows, Distribution);
        _memoryValues = Array.ConvertAll(_byteArrayValues, static value => (ReadOnlyMemory<byte>)value);
        _optionalByteArrayValues = new byte[Rows][];
        _optionalMemoryValues = new ReadOnlyMemory<byte>?[Rows];
        for (var i = 0; i < Rows; i++)
        {
            var present = i % 4 != 0;
            _optionalByteArrayValues[i] = present ? _byteArrayValues[i] : null!;
            _optionalMemoryValues[i] = present ? _memoryValues[i] : null;
        }

        const uint BufferSize = 2 * 1024 * 1024;
        _bufferWriters = new BufferWriterFactory(DefaultParquetBufferPool.Shared, BufferSize, BufferSize, BufferSize,
            BufferSize);
        _writer = new BufferWriter(DefaultParquetBufferPool.Shared, BufferSize, BufferSize);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
        => _writer.Dispose();

    [Benchmark]
    public int DeltaByteArrayByteArrays()
    {
        _writer.Reset();
        DeltaByteArrayEncoding.WriteValues(DeltaByteArrayColumn, _byteArrayValues, _bufferWriters, ref _writer);
        return _writer.WrittenLength;
    }

    [Benchmark]
    public int DeltaByteArrayMemory()
    {
        _writer.Reset();
        DeltaByteArrayEncoding.WriteValues(DeltaByteArrayColumn, _memoryValues, _bufferWriters, ref _writer);
        return _writer.WrittenLength;
    }

    [Benchmark]
    public int DeltaByteArrayOptionalByteArrays()
    {
        _writer.Reset();
        DeltaByteArrayEncoding.WriteOptionalValues<byte[], OptionalByteArrayRow>(DeltaByteArrayColumn, _optionalByteArrayValues,
            _bufferWriters, ref _writer);
        return _writer.WrittenLength;
    }

    [Benchmark]
    public int DeltaByteArrayOptionalMemory()
    {
        _writer.Reset();
        DeltaByteArrayEncoding.WriteOptionalValues<ReadOnlyMemory<byte>?, OptionalMemoryRow>(DeltaByteArrayColumn, _optionalMemoryValues, _bufferWriters,
            ref _writer);
        return _writer.WrittenLength;
    }

    [Benchmark]
    public int DeltaLengthByteArrayByteArrays()
    {
        _writer.Reset();
        DeltaLengthByteArrayEncoding.WriteValues(DeltaLengthByteArrayColumn, _byteArrayValues, _bufferWriters,
            ref _writer);
        return _writer.WrittenLength;
    }

    [Benchmark]
    public int DeltaLengthByteArrayMemory()
    {
        _writer.Reset();
        DeltaLengthByteArrayEncoding.WriteValues(DeltaLengthByteArrayColumn, _memoryValues, _bufferWriters,
            ref _writer);
        return _writer.WrittenLength;
    }

    [Benchmark]
    public int DeltaLengthByteArrayOptionalByteArrays()
    {
        _writer.Reset();
        DeltaLengthByteArrayEncoding.WriteOptionalValues<byte[], OptionalByteArrayRow>(DeltaLengthByteArrayColumn, _optionalByteArrayValues,
            _bufferWriters, ref _writer);
        return _writer.WrittenLength;
    }

    [Benchmark]
    public int DeltaLengthByteArrayOptionalMemory()
    {
        _writer.Reset();
        DeltaLengthByteArrayEncoding.WriteOptionalValues<ReadOnlyMemory<byte>?, OptionalMemoryRow>(DeltaLengthByteArrayColumn, _optionalMemoryValues,
            _bufferWriters, ref _writer);
        return _writer.WrittenLength;
    }

    static byte[][] CreateValues(int count, string distribution)
    {
        var separator = distribution.IndexOf('-');
        var prefixDistribution = distribution[..separator];
        var mixedLengths = distribution.EndsWith("mixed", StringComparison.Ordinal);
        var values = new byte[count][];
        var random = new Random(42);

        for (var i = 0; i < count; i++)
        {
            var length = GetLength(prefixDistribution, mixedLengths, i);
            var value = new byte[length];
            random.NextBytes(value);

            var sharedPrefixLength = prefixDistribution switch
            {
                "none" => 0,
                "short" => 3,
                "long" => 63,
                _ => throw new InvalidOperationException($"Unknown distribution '{distribution}'.")
            };
            if (sharedPrefixLength > 0)
                value.AsSpan(0, sharedPrefixLength).Fill(0x5a);
            if (value.Length > sharedPrefixLength)
                value[sharedPrefixLength] = unchecked((byte)i);
            if (sharedPrefixLength == 0 && value.Length > 0 && i > 0 && values[i - 1].Length > 0 &&
                value[0] == values[i - 1][0])
                value[0]++;

            values[i] = value;
        }

        return values;
    }

    static int GetLength(string prefixDistribution, bool mixedLengths, int index)
    {
        if (!mixedLengths)
            return prefixDistribution == "long" ? 128 : 32;

        return prefixDistribution switch
        {
            "none" => new[] { 0, 1, 7, 16, 127 }[index % 5],
            "short" => new[] { 8, 12, 24, 64, 127 }[index % 5],
            "long" => new[] { 64, 65, 80, 127, 256 }[index % 5],
            _ => throw new InvalidOperationException($"Unknown prefix distribution '{prefixDistribution}'.")
        };
    }

}
