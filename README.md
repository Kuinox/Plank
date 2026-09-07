# Plank

Plank is a high-performance Parquet reader and writer for .NET.

The repository contains the library, its source generator, native compression support,
documentation, samples, and correctness/compatibility tests. Experimental benchmarks,
fuzzing infrastructure, and autonomous research tooling live in
[Plank-Lab](https://github.com/Kuinox/Plank-Lab).

## Getting started

Plank requires .NET 10:

```sh
dotnet new console --framework net10.0 --name PlankDemo
cd PlankDemo
dotnet add package Plank
```

Start with [schema declarations](https://kuinox.github.io/Plank/articles/schema.html),
[row writing](https://kuinox.github.io/Plank/articles/writing/rows.html), and [row reading](https://kuinox.github.io/Plank/articles/reading/rows.html).

## Projects

- `Plank` — the reader and writer package.
- `Plank.SourceGen` — generated row readers and writers.
- `Plank.Native.Zlib` — packaged Zlib-ng compression support.
- `Plank.Tests` and `Plank.SourceGen.Tests` — package and generator tests.
- `Samples/Plank.Sample` — row and column API examples.

## Build and test

```sh
git submodule update --init third_party/parquet-testing
dotnet test --solution Plank.sln --configuration Release
```

## Documentation

For on-demand PR performance comparisons, see [Running PR benchmarks](CONTRIBUTING.md#running-pr-benchmarks).

The documentation source is under `docs/`. Build it with:

```powershell
pwsh ./docs/build.ps1
```

The docs build runs the examples in `Samples/Plank.Sample`.

Plank is under active development; public APIs and format coverage may still evolve.
