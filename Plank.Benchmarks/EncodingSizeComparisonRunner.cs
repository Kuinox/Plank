using Parquet;
using Parquet.Data;
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

public static class EncodingSizeComparisonRunner
{
    static readonly IPageStrategy ForceDictionaryPageStrategyInstance = new ForceDictionaryPageStrategy();

    public static async Task<int> RunAsync(string[] args)
    {
        var rows = ReadRows(args);
        var data = CreateData(rows);
        var results = new List<ResultRow>(capacity: 256);

        foreach (var scenario in SingleColumnScenarioCatalog.Plank)
            results.Add(await WritePlankAsync(rows, scenario, data).ConfigureAwait(false));
        foreach (var scenario in SingleColumnScenarioCatalog.ParquetSharp)
            results.Add(WriteParquetSharp(rows, scenario, data));
        foreach (var scenario in SingleColumnScenarioCatalog.ParquetNet)
            results.Add(await WriteParquetNetAsync(rows, scenario, data).ConfigureAwait(false));

        PrintResults(results);
        return 0;
    }

    static int ReadRows(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!string.Equals(arg, "--rows", StringComparison.OrdinalIgnoreCase))
                continue;
            if (i + 1 >= args.Length)
                throw new ArgumentException("Missing value for --rows.");
            if (!int.TryParse(args[i + 1], out var rows) || rows <= 0)
                throw new ArgumentException($"Invalid --rows value '{args[i + 1]}'.");
            return rows;
        }

        return 1_000_000;
    }

    static BenchmarkData CreateData(int rows)
    {
        var boolValues = new bool[rows];
        var int32Values = new int[rows];
        var int64Values = new long[rows];
        var floatValues = new float[rows];
        var doubleValues = new double[rows];
        var stringValues = new string[rows];
        var stringByteValues = new byte[rows][];

        for (var i = 0; i < rows; i++)
        {
            boolValues[i] = (i & 1) == 0;
            int32Values[i] = i % 100_000;
            int64Values[i] = i * 37L;
            floatValues[i] = (i % 10_000) / 3f;
            doubleValues[i] = (i % 10_000) / 7d;
            stringValues[i] = $"val-{i % 2048}";
            stringByteValues[i] = System.Text.Encoding.UTF8.GetBytes(stringValues[i]);
        }

        return new BenchmarkData(boolValues, int32Values, int64Values, floatValues, doubleValues, stringValues,
            stringByteValues);
    }

    static async Task<ResultRow> WritePlankAsync(int rows, SingleColumnScenario scenario, BenchmarkData data)
    {
        var column = new PlankColumn(
            "value",
            MapPlankPhysicalType(scenario.DataType),
            new ColumnOptions(ParquetRepetition.Required, [MapPlankEncoding(scenario.EncodingName)]));
        var schema = new PlankSchema([column]);

        await using var stream = new MemoryStream(capacity: rows * 16);
        var writer = Plank.Writing.ParquetWriter.Create(stream, schema, new ParquetWriterOptions
        {
            Compression = CompressionKind.None
        });
        var rowGroup = writer.StartRowGroup();
        var serialized = writer.CreateSerializedColumn();
        var forceDictionary = string.Equals(scenario.EncodingName, "dictionary", StringComparison.Ordinal);

        void SerializeValues<T>(ReadOnlySpan<T> values)
            where T : notnull
        {
            if (forceDictionary)
                serialized.Serialize(column, values, ForceDictionaryPageStrategyInstance);
            else
                serialized.Serialize(column, values);
        }

        switch (scenario.DataType)
        {
            case "bool":
                SerializeValues(data.BoolValues);
                break;
            case "int32":
                SerializeValues(data.Int32Values);
                break;
            case "int64":
                SerializeValues(data.Int64Values);
                break;
            case "float":
                SerializeValues(data.FloatValues);
                break;
            case "double":
                SerializeValues(data.DoubleValues);
                break;
            case "string":
                SerializeValues(data.StringByteValues);
                break;
            default:
                throw new InvalidOperationException($"Unknown type '{scenario.DataType}'.");
        }

        rowGroup.Write(serialized);
        writer.CloseFile();
        var buffer = stream.ToArray();
        ValidateDictionaryIfRequired("plank", scenario, buffer);
        var snapshot = ReadColumnBytes(buffer);
        return new ResultRow("plank", scenario, rows, snapshot);
    }

    static ResultRow WriteParquetSharp(int rows, SingleColumnScenario scenario, BenchmarkData data)
    {
        using var stream = new MemoryStream(capacity: rows * 16);
        using var writerProperties = BuildParquetSharpWriterProperties(scenario, rows);
        using var writer = new ParquetFileWriter(stream, [BuildParquetSharpColumn(scenario.DataType)], null,
            writerProperties, null, true);
        using var rowGroupWriter = writer.AppendRowGroup();

        switch (scenario.DataType)
        {
            case "bool":
                using (var col = rowGroupWriter.NextColumn().LogicalWriter<bool>())
                    col.WriteBatch(data.BoolValues);
                break;
            case "int32":
                using (var col = rowGroupWriter.NextColumn().LogicalWriter<int>())
                    col.WriteBatch(data.Int32Values);
                break;
            case "int64":
                using (var col = rowGroupWriter.NextColumn().LogicalWriter<long>())
                    col.WriteBatch(data.Int64Values);
                break;
            case "float":
                using (var col = rowGroupWriter.NextColumn().LogicalWriter<float>())
                    col.WriteBatch(data.FloatValues);
                break;
            case "double":
                using (var col = rowGroupWriter.NextColumn().LogicalWriter<double>())
                    col.WriteBatch(data.DoubleValues);
                break;
            case "string":
                using (var col = rowGroupWriter.NextColumn().LogicalWriter<string>())
                    col.WriteBatch(data.StringValues);
                break;
            default:
                throw new InvalidOperationException($"Unknown type '{scenario.DataType}'.");
        }

        writer.Close();
        var buffer = stream.ToArray();
        ValidateDictionaryIfRequired("parquetsharp", scenario, buffer);
        var snapshot = ReadColumnBytes(buffer);
        return new ResultRow("parquetsharp", scenario, rows, snapshot);
    }

    static async Task<ResultRow> WriteParquetNetAsync(int rows, SingleColumnScenario scenario, BenchmarkData data)
    {
        var options = BuildParquetNetOptions(scenario.EncodingName);
        await using var stream = new MemoryStream(capacity: rows * 16);

        switch (scenario.DataType)
        {
            case "bool":
                await WriteParquetNetColumnAsync(new DataField<bool>("value"), data.BoolValues).ConfigureAwait(false);
                break;
            case "int32":
                await WriteParquetNetColumnAsync(new DataField<int>("value"), data.Int32Values).ConfigureAwait(false);
                break;
            case "int64":
                await WriteParquetNetColumnAsync(new DataField<long>("value"), data.Int64Values).ConfigureAwait(false);
                break;
            case "float":
                await WriteParquetNetColumnAsync(new DataField<float>("value"), data.FloatValues).ConfigureAwait(false);
                break;
            case "double":
                await WriteParquetNetColumnAsync(new DataField<double>("value"), data.DoubleValues).ConfigureAwait(false);
                break;
            case "string":
                await WriteParquetNetColumnAsync(new DataField<string>("value"), data.StringValues).ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException($"Unknown type '{scenario.DataType}'.");
        }

        await stream.FlushAsync().ConfigureAwait(false);
        var buffer = stream.ToArray();
        ValidateDictionaryIfRequired("parquet.net", scenario, buffer);
        var snapshot = ReadColumnBytes(buffer);
        return new ResultRow("parquet.net", scenario, rows, snapshot);

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

    static void ValidateDictionaryIfRequired(string library, SingleColumnScenario scenario, byte[] parquetData)
    {
        if (!string.Equals(scenario.EncodingName, "dictionary", StringComparison.Ordinal))
            return;

        var encodings = ReadColumnEncodings(parquetData);
        if (ContainsDictionaryEncoding(encodings))
            return;

        throw new InvalidOperationException(
            $"Dictionary encoding was requested for scenario '{scenario}' ({library}), but output metadata did not contain dictionary encoding.");
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

    static void PrintResults(List<ResultRow> results)
    {
        Console.WriteLine("library,data_type,encoding,rows,column_compressed_bytes,column_uncompressed_bytes,file_bytes");
        foreach (var row in results.OrderBy(static row => row.Library, StringComparer.Ordinal)
                     .ThenBy(static row => row.Scenario.DataType, StringComparer.Ordinal)
                     .ThenBy(static row => row.Scenario.EncodingName, StringComparer.Ordinal))
        {
            Console.WriteLine(
                $"{row.Library},{row.Scenario.DataType},{row.Scenario.EncodingName},{row.Rows},{row.Snapshot.ColumnCompressedBytes},{row.Snapshot.ColumnUncompressedBytes},{row.Snapshot.FileBytes}");
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

    readonly record struct BenchmarkData(
        bool[] BoolValues,
        int[] Int32Values,
        long[] Int64Values,
        float[] FloatValues,
        double[] DoubleValues,
        string[] StringValues,
        byte[][] StringByteValues);

    readonly record struct ResultRow(string Library, SingleColumnScenario Scenario, int Rows,
        EncodingBenchmarkSizeSnapshot Snapshot);

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
