using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.Encoding;

namespace Plank.Benchmarks;

public static class PlainEncodingBenchmark
{
    [Config(typeof(OptimizationBenchmarkConfig))]
    public class Boolean
    {
        static readonly Column Column = new("value", ParquetPhysicalType.Boolean);

        bool[] _values = [];
        BufferWriter _writer;

        [Params(31, 4_096)]
        public int Rows { get; set; }

        [Params("AllTrue", "Mixed")]
        public string Distribution { get; set; } = null!;

        [GlobalSetup]
        public void GlobalSetup()
        {
            _values = new bool[Rows];
            if (Distribution == "AllTrue")
                Array.Fill(_values, true);
            else
            {
                var random = new Random(42);
                for (var i = 0; i < _values.Length; i++)
                    _values[i] = random.Next(2) != 0;
            }

            var bufferSize = checked((uint)((Rows + 7) / 8 + 64));
            _writer = new BufferWriter(DefaultParquetBufferPool.Shared, bufferSize, bufferSize);
        }

        [GlobalCleanup]
        public void GlobalCleanup()
            => _writer.Dispose();

        [Benchmark]
        public int Write()
        {
            _writer.Reset();
            PlainEncoding.WriteValues(Column, _values, ref _writer);
            return _writer.WrittenLength;
        }
    }

    [Config(typeof(OptimizationBenchmarkConfig))]
    public class Numeric
    {
        static readonly Column Int32Column = new("value", ParquetPhysicalType.Int32);
        static readonly Column Int64Column = new("value", ParquetPhysicalType.Int64);
        static readonly Column FloatColumn = new("value", ParquetPhysicalType.Float);
        static readonly Column DoubleColumn = new("value", ParquetPhysicalType.Double);

        byte[] _byteValues = [];
        ushort[] _ushortValues = [];
        int[] _intValues = [];
        uint[] _uintValues = [];
        long[] _longValues = [];
        ulong[] _ulongValues = [];
        float[] _floatValues = [];
        double[] _doubleValues = [];
        BufferWriter _writer;

        [Params(31, 4_096)]
        public int Rows { get; set; }

        [GlobalSetup]
        public void GlobalSetup()
        {
            _byteValues = new byte[Rows];
            _ushortValues = new ushort[Rows];
            _intValues = new int[Rows];
            _uintValues = new uint[Rows];
            _longValues = new long[Rows];
            _ulongValues = new ulong[Rows];
            _floatValues = new float[Rows];
            _doubleValues = new double[Rows];

            var random = new Random(42);
            random.NextBytes(_byteValues);
            random.NextBytes(MemoryMarshal.AsBytes(_ushortValues.AsSpan()));
            random.NextBytes(MemoryMarshal.AsBytes(_intValues.AsSpan()));
            random.NextBytes(MemoryMarshal.AsBytes(_uintValues.AsSpan()));
            random.NextBytes(MemoryMarshal.AsBytes(_longValues.AsSpan()));
            random.NextBytes(MemoryMarshal.AsBytes(_ulongValues.AsSpan()));
            random.NextBytes(MemoryMarshal.AsBytes(_floatValues.AsSpan()));
            random.NextBytes(MemoryMarshal.AsBytes(_doubleValues.AsSpan()));

            var bufferSize = checked((uint)(Rows * sizeof(double) + 64));
            _writer = new BufferWriter(DefaultParquetBufferPool.Shared, bufferSize, bufferSize);
        }

        [GlobalCleanup]
        public void GlobalCleanup()
            => _writer.Dispose();

        [Benchmark]
        public int WriteByteAsInt32()
            => Write(Int32Column, _byteValues);

        [Benchmark]
        public int WriteUInt16AsInt32()
            => Write(Int32Column, _ushortValues);

        [Benchmark]
        public int WriteInt32()
            => Write(Int32Column, _intValues);

        [Benchmark]
        public int WriteUInt32()
            => Write(Int32Column, _uintValues);

        [Benchmark]
        public int WriteInt64()
            => Write(Int64Column, _longValues);

        [Benchmark]
        public int WriteUInt64()
            => Write(Int64Column, _ulongValues);

        [Benchmark]
        public int WriteFloat()
            => Write(FloatColumn, _floatValues);

        [Benchmark]
        public int WriteDouble()
            => Write(DoubleColumn, _doubleValues);

        int Write<T>(Column column, T[] values)
            where T : notnull
        {
            _writer.Reset();
            PlainEncoding.WriteValues(column, values, ref _writer);
            return _writer.WrittenLength;
        }
    }

}
