using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using ParquetSharp;
using Encoding = ParquetSharp.Encoding;

namespace Plank.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class ParquetSharpDictionaryReadMatrixBdnBenchmark
{
    const string ColumnName = "value";

    long[] _values = [];
    byte[] _plainParquet = [];
    byte[] _dictionaryParquet = [];

    [Params(1_000_000)]
    public int Rows { get; set; }

    [Params(1, 2, 5, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100)]
    public int UniquePercent { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        var uniqueCount = Math.Max(1, checked((int)Math.Ceiling(Rows * (UniquePercent / 100d))));
        _values = new long[Rows];
        var state = 0x6D2B79F5u;
        for (var i = 0; i < Rows; i++)
        {
            state = unchecked(state * 1664525u + 1013904223u);
            _values[i] = state % uniqueCount;
        }

        _plainParquet = WriteParquet(values: _values, useDictionary: false);
        _dictionaryParquet = WriteParquet(values: _values, useDictionary: true);

        var plainEncodings = GetEncodings(_plainParquet);
        var dictionaryEncodings = GetEncodings(_dictionaryParquet);
        if (!ContainsEncoding(plainEncodings, Encoding.Plain))
            throw new InvalidOperationException("Plain benchmark input is missing plain encoding.");
        if (ContainsEncoding(plainEncodings, Encoding.PlainDictionary)
            || ContainsEncoding(plainEncodings, Encoding.RleDictionary))
            throw new InvalidOperationException("Plain benchmark input unexpectedly contains dictionary encoding.");
        if (!ContainsEncoding(dictionaryEncodings, Encoding.PlainDictionary)
            && !ContainsEncoding(dictionaryEncodings, Encoding.RleDictionary))
            throw new InvalidOperationException("Dictionary benchmark input is missing dictionary encoding.");
    }

    [Benchmark(Baseline = true)]
    public int ReadPlainWithParquetSharp()
        => ReadAllRows(_plainParquet);

    [Benchmark]
    public int ReadDictionaryWithParquetSharp()
        => ReadAllRows(_dictionaryParquet);

    int ReadAllRows(byte[] parquetBuffer)
    {
        using var stream = new MemoryStream(parquetBuffer, writable: false);
        using var reader = new ParquetFileReader(stream);
        using var rowGroup = reader.RowGroup(0);
        using var valueReader = rowGroup.Column(0).LogicalReader<long>();
        return valueReader.ReadAll(Rows).Length;
    }

    static byte[] WriteParquet(long[] values, bool useDictionary)
    {
        using var stream = new MemoryStream(values.Length * sizeof(long) + 4096);
        using var writerProperties = BuildWriterProperties(useDictionary, values.Length);
        using var writer = new ParquetFileWriter(stream, [new Column<long>(ColumnName)], null, writerProperties, null,
            true);
        using var rowGroup = writer.AppendRowGroup();
        using (var valueWriter = rowGroup.NextColumn().LogicalWriter<long>())
            valueWriter.WriteBatch(values);
        writer.Close();
        return stream.ToArray();
    }

    static WriterProperties BuildWriterProperties(bool useDictionary, int rowCount)
    {
        var builder = new WriterPropertiesBuilder().Compression(Compression.Uncompressed);
        if (useDictionary)
        {
            var maxDictionarySize = checked((long)rowCount * sizeof(long) * 2L);
            return builder.EnableDictionary().DictionaryPagesizeLimit(maxDictionarySize).Build();
        }

        return builder.DisableDictionary().Encoding(Encoding.Plain).Build();
    }

    static Encoding[] GetEncodings(byte[] parquetBuffer)
    {
        using var stream = new MemoryStream(parquetBuffer, writable: false);
        using var reader = new ParquetFileReader(stream);
        using var rowGroup = reader.RowGroup(0);
        using var chunk = rowGroup.MetaData.GetColumnChunkMetaData(0);
        return chunk.Encodings;
    }

    static bool ContainsEncoding(Encoding[] encodings, Encoding target)
    {
        for (var i = 0; i < encodings.Length; i++)
            if (encodings[i] == target)
                return true;
        return false;
    }
}
