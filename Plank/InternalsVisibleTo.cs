using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Plank.Tests")]
[assembly: InternalsVisibleTo("Plank.Benchmarks")]

// The decompressors are internal, and reaching them through a Parquet file means
// the writer has to be able to produce that codec. It cannot for Lz4Legacy, which
// left a 326-line hand-rolled LZ4 frame parser with no input that reaches it.
// Fuzzing it directly needs no envelope at all.
[assembly: InternalsVisibleTo("Plank.Fuzzing")]
