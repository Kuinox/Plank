# Encoding write-path performance review

One table per encoder, with a row for every algorithm variant the encoder selects between and every
input shape that steers that selection — plus the two shared passes (definition levels and page
statistics) that run alongside every encoding and turn out to hold two of the largest wins.

## How this was measured

| | |
| --- | --- |
| Machine | Intel Xeon (family 6 model 207, Emerald Rapids), 4 vCPU @ 2.10 GHz, KVM guest |
| ISA | AVX-512F/BW/DQ/VL/VBMI/VBMI2/VNNI + BMI2 — every `Avx512F.IsSupported` path in Plank is live here |
| Caches | L1d 48 KiB/core, L2 2 MiB/core, L3 shared (host-reported 260 MiB) |
| Runtime | .NET 10.0.11, RyuJIT `x86-64-v4`, Release |
| Harness | `dotnet run -c Release --project Plank.Benchmarks -- --encoding-profile` (added by this review) |
| Cross-check | `--filter "*WritePlank*"` end-to-end, plus the existing BenchmarkDotNet encoder benchmarks |

### No hardware counters were available — here is what replaced them

This is a KVM guest with no PMU exposed (`/sys/bus/event_source/devices/` has no `cpu` node and
`linux-tools` will not install), BenchmarkDotNet's `HardwareCounter` diagnoser is Windows/ETW-only so
the `PLANK_BENCHMARK_HARDWARE_COUNTERS` switch in `OptimizationBenchmarkConfig` is inert here, and its
`DisassemblyDiagnoser` reports `No benchmarks were disassembled` on Linux. **So no branch-miss or
cache-miss rate in this report is directly measured.** Every claim about branching or cache behaviour
is inferred from two things that *are* measured:

1. **A copy roofline** (`Span.CopyTo`) taken on the same machine at the same working-set size. An
   encoder reading *n* bytes and writing *m* moves `n+m`; the roofline moves `2×` its copy size, so
   `% roofline` below is `(in+out) / (2 × copy GB/s)`. A kernel near 100% is bandwidth-bound and has
   nothing left to give; one far below it at a DRAM-resident size is spending its time elsewhere.
2. **Scaling across three working-set sizes** (L1-resident 4 K values → 256 K → DRAM-resident 4 M) and
   **input shapes chosen to flip data-dependent branches** — sorted vs random, runs vs literals,
   scattered vs blocked nulls, min-is-zero vs min-is-nonzero. Flat-but-slow across sizes means compute
   or branch bound; degrading with size means the access pattern is the problem. Where a difference
   could have two causes, a control scenario isolates it — see the `Guid` vs `byte[16]` pair and the
   `float (min is 0)` vs `float (min is not 0)` pair, which are the two load-bearing controls here.

Unless stated otherwise all numbers are at the **largest, DRAM-resident size**: 4,194,304 values for
fixed-width encoders, 1,048,576 for byte-array encoders. Copy roofline there: **14.08 GB/s copied =
28.2 GB/s moved**. Full three-size output: `artifacts/benchmarks/encoding-profile.md` — the numbers
below are from that same run.

**Variance.** This is a shared 4-vCPU guest; repeated runs of the harness move individual figures by
up to ~30% (`plain flba-copy byte[16]` measured 2.78 and 3.73 in two runs). Every conclusion below
therefore rests on a **ratio measured within a single run** — an encoder against its own control, or
against the roofline taken in the same run — never on an absolute nanosecond count. Ratios were stable
across four runs; absolutes were not.

---

## Findings, ranked by size of win × ease

