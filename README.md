# Plank

Plank is a high-performance Parquet reader and writer for .NET.

The repository contains the library, its source generator, native compression support,
documentation, samples, and correctness/compatibility tests. Experimental benchmarks,
fuzzing infrastructure, and autonomous research tooling live in
[Plank-Lab](https://github.com/Kuinox/Plank-Lab).

## Getting started

Consumer projects target .NET 10:

```sh
dotnet new console --framework net10.0 --name PlankDemo
cd PlankDemo
dotnet add package Plank
```

The package command applies once Plank is published to your NuGet feed. Before publication,
run the checked-in samples from a source checkout:

```sh
dotnet run --project Samples/Plank.Sample --configuration Release
```

The sample writes and reads rows, checks null and empty binary values, roundtrips decimals and
UTC timestamp instants, and writes partitioned datasets. It uses temporary files and deletes
them after verification. Start with [schema declarations](https://kuinox.github.io/Plank/articles/schema.html),
[row writing](https://kuinox.github.io/Plank/articles/writing/rows.html), and [row reading](https://kuinox.github.io/Plank/articles/reading/rows.html).
The documentation includes the same compiled sample code.

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

The documentation source is under `docs/`. Build it with:

```powershell
pwsh ./docs/build.ps1
```

The build runs the documentation samples before rendering. Row and dataset quickstart snippets
are included from named regions in `Samples/Plank.Sample`; edit those sources so code and docs
stay together. CI also executes the samples for pull requests.

Plank is under active development; public APIs and format coverage may still evolve.
