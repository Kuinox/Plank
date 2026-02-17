# TODO

- `Plank2/Writing/SerializedColumn.cs`: remove the temporary `static readonly IPageStrategy DefaultPageStrategy = new DefaultStrategy();` and route strategy selection/injection through the intended writer/page pipeline.
- `Plank2/Schema`: support nested Parquet column paths as name segments (not only a single flat `string Name`) so leaf identity is unambiguous.
- `Plank2/Writing`: precompute per-leaf `maxDefinitionLevel` / `maxRepetitionLevel` from schema paths and write def/rep levels in data pages when required.
- `Plank2/Writing`: keep null values unsupported for now; only enable nullable columns after definition/repetition level encoding is implemented end-to-end.
- `Plank2/Writing/Encoding.cs`: replace `Dictionary<T, int>` in dictionary encoding with a zero-allocation dictionary/hash table path.
- `Plank2/Writing/Compression`: remove remaining per-call allocations (stream/wrapper and fallback scratch growth paths), keeping compression fully zero-allocation once buffers are pre-sized.