| # | Where | Algorithm / input | Now | Problem | Expected after fix |
| --- | --- | --- | --- | --- | --- |
| 1 | definition levels | optional column, scattered nulls | 27.9 ns/row, **1.00 byte/row of levels** | Level writer only ever emits RLE runs, never a bit-packed run. Scattered nulls ⇒ mean run length 2 ⇒ 2 bytes per 2 rows. | ~0.13 byte/row (**8× smaller**), ~8 ns/row (**3.5×**) |
| 2 | statistics | `byte[]` min/max | **12.2 ns/value** | Two `SequenceCompareTo` calls per value. Costs *more than the encoder it decorates* (plain byte-array is 10.7). Largest term in the ~42 ns/row every string encoding lands on. | 3–5× |
| 3 | rle_hybrid | booleans, unpredictable data | 6.55 ns/value, **0.6% of roofline** | Run detection restarts a `Vector512` scan at nearly every position: ~1 full vector scan per value of progress. | ≥10× |
| 4 | byte_stream_split | FLBA / `Guid` | 29 / 55 ns/value, 2–4% of roofline | Transpose runs at full column height: 16 stores `n` bytes apart per value (`Guid`), or 16 full pointer-chase passes (`byte[16]`). | 5–10× |
| 5 | statistics | float/double where min or max is `0` | 1.06 / 1.44 ns/value vs 0.33 / 0.76 | `CanonicalizeSignedZeroBounds` rescans the whole page **scalarly** for a `-0.0` that is almost never there. Any column containing a zero pays it. | **3.2× / 1.9×** |
| 6 | rle_hybrid | dictionary indexes, non-byte-aligned width | 1.70 ns/value vs 0.45 at width 16 | Generic bit packer is a byte-at-a-time loop with a data-dependent inner `while`. Every dictionary whose unique count is not exactly 256 or 65 536 lands here. | 3× |
| 7 | rle_hybrid | boolean literals | 0.57 ns/value | `WriteBooleanBitPackedRun` uses a scalar 8-way OR chain while `PlainEncoding` has an AVX-512 boolean packer in the same folder. | ~15× (to 0.04) |
| 8 | plain, delta_length_byte_array | `byte[]` values | 10.7 / 11.7 ns/value | Two full pointer-chase passes over `byte[][]` (size, then copy). | ~2× |
| 9 | byte_stream_split | `byte`/`ushort` → int32 | 1.49 ns/value, 12–14% of roofline | Scalar lane splitting only; writes 4 lanes `n` bytes apart per value, 3 of them constant zero. | 5× |
| 10 | delta_binary_packed | all shapes | 1.06–3.44 ns/value, 9–25% of roofline | Every value round-trips a 1 KiB stack buffer 3× as `int64` even for int32 input; `GetMinimum`/`GetMaximum` do 8 scalar `GetElement` extracts instead of a vector fold. | 1.3–2× |
| 11 | dictionary | `bool` column | 3.00 ns/row end-to-end vs 0.024 for plain | Boolean fast path exists, but still materialises a 4-byte `int` index per row and RLE-packs it, to produce 1 bit/row. | large |
| 12 | delta_byte_array | `byte[]` values | 16.9–29.8 ns/value | Four passes: prefix scan, DBP(prefix), DBP(suffix), suffix copy — two of them pointer-chasing. | ~1.4× |

Nothing in `plain` fixed-width needs work: 96–110% of the copy roofline for every physical type.

> The "Why Parquet.NET Is Faster — 4 Cases" note in the root `README.md` is now stale on three of its
> four items. The bool/dictionary fast path exists (`WriteBooleanDictionaryPage`), byte_stream_split
> float/double are AVX-512 lane splits sitting at 78–104% of roofline, and plain bool packing is
> AVX-512 and at roofline. Only the general shape of its item 2 still applies, and only to the
> FLBA/`Guid` variants — finding 4.

---

## End to end, per (physical type, encoding)

`EncodingBenchmark.WritePlank`, 1,000,000 rows, no compression, `DefaultJob`. This is the whole write
path — encoder plus statistics plus levels plus page and footer assembly — so it is where the shared
passes surface.

