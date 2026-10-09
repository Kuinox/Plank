# Large-file row-group experiment handoff

Start here when continuing the conversation on `codex/rowgroup-footer-profiler`.
This directory is the durable record; scratch paths and previous Python/.NET
installations are disposable. The user asked to pack this context and the
rebuild instructions onto this branch and push it.

## Goal and requested report

Use the actual large ClickBench dataset, rather than a small sample or a
throughput-only calibration report. Generate exact row-group targets:
**1,000; 10,000; 100,000; 200,000; 500,000; 1M; 2M; 5M; 10M rows.**

The requested tool report contains only an engine selector, file selector,
request graph and linked request table. Graph X is file byte offset; graph Y is
milliseconds since query start, increasing downward. Hide HEAD requests on the
graph, retain them in the table, and link hover/focus highlights both ways.
Engines: DuckDB, Polars, DataFusion and ClickHouse/chDB. Label failed runs;
never present their partial traces as successful queries. The viewer defaults
to a successful engine and accounts for elapsed query time and empty traces.

Do not substitute the older NYC taxi, original-file relay, or S3 throughput
reports for this nine-layout report. The tool's older `profile rowgroups` CLI
measures footer inspection; this experiment uses actual selective SQL queries
through the S3 simulator. Its separate scripts are not yet CLI-integrated.

## Completed experiment

Source: https://clickhouse-public-datasets.s3.amazonaws.com/hits_compatible/hits.parquet

- Downloaded and verified 14,779,976,446 bytes against SHA-256
  `a390f6cb782f6aaef278c72fc1dd86c4f30bc843ebab3c159e9bd4d45ddb079f`.
- 99,997,497 rows, 105 flat columns, 226 original row groups.
- Normalized to dictionary-free Zstd level 3, V1 data pages, exactly 1,000
  rows per page except the final 497-row page. Combined Arrow chunks before
  writing; an initial attempt retained incoming 450,560-row boundaries and was
  replaced. All decoded values were verified in 1M-row Arrow IPC batches.
- The saved backing file is 11,499,803,268 bytes. Its whole-file checksum and
  23 part checksums are in `shared-data.json`. Historical value fingerprints
  and preparation settings are in `preparation.json`.
- Repacked the same compressed pages into all nine layouts. Recomputed group
  statistics, preserved schema wire bytes, and checked representative output
  chunks against the backing pages. These layouts share identical page
  payloads; compression differs from the original Snappy/dictionary dataset.
- Captured 36 real engine runs through local simulated S3. All 33 successful
  runs returned the exact expected row. Three 1,000-row-layout failures remain
  recorded with their captured requests. All four engines passed every target
  from 10k through 10M. These are simulator measurements, not live cloud S3
  measurements. No rerun is needed to rebuild the saved report.

| Rows/group | Groups | Final group rows | File bytes | Footer bytes |
|---:|---:|---:|---:|---:|
| 1,000 | 99,998 | 497 | 12,305,708,099 | 806,875,248 |
| 10,000 | 10,000 | 7,497 | 11,581,826,508 | 82,993,657 |
| 100,000 | 1,000 | 97,497 | 11,507,195,138 | 8,362,287 |
| 200,000 | 500 | 197,497 | 11,503,043,552 | 4,210,701 |
| 500,000 | 200 | 497,497 | 11,500,541,963 | 1,709,112 |
| 1,000,000 | 100 | 997,497 | 11,499,691,978 | 859,127 |
| 2,000,000 | 50 | 1,997,497 | 11,499,275,128 | 442,277 |
| 5,000,000 | 20 | 4,997,497 | 11,499,017,082 | 184,231 |
| 10,000,000 | 10 | 9,997,497 | 11,498,930,327 | 97,476 |

The execution container had an 8 GiB cgroup memory limit. DuckDB and ClickHouse
were OOM-killed while parsing the 1k layout's ~807 MB footer. Polars succeeded.
DataFusion 54.1.0 rejected row group ordinal 32,768 because it exceeds i16.
Keep these qualifications with the results; they are not generic engine speed
claims. Capturing HTTP in the parent and running each native engine in a child
preserves requests when that child is killed. Do not keep the repacker's page
index in memory while benchmarking; release it by ending the generator process.

