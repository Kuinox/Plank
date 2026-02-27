using System.Collections.Immutable;
using Parquet;
using Parquet.Data;
using Parquet.Schema;
using ParquetSharp;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.PageStrategy;
using CompressionMethod = Parquet.CompressionMethod;
using ParquetEncoding = ParquetSharp.Encoding;
using PlankColumn = Plank.Schema.Column;
using PlankSchema = Plank.Schema.ParquetSchema;
using PlankWriter = Plank.Writing.ParquetWriter;

namespace Plank.Tests.E2E;

internal sealed class BenchmarkEncodingCompatibilityTests
{
    static readonly string[] Libraries = ["plank", "parquetsharp", "parquet.net"];
    static readonly string[] DataTypes = ["bool", "int32", "int64", "float", "double", "string"];
    static readonly string[] Encodings =
        ["plain", "dictionary", "delta_binary_packed", "delta_length_byte_array", "delta_byte_array", "byte_stream_split"];

    const int RowCount = 4_096;

    [Test]
    public async Task GenerateCompatibilityMatrixFromBenchmarkWriterSettings()
    {
        var rows = new List<CompatibilityRow>(Libraries.Length * DataTypes.Length * Encodings.Length);
        foreach (var library in Libraries)
            foreach (var dataType in DataTypes)
                foreach (var encoding in Encodings)
                    rows.Add(await EvaluateCaseAsync(library, dataType, encoding).ConfigureAwait(false));

        var outputPath = WriteCsv(rows);
        if (!File.Exists(outputPath))
            throw new InvalidOperationException($"Compatibility CSV was not written: {outputPath}");
    }