| Physical type | Encoding | µs / 1M rows | ns/row | Encoder alone | Gap explained by |
| --- | --- | ---: | ---: | ---: | --- |
| bool | plain | 24.2 | **0.024** | 0.038 | at the encoder |
| bool | dictionary | 2 997 | 3.00 | 0.94 (w1 indexes) | finding 11 |
| int32 | plain | 774.7 | 0.77 | 0.273 | + 0.14 statistics |
| int32 | byte_stream_split | 1 438 | 1.44 | 0.274 | |
| int32 | delta_binary_packed | 2 138 | 2.14 | 1.06–3.07 | at the encoder |
| int32 | dictionary | 8 426 | 8.43 | — | dictionary build (100 k uniques) |
| int64 | plain | 1 934 | 1.93 | 0.555 | + 0.31 statistics |
| int64 | byte_stream_split | 2 404 | 2.40 | 0.723 | |
| int64 | delta_binary_packed | 2 363 | 2.36 | 1.12–3.44 | at the encoder |
| int64 | dictionary | 9 059 | 9.06 | — | dictionary build |
| float | plain | 2 771 | **2.77** | 0.285 | **+ 1.06 statistics** — finding 5 |
| float | byte_stream_split | 2 305 | 2.31 | 0.272 | |
| float | dictionary | 9 787 | 9.79 | — | |
| double | plain | 4 128 | **4.13** | 0.568 | **+ 1.44 statistics** — finding 5 |
| double | byte_stream_split | 3 637 | 3.64 | 0.727 | |
| double | dictionary | 10 550 | 10.55 | — | |
| string | plain | 42 591 | **42.59** | 10.7 | **+ 12.2 statistics** — finding 2 |
| string | delta_length_byte_array | 41 659 | 41.66 | 11.7 | same |
| string | delta_byte_array | 42 501 | 42.50 | 16.9 | same |
| string | dictionary | 13 091 | 13.09 | — | dictionary wins here (2 048 uniques) |

Three things to read off this table:

- **`float/plain` costs 3.6× `int32/plain`** and `double/plain` 2.1× `int64/plain`, for the same byte
  counts through the same `MemoryMarshal.AsBytes(...).CopyTo(...)`. The encoder profile says the two
  encoders are identical (0.285 vs 0.273 ns/value). The whole difference is finding 5.
- **Every string encoding converges on ~42 ns/row** regardless of which encoder runs, because a shared
  ~12 ns/row statistics pass plus per-value pointer chasing dominates whatever the encoder does. Note
  `string/dictionary` at 13.09 is **3× faster** than `string/plain` — for byte arrays the dictionary is
  the fast path, not the slow one.
- **`float/byte_stream_split` (2.31) beats `float/plain` (2.77)** even though the BSS encoder is
  marginally slower in isolation. Plain fixed-width required columns are split across multiple data
  pages and byte_stream_split is not, so plain pays more per-page overhead. Worth a separate look; not
  counted as a finding because it was not isolated.

---

## plain

| Algorithm | Input | ns/value | in GB/s | out GB/s | % roofline | Read |
| --- | --- | ---: | ---: | ---: | ---: | --- |
| bitpack-simd (AVX-512 mask) | bool alternating | 0.038 | 26.2 | 3.3 | 105% | At roofline |
| bitpack-simd | bool random | 0.036 | 27.5 | 3.4 | 110% | **Data-independent** — random costs the same as constant, so no branch problem |
| bitpack-simd | bool all-true | 0.038 | 26.3 | 3.3 | 105% | At roofline |
| memcpy | int32 random | 0.273 | 14.6 | 14.6 | 104% | At roofline |
| memcpy | int64 random | 0.555 | 14.4 | 14.4 | 102% | At roofline |
| memcpy | float random | 0.285 | 14.0 | 14.0 | 100% | At roofline |
| memcpy | double random | 0.568 | 14.1 | 14.1 | 100% | At roofline |
| widen-byte-simd | byte → int32 | 0.184 | 5.4 | 21.7 | 96% | At roofline (output-bound) |
| widen-uint16-simd | ushort → int32 | 0.200 | 10.0 | 20.0 | 106% | At roofline |
| length-prefixed-copy | `byte[]` ~8 bytes | 10.672 | 0.70 | 1.05 | 6% | **Two pointer-chase passes** — finding 8 |
| length-prefixed-copy | `byte[]` 64 bytes | 23.212 | 2.8 | 2.9 | 20% | Same, amortised over more payload |
| flba-copy | `byte[16]` | 2.777 | 5.8 | 5.8 | 41% | One pass + pointer chase |
| flba-guid | `Guid` | 1.407 | 11.4 | 11.4 | 81% | **Control:** same 16 bytes/value, no pointer chase |

