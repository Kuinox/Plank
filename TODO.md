# TODO

## Deferred While Implementing Thrift Metadata

- `Plank/Writing/Encoding.cs`: force dictionary mode to disabled for now; re-enable only after dictionary page headers/metadata are emitted with real Thrift structures.
- `Plank.Tests/E2E/WriterInteropE2ETests.cs`: keep Parquet.Net + ParquetSharp interop tests as the acceptance gate while footer/page format work is in progress.

## Existing Backlog

- `Plank/Writing/SerializedColumn.cs`: remove the temporary `static readonly IPageStrategy DefaultPageStrategy = new DefaultStrategy();` and route strategy selection/injection through the intended writer/page pipeline.
- `Plank/Schema`: support nested Parquet column paths as name segments (not only a single flat `string Name`) so leaf identity is unambiguous.
- `Plank/Writing`: support repeated/list columns through schema paths + list/map annotated groups and compute `maxDefinitionLevel` / `maxRepetitionLevel` from leaf paths.
- `Plank/Writing`: keep null values unsupported for now; only enable nullable columns after definition/repetition level encoding is implemented end-to-end.
- `Plank/Writing/Encoding.cs`: replace `Dictionary<T, int>` in dictionary encoding with a zero-allocation dictionary/hash table path.
- `Plank/Writing/Compression/SnappyCompression.cs`: current `Snappier` backend still allocates per call (~48 bytes in tests). Replace backend with a true zero-allocation snappy implementation.
