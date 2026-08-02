# Plank feature comparison

Last checked: 2026-08-02

This compares Plank at commit b09d2d535884fb98b088f7811776d2634eb463b1 with the current stable packages available when checked: [ParquetSharp 24.0.0][ps-nuget] and [Parquet.Net 6.0.3][pn-nuget]. Competitor behavior is taken from their official documentation and repositories, not inferred from similarly named generated Thrift classes.

The status terms in this document are:

- **Complete**: a supported public implementation was confirmed for the stated scope.
- **Partial**: only some directions, types, API layers, or configurations are implemented.
- **Absent**: no implementation or public entry point was found.
- **Not confirmed**: lower-level format types or broad claims exist, but the official public surface does not establish usable support.

The existing Plank interop and benchmark references are older than this market snapshot: tests use [Parquet.Net 5.5.0 and ParquetSharp 23.0.0.2][plank-test-project], while benchmarks use [Parquet.Net 6.0.0-pre.8 and ParquetSharp 23.0.0.2][plank-benchmark-project]. Results from those projects should not be presented as comparisons with the current stable competitors until their baselines are updated.

## Executive summary

Plank already has a strong, performance-oriented flat-column core. Its distinctive strengths are compile-time generated typed row and column APIs, reusable readers and writers, explicit pooled-buffer ownership, a public raw-page cursor, page-index writing and inspection, and callback-driven page pruning.

The largest functional gap is nested/repeated reading. Plank can define and interoperably write groups, lists, maps, and nested lists, but its logical reader explicitly rejects repeated readback and its source generator excludes repeated columns. ParquetSharp and Parquet.Net both read and write nested data.

ParquetSharp has the broadest format and analytics surface: Apache Parquet C++ behavior, current logical types, custom type factories, Arrow record batches, modular encryption, page checksums, and detailed writer controls. Its trade-off is a native 64-bit runtime and native lifetime hazards.

Parquet.Net has the broadest managed application-level surface: fully managed modern .NET, async low- and high-level APIs, nested class serialization, untyped dictionaries, reopen-and-append, DataFrame integration, and file merging. Its writer controls and advanced integrity/security surface are narrower than ParquetSharp's.

## Capability matrix