The `Guid` vs `byte[16]` pair is the cleanest evidence in this report: same physical type, same 16
bytes per value, same encoder — the only difference is that `Guid[]` is contiguous while `byte[][]` is
a pointer array. 1.41 vs 2.78 ns/value prices the pointer chase at **~1.4 ns/value**, and `byte[]`
~8 bytes at 10.7 ns/value is roughly *two* such chases plus per-value `Memmove` call overhead on an
8-byte copy.

**Actions.** (a) Fuse the sizing pass and the copy pass in `WriteByteArrayValues` — write into a
growable `BufferWriter` span instead of pre-computing `byteCount`. (b) Inline small copies (≤16 bytes)
rather than calling `Span.CopyTo`/`Memmove` per value. Fixed-width plain needs nothing.

---

## byte_stream_split

| Algorithm | Input | ns/value | in GB/s | out GB/s | % roofline | Read |
| --- | --- | ---: | ---: | ---: | ---: | --- |
| uint32-lanes (AVX-512) | int32 random | 0.274 | 14.6 | 14.6 | 104% | At roofline |
| uint32-lanes (AVX-512) | float random | 0.272 | 14.7 | 14.7 | 104% | At roofline |
| uint64-lanes (AVX-512) | int64 random | 0.723 | 11.1 | 11.1 | 79% | 25% behind the 32-bit path |
| uint64-lanes (AVX-512) | double random | 0.727 | 11.0 | 11.0 | 78% | Same |
| scalar-lanes | byte → int32 | 1.485 | 0.67 | 2.7 | 12% | **No SIMD path at all** — finding 9 |
| scalar-lanes | ushort → int32 | 1.487 | 1.3 | 2.7 | 14% | Same |
| flba-lane-outer | `byte[16]` | 29.286 | 0.55 | 0.55 | 4% | **16 pointer-chase passes** — finding 4 |
| flba-value-outer | `Guid` | 54.501 | 0.29 | 0.29 | 2% | **16 scattered stores per value** — finding 4 |

**32-bit vs 64-bit lanes (104% vs 79%).** `WriteUInt32Lanes` narrows `Vector512<uint>` to
`Vector128<byte>` and stores all 16 bytes of a lane with one `StoreUnsafe`. `WriteUInt64Lanes` narrows
`Vector512<ulong>` to 8 useful bytes and stores them with `Sse2.StoreScalar` — an 8-byte store per
lane, 8 lanes, per 8 values: twice the store count per byte produced. Processing **two**
`Vector512<ulong>` per iteration and combining the halves into one 16-byte store per lane would match
the 32-bit path.

**FLBA and `Guid` are the worst encoder numbers in the report.** Two variants, one root cause — the
transpose runs at full column height instead of in cache-sized tiles:

- `Guid` (`flba-value-outer`, 55 ns/value): value-outer, lane-inner, writing
  `guidDestination[(lane * count) + i]` across 16 lanes. At 1 M values those 16 stores are 1 MB apart,
  so every value touches 16 distinct cache lines and each is evicted long before the next value reuses
  it. ~16 cache misses per value.
