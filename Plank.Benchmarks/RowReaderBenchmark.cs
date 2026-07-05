using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using System.Collections.Immutable;
using Parquet;
using Parquet.Schema;
using Plank.Reading;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.PageStrategy;
using PlankColumn = Plank.Schema.Column;
using PlankSchema = Plank.Schema.ParquetSchema;

namespace Plank.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class RowReaderBenchmark
{
    static readonly DataField<int> _parquetNetIdField = new("id");
    static readonly DataField<long> _parquetNetTimestampField = new("timestamp");
    static readonly DataField<double> _parquetNetValueField = new("value");
    static readonly DataField<int> _parquetNetCategoryField = new("category");

    byte[] _fileBytes = [];
    MemoryReadSource _fileSource = null!;
    RowReaderBenchmarkSchema.RowReader _plankReader = null!;

    [Params(100_000)]
    public int Rows { get; set; }

    [Params("all", "projected")]
    public string Projection { get; set; } = "all";

    [Params("plain", "dictionary", "byte_stream_split", "delta_binary_packed", "delta_byte_array", "delta_length_byte_array")]
    public string Encoding { get; set; } = "plain";

    [GlobalSetup]
    public void GlobalSetup()
    {
        _fileBytes = CreateFileBytes(Rows, Encoding);
        _fileSource = new MemoryReadSource(_fileBytes);
        _plankReader = RowReaderBenchmarkSchema.CreateRowReader(_fileSource);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
        => _plankReader.Dispose();

    [Benchmark(Baseline = true)]
    public long ReadPlankGeneratedRowReader()
    {
        _plankReader.Reset(_fileSource, GetPlankProjection());
        var checksum = 0L;

        if (Projection == "all")
        {
            foreach (var row in _plankReader)
            {
                checksum += row.Id;
                checksum += row.Timestamp;
                checksum += (long)row.Value;
                checksum += row.Category;
            }

            return checksum;
        }

        foreach (var row in _plankReader)
        {
            checksum += row.Id;
            checksum += row.Timestamp;
        }

        return checksum;
    }

    [Benchmark]
    public async Task<long> ReadParquetNetAsync()
    {
        if (Encoding is "delta_binary_packed" or "delta_byte_array" or "delta_length_byte_array")
            throw new NotSupportedException($"ParquetNet does not produce '{Encoding}' encoding.");
        using var stream = new MemoryStream(_fileBytes, writable: false);
        await using var reader = await Parquet.ParquetReader.CreateAsync(stream).ConfigureAwait(false);
        var checksum = 0L;

        for (var rowGroupIndex = 0; rowGroupIndex < reader.RowGroupCount; rowGroupIndex++)
        {
            using var rowGroup = reader.OpenRowGroupReader(rowGroupIndex);
            var rowCount = checked((int)rowGroup.RowCount);
            var ids = await ReadColumnAsync(rowGroup, _parquetNetIdField, rowCount).ConfigureAwait(false);
            var timestamps = await ReadColumnAsync(rowGroup, _parquetNetTimestampField, rowCount).ConfigureAwait(false);

            if (Projection == "all")
            {
                var values = await ReadColumnAsync(rowGroup, _parquetNetValueField, rowCount).ConfigureAwait(false);
                var categories = await ReadColumnAsync(rowGroup, _parquetNetCategoryField, rowCount).ConfigureAwait(false);
                for (var i = 0; i < ids.Length; i++)
                {
                    checksum += ids[i];
                    checksum += timestamps[i];
                    checksum += (long)values[i];
                    checksum += categories[i];
                }
            }
            else
            {
                for (var i = 0; i < ids.Length; i++)
                {
                    checksum += ids[i];
                    checksum += timestamps[i];
                }
            }
        }

        return checksum;
    }

    RowReaderBenchmarkSchema.Projection GetPlankProjection()
        => Projection == "all"
            ? RowReaderBenchmarkSchema.Projection.All
            : RowReaderBenchmarkSchema.Projection.Id | RowReaderBenchmarkSchema.Projection.Timestamp;

    static async Task<T[]> ReadColumnAsync<T>(ParquetRowGroupReader rowGroup, DataField<T> field, int rowCount)
        where T : struct
    {
        var values = new T[rowCount];
        await rowGroup.ReadAsync<T>(field, values, null, default).ConfigureAwait(false);
        return values;
    }

    static byte[] CreateFileBytes(int rows, string encoding)
    {
        using var stream = new MemoryStream();
        var schema = CreateWriteSchema(encoding);
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None
        });
        var rowGroup = writer.StartRowGroup();

