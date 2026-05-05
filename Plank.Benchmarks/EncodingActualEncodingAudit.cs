using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Parquet;
using Parquet.Schema;
using ParquetSharp;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.PageStrategy;
using ParquetEncoding = ParquetSharp.Encoding;
using PlankColumn = Plank.Schema.Column;
using PlankSchema = Plank.Schema.ParquetSchema;
using PlankWriter = Plank.Writing.ParquetWriter;

namespace Plank.Benchmarks;

static class EncodingActualEncodingAudit
{
    const int RowCount = 4_096;
    static readonly string[] Libraries = ["plank", "parquetsharp", "parquet.net"];
    static readonly string[] DataTypes = ["bool", "int32", "int64", "float", "double", "string"];
    static readonly string[] RequestedEncodings =
        ["plain", "dictionary", "delta_binary_packed", "delta_length_byte_array", "delta_byte_array", "byte_stream_split"];

    public static async Task RunAsync()
    {
        var rows = new List<AuditRow>(Libraries.Length * DataTypes.Length * RequestedEncodings.Length);
        foreach (var library in Libraries)
            foreach (var dataType in DataTypes)
                foreach (var requestedEncoding in RequestedEncodings)
                    rows.Add(await AuditCaseAsync(library, dataType, requestedEncoding).ConfigureAwait(false));

        var reportDir = Path.Combine(FindRepoRoot(), "benchmarks", "report");
        Directory.CreateDirectory(reportDir);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var csvPath = Path.Combine(reportDir, $"actual_encoding_audit_{timestamp}.csv");
        var markdownPath = Path.Combine(reportDir, $"actual_encoding_audit_{timestamp}.md");
        WriteCsv(csvPath, rows);
        WriteMarkdown(markdownPath, rows);
        Console.WriteLine(csvPath);
        Console.WriteLine(markdownPath);
    }

    static async Task<AuditRow> AuditCaseAsync(string library, string dataType, string requestedEncoding)
    {
        if (!EncodingSupportMatrix.IsParquetSupported(dataType, requestedEncoding))
            return new AuditRow(library, dataType, requestedEncoding, "not_parquet_supported", "", "",
                "The Parquet format does not support this encoding for this physical type.");

        var path = Path.Combine(Path.GetTempPath(),
            $"plank-actual-encoding-{library}-{dataType}-{requestedEncoding}-{Guid.NewGuid():N}.parquet");
        try
        {
            switch (library)
            {
                case "plank":
                    WritePlank(path, dataType, requestedEncoding);
                    break;
                case "parquetsharp":
                    WriteParquetSharp(path, dataType, requestedEncoding);
                    break;
                case "parquet.net":
                    await WriteParquetNetAsync(path, dataType, requestedEncoding).ConfigureAwait(false);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown library '{library}'.");
            }

            var rawEncodings = ReadColumnEncodings(path);
            var actualEncoding = ClassifyActualEncoding(rawEncodings);
            var status = actualEncoding == requestedEncoding ? "used_requested" : "gave_up";
            return new AuditRow(library, dataType, requestedEncoding, status, actualEncoding, rawEncodings, "");
        }
        catch (Exception ex)
        {
            return new AuditRow(library, dataType, requestedEncoding, "unsupported", "", "", ex.Message);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    static void WritePlank(string path, string dataType, string requestedEncoding)
    {
        var column = new PlankColumn(
            "value",
            MapPlankPhysicalType(dataType),
            new ColumnOptions(ParquetRepetition.Required, [MapPlankEncoding(requestedEncoding)]));
        var schema = string.Equals(requestedEncoding, "dictionary", StringComparison.Ordinal)
            ? new PlankSchema([column])
            {
                PageStrategiesByColumnName = ImmutableDictionary<string, IPageStrategy>.Empty
                    .WithComparers(StringComparer.Ordinal)
                    .Add(column.Name, ForceDictionaryPageStrategy.Shared)
            }
            : new PlankSchema([column]);

        using var stream = File.Create(path);
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None
        });
        var rowGroup = writer.StartRowGroup();
        switch (dataType)
        {
            case "bool":
                WritePlankColumn(writer, rowGroup, column, CreateBooleanValues());
                break;
            case "int32":
                WritePlankColumn(writer, rowGroup, column, CreateInt32Values());
                break;
            case "int64":
                WritePlankColumn(writer, rowGroup, column, CreateInt64Values());
                break;
            case "float":
                WritePlankColumn(writer, rowGroup, column, CreateFloatValues());
                break;
            case "double":
                WritePlankColumn(writer, rowGroup, column, CreateDoubleValues());
                break;
            case "string":
                WritePlankColumn(writer, rowGroup, column, CreateStringBytes());
                break;
            default:
                throw new InvalidOperationException($"Unknown data type '{dataType}'.");
        }
        writer.CloseFile();
    }