- `byte[16]` (`flba-lane-outer`, 29 ns/value): the loop is already inverted to lane-outer, which fixes
  the *store* pattern, but now each of the 16 lane passes re-walks the whole `byte[][]` pointer array
  and dereferences 1 M separate heap objects — 16 full pointer-chase passes.

**Action.** Transpose in tiles: walk the input in blocks of ~64 values and write all lanes per block.
Per-block working set is `64 × valueLength` (1 KiB for a 16-byte type), which stays in L1, and each
input value is dereferenced exactly once. One shared loop shape fixes both variants. Target is
`plain flba-copy`, 2.8 ns/value.

**Action.** For `byte`/`ushort` → int32, widen to `uint` and reuse `WriteUInt32Lanes`. For `byte`
input, lanes 1–3 are all zero and can be one `Clear()` instead of three stores per value.

---

## delta_binary_packed

| Algorithm | Input | ns/value | in GB/s | out bytes/value | % roofline | Read |
| --- | --- | ---: | ---: | ---: | ---: | --- |
| pack-narrow (widths 1–4) | int32 constant delta | 1.061 | 3.8 | ~0 | 13% | Bit width 0 — this is prepare+normalise alone |
| pack-13bit (specialised) | int32 timestamp-like | 1.615 | 2.5 | 1.63 | 12% | |
| pack-generic | int32 random | 3.071 | 1.3 | ~1.3 | 9% | Generic bit packer |
| pack-narrow | int64 constant delta | 1.122 | 7.1 | ~0 | 25% | |
| pack-13bit | int64 timestamp-like | 2.017 | 4.0 | 1.63 | 17% | |
| pack-generic | int64 random | 3.436 | 2.3 | ~2.3 | 17% | |

The `constant delta` rows are the diagnostic: bit width is **0**, so *no packing happens at all*, and
1.06 ns/value (≈2.2 cycles) is the cost of `PrepareInt32Block` + `NormalizeDeltasVectorized` alone.
That is a lot for a fully vectorised delta-and-min pass, and it is structural:

1. **Everything is widened to int64, including int32 input.** `PrepareInt32Block` sign-extends
   `Vector256<int>` → `Vector512<long>` and stores 8-byte deltas into a 128-entry (1 KiB) stack
   buffer; `NormalizeDeltasVectorized` re-reads that buffer, subtracts the min and writes it back; the
   packer reads it a third time. An int32 column moves **24 bytes of L1 traffic per 4-byte value.**
   Deltas that fit in 32 bits could stay in `Vector512<int>` lanes and halve that.
2. **`GetMinimum` and `GetMaximum` are scalar extract loops** — `for (i = 1..Count) result =
   Min(result, values.GetElement(i))`, 8 extracts each. `GetMaximum` runs once per mini-block, so
   **40 extracts per 128 values** ≈ 0.31 extracts/value at ~3 cycles ≈ a meaningful slice of the
   2.2 cycles/value. Replacing both with a three-step vector fold (`Vector512` → `Vector256` →
   `Vector128` → scalar) is contained, low-risk, and the easiest win in this encoder.
3. **`PackUnsignedValues` is a serial bit buffer** — `low`/`high`/`bufferedBits` carry a loop-borne
   dependency across all 32 values of a mini-block. The codebase already knows the fix: at width *w*,
   each group of 8 values is exactly *w* bytes, which is what `Pack13BitUnsignedValues` exploits to
   emit fixed unaligned 64/32-bit stores with no state machine. Generalising that shape to all widths
   removes the dependency chain — and is the same fix as finding 6.

---

## rle_hybrid (dictionary indexes and RLE booleans)