        var ids = new int[rows];
        var timestamps = new long[rows];
        var values = new double[rows];
        var categories = new int[rows];
        var labels = new byte[rows][];
        for (var i = 0; i < rows; i++)
        {
            ids[i] = i;
            timestamps[i] = 1_700_000_000_000L + i;
            values[i] = i * 0.25d;
            categories[i] = i % 2048;
            labels[i] = System.Text.Encoding.UTF8.GetBytes($"label-{i % 1000}");
        }

        var idColumn = rowGroup.CreateSerializedColumn<int>(schema.Columns[0]);
        idColumn.Serialize(ids);
        rowGroup.Write(idColumn);

        var timestampColumn = rowGroup.CreateSerializedColumn<long>(schema.Columns[1]);
        timestampColumn.Serialize(timestamps);
        rowGroup.Write(timestampColumn);

        var valueColumn = rowGroup.CreateSerializedColumn<double>(schema.Columns[2]);
        valueColumn.Serialize(values);
        rowGroup.Write(valueColumn);

        var categoryColumn = rowGroup.CreateSerializedColumn<int>(schema.Columns[3]);
        categoryColumn.Serialize(categories);
        rowGroup.Write(categoryColumn);

        var labelColumn = rowGroup.CreateSerializedColumn<byte[]>(schema.Columns[4]);
        labelColumn.Serialize(labels);
        rowGroup.Write(labelColumn);

        writer.CloseFile();
        return stream.ToArray();
    }

    static PlankSchema CreateWriteSchema(string encoding)
    {
        // delta_byte_array / delta_length_byte_array apply only to ByteArray columns;
        // delta_binary_packed applies only to integer columns (not double).
        // For mixed cases we use plain on columns where the target encoding doesn't apply.
        var isByteArrayEncoding = encoding is "delta_byte_array" or "delta_length_byte_array";
        var isIntEncoding = encoding is "delta_binary_packed";

        var numericEncoding = isByteArrayEncoding ? EncodingKind.Plain : MapNumericEncoding(encoding);
        var byteArrayEncoding = isIntEncoding ? EncodingKind.Plain : MapByteArrayEncoding(encoding);

        var numericOptions = new ColumnOptions(ParquetRepetition.Required, ImmutableArray.Create(numericEncoding));
        var byteArrayOptions = new ColumnOptions(ParquetRepetition.Required, ImmutableArray.Create(byteArrayEncoding));

        var columns = ImmutableArray.Create(
            new PlankColumn("id", ParquetPhysicalType.Int32, numericOptions),
            new PlankColumn("timestamp", ParquetPhysicalType.Int64, numericOptions),
            new PlankColumn("value", ParquetPhysicalType.Double,
                new ColumnOptions(ParquetRepetition.Required, ImmutableArray.Create(
                    isIntEncoding ? EncodingKind.Plain : numericEncoding))),
            new PlankColumn("category", ParquetPhysicalType.Int32, numericOptions),
            new PlankColumn("label", ParquetPhysicalType.ByteArray, byteArrayOptions));

        if (encoding != "dictionary")
            return new PlankSchema(columns);

        return new PlankSchema(columns)
        {
            PageStrategiesByColumnName = ImmutableDictionary<string, IPageStrategy>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add("id", ForceDictionaryPageStrategy.Shared)
                .Add("timestamp", ForceDictionaryPageStrategy.Shared)
                .Add("value", ForceDictionaryPageStrategy.Shared)
                .Add("category", ForceDictionaryPageStrategy.Shared)
                .Add("label", ForceDictionaryPageStrategy.Shared)
        };
    }

    static EncodingKind MapNumericEncoding(string encoding)
        => encoding switch
        {
            "plain" => EncodingKind.Plain,
            "dictionary" => EncodingKind.RleDictionary,
            "byte_stream_split" => EncodingKind.ByteStreamSplit,
            "delta_binary_packed" => EncodingKind.DeltaBinaryPacked,
            _ => EncodingKind.Plain
        };

    static EncodingKind MapByteArrayEncoding(string encoding)
        => encoding switch
        {
            "plain" => EncodingKind.Plain,
            "dictionary" => EncodingKind.RleDictionary,
            "delta_byte_array" => EncodingKind.DeltaByteArray,
            "delta_length_byte_array" => EncodingKind.DeltaLengthByteArray,
            _ => EncodingKind.Plain
        };
}
