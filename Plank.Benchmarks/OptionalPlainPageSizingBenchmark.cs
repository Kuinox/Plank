using BenchmarkDotNet.Attributes;
using Plank.Benchmarks.Published;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Benchmarks;

[Config(typeof(OptimizationBenchmarkConfig))]
public class OptionalPlainPageSizingBenchmark
{
    NonClosingMemoryStream _int64Stream = null!;
    NonClosingMemoryStream _doubleStream = null!;
    ParquetWriter _int64Writer = null!;
    ParquetWriter _doubleWriter = null!;
    SerializedColumn<long?> _int64Column = null!;
    SerializedColumn<double?> _doubleColumn = null!;
    long?[] _int64Values = [];
    double?[] _doubleValues = [];

    [Params(1_000_000)]
    public int Rows { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _int64Values = new long?[Rows];
        _doubleValues = new double?[Rows];
        for (var i = 0; i < Rows; i++)
        {
            _int64Values[i] = unchecked((long)((ulong)i * 0x9E3779B97F4A7C15UL));
            _doubleValues[i] = (i % 100_000) / 7d;
        }

        _int64Stream = new NonClosingMemoryStream();
        _doubleStream = new NonClosingMemoryStream();
        (_int64Writer, _int64Column) = CreateWriter<long>(_int64Stream, ParquetPhysicalType.Int64);
        (_doubleWriter, _doubleColumn) = CreateWriter<double>(_doubleStream, ParquetPhysicalType.Double);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _int64Stream.Dispose();
        _doubleStream.Dispose();
    }

    [Benchmark]
    public long WriteInt64()
        => Write(_int64Writer, _int64Column, _int64Values, _int64Stream);

    [Benchmark]
    public long WriteDouble()
        => Write(_doubleWriter, _doubleColumn, _doubleValues, _doubleStream);

    static (ParquetWriter Writer, SerializedColumn<T?> Column) CreateWriter<T>(NonClosingMemoryStream stream,
        ParquetPhysicalType physicalType)
        where T : struct
    {
        var schema = new ParquetSchema([
            ColumnDefinition.OptionalLeaf("value", physicalType,
                new ColumnOptions(encodings: [EncodingKind.Plain]))
        ]);
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None,
            WritePageIndexes = false,
            WritePageCrc = false
        });
        return (writer, writer.CreateSerializedColumn<T?>(schema.LeafColumns[0]));
    }

    static long Write<T>(ParquetWriter writer, SerializedColumn<T?> column, T?[] values,
        NonClosingMemoryStream stream)
        where T : struct
    {
        stream.Reset();
        writer.Reset(stream);
        column.Serialize(values);
        writer.StartRowGroup().Write(column);
        writer.CloseFile();
        return stream.Length;
    }
}