    static void WritePlankColumn<T>(PlankWriter writer, Plank.Writing.RowGroupWriter rowGroup, PlankColumn column, T[] values)
    {
        var serialized = writer.CreateSerializedColumn<T>(column);
        serialized.Serialize(values);
        rowGroup.Write(serialized);
    }

    static void WriteParquetSharp(string path, string dataType, string requestedEncoding)
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

        using var writerProperties = BuildParquetSharpWriterProperties(requestedEncoding, dataType);
        using var file = File.Create(path);
        using var writer = new ParquetFileWriter(file, [column], null, writerProperties, null, true);
        using var rowGroup = writer.AppendRowGroup();
        switch (dataType)
        {
            case "bool":
                using (var c = rowGroup.NextColumn().LogicalWriter<bool>())
                    c.WriteBatch(CreateBooleanValues());
                break;
            case "int32":
                using (var c = rowGroup.NextColumn().LogicalWriter<int>())
                    c.WriteBatch(CreateInt32Values());
                break;
            case "int64":
                using (var c = rowGroup.NextColumn().LogicalWriter<long>())
                    c.WriteBatch(CreateInt64Values());
                break;
            case "float":
                using (var c = rowGroup.NextColumn().LogicalWriter<float>())
                    c.WriteBatch(CreateFloatValues());
                break;
            case "double":
                using (var c = rowGroup.NextColumn().LogicalWriter<double>())
                    c.WriteBatch(CreateDoubleValues());
                break;
            case "string":
                using (var c = rowGroup.NextColumn().LogicalWriter<string>())
                    c.WriteBatch(CreateStringValues());
                break;
            default:
                throw new InvalidOperationException($"Unknown data type '{dataType}'.");
        }

