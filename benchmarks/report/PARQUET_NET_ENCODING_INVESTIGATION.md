# Parquet.Net 6 Encoding Investigation

Date: 2026-05-04

Scope: investigate the encoding behavior in `Parquet.Net 6.0.0-pre.8`, compare it with Plank's current write path, and identify implementation candidates. This is a handoff report only; no Plank optimization is implemented here.

Sources inspected:

- Decompiled package: `artifacts/parquetnet_pre8_decompiled`
- Parquet.Net write dispatch: `artifacts/parquetnet_pre8_decompiled/Parquet.File/DataColumnWriter.cs`
- Parquet.Net column packing: `artifacts/parquetnet_pre8_decompiled/Parquet/WritingColumn.cs`
- Parquet.Net dictionary helper: `artifacts/parquetnet_pre8_decompiled/Parquet.Encodings/ParquetDictionaryEncoder.cs`
- Plank write dispatch: `Plank/Writing/Encoding/Encoding.cs`
- Plank reusable dictionary: `Plank/Writing/Encoding/ReusableDictionaryState.cs`

## Executive Summary

The large Parquet.Net 6 benchmark improvement for numeric `dictionary` scenarios is not primarily a faster numeric dictionary encoder. In `6.0.0-pre.8`, `EncodingHint.Dictionary` only packs string-like `ReadOnlyMemory<char>` columns into a dictionary. For primitive numeric columns, Parquet.Net does not build a dictionary page; it falls through to the plain encoder.

That means the benchmark comparison labeled `int32/dictionary`, `int64/dictionary`, `float/dictionary`, and `double/dictionary` is not comparing dictionary encoding against dictionary encoding after the Parquet.Net 6 update. Parquet.Net is mostly writing plain pages there, avoiding dictionary hash construction, dictionary page emission, and index RLE encoding entirely.

Plank currently honors `PlainDictionary` / `RleDictionary` for numeric primitives, especially when `ForceDictionaryPageStrategy` is used by the benchmark. That is semantically stricter, but slower for high-cardinality numeric data.

Main candidate to lift: make dictionary encoding adaptive for non-string primitive types unless explicitly forced, and make the benchmark/report distinguish "requested dictionary" from "actual dictionary". Do not silently change forced dictionary semantics without deciding that compatibility with requested encodings is not important.

## Plain

Parquet.Net behavior:

- `DataColumnWriter.WriteAsync<T>` writes one data page.
- If no dictionary, delta, or byte-stream-split flag is active, it calls `ParquetPlainEncoder.Encode(wc.Values, ms, tse, stats)`.
- Primitive fixed-width values use `MemoryMarshal.AsBytes(data)` and write the byte span directly.
- Strings are handled as `ReadOnlyMemory<char>` and encoded to UTF-8 into a pooled byte buffer before writing length-prefixed byte arrays.

Plank comparison:

- Plank's fixed-width plain encoding is already the right shape: write raw little-endian primitive bytes.
- For string/binary, Plank works with `byte[]` / `ReadOnlyMemory<byte>` and can avoid UTF-16 to UTF-8 work when callers already provide bytes.

Optimization candidates:

- No obvious Parquet.Net plain primitive optimization to lift.
- For string plain, preserve Plank's advantage of accepting pre-encoded UTF-8 bytes.
- If adding a `string` writer fast path, keep the "dictionary on string, encode unique values only" design rather than pre-encoding all input rows when dictionary is likely.

## Dictionary

Parquet.Net behavior:

- `WritingColumn<T>.Pack(options)` only attempts dictionary extraction when all of these are true:
  - field is not an array
  - `typeof(T) == typeof(ReadOnlyMemory<char>)`
  - `options.GetEncodingHint(Field) == EncodingHint.Dictionary`
- Numeric `int`, `long`, `float`, `double`, and `bool` never enter the dictionary extractor.
- For numeric columns with `EncodingHint.Dictionary`, `wc.HasDictionary` stays false, so `DataColumnWriter` writes a plain data page.
- The string dictionary extractor uses `Dictionary<ReadOnlyMemory<char>, int>` with an ordinal comparer, fills an index buffer, and then emits a dictionary page plus RLE/bit-packed indexes.
- `DictionaryEncodingThreshold` can stop string dictionary extraction when uniqueness crosses the threshold. `DictionaryEncodingSampleSize` can also skip full dictionary extraction after sampling.

Plank comparison:

- Plank dictionary encoding is generic and supports primitive numeric values, strings, and byte arrays.
- `ForceDictionaryPageStrategy` always keeps dictionary encoding and never drops it.
- `DefaultStrategy` can drop dictionary in `Maybe` mode, but the encoding matrix benchmark uses `ForceDictionaryPageStrategy` for `dictionary`, so Plank pays full dictionary cost even for high-cardinality numeric data.
- Plank's `ReusableDictionaryState<T>` is already specialized and reusable; the Parquet.Net helper is not obviously faster as a dictionary data structure.

Optimization candidates:

- Add an adaptive "dictionary hint" mode distinct from "force dictionary". In adaptive mode, primitive numeric columns should quickly fall back to plain when cardinality is high or when dictionary encoding is not expected to reduce size.
- Consider matching Parquet.Net's policy for numeric dictionary hints: for non-string primitive columns, dictionary may be treated as a preference, not a requirement.
- Keep `ForceDictionaryPageStrategy` semantics if tests or users explicitly request dictionary output. Existing interop tests assert dictionary pages for `PlainDictionary` / `RleDictionary`.
- Update benchmark reporting to validate actual encodings. Otherwise Parquet.Net 6 will appear faster on "dictionary" scenarios while producing plain pages.
- Add a benchmark variant for "requested dictionary, actual dictionary required" versus "dictionary allowed/adaptive".

