# Plank CLI

A ConsoleAppFramework .NET tool for schema inspection, write-profile search, SQL profiling and Parquet merging. Query engines live in an automatically managed Python environment; they are not bundled in the .NET package.

```sh
# Discover output formats, or inspect with an explicit format in the command.
plank schema
plank schema json input.parquet
plank schema tsv input.parquet
plank schema lines input.parquet

# Column IDs are zero-based; complete column paths are also accepted.
plank profile encoding input.parquet --columns 0,3,trip_distance

# Optional filters for a smaller sweep.
plank profile encoding input.parquet --columns trip_distance \
  --encodings Plain,RleDictionary,ByteStreamSplit --compressions Zstd --compression-levels 1,3,9,19

# Merge sharded profiles. Output is positional; input schemas must match.
plank merge combined.parquet part1.parquet part2.parquet

# Generate a standalone HTML report with embedded data and browser dependencies.
plank report combined.parquet report.html
# Older profiles can recover physical types from the original file.
plank report old-profile.parquet report.html --source input.parquet

# Every engine registers the input as the SQL table data.
plank profile query input.parquet --query "SELECT SUM(trip_distance) FROM data" \
  --engines duckdb,polars,datafusion,clickhouse

plank profile rowgroups input.parquet --sizes 10000,100000,1000000
```

Profilers default to `INPUT.encoding-profile.parquet`, `INPUT.query-profile.parquet`, and `INPUT.rowgroups-profile.parquet`. Reports default to the input basename with `.html`. Successful file-writing commands print the absolute result path on stdout; progress and errors use stderr. Existing destinations are rejected. Results are published only when complete.

Encoding profiling uses **Plank**, with source loading outside the timer, an untimed lossless round-trip check for every candidate, two warmups, and five measured iterations by default. It retains each original row group's rows. Times are the mean **total** write/read time across all groups; sizes are **total compressed column-chunk bytes** before/after, including page and dictionary headers and excluding file footer, indexes and bloom filters. `PlainDictionary` and `RleDictionary` are one candidate. Unsupported physical-type/encoding combinations are skipped with a reason on stderr. The initial profiler supports flat non-repeated leaves.

Compression sweep defaults: None and Snappy; Gzip levels 0–9; Brotli 0–11; LZ4_RAW 0,3,6,9,12; Zstd -5,-1,1,3,6,9,12,15,19,22. Optional `--compression-levels` picks levels that are valid for each selected codec. Legacy LZ4 writing is not supported.

Encoding result schema (version 4): `column_path`, `encoding` (verified actual data-page encoding), `compression`, `original_size_bytes`, `compressed_size_bytes`, `write_time_ns`, `read_time_ns`, nullable `compression_level` (Int32), nullable `physical_type`. Common timing/source parameters are footer metadata.

Query profiling consumes all result rows and validates repeatability outside the timer (floating results use 12 significant digits to tolerate reduction-order rounding). Imports, engine initialization, registration, warmups and validation are excluded. The result schema records file, engine/version, query, mean/median time, iterations, threads and result row count. Warm filesystem caches are used. DuckDB, Polars, DataFusion and ClickHouse/chDB were executed here. Spark's adapter requires Java and has not been executed in this environment.

Python 3.10+ with venv must be on PATH. The tool creates its project, checks/bootstraps pip, installs only selected engines plus worker dependencies, and records resolved versions in `requirements.lock.txt` and `pyproject.toml`. Use `--worker-directory` to choose its cache. The row-group profiler's scope and measured footer operations are documented in [tools/rowgroups/README.md](../tools/rowgroups/README.md).

The recovered viewer supports local Parquet uploads, multiple reports, physical types, read/write selection, shifted logarithmic size, linear time from zero, the whole Pareto frontier, muted dominated points, compression colors, encoding shapes, reader-feature controls, explicit capability filters, clear filters, sorting and mobile cards. Its recovered runtime is self-contained; no remote scripts or uploads are required.

## Direct validation

Build the tool, then run `tools/cli/check_restored.py DOTNET WORKER_DIR [ENGINES]` using Python with PyArrow installed. Tests cover all restored commands, column selection, dictionary alias normalization, levels, chunk sizes, exact merge page preservation, output protection, errors and generated HTML. The separate `tools/rowgroups` checks cover the newer command. These direct checks do not invoke the repository test runner.
