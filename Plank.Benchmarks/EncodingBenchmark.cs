using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using System.Collections.Immutable;
using Parquet;
using Parquet.Schema;
using ParquetSharp;
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

[MemoryDiagnoser]
[SimpleJob]
public class EncodingBenchmark
{
    static readonly ParquetSharp.Column<bool> _parquetSharpBoolColumn = new("value");
    static readonly ParquetSharp.Column<int> _parquetSharpInt32Column = new("value");
    static readonly ParquetSharp.Column<long> _parquetSharpInt64Column = new("value");
    static readonly ParquetSharp.Column<float> _parquetSharpFloatColumn = new("value");
    static readonly ParquetSharp.Column<double> _parquetSharpDoubleColumn = new("value");
    static readonly ParquetSharp.Column<string> _parquetSharpStringColumn = new("value");
    static readonly DataField<bool> _parquetNetBoolField = new("value");
    static readonly DataField<int> _parquetNetInt32Field = new("value");
    static readonly DataField<long> _parquetNetInt64Field = new("value");
    static readonly DataField<float> _parquetNetFloatField = new("value");
    static readonly DataField<double> _parquetNetDoubleField = new("value");
    static readonly DataField<string> _parquetNetStringField = new("value");
    static readonly ParquetSchema _parquetNetBoolSchema = new(_parquetNetBoolField);
    static readonly ParquetSchema _parquetNetInt32Schema = new(_parquetNetInt32Field);
    static readonly ParquetSchema _parquetNetInt64Schema = new(_parquetNetInt64Field);
    static readonly ParquetSchema _parquetNetFloatSchema = new(_parquetNetFloatField);
    static readonly ParquetSchema _parquetNetDoubleSchema = new(_parquetNetDoubleField);
    static readonly ParquetSchema _parquetNetStringSchema = new(_parquetNetStringField);

    MemoryStream _sharedStream = null!;
    PlankColumn _plankColumn = null!;
    Plank.Writing.ParquetWriter _plankWriter = null!;
    ParquetSharp.Column _parquetSharpColumn = null!;

    bool[] _boolValues = [];
    int[] _int32Values = [];
    long[] _int64Values = [];
    float[] _floatValues = [];
    double[] _doubleValues = [];
    string[] _stringValues = [];
    byte[][] _stringByteValues = [];

    [Params(1_000_000)]
    public int Rows { get; set; }

    [Params("bool", "int32", "int64", "float", "double", "string")]
    public string DataType { get; set; } = "bool";

    [Params("plain", "dictionary", "delta_binary_packed", "delta_length_byte_array", "delta_byte_array", "byte_stream_split")]
    public string EncodingName { get; set; } = "plain";

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