Risk:

- Silently falling back from requested dictionary to plain changes observable file metadata. Tests such as encoding compatibility checks expect dictionary encodings to be present.

## Delta Binary Packed

Parquet.Net behavior:

- `DataColumnWriter` enables delta binary packed only when:
  - `options.GetEncodingHint(field) == EncodingHint.DeltaBinaryPacked`
  - `DeltaBinaryPackedEncoder.CanEncode(wc.Values)` returns true
- Supported types include signed/unsigned integer widths, with `ulong` guarded so values must fit the supported range.
- Encoding uses block size `1024` and miniblock size `32`.
- It computes deltas, tracks minimum delta per block, subtracts the minimum from all deltas, computes miniblock bit widths, then bit-packs values.

Plank comparison:

- Plank already supports delta binary packed for integer types.
- In the updated benchmark report, Plank remains faster than Parquet.Net 6 for `int32/delta_binary_packed` and `int64/delta_binary_packed`.

Optimization candidates:

- Do not prioritize porting Parquet.Net's delta binary packed implementation wholesale.
- Review Plank's delta encoder only for small mechanical details if needed: block size/miniblock size parity, avoiding temporary arrays, and bit-width aggregation.
- Preserve Plank's current lead unless a targeted microbenchmark shows a regression.

## Delta Length Byte Array

Parquet.Net behavior:

- The decompiled package contains a decoder for `DeltaLengthByteArrayEncoder`.
- The low-level write path inspected in `DataColumnWriter` does not select delta length byte array for writes.
- The benchmark adapter already treats this as unsupported for Parquet.Net.

Plank comparison:

- Plank supports writing `DeltaLengthByteArray` for byte-array/string-like physical data.

Optimization candidates:

- There is no Parquet.Net write-side optimization to lift for this encoding.
- Keep Plank's implementation as the reference point.
- If optimizing Plank, look locally at prefix/length collection and temporary allocation patterns rather than Parquet.Net.

## Delta Byte Array

Parquet.Net behavior:

- The decompiled package contains a decoder for `DeltaByteArrayEncoder`.
- It decodes prefix lengths using delta binary packed, decodes suffixes using delta length byte array, then reconstructs full values.
- The inspected low-level write path does not select delta byte array for writes.
- The benchmark adapter treats this as unsupported for Parquet.Net.

Plank comparison:

- Plank supports writing `DeltaByteArray` for byte-array/string-like physical data.

Optimization candidates:

- There is no Parquet.Net write-side optimization to lift.
- Keep focus on Plank-local improvements, especially avoiding repeated allocation when constructing prefix/suffix segments.

## Byte Stream Split

Parquet.Net behavior:

- `DataColumnWriter` uses byte-stream-split only when:
  - `options.GetEncodingHint(field) == EncodingHint.ByteSplitStream`
  - `ByteStreamSplitEncoder.IsSupported(typeof(T))`
- Supported types include fixed-width numeric types such as `float`, `double`, `int`, and `long`.
- Encode casts values to bytes with `MemoryMarshal.AsBytes`, then writes byte plane by byte plane.
- It rents a temporary byte buffer sized up to `8192` values and streams each byte lane in chunks.
- Decode has hardware-accelerated paths for `float` and `double`, but encode is scalar/chunked.

Plank comparison:

- Plank supports byte-stream-split for supported fixed-width physical types.
- In prior matrix runs, Parquet.Net benchmark path marked byte-stream-split unsupported because the adapter only exposed plain, dictionary, and delta binary packed options. With 6.x `EncodingHint.ByteSplitStream`, Parquet.Net can likely be benchmarked here if the adapter is extended.

Optimization candidates:

- Compare Plank's byte-stream-split encoder against Parquet.Net's chunked lane-copy algorithm.
- If Plank currently writes one value at a time, port the chunked plane-writing shape: cast to bytes once, rent a lane buffer, and write each byte lane in blocks.
- Add Parquet.Net `byte_stream_split` support to benchmark adapter before drawing conclusions.

## Benchmark Interpretation

The current updated report shows Parquet.Net 6 winning many `dictionary` scenarios, but for primitive numeric types this is largely because Parquet.Net is writing plain pages. That is a valid library behavior if dictionary is considered a hint, but it is not the same as Plank's forced dictionary benchmark.

Recommended benchmark cleanup before implementation:

- Record actual encodings in the benchmark report or compatibility matrix for every run.
- Split benchmark scenarios into:
  - `plain`
  - `dictionary_forced`
  - `dictionary_adaptive`
  - `delta_binary_packed`
  - `delta_length_byte_array`
  - `delta_byte_array`
  - `byte_stream_split`
- For Parquet.Net 6, update the compatibility code to the new API and use `ColumnEncodingHints`.

## Suggested Implementation Order

1. Preserve explicit forced dictionary behavior.
2. Add an adaptive dictionary policy for default dictionary-capable schemas.
3. In adaptive mode, short-circuit high-cardinality primitive numeric dictionary attempts to plain.
4. Add tests proving forced dictionary still emits dictionary pages.
5. Add tests proving adaptive high-cardinality numeric data can fall back to plain.
6. Extend benchmark reporting to show actual output encodings.
7. Re-benchmark numeric dictionary, string dictionary, and byte-stream-split separately.