| Capability | Plank | ParquetSharp 24.0.0 | Parquet.Net 6.0.3 |
| --- | --- | --- | --- |
| Runtime and portability | **Partial.** Main assembly targets only [net10.0][plank-project-target]. C# codecs are mixed with native Snappy and zlib runtime assets plus managed LZ4/Zstd packages ([project references][plank-project-packages]). | **Partial.** Native P/Invoke wrapper over Apache Parquet C++; x64 and arm64 on Linux, Windows, and macOS, and 64-bit only. Package targets net8.0, netstandard2.1, and net471. See the [official repository][ps-repo] and [package][ps-nuget]. | **Complete for modern .NET.** Officially fully managed and zero-dependency, targeting net8.0 and net10.0. See the [official repository][pn-repo]. |
| Runtime schema construction | **Complete.** Dynamic leaf/group/list/map schemas are supported by [ColumnDefinition][plank-column-definition] and [ParquetSchema][plank-schema]. | **Complete.** Column arrays and explicit Node graphs are supported by the [writing API][ps-writing]. | **Complete.** Dynamic DataField, StructField, ListField, and MapField schemas are documented in the [official repository][pn-repo]. |
| Physical Parquet types | **Complete.** All eight physical types have explicit writer and reader implementations; see [ParquetPhysicalType][plank-physical-types], [PlainEncoding][plank-plain-encoding], and the reader dispatch in [ColumnChunkReader][plank-reader-physical-dispatch]. INT96 is exposed as a raw 12-byte payload rather than a temporal CLR type. | **Complete.** All physical types are exposed through physical and logical readers/writers; INT96 remains a raw type. See the [reading guide][ps-reading]. | **Complete.** The official project advertises all Parquet physical types and documents INT96 temporal handling in its [type mapping][pn-repo]. |
| Logical Parquet types | **Partial.** String, JSON, UUID, date, time, timestamp, integer, and decimal are modeled in [LogicalType][plank-logical-types]; list/map annotations are modeled structurally. Enum, BSON, Float16, interval, geography, geometry, variant, and unknown-logical-type preservation are missing. | **Complete.** The current [LogicalTypeEnum][ps-logical-types] includes list/map, enum, BSON, Float16, interval, geography, geometry, and variant in addition to Plank's set. | **Partial.** Broad scalar, decimal, temporal, enum, list, map, and struct handling is documented, but variant support remains an [open feature request][pn-variant]. |
| Direct CLR mappings | **Partial.** Fixed mappings cover bool, byte/ushort/int/uint, long/ulong, float/double, string, byte arrays, Guid, DateOnly, TimeOnly, DateTime, and DateTimeOffset; see [ParquetTypeMap][plank-type-map]. There is no CLR decimal, sbyte/short, enum, or custom converter mapping. | **Complete.** Built-ins include decimal and temporal types, and public [type factories][ps-type-factories] allow arbitrary user mappings and converters. | **Complete for documented serializers.** Decimal, enums, temporal types, nullable values, collections, and complex classes are documented in the [official repository][pn-repo]. |
| Flat column read/write | **Complete.** Physical, logical, and generated typed column APIs are implemented; logical enumeration uses page-bound pooled buffers. See [physical reading](articles/reading/physical.md) and [logical reading](articles/reading/logical.md). | **Complete.** Typed physical and logical column APIs are the primary [read][ps-reading] and [write][ps-writing] surfaces. | **Complete.** Async low-level column reads accept preallocated buffers, and writes accept column arrays; see the [low-level API][pn-repo]. |
| Row/class API | **Complete for supported flat schemas.** The source generator emits typed pipeline writers, row readers, projections, and reset methods; see [ParquetRowGenerator][plank-row-generator] and [row reading](articles/reading/rows.md). It is compile-time rather than reflection based. | **Complete.** Tuple/struct row readers and writers plus custom mapping are provided, although the [row-oriented guide][ps-rows] warns that a whole row group is buffered and the API trades performance for convenience. | **Complete.** Runtime-compiled class serialization/deserialization supports fields and properties; deserialization requires a parameterless class constructor and does not support structs. See the [high-level API][pn-repo]. |
| Nested/repeated write | **Partial.** Runtime schemas and column serialization write interoperable groups, required/optional lists, optional elements, maps, and nested lists; see [ListInteropE2ETests][plank-list-interop-tests] and [NestedInteropE2ETests][plank-nested-interop-tests]. The generated row API excludes repeated columns at [ParquetRowGenerator.cs:547][plank-generator-repeated]. | **Complete, leaf-oriented.** Explicit schema graphs and Nested values represent repeated/optional structures; see the [nested guide][ps-nested]. | **Complete.** Low-level levels and high-level nested structs, lists of lists/structs, maps, and legacy repeated fields are documented in the [official repository][pn-repo]. |
| Nested/repeated read | **Partial at the physical layer; absent at logical and row layers.** Schema and raw pages can be inspected, but logical reads throw at [ColumnChunkReader.cs:1047][plank-reader-repeated-1] and [ColumnChunkReader.cs:1143][plank-reader-repeated-2]. Generated rows also exclude repeated columns. | **Complete, leaf-oriented.** Nested values are reconstructed through repetition and definition levels, with limitations described in the [nested guide][ps-nested]. | **Complete.** Nested low-level columns and high-level List and Dictionary materialization are documented. Exact supported collection directions are listed in the [collection table][pn-repo]. |
| Unknown-schema/untyped rows | **Absent.** Runtime schema discovery and generic column reads exist, but there is no JSON-like row serializer. | **Partial.** Visitor APIs handle unknown physical/logical column types, but no Dictionary-style row serializer is documented; see the [writing guide][ps-writing]. | **Complete.** Untyped read/write uses Dictionary<string, object>; see the [untyped serializer][pn-untyped]. |
| Modern encodings | **Complete with type restrictions.** PLAIN, dictionary, RLE, DELTA_BINARY_PACKED, DELTA_LENGTH_BYTE_ARRAY, DELTA_BYTE_ARRAY, and BYTE_STREAM_SPLIT have writer dispatch in [EncodingCompatibility][plank-encoding-compatibility] and reader dispatch in [ColumnChunkReader][plank-reader-encoding-dispatch]. Deprecated BIT_PACKED is read for legacy levels but deliberately rejected by the writer. | **Complete for the documented modern families.** The [encoding enum][ps-encodings] exposes the same standard families and writer properties select encoding globally or per column. Presence of legacy BIT_PACKED in the enum is not treated here as proof that it can be selected for new files. | **Partial writer control.** The project advertises all readers, while the confirmed public writer hints are Dictionary, DeltaBinaryPacked, and ByteStreamSplit; the [official encoding example][pn-repo] documents per-column hints. |
| Compression codecs | **Partial.** None, Snappy, Gzip, Zstd, LZ4 raw, and Brotli have explicit [writer][plank-compression-writer] and [reader][plank-compression-reader] dispatch. LZO, BZ2, and legacy/framed/Hadoop LZ4 variants are absent from [CompressionKind][plank-compression-kind]. | **Complete for its published selectable codec set.** The writer accepts compression globally or per column through [WriterPropertiesBuilder][ps-writer-properties]; its [Compression][ps-compression] set includes uncompressed, Snappy, Gzip, Brotli, Zstd, LZ4 raw/frame/Hadoop, LZO, and BZ2. | **Partial versus ParquetSharp.** None, Snappy, Gzip, LZO, Brotli, LZ4, Zstd, and LZ4 raw are public in [CompressionMethod][pn-compression]. BZ2 is absent. |
| Per-column encoding/compression | **Partial.** Encoding sequences and custom page strategies are per column through [ColumnOptions][plank-column-options], but compression and compression level are file-wide in [ParquetWriterOptions][plank-writer-options]. | **Complete.** Encoding, compression, compression level, dictionary, statistics, and page-index controls can be global or per-column through [WriterPropertiesBuilder][ps-writer-properties]. | **Partial.** Encoding hints are per column, while compression is global in [ParquetOptions][pn-options]. |
| Data page versions and sizing | **Partial.** Reads V1 and V2, writes V2 only at [RowGroupWriter.cs:191][plank-data-page-v2], and exposes target size/custom page strategy. No public file-format or data-page-version option exists. | **Complete.** Parquet format version, V1/V2 data pages, page sizes, dictionary page limit, and batch size are configurable in [WriterPropertiesBuilder][ps-writer-properties]. | **Partial/not separately documented.** Broad V1/V2 interoperability is claimed, but no public page-version selector is documented in the current options. |
| Statistics and page indexes | **Complete and unusually accessible.** Column/page statistics and offset/column indexes are written by default; [ParquetColumnChunkMetadata][plank-chunk-metadata] exposes them, and [ParquetPagePruner][plank-page-pruner] skips rejected page reads. | **Partial.** Statistics and page-index writing are configurable in [WriterPropertiesBuilder][ps-writer-properties]. A documented public page-index inspection/pruning API was not found, so reader-side parity with Plank is not confirmed. | **Partial.** Row-group/column statistics are public. A documented page-index writer, reader, or page-pruning public API was not found. Generated metadata classes alone are not counted as implementation. |
| File/custom metadata | **Partial.** File/schema/row-group/chunk/page metadata and statistics are public, but custom key-value metadata, created_by configuration, field IDs, and sorting-column declarations are absent from the [public API baseline][plank-public-api]. | **Complete for compared scope.** File metadata exposes created_by and key-value metadata, and the writer configures both metadata and sorting columns; see [FileMetaData][ps-file-metadata] and [WriterPropertiesBuilder][ps-writer-properties]. | **Complete for key-value metadata.** Reader/writer and serializer custom metadata are documented in the [official repository][pn-repo]. Advanced field-ID and sorting configuration was not confirmed. |
| Raw physical page access | **Complete.** [ParquetPageCursor][plank-page-cursor] exposes each parsed header and decompressed payload, while [ParquetFileMetadata][plank-file-metadata] exposes compact footer structures. | **Partial.** Low-level physical values and rich metadata are public, but a raw encoded/decompressed page-payload cursor was not found in the documented API. | **Partial.** Raw metadata structures are exposed, but no public raw page-payload cursor was confirmed. |
| Sync/async I/O | **Partial.** Public read/write operations are synchronous. Random-access sources are extensible through [IParquetReadSource][plank-read-source]; streams must be seekable. | **Partial.** Low-level column I/O is synchronous. The [Arrow API][ps-arrow] supports async record-batch reads, threaded parsing, paths, streams, and random-access files. | **Complete.** Low- and high-level reads/writes are async and accept paths or random-access streams; see the [official repository][pn-repo]. |
| Reuse and append | **Partial with a unique strength.** Readers and writers reset over new sources/streams without rebuilding their reusable state; see [ParquetWriter.Reset][plank-writer-reset] and [ParquetReader.Reset][plank-reader-reset]. Multiple row groups can be added while open, but an existing closed file cannot be reopened and appended. | **Partial.** Multiple row groups can be appended while a writer remains open. No documented reopen-and-append API was found. | **Complete.** Existing files can be reopened, their footer replaced, and new immutable row groups appended; see [appending][pn-append]. |
| Parallelism, projection, evolution | **Complete for supported flat rows.** Generated pipeline writers use configurable workers, row readers support one-row-group read-ahead/backpressure, projections avoid unselected columns, and [ParquetSchemaEvolutionOptions][plank-schema-evolution] explicitly controls missing/repetition/physical/logical/materialized changes. | **Partial.** Arrow reads support column/row-group projection and threaded parsing; lower-level schema conversion can be customized, but no equivalent explicit evolution policy object is documented. See [Arrow][ps-arrow] and [type factories][ps-type-factories]. | **Partial.** Async APIs and class projections exist, but the official guidance says write parallelism is absent and parallel row-group reads require separate readers; see [parallelism][pn-repo]. No equivalent explicit evolution policy object was confirmed. |
| Arrow/DataFrame ecosystem | **Absent.** No Arrow or Microsoft.Data.Analysis adapter is present. | **Complete for Arrow; add-on for DataFrame.** The [Arrow API][ps-arrow] supports zero-copy C-data-interface record batches, projection, threading, and read/write. A separate [DataFrame package][ps-dataframe] is linked by the project. | **Partial.** No Arrow API is documented. A separate DataFrame package supports primitive columns only; see [DataFrame support][pn-dataframe]. |
| Encryption and page integrity | **Absent.** No modular encryption, decryption, page CRC writing, or CRC verification API is present. | **Complete.** High- and low-level modular encryption, per-column/footer keys, KMS envelope encryption, and Arrow compatibility are documented in [encryption][ps-encryption]. Page CRC write and verification are public in [writer][ps-writer-properties] and [reader properties][ps-reader-properties]. | **Absent/not confirmed.** Modular encryption is still an [open feature request][pn-encryption]. No public page-checksum configuration was found; Thrift Crc fields alone are not proof of implementation. |
| Utilities | **Absent.** No file merger, DataFrame adapter, or Arrow adapter is present. | **Partial.** Arrow and DataFrame integration exist, but no file merger was found in the documented core surface. | **Complete for compared utilities.** FileMerger can copy row groups as-is or recombine them, and DataFrame/untyped adapters are documented in the [official repository][pn-repo]. |