| Algorithm | Input | ns/value | in GB/s | out GB/s | % roofline | Read |
| --- | --- | ---: | ---: | ---: | ---: | --- |
| rle-runs | indexes, long runs | 0.235 | 17.0 | ~0 | 60% | Run detection is fine when runs are real |
| bitpack-w16 (byte-aligned) | 65 536 unique | 0.448 | 8.9 | 4.5 | 48% | Fast path: `WriteByteAlignedLiteralsUnchecked` |
| bitpack-w8 (byte-aligned) | 256 unique | 0.533 | 7.5 | 1.9 | 33% | Fast path |
| bitpack-w1 | 2 unique | 0.944 | 4.2 | 0.13 | 16% | Not byte-aligned → generic packer |
| bitpack-w11 (**not** aligned) | 2 048 unique | 1.701 | 2.4 | 0.8 | 11% | **3.8× the width-16 cost** — finding 6 |
| bool-rle | bool all-true | 0.036 | 27.9 | ~0 | 99% | At roofline |
| bool-bitpack | bool alternating | 0.566 | 1.8 | 0.22 | 7% | **Scalar packer** — finding 7 |
| bool-bitpack | bool random | 6.547 | 0.15 | 0.02 | **0.6%** | **Scan restart** — finding 3 |

**Finding 6 — the byte-alignment cliff is the common case, not the rare one.** Dictionary index bit
width is `GetBitWidthFromMaxValue(uniqueCount - 1)`, a multiple of 8 only when the dictionary holds
exactly 256 or 65 536 entries. Every other dictionary takes `WriteBitPackedRunUnchecked`'s generic
tail:

```csharp
bitBuffer |= (ulong)(value & mask) << bufferedBits;
bufferedBits += bitWidth;
while (bufferedBits >= 8) { destination[outputOffset++] = (byte)bitBuffer; bitBuffer >>= 8; bufferedBits -= 8; }
```

one bounds-checked byte store at a time, an inner loop whose trip count alternates 1/2 in a
width-dependent pattern, all on a serial `bitBuffer` dependency. 1.70 vs 0.45 ns/value against the
byte-aligned path is what that costs. The fix is the one `Pack13BitUnsignedValues` already uses:
**8 values at width *w* are exactly *w* bytes**, so a group of 8 can be built with fixed shifts into
one or two `ulong`s and emitted with `Unsafe.WriteUnaligned` — no inner loop, no per-byte bounds
check, no carry between groups.

**Finding 7 — a scalar boolean packer sitting next to an AVX-512 one.**
`RleBitPackingHybridEncoding.WriteBooleanBitPackedRun` packs with eight `? 1 : 0` shifts per byte,
while `PlainEncoding.WriteBooleanValues` in the same folder packs booleans with
`Vector512.Equals(...).ExtractMostSignificantBits()`. Measured on the same data: 0.566 vs
0.038 ns/value. Hoisting the SIMD packer into a shared helper is mechanical.

**Finding 3 — the worst throughput measured anywhere: 6.55 ns/value, 0.6% of roofline.** For
unpredictable booleans `WriteBooleans` restarts the vector machinery at nearly every position:
`SkipDistinctAdjacentBooleanValues` issues two `Vector512` loads, a compare, a mask extract and a
`tzcnt` to advance ~2 positions, then `CountBooleanRunLength` does the same again to discover a run of
length 2. A 64-byte-wide scan yields about *one* value of progress, and the run-vs-literal branch is
unpredictable by construction. The proof that this is scan cost and not packing cost is in the table:
the same packer on `bool alternating` — where the scan strides full vectors because no two neighbours
are ever equal — costs 0.566 ns/value, **12× less**.

**Action.** Compute the neighbour-equality mask once per 64-byte block
(`Vector512.Equals(current, next).ExtractMostSignificantBits()` → one `ulong`) and find runs of ≥7
consecutive set bits inside that mask with bit tricks, instead of re-issuing a vector scan per
position. That turns ~1 vector scan per value into 1 per 64 values.

The integer variants do **not** have this problem — `bitpack-w8` at 0.533 ns/value is packing-bound,
not scan-bound — because the scan strides full vectors whenever adjacent duplicates are rare. The
boolean case is pathological precisely because duplicates are common.

