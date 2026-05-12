using BenchmarkDotNet.Running;
using Parquet;
using Parquet.Schema;
using Plank.Benchmarks;

if (args is ["--audit-encodings", ..])
{
    await EncodingActualEncodingAudit.RunAsync();
    return;
}

BenchmarkSwitcher.FromTypes([typeof(EncodingBenchmark),
    typeof(RowReaderBenchmark),
    typeof(DictionaryImplementationBenchmark)])
    .Run(args);

var field = new DataField<int>("value");
var schema = new Parquet.Schema.ParquetSchema(field);
var options = new ParquetOptions
{
    CompressionMethod = CompressionMethod.None,
    DictionaryEncodingThreshold = 1.0,
    DictionaryEncodingSampleSize = 0
};
options.ColumnEncodingHints.Add(field.Path.ToString(), EncodingHint.Dictionary);
var values = Enumerable.Range(0, 4_096).Select(static i => i % 16).ToArray();

await using var stream = File.Create("parquetnet-int32-dictionary-hint.parquet");
await using var writer = await Parquet.ParquetWriter.CreateAsync(schema, stream, options, false);
using var rowGroup = writer.CreateRowGroup();
await rowGroup.WriteAsync<int>(field, values.AsMemory(), null, null, default);
