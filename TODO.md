# TODO

## Deferred While Implementing Thrift Metadata

- `Plank/Writing/Encoding.cs`: force dictionary mode to disabled for now; re-enable only after dictionary page headers/metadata are emitted with real Thrift structures.
- `Plank/Writing/Encoding.cs`: replace the custom data-page header bytes with real Thrift `PageHeader` + `DataPageHeaderV2` emission.
- `Plank.Tests/E2E/WriterInteropE2ETests.cs`: keep Parquet.Net + ParquetSharp interop tests as the acceptance gate while footer/page format work is in progress.

## Existing Backlog

- `Plank/Writing/SerializedColumn.cs`: remove the temporary `static readonly IPageStrategy DefaultPageStrategy = new DefaultStrategy();` and route strategy selection/injection through the intended writer/page pipeline.
- `Plank/Schema`: support nested Parquet column paths as name segments (not only a single flat `string Name`) so leaf identity is unambiguous.
- `Plank/Writing`: precompute per-leaf `maxDefinitionLevel` / `maxRepetitionLevel` from schema paths and write def/rep levels in data pages when required.
- `Plank/Writing`: keep null values unsupported for now; only enable nullable columns after definition/repetition level encoding is implemented end-to-end.
- `Plank/Writing/Encoding.cs`: replace `Dictionary<T, int>` in dictionary encoding with a zero-allocation dictionary/hash table path.
- `Plank/Writing/Compression`: remove remaining per-call allocations (stream/wrapper and fallback scratch growth paths), keeping compression fully zero-allocation once buffers are pre-sized.