## Plank strengths worth preserving

- Compile-time generated typed APIs avoid the reflection/expression-tree startup path used by general class serializers.
- Reusable Reset-based readers and writers are valuable for steady-state services and benchmark fairness.
- Public buffer-pool injection, owned/reference-counted buffers, and page-boundary enumeration make allocation behavior explicit.
- Pre-serialized columns and the generated background pipeline provide useful producer/encoder/writer separation.
- Raw physical page access plus page-index inspection and callback pruning is a stronger diagnostic and scan primitive than the documented competitor surfaces.
- Explicit schema-evolution policies are clearer than silently accepting or rejecting mismatches.

## Prioritized gaps

### P0: correctness and core interoperability

1. **Nested/repeated logical and row reading - public API approval likely required.** This is the largest parity gap and blocks lists, maps, repeated primitives, and nested objects from both logical and generated row readers. Existing public buffers represent one materialized value per row and do not expose repetition levels, so a useful implementation likely needs a new public repeated/nested buffer shape or collection mapping. Do not implement that public shape without approval.

2. **Preserve or explicitly represent unsupported logical annotations - public API approval likely required.** Current physical metadata recognizes only part of the logical-type union; newer or unsupported variants are skipped and effectively become None in [PhysicalMetadataThriftReader][plank-physical-metadata-reader]. Adding enum members and LogicalType records changes public API. At minimum, add interoperability tests for Enum, BSON, Float16, interval, geography, geometry, and variant before choosing a representation.

