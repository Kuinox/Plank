using Parquet;

namespace Plank.Benchmarks;

static class ParquetNetEncodingOptions
{
    public static ParquetOptions ForEncoding(string encoding)
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
}