    static async Task<CompatibilityRow> EvaluateCaseAsync(string library, string dataType, string encoding)
    {
        var path = NewPath($"{library}-{dataType}-{encoding}");
        try
        {
            switch (library)
            {
                case "plank":
                    WritePlank(path, dataType, encoding);
                    break;
                case "parquetsharp":
                    WriteParquetSharp(path, dataType, encoding);
                    break;
                case "parquet.net":
                    await WriteParquetNetAsync(path, dataType, encoding).ConfigureAwait(false);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown library '{library}'.");
            }

            var produced = ProducesRequestedEncoding(path, encoding);
            return produced
                ? new CompatibilityRow(library, dataType, encoding, true, "supported", "")
                : new CompatibilityRow(library, dataType, encoding, true, "unsupported",
                    $"Produced encoding does not match requested '{encoding}'.");
        }
        catch (Exception ex)
        {
            return new CompatibilityRow(library, dataType, encoding, true, "unsupported", ex.Message);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    static bool ProducesRequestedEncoding(string path, string requestedEncoding)
    {
        using var reader = new ParquetFileReader(path);
        using var rowGroup = reader.RowGroup(0);
        using var column = rowGroup.MetaData.GetColumnChunkMetaData(0);
        var encodings = column.Encodings;
        return requestedEncoding switch
        {
            "plain" => encodings.Contains(ParquetEncoding.Plain)
                       && !encodings.Contains(ParquetEncoding.RleDictionary)
                       && !encodings.Contains(ParquetEncoding.PlainDictionary),
            "dictionary" => encodings.Contains(ParquetEncoding.RleDictionary)
                            || encodings.Contains(ParquetEncoding.PlainDictionary),
            "delta_binary_packed" => encodings.Contains(ParquetEncoding.DeltaBinaryPacked),
            "delta_length_byte_array" => encodings.Contains(ParquetEncoding.DeltaLengthByteArray),
            "delta_byte_array" => encodings.Contains(ParquetEncoding.DeltaByteArray),
            "byte_stream_split" => encodings.Contains(ParquetEncoding.ByteStreamSplit),
            _ => false
        };
    }

    static void WritePlank(string path, string dataType, string encoding)
    {
        var column = new PlankColumn(
            "value",
            MapPlankPhysicalType(dataType),
            new ColumnOptions(ParquetRepetition.Required, [MapPlankEncoding(encoding)]));
        var schema = string.Equals(encoding, "dictionary", StringComparison.Ordinal)
            ? new PlankSchema([column])
            {
                PageStrategiesByColumnName = ImmutableDictionary<string, IPageStrategy>.Empty
                    .WithComparers(StringComparer.Ordinal)
                    .Add(column.Name, ForceDictionaryPageStrategy.Shared)
            }
            : new PlankSchema([column]);

        using var stream = File.Create(path);
        var writer = PlankWriter.Create(stream, schema, new ParquetWriterOptions { Compression = CompressionKind.None });
        var serialized = writer.CreateSerializedColumn();
        var rowGroup = writer.StartRowGroup();
        switch (dataType)
        {
            case "bool":
                serialized.Serialize(column, CreateBooleanValues(RowCount));
                break;
            case "int32":
                serialized.Serialize(column, CreateInt32Values(RowCount));
                break;
            case "int64":
                serialized.Serialize(column, CreateInt64Values(RowCount));
                break;
            case "float":
                serialized.Serialize(column, CreateFloatValues(RowCount));
                break;
            case "double":
                serialized.Serialize(column, CreateDoubleValues(RowCount));
                break;
            case "string":
                serialized.Serialize(column, CreateStringBytes(RowCount));
                break;
            default:
                throw new InvalidOperationException($"Unknown data type '{dataType}'.");
        }

        rowGroup.Write(serialized);
        writer.CloseFile();
    }

    static void WriteParquetSharp(string path, string dataType, string encoding)
    {
        var column = dataType switch
        {
            "bool" => (ParquetSharp.Column)new ParquetSharp.Column<bool>("value"),
            "int32" => new ParquetSharp.Column<int>("value"),
            "int64" => new ParquetSharp.Column<long>("value"),
            "float" => new ParquetSharp.Column<float>("value"),
            "double" => new ParquetSharp.Column<double>("value"),
            "string" => new ParquetSharp.Column<string>("value"),
            _ => throw new InvalidOperationException($"Unknown data type '{dataType}'.")
        };

        using var writerProperties = BuildParquetSharpWriterProperties(encoding, dataType, RowCount);
        using var file = File.Create(path);
        using var writer = new ParquetFileWriter(file, [column], null, writerProperties, null, true);
        using var rowGroup = writer.AppendRowGroup();
        switch (dataType)
        {
            case "bool":
                using (var c = rowGroup.NextColumn().LogicalWriter<bool>())
                    c.WriteBatch(CreateBooleanValues(RowCount));
                break;
            case "int32":
                using (var c = rowGroup.NextColumn().LogicalWriter<int>())
                    c.WriteBatch(CreateInt32Values(RowCount));
                break;
            case "int64":
                using (var c = rowGroup.NextColumn().LogicalWriter<long>())
                    c.WriteBatch(CreateInt64Values(RowCount));
                break;
            case "float":
                using (var c = rowGroup.NextColumn().LogicalWriter<float>())
                    c.WriteBatch(CreateFloatValues(RowCount));
                break;
            case "double":
                using (var c = rowGroup.NextColumn().LogicalWriter<double>())
                    c.WriteBatch(CreateDoubleValues(RowCount));
                break;
            case "string":
                using (var c = rowGroup.NextColumn().LogicalWriter<string>())
                    c.WriteBatch(CreateStringValues(RowCount));
                break;
            default:
                throw new InvalidOperationException($"Unknown data type '{dataType}'.");
        }

        writer.Close();
    }

    static async Task WriteParquetNetAsync(string path, string dataType, string encoding)
    {
        var options = BuildParquetNetOptions(encoding);
        await using var stream = File.Create(path);
        switch (dataType)
        {
            case "bool":
                await WriteParquetNetColumnAsync(new DataField<bool>("value"), CreateBooleanValues(RowCount))
                    .ConfigureAwait(false);
                break;
            case "int32":
                await WriteParquetNetColumnAsync(new DataField<int>("value"), CreateInt32Values(RowCount))
                    .ConfigureAwait(false);
                break;
            case "int64":
                await WriteParquetNetColumnAsync(new DataField<long>("value"), CreateInt64Values(RowCount))
                    .ConfigureAwait(false);
                break;
            case "float":
                await WriteParquetNetColumnAsync(new DataField<float>("value"), CreateFloatValues(RowCount))
                    .ConfigureAwait(false);
                break;
            case "double":
                await WriteParquetNetColumnAsync(new DataField<double>("value"), CreateDoubleValues(RowCount))
                    .ConfigureAwait(false);
                break;
            case "string":
                await WriteParquetNetColumnAsync(new DataField<string>("value"), CreateStringValues(RowCount))
                    .ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException($"Unknown data type '{dataType}'.");
        }

        async Task WriteParquetNetColumnAsync<T>(DataField<T> field, T[] values)
        {
            var schema = new Parquet.Schema.ParquetSchema(field);
            await using var writer = await Parquet.ParquetWriter.CreateAsync(schema, stream, options, false)
                .ConfigureAwait(false);
            writer.CompressionMethod = CompressionMethod.None;
            using var rowGroup = writer.CreateRowGroup();
            await rowGroup.WriteColumnAsync(new DataColumn(field, values)).ConfigureAwait(false);
        }
    }

    static WriterProperties BuildParquetSharpWriterProperties(string encoding, string dataType, int rows)
    {
        var builder = new WriterPropertiesBuilder().Compression(Compression.Uncompressed);
        if (encoding == "dictionary")
            return builder.EnableDictionary().DictionaryPagesizeLimit(GetForcedDictionaryPageSizeLimitBytes(dataType, rows))
                .Build();

        var mapped = encoding switch
        {
            "plain" => ParquetEncoding.Plain,
            "delta_binary_packed" => ParquetEncoding.DeltaBinaryPacked,
            "delta_length_byte_array" => ParquetEncoding.DeltaLengthByteArray,
            "delta_byte_array" => ParquetEncoding.DeltaByteArray,
            "byte_stream_split" => ParquetEncoding.ByteStreamSplit,
            _ => ParquetEncoding.Plain
        };
        return builder.DisableDictionary().Encoding(mapped).Build();
    }

    static ParquetOptions BuildParquetNetOptions(string encoding)
        => encoding switch
        {
            "plain" => new ParquetOptions
            {
                UseDictionaryEncoding = false,
                UseDeltaBinaryPackedEncoding = false
            },
            "dictionary" => new ParquetOptions
            {
                UseDictionaryEncoding = true,
                DictionaryEncodingThreshold = 1.0,
                UseDeltaBinaryPackedEncoding = false
            },
            "delta_binary_packed" => new ParquetOptions
            {
                UseDictionaryEncoding = false,
                UseDeltaBinaryPackedEncoding = true
            },
            _ => throw new NotSupportedException(
                $"Parquet.Net benchmark path does not support encoding '{encoding}'.")
        };

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

    static Plank.Schema.ParquetPhysicalType MapPlankPhysicalType(string dataType)
        => dataType switch
        {
            "bool" => Plank.Schema.ParquetPhysicalType.Boolean,
            "int32" => Plank.Schema.ParquetPhysicalType.Int32,
            "int64" => Plank.Schema.ParquetPhysicalType.Int64,
            "float" => Plank.Schema.ParquetPhysicalType.Float,
            "double" => Plank.Schema.ParquetPhysicalType.Double,
            "string" => Plank.Schema.ParquetPhysicalType.ByteArray,
            _ => throw new InvalidOperationException($"Unknown data type '{dataType}'.")
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
            _ => EncodingKind.Plain
        };

    static bool[] CreateBooleanValues(int count)
    {
        var values = new bool[count];
        for (var i = 0; i < count; i++)
            values[i] = (i & 1) == 0;
        return values;
    }

    static int[] CreateInt32Values(int count)
    {
        var values = new int[count];
        for (var i = 0; i < count; i++)
            values[i] = i % 100_000;
        return values;
    }

    static long[] CreateInt64Values(int count)
    {
        var values = new long[count];
        for (var i = 0; i < count; i++)
            values[i] = i * 37L;
        return values;
    }

    static float[] CreateFloatValues(int count)
    {
        var values = new float[count];
        for (var i = 0; i < count; i++)
            values[i] = (i % 10_000) / 3f;
        return values;
    }

    static double[] CreateDoubleValues(int count)
    {
        var values = new double[count];
        for (var i = 0; i < count; i++)
            values[i] = (i % 10_000) / 7d;
        return values;
    }

    static string[] CreateStringValues(int count)
    {
        var values = new string[count];
        for (var i = 0; i < count; i++)
            values[i] = $"val-{i % 2048}";
        return values;
    }

    static byte[][] CreateStringBytes(int count)
    {
        var strings = CreateStringValues(count);
        var values = new byte[count][];
        for (var i = 0; i < count; i++)
            values[i] = System.Text.Encoding.UTF8.GetBytes(strings[i]);
        return values;
    }

    static string NewPath(string suffix)
        => Path.Combine(Path.GetTempPath(), $"plank-bench-compat-{suffix}-{Guid.NewGuid():N}.parquet");

    static string WriteCsv(IReadOnlyList<CompatibilityRow> rows)
    {
        var root = FindRepoRoot();
        var reportDir = Path.Combine(root, "benchmarks", "report");
        Directory.CreateDirectory(reportDir);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var path = Path.Combine(reportDir, $"compat_matrix_run_{timestamp}.csv");
        using var writer = new StreamWriter(path);
        writer.WriteLine("library,data_type,encoding,expected_supported,status,error");
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            writer.Write(row.Library);
            writer.Write(',');
            writer.Write(row.DataType);
            writer.Write(',');
            writer.Write(row.Encoding);
            writer.Write(',');
            writer.Write(row.ExpectedSupported ? "true" : "false");
            writer.Write(',');
            writer.Write(row.Status);
            writer.Write(',');
            writer.WriteLine(EscapeCsv(row.Error));
        }

        return path;
    }

    static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Plank.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root containing Plank.sln.");
    }

    static string EscapeCsv(string value)
    {
        if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\n') && !value.Contains('\r'))
            return value;
        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    readonly record struct CompatibilityRow(
        string Library,
        string DataType,
        string Encoding,
        bool ExpectedSupported,
        string Status,
        string Error);
}