3. **CLR decimal materialization and serialization.** Decimal schema metadata exists, but [ParquetTypeMap][plank-type-map] and the generated row mappings do not support System.Decimal. Supporting a fixed default through existing generic APIs may be possible without adding a hand-written public member. Exposing precision and scale on [ParquetColumnAttribute][plank-column-attribute] would be a public API change and requires approval.

4. **Refresh competitor interop baselines without changing Plank public API.** Move tests and benchmarks to current stable competitor versions, retain representative files from older versions, and distinguish format interoperability tests from performance benchmarks.

### P1: adoption and application completeness

1. **Target net8.0 as well as net10.0 where feasible.** This is packaging/build work rather than an API-shape change and would match both current competitors' supported modern baseline.

2. **Custom key-value metadata and created_by - public API approval required.** These are common for GeoParquet, Arrow schema preservation, lake metadata, and provenance.

3. **Async/cancellable I/O and stream ownership - public API approval required.** New async methods, cancellation tokens, and leave-open semantics necessarily extend the public surface. Benchmark first on real file/network sources; async does not inherently make CPU encoding faster.

4. **Reopen-and-append and merge utilities - public API approval required.** Parquet.Net demonstrates demand for batch ingestion and compaction. A separate utility assembly could keep the core surface focused, but it still introduces public API.

