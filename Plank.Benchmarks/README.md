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

Use `--data-dir`, `--output`, `--warmups`, `--iterations`, `--workers`, `--synthetic-rows`, or `--synthetic-width` to override the defaults.
