# Row-group footer profiler

The recovered CLI uses ConsoleAppFramework and manages an isolated Python project automatically. Only selected engines and the repacking/result dependencies are installed; query engines are not bundled into the .NET tool.

```sh
dotnet run --project Plank.Tool -c Release -- profile rowgroups input.parquet \
  --sizes 10000,100000,1000000 --engines duckdb,polars,datafusion,clickhouse
```

The default result is `input.rowgroups-profile.parquet`. The command prints its absolute path to stdout; installation and progress go to stderr. Variants are retained in `input.rowgroups-profile/`. Existing output files or variant directories are rejected. Python 3.10+ with `venv` must exist; pip is checked and bootstrapped using `ensurepip` when missing. Spark additionally requires Java. Use `--worker-directory` to choose the cached worker project; its resolved dependencies are recorded in `requirements.lock.txt` and `pyproject.toml`.

The original input is included as a baseline with a null `requested_rows_per_group`. Result fields record the variant file, requested rows, actual minimum/maximum rows and group count, total rows, footer/file byte sizes, engine/version, measured operation, mean/median/min/max nanoseconds, iteration count, and threads. Imports, installation, engine startup, repacking, warmups and count validation are outside the timer. Measurements use a warm OS filesystem cache. Variant order is shuffled within each measured round; DuckDB's external file cache is disabled.

| Engine | Measured operation |
|---|---|
| DuckDB | `parquet_file_metadata`, including SQL/result materialization |
| Polars | `read_parquet_metadata`, including custom metadata conversion |
| DataFusion | `read_parquet(...).schema()`, including schema inference and planning |
| ClickHouse/chDB | `file(..., ParquetMetadata)`, including SQL/JSON result materialization |
| Spark | `read.parquet(...).schema`, including JVM schema-inference task overhead |

These are engine-native footer inspection timings, not isolated Thrift decoder microbenchmarks. The operation is recorded in every row so the costs cannot be mistaken for equivalent APIs. Spark's adapter is implemented but has not been executed in this environment.

## Current repacking scope

Compressed page headers and payloads are copied unchanged. Targets select available common page boundaries; actual sizes are always reported. Dictionary or codec incompatibilities retain required boundaries. The initial implementation supports flat non-repeated columns and rejects nested, encrypted, external-chunk and unsupported-page files. It does not silently decode/reencode to achieve an unavailable size.

Chunk statistics, page indexes, bloom filters, and row-group sort metadata are omitted when rebuilding chunks. Per-page statistics and checksums remain with copied pages. Therefore the original baseline can have a different metadata composition from repacked variants; comparisons among repacked variants isolate a consistent metadata policy.

On NYC yellow taxi January 2024, the three original groups are the only shared cuts in the initial implementation. Targets 10k, 100k and 1m consequently produce the same layout; their timings do not establish a row-group-size effect. Further splitting/grouping work is deferred.

## Validation

Run direct correctness checks without the repository's test runner:

```sh
python tools/rowgroups/check_repack.py
```

The checks use PyArrow and fastparquet from the worker environment and cover 20 variants: V1/V2 pages, plain/dictionary, nullable data, dictionary incompatibility, splitting/merging, page checksums, omitted indexes, decoded equality and unchanged data-page bytes. Real footer operations were exercised with DuckDB, Polars, DataFusion and ClickHouse, plus fresh/cached CLI bootstrap and input/output validation.

Schema inspection, encoding and query profiling, page-preserving merge, and HTML reports are also restored; see [Plank.Tool/README.md](../../Plank.Tool/README.md).