Query:

```sql
SELECT "WatchID" FROM data
WHERE "CounterID" = 17 AND "WatchID" = 9110818468285196899
```

One engine thread; no warmup; one measured run. Simulator settings: 100 ms
latency, 1.25 MiB/s initial throughput, 700 ms ramp, 53 MiB/s shared maximum.
The saved elapsed times extend through worker exit (including worker close),
with engine initialization excluded. `network-profile.json` is the authoritative
capture including engine versions, requests, result verification and failures.

## Files and recovery

Committed here: saved trace JSON, verified preparation record, normalized
part manifest, all nine size/count records, pinned Python versions, portable
rebuild scripts and this handoff. The page-preserving implementation is
`../stream_repack.py`; the viewer template is
`../../../Plank.Tool/Querying/request_report.html`.

Saved ChatGPT files:

- `large-file-tool-report.html`: latest nine-layout report (version 1).
- `large-rowgroups-reproduction.zip`: original restoration package, including
  full manifests and the same page-preserving implementation.
- `hits-aligned.parquet.part-000` through `hits-aligned.parquet.part-022`:
  23 saved shared-data parts; first 22 are 500,000,000 bytes each. Last part is
  smaller. Download by those exact names. Their checksums are committed here.

Use the Library skill to retrieve those named files if continuing in ChatGPT.
The native variants were generated/tested sequentially and then removed to fit
32 GB of disk. They are reproducible from the saved backing; nine native
11–12 GB files were **not** separately saved. The Library per-file limit was
512 MiB, hence the shared parts. Original scratch data and environments no
longer exist. Do not assume the former absolute paths still resolve.

## Environment

From the repository root (Python 3.12 was used):

```sh
python3 -m venv tools/rowgroups/large-file/.venv
. tools/rowgroups/large-file/.venv/bin/activate
python -m pip install -r tools/rowgroups/large-file/requirements.txt
```

The versions are those recorded by the completed experiment. Restoring/repacking
only needs numpy and fastparquet; normalizing from original also needs PyArrow.
Rebuilding HTML from saved JSON uses only Python's standard library. Query
profiling needs the four engine packages; DuckDB's httpfs extension must be
available and may download on first use. .NET is not needed for these scripts;
use the existing README for the .NET CLI and its isolated Python worker.

## Rebuild the report immediately

Choose a new output path; report writing rejects an existing file.

```sh
python tools/rowgroups/large-file/rebuild_report.py \
  --output /tmp/large-file-tool-report.html
```

This renders the committed capture using the committed tool template. It needs
neither the dataset nor installed query engines. All nine choices and the three
failure labels are retained.

## Restore exact saved data and generate variants

Download the 23 saved parts into `/data/plank-parts`. Keep their exact filenames.
Allow about 139 GB including downloaded parts, restored backing and all nine
native variants. Without retaining the parts, backing plus variants takes about
116 GB. Work outside the repository or in this directory's ignored `work/`.

```sh
python tools/rowgroups/large-file/rebuild_variants.py \
  --parts /data/plank-parts --output /data/plank-large
```

Every part and the assembled backing are SHA-256 checked before repacking.
This creates `hits-aligned.parquet`, `hits-rows-N.parquet` and an adjacent
`hits-rows-N.json` describing each output. Existing outputs are rejected.
For a subset, add `--rows 1000 10000`, for example. Once the backing exists,
use `--source /data/plank-large/hits-aligned.parquet` instead of `--parts`.
For limited disk, assemble only the backing first:

```sh
python tools/rowgroups/large-file/rebuild_variants.py \
  --parts /data/plank-parts --output /data/plank-large --restore-only
```

After successful checksum verification, local downloaded part copies can be
removed to free space; the originals remain saved. Then generate/test one
variant with `--source` and `--rows` at a time. Backing plus one variant needs
about 24 GB, excluding the environment and original source.

Generation loads a page index; it copies payloads with bounded buffers and
streams footer groups to avoid holding millions of metadata objects.

## Alternative: download original and normalize again

