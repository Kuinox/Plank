# Plank

Plank is a high-performance Parquet reader and writer for .NET.

The repository contains the library, its source generator, native compression support,
documentation, samples, and correctness/compatibility tests. Experimental benchmarks,
fuzzing infrastructure, and autonomous research tooling live in
[Plank-Lab](https://github.com/Kuinox/Plank-Lab).

## Projects

- `Plank` — the reader and writer package.
- `Plank.SourceGen` — generated row readers and writers.
- `Plank.Snappy` and `Plank.Native.Zlib` — compression support.
- `Plank.Tests` and `Plank.SourceGen.Tests` — package and generator tests.
- `Samples/Plank.Sample` — row and column API examples.

## Build and test

```sh
git submodule update --init third_party/parquet-testing
dotnet test --solution Plank.sln --configuration Release
```

## Buffer pooling

Native buffers flow through `IParquetBufferPool`. `DefaultParquetBufferPool.Shared`
adaptively retains the rolling p99 peak demand for each buffer size. For allocation-free
reuse after warmup, pass
`new DefaultParquetBufferPool(ParquetBufferRetentionPolicy.ZeroAllocation)` through
the writer or reader options. Custom pools can also set a hard retained-byte limit and
release idle memory with `Trim()`.

## Documentation

The documentation source is under `docs/`. Build it with:

```powershell
pwsh ./docs/build.ps1
```

Plank is under active development; public APIs and format coverage may still evolve.
