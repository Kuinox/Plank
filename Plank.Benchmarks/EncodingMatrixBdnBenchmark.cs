using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Parquet;
using Parquet.Data;
using Parquet.Schema;
using ParquetSharp;
using Plank.Schema;
using Plank.Writing;
using CompressionMethod = Parquet.CompressionMethod;
using DataColumn = Parquet.Data.DataColumn;
using DataField = Parquet.Schema.DataField;
using Encoding = ParquetSharp.Encoding;
using ParquetSchema = Parquet.Schema.ParquetSchema;
using PlankColumn = Plank.Schema.Column;
using PlankSchema = Plank.Schema.ParquetSchema;

namespace Plank.Benchmarks;

[Config(typeof(EncodingMatrixBenchmarkConfig))]
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput)]
[InvocationCount(100)]
public class EncodingMatrixBdnBenchmark
{
    static readonly string[] Libraries = ["plank", "parquetsharp", "parquet.net"];
    static readonly string[] DataTypes = ["bool", "int32", "int64", "float", "double", "string"];

    static readonly string[] Encodings =
    [
        "plain",
        // "dictionary",
        // "delta_binary_packed",
        // "delta_length_byte_array",
        // "delta_byte_array",
        // "byte_stream_split"
    ];

    bool[] _boolValues = [];
    int[] _int32Values = [];
    long[] _int64Values = [];
    float[] _floatValues = [];
    double[] _doubleValues = [];
    string[] _stringValues = [];
    byte[] _lastParquetBuffer = [];

    [Params(1_000_000)] public int Rows { get; set; }

    [ParamsSource(nameof(AllCases))] public EncodingBenchmarkCase Case { get; set; }