Use this if the saved parts are unavailable. Allow at least ~30 GB to retain
the original and normalized backing, and another ~12 GB for one variant.

```sh
python tools/rowgroups/large-file/prepare_source.py \
  --download --output /data/plank-prepared
python tools/rowgroups/large-file/rebuild_variants.py \
  --source /data/plank-prepared/hits-aligned.parquet \
  --output /data/plank-large --rows 10000
```

Alternatively pass `--source /path/to/hits.parquet` to preparation. The original
checksum/size is mandatory. Preparation combines incoming Arrow boundaries,
writes exactly 1,000-row pages and compares fingerprints for every decoded
1M-row batch. This replacement preparation script is portable; the original
scratch-only preparation used hole-punching to reclaim consumed input and was
not preserved. Restoring the saved parts is the route to the exact historical
backing bytes. Fresh normalization preserves values and the layout policy;
encoder changes/settings can change byte sizes, so recapture timings for it.
Do not reuse historical trace coordinates for freshly encoded data.

## Capture fresh requests

Generator and profiler are separate commands so the engine has the memory
budget to itself. Adjacent per-file `.json` records are required.

```sh
python tools/rowgroups/large-file/profile_requests.py \
  --files /data/plank-large/hits-rows-10000.parquet \
          /data/plank-large/hits-rows-100000.parquet \
  --output /data/plank-large/fresh-profile.json
python tools/rowgroups/large-file/rebuild_report.py \
  --profile /data/plank-large/fresh-profile.json \
  --output /data/plank-large/fresh-report.html
```

The default is all four engines; `--engines polars` runs a subset. Each native
query runs in a separate process. The parent serves local S3 and retains traces
on engine failure. Successful results must equal the expected row exactly.
A 300-second query timeout is configurable with `--timeout`. Checkpoints are
written to `.incomplete` and installed at the final path when complete.
For small disks, generate/test one target at a time and retain its JSON traces
before removing only that generated variant. Never benchmark concurrently with
normalization/repacking or data-part uploads.

## Technical pitfalls and validation

- Input to `OffsetPages` must be flat, dictionary-free V1 pages aligned at
  1,000 rows. Passing the raw original is unsupported. Incoming Arrow chunk
  boundaries must be combined before encoding; target row group alone is not
  enough to guarantee page alignment.
- Fastparquet's reserialization of schema logical Integer bitWidth could change
  wire types; `stream_repack.py` retains schema bytes verbatim.
- Group statistics are rebuilt from page min/max and null counts. Old page
  indexes, bloom metadata, encoding/size statistics and group sort metadata
  are not reused for rebuilt chunks. The earlier generic repacker omits group
  stats and has different cutting rules; use the large-file scripts here.
- Generation validation checks group totals and selected chunks in the finished
  file. Preparation verifies all decoded values. Successful query validation
  checks the exact expected result, not only the row count.
- The completed viewer was checked across all 36 file/engine combinations for
  request count, HEAD filtering, finite scales, linked hover and failure labels.
- Run `python tools/rowgroups/large-file/check_rebuild.py` for a small portable
  check of source normalization, dictionary removal, exact groups, decoded
  equality, unchanged page bytes, part integrity and saved-report rendering.

## Handoff verification in the recovered session

The portable small-data check passed: source normalization across awkward
incoming row groups, nine exact targets, unsigned schema preservation, nullable
values, every copied compressed page, valid/corrupt part handling and report
rendering. The saved HTML was regenerated byte for byte. CLI variant rebuilding
was also executed on a small aligned file. A fresh Polars native request capture
returned the expected row through the simulator. DuckDB startup failed because
httpfs could not be installed in the clean environment; the capture recorded the
startup failure correctly. That smoke result does not replace the historical
36-run experiment. The new public-source downloader was not rerun on all 14.78 GB
just to pack the handoff.

## Continue from this branch

This task is complete; do not download/regenerate 100+ GB just to recover the
context. Start by rebuilding the saved HTML. Keep future requested work on this
branch unless the user specifies otherwise. Potential follow-up: integrate the
large-file pipeline into the CLI, rerun 1k on a machine with more memory, or
compare other queries. Do not claim those follow-ups have already happened.