5. **Per-column compression and format/page-version controls - public API approval required.** Plank already has per-column encoding and page strategy, so codec selection is the obvious missing tuning dimension.

6. **Custom CLR converter/type mapping - public API approval required.** This would close decimal/domain-type gaps without continuously expanding hard-coded type checks.

### P2: advanced format and ecosystem breadth

1. **Field IDs, sorting declarations, page CRC, Bloom filters, and modular encryption - public API approval required.** Prioritize field IDs and CRC before encryption unless user demand says otherwise; encryption is a substantial key-management and security-testing commitment.

2. **Arrow, DataFrame, and untyped adapters - public API approval required.** Prefer separate packages so dependencies and convenience allocations do not affect the core library.

3. **Additional codecs.** LZO/BZ2 and legacy LZ4 forms improve long-tail read compatibility, but should follow nested read, decimal, metadata, and portability work.

## Public API decision boundary

The following high-value work can proceed without deliberately changing the checked public API: update competitor baselines, add corpus/interop tests, expand malformed-file tests, multi-target if implementation permits, optimize existing encoding/decoding internals, and investigate default decimal handling through existing generics.

Do not start nested collection models, new logical-type enum values/records, metadata options, async methods, append/merge entry points, converter hooks, per-column compression, integrity/security options, or ecosystem adapters until their public design is approved.

## Source notes and uncertainties

- Competitor package versions are pinned to NuGet stable releases, while their official documentation sites may reflect the latest branch. Claims above use documented public entry points and call out uncertain areas.
- ParquetSharp page-index reading/pruning and Bloom-filter APIs were not found in the published public docs. Page-index writing is confirmed. This document does not infer reader or Bloom support from Apache Parquet C++ internals.
- Parquet.Net publishes generated metadata models for page indexes, Bloom filters, CRC, and encryption because those structures are part of the Parquet Thrift schema. This document does not count those model classes as working high-level read/write features.
- Parquet.Net's broad "all types, encodings and compressions" statement is narrower than a verified writer-control matrix. The exact confirmed writer hints are Dictionary, DeltaBinaryPacked, and ByteStreamSplit.