---

## delta_length_byte_array

| Algorithm | Input | ns/value | in GB/s | out GB/s | % roofline | Read |
| --- | --- | ---: | ---: | ---: | ---: | --- |
| lengths + copy | `byte[]` ~8 bytes | 11.730 | 0.64 | ~0.7 | 5% | Two pointer-chase passes |
| lengths + copy | `byte[]` 64 bytes | 23.308 | 2.8 | ~2.8 | 20% | Same, amortised |

Structurally identical to plain byte arrays (finding 8): pass 1 walks `byte[][]` collecting lengths
into scratch, pass 2 walks it again to copy payloads. Because pass 1 already touched every value's
header *and* first cache line, pass 2 re-misses on everything at 1 M values.

**Action.** Copy payload bytes into a scratch buffer during pass 1, then bulk-`memcpy` scratch after
the DBP-encoded lengths: one pointer-chase pass plus one linear copy, instead of two pointer-chase
passes.

---

## delta_byte_array

| Algorithm | Input | ns/value | in GB/s | out GB/s | % roofline | Read |
| --- | --- | ---: | ---: | ---: | ---: | --- |
| prefix + suffix | `byte[]` ~8 bytes | 16.866 | 0.44 | ~0.5 | 3% | Four passes |
| prefix + suffix | `byte[]` shared prefix | 24.422 | 1.9 | ~0.5 | 9% | Prefix scan pays off in size, not time |
| prefix + suffix | `byte[]` 64 bytes | 29.815 | 2.2 | ~2.2 | 15% | |

Four passes per column: prefix/suffix length scan, `DeltaBinaryPackedEncoding.WriteInt32` over prefix
lengths, again over suffix lengths, then the suffix copy. Two of those are pointer-chasing walks of
`byte[][]`, and the two DBP calls cost ~1.1 ns/value each on their own (see the DBP table), which
alone accounts for ~2.2 of the ~16.9 ns.

**Action.** Same fusion as DLBA — stage suffix bytes during the prefix scan. Also the encoder that
benefits most from finding 10, since it pays the DBP cost twice per column.

---

## Definition levels (shared pass, every optional column)

| Input | ns/row | total bytes/row | of which levels | Read |
| --- | ---: | ---: | ---: | --- |
| optional int32, no nulls | 3.129 | 4.00 | ~0 | One run for the whole column |
| optional int32, nulls in blocks of 64 | 7.796 | 2.05 | ~0.05 | Runs are long, RLE is the right shape |
| optional int32, 50% scattered nulls | **27.904** | 3.00 | **~1.00** | **Finding 1** |

`WriteOptionalDefinitionLevels` builds runs and calls `WriteLevelRun` for each — and `WriteLevelRun`
*only* ever emits an RLE run (varint header + one value byte). There is no bit-packed-run branch in
the level writer at all, even though `RleBitPackingHybridEncoding` implements one 700 lines away.

With scattered nulls the mean run length is 2, so the writer emits **2 bytes per 2 rows = 1.00
byte/row** where a bit-packed run would emit 1 bit/row plus headers ≈ 0.13 byte/row. That is measured,
not modelled: total output is 3.00 bytes/row for a column whose present values are 0.5 × 4 = 2.00
bytes/row.

So this is **both** an ~8× size regression on the definition-level stream **and** a ~3.5× time
regression at the same null density, just differently arranged (27.9 vs 7.8 ns/row) — and scattered
nulls are the common real-world shape, not the exotic one.

Two compounding details in the same path:

- `WriteUnsignedVarInt` (the copy local to `Encoding.cs`, not the one in
  `RleBitPackingHybridEncoding`) calls `writer.GetSpan(1)` and `writer.Advance(1)` **per byte**, so
  every run header pays the full segment-check path once per byte.
