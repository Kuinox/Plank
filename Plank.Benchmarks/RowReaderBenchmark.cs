using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using System.Collections.Immutable;
using Parquet;
using Parquet.Schema;
using Plank.Reading;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.PageStrategy;
using ParquetSchema = Parquet.Schema.ParquetSchema;
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
    static readonly ParquetSchema _parquetNetSchema = new(_parquetNetIdField, _parquetNetTimestampField,
        _parquetNetValueField, _parquetNetCategoryField);

    byte[] _fileBytes = [];
    MemoryReadSource _fileSource = null!;
    RowReaderBenchmarkSchema.RowReader _plankReader = null!;

    [Params(100_000)]
    public int Rows { get; set; }

    [Params("all", "projected")]
    public string Projection { get; set; } = "all";

    [Params("plain", "dictionary", "byte_stream_split")]
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
        for (var i = 0; i < rows; i++)
        {
            ids[i] = i;
            timestamps[i] = 1_700_000_000_000L + i;
            values[i] = i * 0.25d;
            categories[i] = i % 2048;
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

        writer.CloseFile();
        return stream.ToArray();
    }

    static PlankSchema CreateWriteSchema(string encoding)
    {
        var encodingKind = MapPlankEncoding(encoding);
        var options = new ColumnOptions(ParquetRepetition.Required, ImmutableArray.Create(encodingKind));
        var columns = ImmutableArray.Create(
            new PlankColumn("id", ParquetPhysicalType.Int32, options),
            new PlankColumn("timestamp", ParquetPhysicalType.Int64, options),
            new PlankColumn("value", ParquetPhysicalType.Double, options),
            new PlankColumn("category", ParquetPhysicalType.Int32, options));
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
        };
    }

    static EncodingKind MapPlankEncoding(string encoding)
        => encoding switch
        {
            "plain" => EncodingKind.Plain,
            "dictionary" => EncodingKind.RleDictionary,
            "byte_stream_split" => EncodingKind.ByteStreamSplit,
            _ => throw new InvalidOperationException($"Encoding '{encoding}' is not supported by {nameof(RowReaderBenchmark)}.")
        };
}
