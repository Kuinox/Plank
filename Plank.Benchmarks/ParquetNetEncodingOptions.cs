using Parquet;

namespace Plank.Benchmarks;

static class ParquetNetEncodingOptions
{
    public static ParquetOptions ForEncoding(string encoding)
        => encoding switch
        {
            "plain" => new ParquetOptions
            {
                CompressionMethod = CompressionMethod.None,
                DictionaryEncodingThreshold = 0,
                ColumnEncodingHints = { ["value"] = EncodingHint.Default }
            },
            "dictionary" => new ParquetOptions
            {
                CompressionMethod = CompressionMethod.None,
                DictionaryEncodingThreshold = 1.0,
                ColumnEncodingHints = { ["value"] = EncodingHint.Dictionary }
            },
            "delta_binary_packed" => new ParquetOptions
            {
                CompressionMethod = CompressionMethod.None,
                DictionaryEncodingThreshold = 0,
                ColumnEncodingHints = { ["value"] = EncodingHint.DeltaBinaryPacked }
            },
            _ => throw new NotSupportedException(
                $"Parquet.Net benchmark path does not support encoding '{encoding}'.")
        };
}