        writer.Close();
    }

    static async Task WriteParquetNetAsync(string path, string dataType, string requestedEncoding)
    {
        var options = ParquetNetEncodingOptions.ForEncoding(requestedEncoding);
        await using var stream = File.Create(path);
        switch (dataType)
        {
            case "bool":
                await WriteParquetNetColumnAsync(stream, options, new DataField<bool>("value"), CreateBooleanValues())
                    .ConfigureAwait(false);
                break;
            case "int32":
                await WriteParquetNetColumnAsync(stream, options, new DataField<int>("value"), CreateInt32Values())
                    .ConfigureAwait(false);
                break;
            case "int64":
                await WriteParquetNetColumnAsync(stream, options, new DataField<long>("value"), CreateInt64Values())
                    .ConfigureAwait(false);
                break;
            case "float":
                await WriteParquetNetColumnAsync(stream, options, new DataField<float>("value"), CreateFloatValues())
                    .ConfigureAwait(false);
                break;
            case "double":
                await WriteParquetNetColumnAsync(stream, options, new DataField<double>("value"), CreateDoubleValues())
                    .ConfigureAwait(false);
                break;
            case "string":
                await WriteParquetNetStringColumnAsync(stream, options, new DataField<string>("value"), CreateStringValues())
                    .ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException($"Unknown data type '{dataType}'.");
        }
    }

    static async Task WriteParquetNetColumnAsync<T>(Stream stream, ParquetOptions options, DataField<T> field, T[] values)
        where T : struct
    {
        var schema = new Parquet.Schema.ParquetSchema(field);
        await using var writer = await Parquet.ParquetWriter.CreateAsync(schema, stream, options, false)
            .ConfigureAwait(false);
        using var rowGroup = writer.CreateRowGroup();
        await rowGroup.WriteAsync<T>(field, values.AsMemory(), null, null, default).ConfigureAwait(false);
    }

    static async Task WriteParquetNetStringColumnAsync(Stream stream, ParquetOptions options, DataField<string> field,
        string[] values)
    {
        var schema = new Parquet.Schema.ParquetSchema(field);
        await using var writer = await Parquet.ParquetWriter.CreateAsync(schema, stream, options, false)
            .ConfigureAwait(false);
        using var rowGroup = writer.CreateRowGroup();
        await rowGroup.WriteAsync(field, values).ConfigureAwait(false);
    }

    static string ReadColumnEncodings(string path)
    {
        using var reader = new ParquetFileReader(path);
        using var rowGroup = reader.RowGroup(0);
        using var column = rowGroup.MetaData.GetColumnChunkMetaData(0);
        return string.Join("|", column.Encodings.Select(static e => e.ToString()).Order(StringComparer.Ordinal));
    }

    static string ClassifyActualEncoding(string rawEncodings)
    {
        var encodings = rawEncodings.Split('|', StringSplitOptions.RemoveEmptyEntries);
        if (Contains(encodings, ParquetEncoding.RleDictionary) || Contains(encodings, ParquetEncoding.PlainDictionary))
            return "dictionary";
        if (Contains(encodings, ParquetEncoding.DeltaBinaryPacked))
            return "delta_binary_packed";
        if (Contains(encodings, ParquetEncoding.DeltaLengthByteArray))
            return "delta_length_byte_array";
        if (Contains(encodings, ParquetEncoding.DeltaByteArray))
            return "delta_byte_array";
        if (Contains(encodings, ParquetEncoding.ByteStreamSplit))
            return "byte_stream_split";
        if (Contains(encodings, ParquetEncoding.Plain))
            return "plain";
        return rawEncodings;
    }

    static bool Contains(string[] encodings, ParquetEncoding encoding)
        => encodings.Any(e => string.Equals(e, encoding.ToString(), StringComparison.Ordinal));

    static WriterProperties BuildParquetSharpWriterProperties(string requestedEncoding, string dataType)
    {
        var builder = new WriterPropertiesBuilder().Compression(Compression.Uncompressed);
        if (requestedEncoding == "dictionary")
            return builder.EnableDictionary().DictionaryPagesizeLimit(GetForcedDictionaryPageSizeLimitBytes(dataType)).Build();

        var mapped = requestedEncoding switch
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

    static long GetForcedDictionaryPageSizeLimitBytes(string dataType)
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
        return checked(RowCount * estimatedValueSizeBytes * 4L);
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

    static EncodingKind MapPlankEncoding(string requestedEncoding)
        => requestedEncoding switch
        {
            "plain" => EncodingKind.Plain,
            "dictionary" => EncodingKind.PlainDictionary,
            "delta_binary_packed" => EncodingKind.DeltaBinaryPacked,
            "delta_length_byte_array" => EncodingKind.DeltaLengthByteArray,
            "delta_byte_array" => EncodingKind.DeltaByteArray,
            "byte_stream_split" => EncodingKind.ByteStreamSplit,
            _ => EncodingKind.Plain
        };

    static bool[] CreateBooleanValues()
    {
        var values = new bool[RowCount];
        for (var i = 0; i < values.Length; i++)
            values[i] = (i & 1) == 0;
        return values;
    }

    static int[] CreateInt32Values()
    {
        var values = new int[RowCount];
        for (var i = 0; i < values.Length; i++)
            values[i] = i % 100_000;
        return values;
    }

    static long[] CreateInt64Values()
    {
        var values = new long[RowCount];
        for (var i = 0; i < values.Length; i++)
            values[i] = i * 37L;
        return values;
    }

    static float[] CreateFloatValues()
    {
        var values = new float[RowCount];
        for (var i = 0; i < values.Length; i++)
            values[i] = (i % 10_000) / 3f;
        return values;
    }

    static double[] CreateDoubleValues()
    {
        var values = new double[RowCount];
        for (var i = 0; i < values.Length; i++)
            values[i] = (i % 10_000) / 7d;
        return values;
    }

    static string[] CreateStringValues()
    {
        var values = new string[RowCount];
        for (var i = 0; i < values.Length; i++)
            values[i] = $"val-{i % 2048}";
        return values;
    }

    static byte[][] CreateStringBytes()
    {
        var strings = CreateStringValues();
        var values = new byte[strings.Length][];
        for (var i = 0; i < values.Length; i++)
            values[i] = System.Text.Encoding.UTF8.GetBytes(strings[i]);
        return values;
    }

    static void WriteCsv(string path, IReadOnlyList<AuditRow> rows)
    {
        using var writer = new StreamWriter(path);
        writer.WriteLine("library,data_type,requested_encoding,status,actual_encoding,raw_encodings,error");
        foreach (var row in rows)
        {
            writer.Write(row.Library);
            writer.Write(',');
            writer.Write(row.DataType);
            writer.Write(',');
            writer.Write(row.RequestedEncoding);
            writer.Write(',');
            writer.Write(row.Status);
            writer.Write(',');
            writer.Write(row.ActualEncoding);
            writer.Write(',');
            writer.Write(EscapeCsv(row.RawEncodings));
            writer.Write(',');
            writer.WriteLine(EscapeCsv(row.Error));
        }
    }

    static void WriteMarkdown(string path, IReadOnlyList<AuditRow> rows)
    {
        using var writer = new StreamWriter(path);
        writer.WriteLine("# Actual Encoding Audit");
        writer.WriteLine();
        writer.WriteLine("This report writes one small parquet file for each library/type/requested-encoding combination,");
        writer.WriteLine("then reads the column chunk metadata back with ParquetSharp.");
        writer.WriteLine();
        writer.WriteLine("Rows marked `not_parquet_supported` are skipped before writing because the Parquet format does not allow");
        writer.WriteLine("that encoding for the requested physical type.");
        writer.WriteLine();
        writer.WriteLine("## Summary");
        writer.WriteLine();
        writer.WriteLine("- `delta_binary_packed` is valid only for `int32` and `int64`; `bool`, `float`, `double`, and `string` are not benchmarked for it.");
        writer.WriteLine("- `delta_length_byte_array` and `delta_byte_array` are valid only for `string` in this matrix.");
        writer.WriteLine("- `byte_stream_split` is valid for `int32`, `int64`, `float`, and `double`; `bool` and `string` are not benchmarked for it.");
        writer.WriteLine();
        writer.WriteLine("## Parquet.Net Actual Usage");
        writer.WriteLine();
        writer.WriteLine("| Requested | Used | Gave up | Unsupported | Not Parquet-supported |");
        writer.WriteLine("| --- | ---: | ---: | ---: | ---: |");
        foreach (var encoding in RequestedEncodings)
        {
            var parquetNetRows = rows.Where(r => r.Library == "parquet.net" && r.RequestedEncoding == encoding).ToArray();
            writer.Write("| ");
            writer.Write(encoding);
            writer.Write(" | ");
            writer.Write(parquetNetRows.Count(r => r.Status == "used_requested"));
            writer.Write(" | ");
            writer.Write(parquetNetRows.Count(r => r.Status == "gave_up"));
            writer.Write(" | ");
            writer.Write(parquetNetRows.Count(r => r.Status == "unsupported"));
            writer.Write(" | ");
            writer.Write(parquetNetRows.Count(r => r.Status == "not_parquet_supported"));
            writer.WriteLine(" |");
        }
        writer.WriteLine();
        writer.WriteLine("| Library | Type | Requested | Status | Actual | Raw encodings | Error |");
        writer.WriteLine("| --- | --- | --- | --- | --- | --- | --- |");
        foreach (var row in rows)
        {
            writer.Write("| ");
            writer.Write(row.Library);
            writer.Write(" | ");
            writer.Write(row.DataType);
            writer.Write(" | ");
            writer.Write(row.RequestedEncoding);
            writer.Write(" | ");
            writer.Write(row.Status);
            writer.Write(" | ");
            writer.Write(row.ActualEncoding);
            writer.Write(" | ");
            writer.Write(row.RawEncodings);
            writer.Write(" | ");
            writer.Write(row.Error.Replace("|", "\\|"));
            writer.WriteLine(" |");
        }
    }

    static string EscapeCsv(string value)
    {
        if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\n') && !value.Contains('\r'))
            return value;
        return $"\"{value.Replace("\"", "\"\"")}\"";
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

    readonly record struct AuditRow(
        string Library,
        string DataType,
        string RequestedEncoding,
        string Status,
        string ActualEncoding,
        string RawEncodings,
        string Error);
}
