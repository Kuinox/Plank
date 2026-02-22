using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Parquet;
using Parquet.Schema;
using ParquetSharp;
using System.Collections.Generic;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.PageStrategy;
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
    static readonly IPageStrategy _forceDictionaryPageStrategy = new ForceDictionaryPageStrategy();

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
        EnsureDictionaryEncodingIfRequired(library, scenario, parquetBuffer);
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
        var forceDictionary = scenario.EncodingName == "dictionary";

        void SerializeValues<T>(ReadOnlySpan<T> values)
            where T : notnull
        {
            if (forceDictionary)
                serialized.Serialize(column, values, _forceDictionaryPageStrategy);
            else
                serialized.Serialize(column, values);
        }

        switch (scenario.DataType)
        {
            case "bool":
                SerializeValues(_boolValues);
                break;
            case "int32":
                SerializeValues(_int32Values);
                break;
            case "int64":
                SerializeValues(_int64Values);
                break;
            case "float":
                SerializeValues(_floatValues);
                break;
            case "double":
                SerializeValues(_doubleValues);
                break;
            case "string":
                SerializeValues(_stringByteValues);
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
        using var writerProperties = BuildParquetSharpWriterProperties(scenario, Rows);
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

    static void EnsureDictionaryEncodingIfRequired(string library, SingleColumnScenario scenario, byte[] parquetData)
    {
        if (scenario.EncodingName != "dictionary")
            return;

        var encodings = ReadColumnEncodings(parquetData);
        if (ContainsDictionaryEncoding(encodings))
            return;

        throw new InvalidOperationException(
            $"Dictionary encoding was requested for benchmark '{scenario}' ({library}), but output column metadata did not contain dictionary encoding.");
    }

    static Encoding[] ReadColumnEncodings(byte[] parquetData)
    {
        using var stream = new MemoryStream(parquetData, writable: false);
        using var reader = new ParquetFileReader(stream);
        using var rowGroup = reader.RowGroup(0);
        using var chunk = rowGroup.MetaData.GetColumnChunkMetaData(0);
        return chunk.Encodings;
    }

    static bool ContainsDictionaryEncoding(Encoding[] encodings)
    {
        for (var i = 0; i < encodings.Length; i++)
            if (encodings[i] is Encoding.PlainDictionary or Encoding.RleDictionary)
                return true;
        return false;
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

    static WriterProperties BuildParquetSharpWriterProperties(SingleColumnScenario scenario, int rows)
    {
        var builder = new WriterPropertiesBuilder().Compression(Compression.Uncompressed);
        if (scenario.EncodingName == "dictionary")
            return builder.EnableDictionary().DictionaryPagesizeLimit(GetForcedDictionaryPageSizeLimitBytes(
                scenario.DataType, rows)).Build();

        var mapped = scenario.EncodingName switch
        {
            "plain" => Encoding.Plain,
            "delta_binary_packed" => Encoding.DeltaBinaryPacked,
            "delta_length_byte_array" => Encoding.DeltaLengthByteArray,
            "delta_byte_array" => Encoding.DeltaByteArray,
            "byte_stream_split" => Encoding.ByteStreamSplit,
            _ => throw new InvalidOperationException($"Unknown encoding '{scenario.EncodingName}'.")
        };
        return builder.DisableDictionary().Encoding(mapped).Build();
    }

    static long GetForcedDictionaryPageSizeLimitBytes(string dataType, int rows)
    {
        var estimatedValueSizeBytes = dataType switch
        {
            "int32" => 4L,
            "int64" => 8L,
            "float" => 4L,
            "double" => 8L,
            "string" => 32L,
            _ => 8L
        };
        return checked(rows * estimatedValueSizeBytes * 4L);
    }

    static ParquetOptions BuildParquetNetOptions(string encoding)
        => encoding switch
        {
            "plain" => new ParquetOptions(),
            "dictionary" => new ParquetOptions { UseDictionaryEncoding = true, DictionaryEncodingThreshold = 1.0 },
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

    sealed class ForceDictionaryPageStrategy : IPageStrategy
    {
        public DictionaryMode GetDictionaryMode(PlankColumn column)
        {
            var encodings = column.Options.Encodings;
            for (var i = 0; i < encodings.Length; i++)
                if (encodings[i] is EncodingKind.PlainDictionary or EncodingKind.RleDictionary)
                    return DictionaryMode.Forced;
            return DictionaryMode.Disabled;
        }

        public bool ShouldDropDictionary<T>(PlankColumn column, IReadOnlyDictionary<T, int> dictionary,
            int totalRowCount, int rowsSeen)
            where T : notnull
            => false;

        public bool ShouldStartNewDataPage(PlankColumn column, int totalRowCount, int rowsWritten, int currentPageRowCount)
            => false;
    }
}