[ps-nuget]: https://www.nuget.org/packages/ParquetSharp/
[ps-repo]: https://github.com/G-Research/ParquetSharp
[ps-writing]: https://g-research.github.io/ParquetSharp/guides/Writing.html
[ps-reading]: https://g-research.github.io/ParquetSharp/guides/Reading.html
[ps-nested]: https://g-research.github.io/ParquetSharp/guides/Nested.html
[ps-arrow]: https://g-research.github.io/ParquetSharp/guides/Arrow.html
[ps-rows]: https://g-research.github.io/ParquetSharp/guides/RowOriented.html
[ps-type-factories]: https://g-research.github.io/ParquetSharp/guides/TypeFactories.html
[ps-encryption]: https://g-research.github.io/ParquetSharp/guides/Encryption.html
[ps-writer-properties]: https://g-research.github.io/ParquetSharp/api/ParquetSharp.WriterPropertiesBuilder.html
[ps-reader-properties]: https://g-research.github.io/ParquetSharp/api/ParquetSharp.ReaderProperties.html
[ps-logical-types]: https://g-research.github.io/ParquetSharp/api/ParquetSharp.LogicalTypeEnum.html
[ps-encodings]: https://g-research.github.io/ParquetSharp/api/ParquetSharp.Encoding.html
[ps-compression]: https://g-research.github.io/ParquetSharp/api/ParquetSharp.Compression.html
[ps-file-metadata]: https://g-research.github.io/ParquetSharp/api/ParquetSharp.FileMetaData.html
[ps-dataframe]: https://www.nuget.org/packages/ParquetSharp.DataFrame
[pn-nuget]: https://www.nuget.org/packages/Parquet.net/
[pn-repo]: https://github.com/aloneguid/parquet-dotnet
[pn-options]: https://github.com/aloneguid/parquet-dotnet/blob/master/src/Parquet/ParquetOptions.cs
[pn-compression]: https://github.com/aloneguid/parquet-dotnet/blob/master/src/Parquet/CompressionMethod.cs
[pn-untyped]: https://aloneguid.github.io/parquet-dotnet/untyped-serializer.html
[pn-append]: https://github.com/aloneguid/parquet-dotnet#appending-to-files
[pn-dataframe]: https://github.com/aloneguid/parquet-dotnet#dataframe-support
[pn-encryption]: https://github.com/aloneguid/parquet-dotnet/issues/726
[pn-variant]: https://github.com/aloneguid/parquet-dotnet/issues/667
[plank-test-project]: https://github.com/Kuinox/Plank/blob/b09d2d535884fb98b088f7811776d2634eb463b1/Plank.Tests/Plank.Tests.csproj
[plank-benchmark-project]: https://github.com/Kuinox/Plank/blob/b09d2d535884fb98b088f7811776d2634eb463b1/Plank.Benchmarks/Plank.Benchmarks.csproj
[plank-project-target]: https://github.com/Kuinox/Plank/blob/b09d2d535884fb98b088f7811776d2634eb463b1/Plank/Plank.csproj#L4
[plank-project-packages]: https://github.com/Kuinox/Plank/blob/b09d2d535884fb98b088f7811776d2634eb463b1/Plank/Plank.csproj#L12
[plank-column-definition]: https://github.com/Kuinox/Plank/blob/b09d2d535884fb98b088f7811776d2634eb463b1/Plank/Schema/ColumnDefinition.cs#L6
[plank-schema]: https://github.com/Kuinox/Plank/blob/b09d2d535884fb98b088f7811776d2634eb463b1/Plank/Schema/ParquetSchema.cs#L8
[plank-physical-types]: https://github.com/Kuinox/Plank/blob/b09d2d535884fb98b088f7811776d2634eb463b1/Plank/Schema/ParquetPhysicalType.cs#L4
[plank-plain-encoding]: https://github.com/Kuinox/Plank/blob/b09d2d535884fb98b088f7811776d2634eb463b1/Plank/Writing/Encoding/PlainEncoding.cs#L13
[plank-reader-physical-dispatch]: https://github.com/Kuinox/Plank/blob/b09d2d535884fb98b088f7811776d2634eb463b1/Plank/Reading/Logical/Internal/ColumnChunkReader.cs#L2172
[plank-logical-types]: https://github.com/Kuinox/Plank/blob/b09d2d535884fb98b088f7811776d2634eb463b1/Plank/Schema/LogicalType.cs#L3
[plank-type-map]: https://github.com/Kuinox/Plank/blob/b09d2d535884fb98b088f7811776d2634eb463b1/Plank/Schema/ParquetTypeMap.cs#L52
[plank-row-generator]: https://github.com/Kuinox/Plank/blob/b09d2d535884fb98b088f7811776d2634eb463b1/Plank.SourceGen/ParquetRowGenerator.cs#L267
[plank-list-interop-tests]: https://github.com/Kuinox/Plank/blob/b09d2d535884fb98b088f7811776d2634eb463b1/Plank.Tests/E2E/ListInteropE2ETests.cs
[plank-nested-interop-tests]: https://github.com/Kuinox/Plank/blob/b09d2d535884fb98b088f7811776d2634eb463b1/Plank.Tests/E2E/NestedInteropE2ETests.cs
[plank-generator-repeated]: https://github.com/Kuinox/Plank/blob/b09d2d535884fb98b088f7811776d2634eb463b1/Plank.SourceGen/ParquetRowGenerator.cs#L547
[plank-reader-repeated-1]: https://github.com/Kuinox/Plank/blob/b09d2d535884fb98b088f7811776d2634eb463b1/Plank/Reading/Logical/Internal/ColumnChunkReader.cs#L1047
[plank-reader-repeated-2]: https://github.com/Kuinox/Plank/blob/b09d2d535884fb98b088f7811776d2634eb463b1/Plank/Reading/Logical/Internal/ColumnChunkReader.cs#L1143
[plank-encoding-compatibility]: https://github.com/Kuinox/Plank/blob/b09d2d535884fb98b088f7811776d2634eb463b1/Plank/Schema/EncodingCompatibility.cs#L14
[plank-reader-encoding-dispatch]: https://github.com/Kuinox/Plank/blob/b09d2d535884fb98b088f7811776d2634eb463b1/Plank/Reading/Logical/Internal/ColumnChunkReader.cs#L2165
[plank-compression-kind]: https://github.com/Kuinox/Plank/blob/b09d2d535884fb98b088f7811776d2634eb463b1/Plank/Schema/CompressionKind.cs#L3
[plank-compression-writer]: https://github.com/Kuinox/Plank/blob/b09d2d535884fb98b088f7811776d2634eb463b1/Plank/Writing/Compression/Compression.cs#L7
[plank-compression-reader]: https://github.com/Kuinox/Plank/blob/b09d2d535884fb98b088f7811776d2634eb463b1/Plank/Reading/ParquetDecompressor.cs#L8
[plank-column-options]: https://github.com/Kuinox/Plank/blob/b09d2d535884fb98b088f7811776d2634eb463b1/Plank/Schema/ColumnOptions.cs#L5
[plank-writer-options]: https://github.com/Kuinox/Plank/blob/b09d2d535884fb98b088f7811776d2634eb463b1/Plank/Writing/ParquetWriterOptions.cs#L6
[plank-data-page-v2]: https://github.com/Kuinox/Plank/blob/b09d2d535884fb98b088f7811776d2634eb463b1/Plank/Writing/RowGroupWriter.cs#L191
[plank-chunk-metadata]: https://github.com/Kuinox/Plank/blob/b09d2d535884fb98b088f7811776d2634eb463b1/Plank/Reading/Logical/ParquetColumnChunkMetadata.cs#L7
[plank-page-pruner]: https://github.com/Kuinox/Plank/blob/b09d2d535884fb98b088f7811776d2634eb463b1/Plank/Reading/Logical/ParquetPagePruner.cs#L9
[plank-public-api]: https://github.com/Kuinox/Plank/blob/b09d2d535884fb98b088f7811776d2634eb463b1/Plank/PublicAPI.Shipped.txt
[plank-page-cursor]: https://github.com/Kuinox/Plank/blob/b09d2d535884fb98b088f7811776d2634eb463b1/Plank/Reading/Physical/ParquetPageCursor.cs#L7
[plank-file-metadata]: https://github.com/Kuinox/Plank/blob/b09d2d535884fb98b088f7811776d2634eb463b1/Plank/Reading/Physical/ParquetFileMetadata.cs#L3
[plank-read-source]: https://github.com/Kuinox/Plank/blob/b09d2d535884fb98b088f7811776d2634eb463b1/Plank/Reading/IParquetReadSource.cs#L6
[plank-writer-reset]: https://github.com/Kuinox/Plank/blob/b09d2d535884fb98b088f7811776d2634eb463b1/Plank/Writing/ParquetWriter.cs#L86
[plank-reader-reset]: https://github.com/Kuinox/Plank/blob/b09d2d535884fb98b088f7811776d2634eb463b1/Plank/Reading/Logical/ParquetReader.cs#L63
[plank-schema-evolution]: https://github.com/Kuinox/Plank/blob/b09d2d535884fb98b088f7811776d2634eb463b1/Plank/Reading/ParquetSchemaEvolutionOptions.cs#L6
[plank-physical-metadata-reader]: https://github.com/Kuinox/Plank/blob/b09d2d535884fb98b088f7811776d2634eb463b1/Plank/Reading/Physical/Internal/PhysicalMetadataThriftReader.cs#L157
[plank-column-attribute]: https://github.com/Kuinox/Plank/blob/b09d2d535884fb98b088f7811776d2634eb463b1/Plank/Schema/ParquetColumnAttribute.cs#L4
