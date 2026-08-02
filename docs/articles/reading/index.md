# Reading

Plank has three read layers. Each layer serves a different purpose.

- [Row read layer](rows.md): strongly typed reader for application-shaped rows.
- [Logical read layer](logical.md): schema-bound reader that decodes column data into typed values.
- [Physical read layer](physical.md): low-level parquet parser for file metadata and encoded column data.

## Compression compatibility

The reader supports uncompressed, Snappy, Gzip, Brotli, Zstandard, and `LZ4_RAW` column chunks. It also reads the
deprecated Parquet `LZ4` codec as `CompressionKind.Lz4Legacy`, accepting Hadoop framing, standard LZ4 frames, and
raw blocks from older parquet-cpp files. `Lz4Legacy` is read-only; writers should use `CompressionKind.Lz4`, which
emits `LZ4_RAW` metadata.
