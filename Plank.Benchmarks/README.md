# Encoding regression suite

`--encoding-regression` measures every encoder path in `Plank.Writing.Encoding` — each supported
(physical type × encoding × repetition) combination, required, optional and repeated — and records a
SHA-256 of each encoded file alongside the timings.

```bash
git checkout master
dotnet run -c Release --project Plank.Benchmarks -- --encoding-regression --label baseline
git checkout my-branch
dotnet run -c Release --project Plank.Benchmarks -- --encoding-regression --label current
dotnet run -c Release --project Plank.Benchmarks -- --encoding-regression-compare \
  artifacts/benchmarks/encoding-regression-baseline.json \
  artifacts/benchmarks/encoding-regression-current.json
```

The compare step exits non-zero when a case flips between `ok` and `failed`, when a case gets slower
than the tolerance, or — most usefully for refactors — when the encoded bytes change at all. A
refactor that is meant to preserve behaviour should report every case as `same`.

**The encoded-bytes check is the exact signal; the timings are a coarse net.** Cases are measured
round-robin (one sample per case per round, 30 rounds) and compared on the fastest observed
iteration, because contention and GC only ever add time. Even so, two *identical* runs on a shared
runner disagree by a median of 2.6% and a p90 of 9%, so the tolerance defaults to 10% and cases whose
baseline is under 100 us are printed but not failed — `bool/plain/required` encodes 200k values in
~30 us, where a few microseconds of jitter reads as tens of percent. Expect the occasional false
positive on a busy machine and re-run before believing a lone timing regression. Tune with
`--tolerance 1.20` and `--significance-floor 500`.

Only `SerializedColumn.Serialize` is timed. That covers level writing, dictionary construction, value
encoding and page splitting, but also the column statistics pass, so encoder-only deltas are damped
rather than shown at full size.

Override the defaults with `--rows` (200,000), `--warmups` (3), `--iterations` (30) or `--output`.

# Published benchmarks

The homepage benchmark is generated manually. DocFX only copies the reviewed JSON snapshot and never runs a benchmark.

Run the small smoke suite first:

```bash
dotnet run -c Release --project Plank.Benchmarks -- --published-write --quick
```

It covers both suites and every writer, and writes `artifacts/benchmarks/write-quick-v1.json`.

Publish the full 2-warmup, 7-iteration suite:

```bash
dotnet run -c Release --project Plank.Benchmarks -- --published-write
```

This preloads January 2024 NYC Yellow Taxi data, validates each output separately, and writes the versioned snapshot to `docs/benchmarks/write-v1.json`.

Run the equivalent read suite with:

```bash
dotnet run -c Release --project Plank.Benchmarks -- --published-read
```

It generates one audited in-memory file per case, then writes `docs/benchmarks/read-v1.json`.
Every timed reader traverses every decoded logical value (and every byte of binary values) into a
deterministic fingerprint. The fingerprint is validated after the timer, so the JIT cannot discard
decoded buffers that a real consumer would observe. Multithreaded readers consume independent columns
in parallel and combine their fingerprints in schema order.

Use `--data-dir`, `--output`, `--warmups`, `--iterations`, `--workers`, `--synthetic-rows`, or `--synthetic-width` to override the defaults.
