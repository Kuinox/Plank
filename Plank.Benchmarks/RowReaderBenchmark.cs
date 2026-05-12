using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Parquet;
using Parquet.Schema;
using Plank.Reading;
using Plank.Writing;
using ParquetSchema = Parquet.Schema.ParquetSchema;

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

    [GlobalSetup]
    public void GlobalSetup()
    {
        _fileBytes = CreateFileBytes(Rows);
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

        foreach (var row in _plankReader)
        {
            checksum += row.Id;
            checksum += row.Timestamp;
            if (Projection == "all")
            {
                checksum += (long)row.Value;
                checksum += row.Category;
            }
        }

        return checksum;
    }

    [Benchmark]
    public long ReadPlankGeneratedRowReaderMoveNext()
    {
        _plankReader.Reset(_fileSource, GetPlankProjection());
        var checksum = 0L;

        while (_plankReader.MoveNext())
        {
            var row = _plankReader.Current;
            checksum += row.Id;
            checksum += row.Timestamp;
            if (Projection == "all")
            {
                checksum += (long)row.Value;
                checksum += row.Category;
            }
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

    static byte[] CreateFileBytes(int rows)
    {
        using var stream = new MemoryStream();
        var writer = RowReaderBenchmarkSchema.Schema.CreateWriter(stream, new ParquetWriterOptions
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

        var idColumn = rowGroup.CreateSerializedColumn<int>(RowReaderBenchmarkSchema.Schema.Columns[0]);
        idColumn.Serialize(ids);
        rowGroup.Write(idColumn);

        var timestampColumn = rowGroup.CreateSerializedColumn<long>(RowReaderBenchmarkSchema.Schema.Columns[1]);
        timestampColumn.Serialize(timestamps);
        rowGroup.Write(timestampColumn);

        var valueColumn = rowGroup.CreateSerializedColumn<double>(RowReaderBenchmarkSchema.Schema.Columns[2]);
        valueColumn.Serialize(values);
        rowGroup.Write(valueColumn);

        var categoryColumn = rowGroup.CreateSerializedColumn<int>(RowReaderBenchmarkSchema.Schema.Columns[3]);
        categoryColumn.Serialize(categories);
        rowGroup.Write(categoryColumn);

        writer.CloseFile();
        return stream.ToArray();
    }
}
