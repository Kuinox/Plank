# Reading parquet files

Plank's reader stack has three user-facing levels. Each level trades convenience for control over allocations, schema binding, and decoding.

| Level | API shape | Use it when |
| --- | --- | --- |
| [Physical reader](physical.md) | `ParquetFileReader`, `ParquetRowGroupInfo`, `ParquetColumnChunkInfo`, `ParquetPageCursor` | You need parquet metadata, schema nodes, row groups, column chunks, page headers, and page payloads with minimal materialization. |
| [Typed column reader](typed.md) | `ParquetReader`, `RowGroupReader`, `ColumnPage<T>` | You have a `ParquetSchema` and want typed column pages bound to that schema. |
| [Generated row reader](generated.md) | generated readers based on `[ParquetSchema]` models | You want the highest-level row-shaped API for an application model. |

The lower levels are useful for inspection, custom projection, and allocation-sensitive integrations. The higher levels are intended for normal application code.