    public static IEnumerable<EncodingBenchmarkCase> AllCases()
    {
        foreach (var library in Libraries)
        foreach (var dataType in DataTypes)
        foreach (var encoding in Encodings)
            if (IsSupported(library, dataType, encoding))
                yield return new EncodingBenchmarkCase(library, dataType, encoding);
    }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _boolValues = new bool[Rows];
        _int32Values = new int[Rows];
        _int64Values = new long[Rows];
        _floatValues = new float[Rows];
        _doubleValues = new double[Rows];
        _stringValues = new string[Rows];
        for (var i = 0; i < Rows; i++)
        {
            _boolValues[i] = (i & 1) == 0;
            _int32Values[i] = i % 100_000;
            _int64Values[i] = i * 37L;
            _floatValues[i] = (i % 10_000) / 3f;
            _doubleValues[i] = (i % 10_000) / 7d;
            _stringValues[i] = $"val-{i % 2048}";
        }
    }

    [Benchmark]
    public async Task WriteSingleColumnAsync()
    {
        switch (Case.Library)
        {
            case "plank":
                await WriteWithPlankAsync().ConfigureAwait(false);
                return;
            case "parquetsharp":
                await WriteWithParquetSharpAsync().ConfigureAwait(false);
                return;
            case "parquet.net":
                await WriteWithParquetNetAsync().ConfigureAwait(false);
                return;
            default:
                throw new InvalidOperationException($"Unknown library '{Case.Library}'.");
        }
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        var snapshot = ReadColumnBytes(_lastParquetBuffer);
        EncodingBenchmarkMetrics.Record(Case, Rows, snapshot);
        _lastParquetBuffer = [];
    }

    async Task WriteWithPlankAsync()
    {
        var column = new PlankColumn(
            "value",
            Case.DataType switch
            {
                "bool" => ParquetPhysicalType.Boolean,
                "int32" => ParquetPhysicalType.Int32,
                "int64" => ParquetPhysicalType.Int64,
                "float" => ParquetPhysicalType.Float,
                "double" => ParquetPhysicalType.Double,
                "string" => ParquetPhysicalType.ByteArray,
                _ => throw new InvalidOperationException($"Unknown type '{Case.DataType}'.")
            },
            new ColumnOptions(ParquetRepetition.Required, [MapPlankEncoding(Case.Encoding)]));
        var schema = new PlankSchema([column]);
        await using var stream = new MemoryStream(capacity: Rows * 16);
        using var writer = Plank.Writing.ParquetWriter.Create(stream, schema, new ParquetWriterOptions
        {
            Compression = CompressionKind.None,
            ExpectedRowGroupCount = 1,
            RowGroupRowCountHint = checked((uint)Rows),
            DateTimeKindHandling = DateTimeKindHandling.PreserveClockTime
        });
        var rowGroup = writer.StartRowGroup();
        switch (Case.DataType)
        {
            case "bool":
                await rowGroup.WriteAsync(column, _boolValues).ConfigureAwait(false);
                break;
            case "int32":
                await rowGroup.WriteAsync(column, _int32Values).ConfigureAwait(false);
                break;
            case "int64":
                await rowGroup.WriteAsync(column, _int64Values).ConfigureAwait(false);
                break;
            case "float":
                await rowGroup.WriteAsync(column, _floatValues).ConfigureAwait(false);
                break;
            case "double":
                await rowGroup.WriteAsync(column, _doubleValues).ConfigureAwait(false);
                break;
            case "string":
                await rowGroup.WriteAsync(column, _stringValues).ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException($"Unknown type '{Case.DataType}'.");
        }

        writer.CloseFile();
        await stream.FlushAsync().ConfigureAwait(false);
        _lastParquetBuffer = stream.ToArray();
    }

    Task WriteWithParquetSharpAsync()
    {
        using var stream = new MemoryStream(capacity: Rows * 16);
        using var writerProperties = BuildParquetSharpWriterProperties(Case.Encoding);
        using var writer = new ParquetFileWriter(stream, [BuildParquetSharpColumn(Case.DataType)], null,
            writerProperties, null, true);
        using var rowGroupWriter = writer.AppendRowGroup();
        switch (Case.DataType)
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
                throw new InvalidOperationException($"Unknown type '{Case.DataType}'.");
        }

        writer.Close();
        stream.Flush();
        _lastParquetBuffer = stream.ToArray();
        return Task.CompletedTask;
    }

    async Task WriteWithParquetNetAsync()
    {
        var options = new ParquetOptions
        {
            UseDictionaryEncoding = Case.Encoding == "dictionary",
            UseDeltaBinaryPackedEncoding = Case.Encoding == "delta_binary_packed"
        };
        await using var stream = new MemoryStream(capacity: Rows * 16);
        switch (Case.DataType)
        {
            case "bool":
            {
                var field = new DataField<bool>("value");
                var schema = new ParquetSchema(field);
                await using var writer = await Parquet.ParquetWriter.CreateAsync(schema, stream, options, false)
                    .ConfigureAwait(false);
                writer.CompressionMethod = CompressionMethod.None;
                using var rowGroupWriter = writer.CreateRowGroup();
                await rowGroupWriter.WriteColumnAsync(new DataColumn(field, _boolValues)).ConfigureAwait(false);
                break;
            }
            case "int32":
            {
                var field = new DataField<int>("value");
                var schema = new ParquetSchema(field);
                await using var writer = await Parquet.ParquetWriter.CreateAsync(schema, stream, options, false)
                    .ConfigureAwait(false);
                writer.CompressionMethod = CompressionMethod.None;
                using var rowGroupWriter = writer.CreateRowGroup();
                await rowGroupWriter.WriteColumnAsync(new DataColumn(field, _int32Values)).ConfigureAwait(false);
                break;
            }
            case "int64":
            {
                var field = new DataField<long>("value");
                var schema = new ParquetSchema(field);
                await using var writer = await Parquet.ParquetWriter.CreateAsync(schema, stream, options, false)
                    .ConfigureAwait(false);
                writer.CompressionMethod = CompressionMethod.None;
                using var rowGroupWriter = writer.CreateRowGroup();
                await rowGroupWriter.WriteColumnAsync(new DataColumn(field, _int64Values)).ConfigureAwait(false);
                break;
            }
            case "float":
            {
                var field = new DataField<float>("value");
                var schema = new ParquetSchema(field);
                await using var writer = await Parquet.ParquetWriter.CreateAsync(schema, stream, options, false)
                    .ConfigureAwait(false);
                writer.CompressionMethod = CompressionMethod.None;
                using var rowGroupWriter = writer.CreateRowGroup();
                await rowGroupWriter.WriteColumnAsync(new DataColumn(field, _floatValues)).ConfigureAwait(false);
                break;
            }
            case "double":
            {
                var field = new DataField<double>("value");
                var schema = new ParquetSchema(field);
                await using var writer = await Parquet.ParquetWriter.CreateAsync(schema, stream, options, false)
                    .ConfigureAwait(false);
                writer.CompressionMethod = CompressionMethod.None;
                using var rowGroupWriter = writer.CreateRowGroup();
                await rowGroupWriter.WriteColumnAsync(new DataColumn(field, _doubleValues)).ConfigureAwait(false);
                break;
            }
            case "string":
            {
                var field = new DataField<string>("value");
                var schema = new ParquetSchema(field);
                await using var writer = await Parquet.ParquetWriter.CreateAsync(schema, stream, options, false)
                    .ConfigureAwait(false);
                writer.CompressionMethod = CompressionMethod.None;
                using var rowGroupWriter = writer.CreateRowGroup();
                await rowGroupWriter.WriteColumnAsync(new DataColumn(field, _stringValues)).ConfigureAwait(false);
                break;
            }
            default:
                throw new InvalidOperationException($"Unknown type '{Case.DataType}'.");
        }

        await stream.FlushAsync().ConfigureAwait(false);
        _lastParquetBuffer = stream.ToArray();
    }

    static EncodingBenchmarkSizeSnapshot ReadColumnBytes(byte[] parquetData)
    {
        using var stream = new MemoryStream(parquetData, writable: false);
        using var reader = new ParquetFileReader(stream);
        using var rowGroup = reader.RowGroup(0);
        using var chunk = rowGroup.MetaData.GetColumnChunkMetaData(0);
        var fileBytes = parquetData.LongLength;
        return new EncodingBenchmarkSizeSnapshot(chunk.TotalCompressedSize, chunk.TotalUncompressedSize, fileBytes);
    }

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

    static bool IsSupported(string library, string dataType, string encoding)
    {
        if (library == "plank")
            return encoding == "plain";

        if (library == "parquetsharp")
        {
            if (encoding is "plain" or "dictionary")
                return true;
            if (encoding == "delta_binary_packed")
                return dataType is "int32" or "int64";
            if (encoding is "delta_length_byte_array" or "delta_byte_array")
                return dataType == "string";
            if (encoding == "byte_stream_split")
                return dataType is "int32" or "int64" or "float" or "double";
            return false;
        }

        if (library == "parquet.net")
            return encoding is "plain" or "dictionary" or "delta_binary_packed";
        return false;
    }
}