        _sharedStream = new MemoryStream(capacity: Rows * 64);
        _plankColumn = new PlankColumn(
            "value",
            MapPlankPhysicalType(DataType),
            new ColumnOptions(ParquetRepetition.Required, [MapPlankEncoding(EncodingName)]));
        var plankSchema = CreatePlankSchema(_plankColumn, EncodingName);
        _plankWriter = plankSchema.CreateWriter(_sharedStream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None
        });
        _parquetSharpColumn = GetParquetSharpColumn(DataType);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
        => _sharedStream.Dispose();

    [Benchmark(Baseline = true)]
    public void WritePlank()
    {
        ResetSharedStream();
        _plankWriter.Reset(_sharedStream);
        var rowGroup = _plankWriter.StartRowGroup();

        switch (DataType)
        {
            case "bool":
                var boolSerialized = _plankWriter.CreateSerializedColumn<bool>(_plankColumn);
                boolSerialized.Serialize(_boolValues);
                rowGroup.Write(boolSerialized);
                break;
            case "int32":
                var int32Serialized = _plankWriter.CreateSerializedColumn<int>(_plankColumn);
                int32Serialized.Serialize(_int32Values);
                rowGroup.Write(int32Serialized);
                break;
            case "int64":
                var int64Serialized = _plankWriter.CreateSerializedColumn<long>(_plankColumn);
                int64Serialized.Serialize(_int64Values);
                rowGroup.Write(int64Serialized);
                break;
            case "float":
                var floatSerialized = _plankWriter.CreateSerializedColumn<float>(_plankColumn);
                floatSerialized.Serialize(_floatValues);
                rowGroup.Write(floatSerialized);
                break;
            case "double":
                var doubleSerialized = _plankWriter.CreateSerializedColumn<double>(_plankColumn);
                doubleSerialized.Serialize(_doubleValues);
                rowGroup.Write(doubleSerialized);
                break;
            case "string":
                var stringSerialized = _plankWriter.CreateSerializedColumn<byte[]>(_plankColumn);
                stringSerialized.Serialize(_stringByteValues);
                rowGroup.Write(stringSerialized);
                break;
            default:
                throw new InvalidOperationException($"Unknown type '{DataType}'.");
        }
    }

    [Benchmark]
    public void WriteParquetSharp()
    {
        ResetSharedStream();
        using var writerProperties = BuildParquetSharpWriterProperties(EncodingName, DataType, Rows);
        using var writer = new ParquetFileWriter(_sharedStream, [_parquetSharpColumn], null,
            writerProperties, null, true);
        using var rowGroupWriter = writer.AppendRowGroup();
        switch (DataType)
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
                throw new InvalidOperationException($"Unknown type '{DataType}'.");
        }

        writer.Close();
    }

    [Benchmark]
    public async Task WriteParquetNetAsync()
    {
        ResetSharedStream();
        var options = BuildParquetNetOptions(EncodingName);

        switch (DataType)
        {
            case "bool":
                await WriteParquetNetColumnAsync(_parquetNetBoolSchema, _parquetNetBoolField, _boolValues)
                    .ConfigureAwait(false);
                break;
            case "int32":
                await WriteParquetNetColumnAsync(_parquetNetInt32Schema, _parquetNetInt32Field, _int32Values)
                    .ConfigureAwait(false);
                break;
            case "int64":
                await WriteParquetNetColumnAsync(_parquetNetInt64Schema, _parquetNetInt64Field, _int64Values)
                    .ConfigureAwait(false);
                break;
            case "float":
                await WriteParquetNetColumnAsync(_parquetNetFloatSchema, _parquetNetFloatField, _floatValues)
                    .ConfigureAwait(false);
                break;
            case "double":
                await WriteParquetNetColumnAsync(_parquetNetDoubleSchema, _parquetNetDoubleField, _doubleValues)
                    .ConfigureAwait(false);
                break;
            case "string":
                await WriteParquetNetColumnAsync(_parquetNetStringSchema, _parquetNetStringField, _stringValues)
                    .ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException($"Unknown type '{DataType}'.");
        }

        async Task WriteParquetNetColumnAsync<T>(ParquetSchema schema, DataField<T> field, T[] values)
        {
            await using var writer = await Parquet.ParquetWriter.CreateAsync(schema, _sharedStream, options, false)
                .ConfigureAwait(false);
            writer.CompressionMethod = CompressionMethod.None;
            using var rowGroupWriter = writer.CreateRowGroup();
            await rowGroupWriter.WriteColumnAsync(new DataColumn(field, values)).ConfigureAwait(false);
        }
    }

    void ResetSharedStream()
    {
        _sharedStream.Position = 0;
        _sharedStream.SetLength(0);
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

    static PlankSchema CreatePlankSchema(PlankColumn column, string encoding)
        => string.Equals(encoding, "dictionary", StringComparison.Ordinal)
            ? new PlankSchema([column])
            {
                PageStrategiesByColumnName = ImmutableDictionary<string, IPageStrategy>.Empty
                    .WithComparers(StringComparer.Ordinal)
                    .Add(column.Name, ForceDictionaryPageStrategy.Shared)
            }
            : new PlankSchema([column]);

    static ParquetSharp.Column GetParquetSharpColumn(string dataType)
        => dataType switch
        {
            "bool" => _parquetSharpBoolColumn,
            "int32" => _parquetSharpInt32Column,
            "int64" => _parquetSharpInt64Column,
            "float" => _parquetSharpFloatColumn,
            "double" => _parquetSharpDoubleColumn,
            "string" => _parquetSharpStringColumn,
            _ => throw new InvalidOperationException($"Unknown type '{dataType}'.")
        };

    static WriterProperties BuildParquetSharpWriterProperties(string encoding, string dataType, int rows)
    {
        var builder = new WriterPropertiesBuilder().Compression(Compression.Uncompressed);
        if (encoding == "dictionary")
            return builder.EnableDictionary().DictionaryPagesizeLimit(GetForcedDictionaryPageSizeLimitBytes(
                dataType, rows)).Build();

        var mapped = encoding switch
        {
            "plain" => Encoding.Plain,
            "delta_binary_packed" => Encoding.DeltaBinaryPacked,
            "delta_length_byte_array" => Encoding.DeltaLengthByteArray,
            "delta_byte_array" => Encoding.DeltaByteArray,
            "byte_stream_split" => Encoding.ByteStreamSplit,
            _ => Encoding.Plain
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
        => ParquetNetEncodingOptions.ForEncoding(encoding);

    static EncodingKind MapPlankEncoding(string encoding)
        => encoding switch
        {
            "plain" => EncodingKind.Plain,
            "dictionary" => EncodingKind.PlainDictionary,
            "delta_binary_packed" => EncodingKind.DeltaBinaryPacked,
            "delta_length_byte_array" => EncodingKind.DeltaLengthByteArray,
            "delta_byte_array" => EncodingKind.DeltaByteArray,
            "byte_stream_split" => EncodingKind.ByteStreamSplit,
            _ => EncodingKind.Plain
        };
}
