using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Parquet;
using Parquet.Schema;
using ParquetSharp;
using Plank.Schema;
using Plank.Writing;
using CompressionMethod = Parquet.CompressionMethod;
using DataColumn = Parquet.Data.DataColumn;
using Encoding = ParquetSharp.Encoding;
using ParquetSchema = Parquet.Schema.ParquetSchema;
using PlankColumn = Plank.Schema.Column;
using PlankSchema = Plank.Schema.ParquetSchema;

namespace Plank.Benchmarks;

[Config(typeof(EncodingMatrixBenchmarkConfig))]
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class EncodingMatrixBdnBenchmark
{
    const string PlankLibrary = "plank";
    const string ParquetSharpLibrary = "parquetsharp";
    const string ParquetNetLibrary = "parquet.net";

    bool[] _boolValues = [];
    int[] _int32Values = [];
    long[] _int64Values = [];
    float[] _floatValues = [];
    double[] _doubleValues = [];
    string[] _stringValues = [];
    byte[][] _stringByteValues = [];

    [Params(1_000_000)]
    public int Rows { get; set; }

    public IEnumerable<SingleColumnScenario> PlankScenarios
        => SingleColumnScenarioCatalog.Plank;

    public IEnumerable<SingleColumnScenario> ParquetSharpScenarios
        => SingleColumnScenarioCatalog.ParquetSharp;

    public IEnumerable<SingleColumnScenario> ParquetNetScenarios
        => SingleColumnScenarioCatalog.ParquetNet;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _boolValues = new bool[Rows];
        _int32Values = new int[Rows];
        _int64Values = new long[Rows];
        _floatValues = new float[Rows];
        _doubleValues = new double[Rows];
        _stringValues = new string[Rows];
        _stringByteValues = new byte[Rows][];
        for (var i = 0; i < Rows; i++)
        {
            _boolValues[i] = (i & 1) == 0;
            _int32Values[i] = i % 100_000;
            _int64Values[i] = i * 37L;
            _floatValues[i] = (i % 10_000) / 3f;
            _doubleValues[i] = (i % 10_000) / 7d;
            _stringValues[i] = $"val-{i % 2048}";
            _stringByteValues[i] = System.Text.Encoding.UTF8.GetBytes(_stringValues[i]);
        }
    }

    [Benchmark(Baseline = true)]
    [ArgumentsSource(nameof(PlankScenarios))]
    public async Task WritePlankAsync(SingleColumnScenario scenario)
    {
        var parquetBuffer = await WriteWithPlankAsync(scenario).ConfigureAwait(false);
        RecordMetrics(PlankLibrary, scenario, parquetBuffer);
    }

    [Benchmark]
    [ArgumentsSource(nameof(ParquetSharpScenarios))]
    public void WriteParquetSharp(SingleColumnScenario scenario)
    {
        var parquetBuffer = WriteWithParquetSharp(scenario);
        RecordMetrics(ParquetSharpLibrary, scenario, parquetBuffer);
    }

    [Benchmark]
    [ArgumentsSource(nameof(ParquetNetScenarios))]
    public async Task WriteParquetNetAsync(SingleColumnScenario scenario)
    {
        var parquetBuffer = await WriteWithParquetNetAsync(scenario).ConfigureAwait(false);
        RecordMetrics(ParquetNetLibrary, scenario, parquetBuffer);
    }

    void RecordMetrics(string library, SingleColumnScenario scenario, byte[] parquetBuffer)
    {
        var snapshot = ReadColumnBytes(parquetBuffer);
        EncodingBenchmarkMetrics.Record(library, scenario.DataType, scenario.EncodingName, Rows, snapshot);
    }

    async Task<byte[]> WriteWithPlankAsync(SingleColumnScenario scenario)
    {
        var column = new PlankColumn(
            "value",
            MapPlankPhysicalType(scenario.DataType),
            new ColumnOptions(ParquetRepetition.Required, [MapPlankEncoding(scenario.EncodingName)]));
        var schema = new PlankSchema([column]);
        await using var stream = new MemoryStream(capacity: Rows * 16);
        var writer = Plank.Writing.ParquetWriter.Create(stream, schema, new ParquetWriterOptions
        {
            Compression = CompressionKind.None
        });
        var rowGroup = writer.StartRowGroup();
        var serialized = writer.CreateSerializedColumn();
        switch (scenario.DataType)
        {
            case "bool":
                serialized.Serialize(column, _boolValues);
                break;
            case "int32":
                serialized.Serialize(column, _int32Values);
                break;
            case "int64":
                serialized.Serialize(column, _int64Values);
                break;
            case "float":
                serialized.Serialize(column, _floatValues);
                break;
            case "double":
                serialized.Serialize(column, _doubleValues);
                break;
            case "string":
                serialized.Serialize(column, _stringByteValues);
                break;
            default:
                throw new InvalidOperationException($"Unknown type '{scenario.DataType}'.");
        }

        rowGroup.Write(serialized);
        writer.CloseFile();
        return stream.ToArray();
    }

    byte[] WriteWithParquetSharp(SingleColumnScenario scenario)
    {
        using var stream = new MemoryStream(capacity: Rows * 16);
        using var writerProperties = BuildParquetSharpWriterProperties(scenario.EncodingName);
        using var writer = new ParquetFileWriter(stream, [BuildParquetSharpColumn(scenario.DataType)], null,
            writerProperties, null, true);
        using var rowGroupWriter = writer.AppendRowGroup();
        switch (scenario.DataType)
        {
            case "bool":
                using (var col = rowGroupWriter.NextColumn().LogicalWriter<bool>())
                    col.WriteBatch(_boolValues);
                break;
            case "int32":
                using (var col = rowGroupWriter.NextColumn().LogicalWriter<int>())
                    col.WriteBatch(_int32Values);
                break;
            case "int64":
                using (var col = rowGroupWriter.NextColumn().LogicalWriter<long>())
                    col.WriteBatch(_int64Values);
                break;
            case "float":
                using (var col = rowGroupWriter.NextColumn().LogicalWriter<float>())
                    col.WriteBatch(_floatValues);
                break;
            case "double":
                using (var col = rowGroupWriter.NextColumn().LogicalWriter<double>())
                    col.WriteBatch(_doubleValues);
                break;
            case "string":
                using (var col = rowGroupWriter.NextColumn().LogicalWriter<string>())
                    col.WriteBatch(_stringValues);
                break;
            default:
                throw new InvalidOperationException($"Unknown type '{scenario.DataType}'.");
        }

        writer.Close();
        return stream.ToArray();
    }

    async Task<byte[]> WriteWithParquetNetAsync(SingleColumnScenario scenario)
    {
        var options = BuildParquetNetOptions(scenario.EncodingName);
        await using var stream = new MemoryStream(capacity: Rows * 16);
        switch (scenario.DataType)
        {
            case "bool":
                await WriteParquetNetColumnAsync(new DataField<bool>("value"), _boolValues).ConfigureAwait(false);
                break;
            case "int32":
                await WriteParquetNetColumnAsync(new DataField<int>("value"), _int32Values).ConfigureAwait(false);
                break;
            case "int64":
                await WriteParquetNetColumnAsync(new DataField<long>("value"), _int64Values).ConfigureAwait(false);
                break;
            case "float":
                await WriteParquetNetColumnAsync(new DataField<float>("value"), _floatValues).ConfigureAwait(false);
                break;
            case "double":
                await WriteParquetNetColumnAsync(new DataField<double>("value"), _doubleValues).ConfigureAwait(false);
                break;
            case "string":
                await WriteParquetNetColumnAsync(new DataField<string>("value"), _stringValues).ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException($"Unknown type '{scenario.DataType}'.");
        }

        await stream.FlushAsync().ConfigureAwait(false);
        return stream.ToArray();

        async Task WriteParquetNetColumnAsync<T>(DataField<T> field, T[] values)
        {
            var schema = new ParquetSchema(field);
            await using var writer = await Parquet.ParquetWriter.CreateAsync(schema, stream, options, false)
                .ConfigureAwait(false);
            writer.CompressionMethod = CompressionMethod.None;
            using var rowGroupWriter = writer.CreateRowGroup();
            await rowGroupWriter.WriteColumnAsync(new DataColumn(field, values)).ConfigureAwait(false);
        }
    }

    static EncodingBenchmarkSizeSnapshot ReadColumnBytes(byte[] parquetData)
    {
        using var stream = new MemoryStream(parquetData, writable: false);
        using var reader = new ParquetFileReader(stream);
        using var rowGroup = reader.RowGroup(0);
        using var chunk = rowGroup.MetaData.GetColumnChunkMetaData(0);
        return new EncodingBenchmarkSizeSnapshot(chunk.TotalCompressedSize, chunk.TotalUncompressedSize,
            parquetData.LongLength);
    }

    static ParquetPhysicalType MapPlankPhysicalType(string dataType)
        => dataType switch
        {
            "bool" => ParquetPhysicalType.Boolean,
            "int32" => ParquetPhysicalType.Int32,
            "int64" => ParquetPhysicalType.Int64,
            "float" => ParquetPhysicalType.Float,
            "double" => ParquetPhysicalType.Double,
            "string" => ParquetPhysicalType.ByteArray,
            _ => throw new InvalidOperationException($"Unknown type '{dataType}'.")
        };

    static ParquetSharp.Column BuildParquetSharpColumn(string dataType)
        => dataType switch
        {
            "bool" => new ParquetSharp.Column<bool>("value"),
            "int32" => new ParquetSharp.Column<int>("value"),
            "int64" => new ParquetSharp.Column<long>("value"),
            "float" => new ParquetSharp.Column<float>("value"),
            "double" => new ParquetSharp.Column<double>("value"),
            "string" => new ParquetSharp.Column<string>("value"),
            _ => throw new InvalidOperationException($"Unknown type '{dataType}'.")
        };

    static WriterProperties BuildParquetSharpWriterProperties(string encoding)
    {
        var builder = new WriterPropertiesBuilder().Compression(Compression.Uncompressed);
        if (encoding == "dictionary")
            return builder.EnableDictionary().Build();

        var mapped = encoding switch
        {
            "plain" => Encoding.Plain,
            "delta_binary_packed" => Encoding.DeltaBinaryPacked,
            "delta_length_byte_array" => Encoding.DeltaLengthByteArray,
            "delta_byte_array" => Encoding.DeltaByteArray,
            "byte_stream_split" => Encoding.ByteStreamSplit,
            _ => throw new InvalidOperationException($"Unknown encoding '{encoding}'.")
        };
        return builder.DisableDictionary().Encoding(mapped).Build();
    }

    static ParquetOptions BuildParquetNetOptions(string encoding)
        => encoding switch
        {
            "plain" => new ParquetOptions(),
            "dictionary" => new ParquetOptions { UseDictionaryEncoding = true },
            "delta_binary_packed" => new ParquetOptions { UseDeltaBinaryPackedEncoding = true },
            _ => throw new InvalidOperationException($"Parquet.Net does not support encoding '{encoding}'.")
        };

    static EncodingKind MapPlankEncoding(string encoding)
        => encoding switch
        {
            "plain" => EncodingKind.Plain,
            "dictionary" => EncodingKind.PlainDictionary,
            "delta_binary_packed" => EncodingKind.DeltaBinaryPacked,
            "delta_length_byte_array" => EncodingKind.DeltaLengthByteArray,
            "delta_byte_array" => EncodingKind.DeltaByteArray,
            "byte_stream_split" => EncodingKind.ByteStreamSplit,
            _ => throw new InvalidOperationException($"Unknown encoding '{encoding}'.")
        };
}
