using BenchmarkDotNet.Attributes;
using System.Collections.Immutable;
using Parquet;
using Parquet.Schema;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.PageStrategy;
using ParquetSchema = Parquet.Schema.ParquetSchema;
using PlankColumn = Plank.Schema.Column;
using PlankSchema = Plank.Schema.ParquetSchema;

namespace Plank.Benchmarks;

[MemoryDiagnoser]
[SimpleJob]
public class EncodingBenchmark
{
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
    SerializedColumn<bool>? _plankBoolColumn;
    SerializedColumn<int>? _plankInt32Column;
    SerializedColumn<long>? _plankInt64Column;
    SerializedColumn<float>? _plankFloatColumn;
    SerializedColumn<double>? _plankDoubleColumn;
    SerializedColumn<byte[]>? _plankStringColumn;

    bool[] _boolValues = [];
    int[] _int32Values = [];
    long[] _int64Values = [];
    float[] _floatValues = [];
    double[] _doubleValues = [];
    string[] _stringValues = [];
    byte[][] _stringByteValues = [];

    [Params(1_000_000)]
    public int Rows { get; set; }

    public static IEnumerable<EncodingBenchmarkCase> Cases
        => EncodingSupportMatrix.GetSelectedCases();

    [ParamsSource(nameof(Cases))]
    public EncodingBenchmarkCase Case { get; set; } = new("bool", "plain");

    string DataType
        => Case.DataType;

    string EncodingName
        => Case.EncodingName;

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
        InitializePlankSerializedColumn();
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
                var boolSerialized = _plankBoolColumn!;
                boolSerialized.Serialize(_boolValues);
                rowGroup.Write(boolSerialized);
                break;
            case "int32":
                var int32Serialized = _plankInt32Column!;
                int32Serialized.Serialize(_int32Values);
                rowGroup.Write(int32Serialized);
                break;
            case "int64":
                var int64Serialized = _plankInt64Column!;
                int64Serialized.Serialize(_int64Values);
                rowGroup.Write(int64Serialized);
                break;
            case "float":
                var floatSerialized = _plankFloatColumn!;
                floatSerialized.Serialize(_floatValues);
                rowGroup.Write(floatSerialized);
                break;
            case "double":
                var doubleSerialized = _plankDoubleColumn!;
                doubleSerialized.Serialize(_doubleValues);
                rowGroup.Write(doubleSerialized);
                break;
            case "string":
                var stringSerialized = _plankStringColumn!;
                stringSerialized.Serialize(_stringByteValues);
                rowGroup.Write(stringSerialized);
                break;
            default:
                throw new InvalidOperationException($"Unknown type '{DataType}'.");
        }
    }

    [Benchmark]
    public async Task WriteParquetNetAsync()
    {
        if (!EncodingSupportMatrix.IsParquetNetSupported(DataType, EncodingName))
            throw new NotSupportedException(
                $"Parquet.Net does not produce requested encoding '{EncodingName}' for data type '{DataType}'.");

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
                await WriteParquetNetStringColumnAsync(_parquetNetStringSchema, _parquetNetStringField, _stringValues)
                    .ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException($"Unknown type '{DataType}'.");
        }

        async Task WriteParquetNetColumnAsync<T>(ParquetSchema schema, DataField<T> field, T[] values)
            where T : struct
        {
            await using var writer = await Parquet.ParquetWriter.CreateAsync(schema, _sharedStream, options, false)
                .ConfigureAwait(false);
            using var rowGroupWriter = writer.CreateRowGroup();
            await rowGroupWriter.WriteAsync<T>(field, values.AsMemory(), null, null, default).ConfigureAwait(false);
        }

        async Task WriteParquetNetStringColumnAsync(ParquetSchema schema, DataField<string> field, string[] values)
        {
            await using var writer = await Parquet.ParquetWriter.CreateAsync(schema, _sharedStream, options, false)
                .ConfigureAwait(false);
            using var rowGroupWriter = writer.CreateRowGroup();
            await rowGroupWriter.WriteAsync(field, values).ConfigureAwait(false);
        }
    }

    void ResetSharedStream()
    {
        _sharedStream.Position = 0;
        _sharedStream.SetLength(0);
    }

    void InitializePlankSerializedColumn()
    {
        switch (DataType)
        {
            case "bool":
                _plankBoolColumn = _plankWriter.CreateSerializedColumn<bool>(_plankColumn);
                break;
            case "int32":
                _plankInt32Column = _plankWriter.CreateSerializedColumn<int>(_plankColumn);
                break;
            case "int64":
                _plankInt64Column = _plankWriter.CreateSerializedColumn<long>(_plankColumn);
                break;
            case "float":
                _plankFloatColumn = _plankWriter.CreateSerializedColumn<float>(_plankColumn);
                break;
            case "double":
                _plankDoubleColumn = _plankWriter.CreateSerializedColumn<double>(_plankColumn);
                break;
            case "string":
                _plankStringColumn = _plankWriter.CreateSerializedColumn<byte[]>(_plankColumn);
                break;
            default:
                throw new InvalidOperationException($"Unknown type '{DataType}'.");
        }
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

    static ParquetOptions BuildParquetNetOptions(string encoding)
        => ParquetNetEncodingOptions.ForEncoding(encoding);

    static EncodingKind MapPlankEncoding(string encoding)
        => encoding switch
        {
            "plain" => EncodingKind.Plain,
            "dictionary" => EncodingKind.RleDictionary,
            "delta_binary_packed" => EncodingKind.DeltaBinaryPacked,
            "delta_length_byte_array" => EncodingKind.DeltaLengthByteArray,
            "delta_byte_array" => EncodingKind.DeltaByteArray,
            "byte_stream_split" => EncodingKind.ByteStreamSplit,
            _ => EncodingKind.Plain
        };
}
