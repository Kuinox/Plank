using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.Encoding;

namespace Plank.Benchmarks;

[Config(typeof(OptimizationBenchmarkConfig))]
public class ByteStreamSplitEncodingBenchmark
{
    static readonly Column Int32Column = new("value", ParquetPhysicalType.Int32);
    static readonly Column Int64Column = new("value", ParquetPhysicalType.Int64);
    static readonly Column FloatColumn = new("value", ParquetPhysicalType.Float);
    static readonly Column DoubleColumn = new("value", ParquetPhysicalType.Double);
    static readonly Column FixedLengthColumn = new("value", ParquetPhysicalType.FixedLenByteArray,
        new ColumnOptions(typeLength: 16));

    int[] _int32Values = [];
    long[] _int64Values = [];
    float[] _floatValues = [];
    double[] _doubleValues = [];
    byte[][] _fixedLengthValues = [];
    BufferWriter _writer;

    [Params(31, 4_096)]
    public int Rows { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _int32Values = new int[Rows];
        _int64Values = new long[Rows];
        _floatValues = new float[Rows];
        _doubleValues = new double[Rows];
        _fixedLengthValues = new byte[Rows][];

        var random = new Random(42);
        random.NextBytes(MemoryMarshal.AsBytes(_int32Values.AsSpan()));
        random.NextBytes(MemoryMarshal.AsBytes(_int64Values.AsSpan()));
        random.NextBytes(MemoryMarshal.AsBytes(_floatValues.AsSpan()));
        random.NextBytes(MemoryMarshal.AsBytes(_doubleValues.AsSpan()));
        for (var i = 0; i < _fixedLengthValues.Length; i++)
        {
            _fixedLengthValues[i] = new byte[16];
            random.NextBytes(_fixedLengthValues[i]);
        }

        var bufferSize = checked((uint)(Rows * 16 + 1_024));
        _writer = new BufferWriter(DefaultParquetBufferPool.Shared, bufferSize, bufferSize);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
        => _writer.Dispose();

    [Benchmark]
    public int WriteInt32()
    {
        _writer.Reset();
        ByteStreamSplitEncoding.WriteValues(Int32Column, _int32Values, ref _writer);
        return _writer.WrittenLength;
    }

    [Benchmark]
    public int WriteInt64()
    {
        _writer.Reset();
        ByteStreamSplitEncoding.WriteValues(Int64Column, _int64Values, ref _writer);
        return _writer.WrittenLength;
    }

    [Benchmark]
    public int WriteFloat()
    {
        _writer.Reset();
        ByteStreamSplitEncoding.WriteValues(FloatColumn, _floatValues, ref _writer);
        return _writer.WrittenLength;
    }

    [Benchmark]
    public int WriteDouble()
    {
        _writer.Reset();
        ByteStreamSplitEncoding.WriteValues(DoubleColumn, _doubleValues, ref _writer);
        return _writer.WrittenLength;
    }

    [Benchmark]
    public int WriteFixedLengthByteArray()
    {
        _writer.Reset();
        ByteStreamSplitEncoding.WriteValues(FixedLengthColumn, _fixedLengthValues, ref _writer);
        return _writer.WrittenLength;
    }
}
