# Plank

Plank is a low-allocation Parquet library for .NET. It currently focuses on fast writer paths, source-generated row APIs, and reusable buffers for parquet workloads where allocation control matters.

> [!NOTE]
> Plank is still under construction and is not released as a stable package yet.

## Start here

- [Getting started](articles/getting-started.md)
- [Writing parquet files](articles/writing.md)
- [Reading parquet files](articles/reading/index.md)
- [API reference](api/index.md)

## Current focus

- Column-oriented writing with pre-serialized columns.
- Source-generated row writers from `[ParquetSchema]` models.
- Configurable compression with `ParquetWriterOptions`.
- Reusable buffer pools through `IParquetBufferPool`.
- Page sizing controls through writer options and page strategies.