- Optional columns make three separate branchy passes over the same nullable array —
  `CountPresentValues`, `CopyPresentValues` and `WriteOptionalDefinitionLevels` — each with a
  per-value unpredictable branch. They can be fused into one pass producing the count, the dense
  values and the level runs together.

**Action.** Give the level writer the same run-vs-bit-packed decision the dictionary index writer
already makes: buffer levels, and emit a bit-packed run when the pending literal group is shorter than
the 8-value RLE threshold. Highest-value item in this report.

---

## Page statistics (shared pass, every column)

| Input | ns/value | in GB/s | Read |
| --- | ---: | ---: | --- |
| int32 | 0.144 | 27.8 | Vectorised, memory-bound |
| int64 | 0.305 | 26.3 | Vectorised, memory-bound |
| float, **min is not 0** | 0.333 | 12.0 | Control |
| float, **min is 0** | **1.059** | 3.8 | **3.2× the control** — finding 5 |
| double, **min is not 0** | 0.756 | 10.6 | Control |
| double, **min is 0** | **1.443** | 5.5 | **1.9× the control** |
| `byte[]` ~8 bytes | **12.224** | 0.65 | **Costs more than the encoder** — finding 2 |

**Finding 5.** `CanonicalizeSignedZeroBounds` exists to report `-0.0`/`+0.0` bounds correctly. But it
triggers whenever `min == 0` or `max == 0`, and then walks the *entire page scalarly* until it can
prove the answer — and its early-out requires *finding* a negative zero, so a page containing `+0.0`
and no `-0.0` (overwhelmingly the common case) always runs to completion. The two float rows above
differ only in whether the data contains a zero; everything else is identical. This is the whole
reason `float/plain` costs 3.6× `int32/plain` end to end.

**Action.** Fold the two questions the canonicalisation actually asks — "is any value `-0.0`?" and "is
any value `+0.0`?" — into the existing vectorised min/max pass. Both are single-bit sign tests on
values already loaded, so the extra pass disappears rather than merely being vectorised.

**Finding 2.** `byte[]` min/max runs two `SequenceCompareTo` calls per value against the running min
and max. At 12.2 ns/value it costs **more than the plain byte-array encoder it decorates** (10.7), and
it is the single largest term in the ~42 ns/row that *every* string encoding converges on.

**Action.** Add a cheap reject before the call: compare the first 8 bytes as a `ulong` (with length)
and fall back to `SequenceCompareTo` only when that is inconclusive. Almost every value loses against
both the running min and the running max on its first word, so the call disappears for nearly all of
them.

---

## Anomaly worth re-checking on a machine with a PMU

`byte_stream_split` on `long[]` at exactly **4 096 values** measured 5.56 / 5.86 / 5.90 ns/value across
three independent runs, while `double[]` through the identical `WriteUInt64Lanes` kernel measured
0.67–0.74 at the same size — and both converge to ~0.73 at 256 K and 4 M values. Two control scenarios
were added to separate the cause: the *same array* re-typed through the double entry point runs at
0.686, and a *different array* through the int64 entry point runs at 0.724. So the slowdown follows
neither the code path nor the buffer alone, only their original pairing, and only at one size.

No structural explanation fits that, and this is a 4-vCPU shared KVM guest, so it is most likely an
addressing/aliasing artifact of this environment rather than a property of the encoder. It is **not**
counted as a finding. One run of `perf stat -e L1-dcache-load-misses` on bare metal would settle it.

---

## Reproducing

```bash
# per-algorithm throughput profile at three working-set sizes (every table above)
dotnet run -c Release --project Plank.Benchmarks -- --encoding-profile

# end-to-end, per (physical type, encoding), 1M rows
dotnet run -c Release --project Plank.Benchmarks -- --filter "*WritePlank*"
```

`--encoding-profile` writes `artifacts/benchmarks/encoding-profile.md` with all three working-set
sizes; the tables above quote the largest size only.
