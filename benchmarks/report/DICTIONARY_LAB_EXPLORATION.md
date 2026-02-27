# Dictionary Lab Exploration

State source: `/home/kuinox/dev/Plank/BenchmarkDotNet.Artifacts/dictionary-lab-explorer.baselinefix.v1.json`  
Last update: 2026-02-26

## Current Leaderboard

| Rank | Node | Mean merged speedup vs .NET | Mean string speedup | Mean utf8 speedup | Samples |
|---|---|---:|---:|---:|---:|
| 1 | `hash.linear.packed.ultra-sparse.touched.v1` | 1.370x | 1.101x | 1.718x | 14 |
| 2 | `hash.linear.packed.ultra-sparse.v1` | 1.369x | 1.056x | 1.777x | 15 |
| 3 | `hash.simd-group.ultra-sparse.touched.v1` | 1.304x | 0.947x | 1.846x | 11 |
| 4 | `hash.linear.tagged.ultra-sparse.v1` | 1.299x | 0.951x | 1.781x | 12 |
| 5 | `hash.linear.tagged.ultra-sparse.tuned.unrolled4.v1` | 1.274x | 0.961x | 1.696x | 12 |
| 6 | `hash.simd-group.ultra-sparse.v1` | 1.256x | 0.976x | 1.622x | 10 |
| 7 | `hash.linear.tagged.ultra-sparse.tuned.length-prefilter.v1` | 1.228x | 0.892x | 1.718x | 10 |
| 8 | `hash.linear.tagged.ultra-sparse.tuned.touched.v1` | 1.228x | 0.878x | 1.758x | 10 |
| 9 | `hash.linear.tagged.ultra-sparse.tuned.v1` | 1.189x | 0.884x | 1.626x | 7 |
| 10 | `hash.robinhood.tagged.ultra-sparse.tuned.swap-reduced.probe-reuse.v1` | 1.189x | 0.890x | 1.594x | 7 |
| 11 | `hash.robinhood.tagged.ultra-sparse.tuned.swap-reduced.probe-reuse.meta-packed.v1` | 1.183x | 0.901x | 1.556x | 9 |
| 12 | `hash.linear.tagged.ultra-sparse.tuned.length-prefilter.noinline.v1` | 1.181x | 0.894x | 1.575x | 9 |
| 13 | `hash.cuckoo2.cached-hash.v1` | 1.173x | 0.883x | 1.561x | 12 |
| 14 | `hash.linear.tagged.sparse.v1` | 1.151x | 0.896x | 1.480x | 8 |
| 15 | `hash.linear.touched-reset.tagged.v1` | 1.135x | 0.875x | 1.477x | 6 |
| 16 | `hash.linear.tagged.v1` | 1.133x | 0.865x | 1.488x | 6 |
| 17 | `hash.cuckoo2.cached-hash.length-prefilter.v1` | 1.109x | 0.864x | 1.426x | 6 |
| 18 | `hash.cuckoo2.v1` | 1.084x | 0.836x | 1.411x | 8 |
| 19 | `hash.linear.half-load.v1` | 1.068x | 0.825x | 1.391x | 5 |
| 20 | `hash.linear.tagged.half-load.v1` | 1.065x | 0.868x | 1.316x | 5 |
| 21 | `hash.robinhood.tagged.v1` | 1.056x | 0.836x | 1.335x | 5 |
| 22 | `hash.linear.tagged.sparse.touched.v1` | 1.037x | 0.785x | 1.372x | 6 |
| 23 | `hash.linear.v1` | 1.023x | 0.792x | 1.320x | 5 |
| 24 | `hash.linear.tagged.half-load.touched.v1` | 1.017x | 0.766x | 1.356x | 6 |
| 25 | `hash.linear.tagged.ultra-sparse.tuned.noinline.v1` | 0.972x | 0.788x | 1.203x | 4 |
| 26 | `hash.robinhood.tagged.ultra-sparse.tuned.v1` | 0.961x | 0.767x | 1.206x | 4 |
| 27 | `hash.linear.tagged.ultra-sparse.tuned.miss-bounded.unrolled4.v1` | 0.950x | 0.749x | 1.208x | 4 |
| 28 | `hash.chained.v1` | 0.933x | 0.696x | 1.254x | 24 |
| 29 | `hash.robinhood.tagged.sparse.v1` | 0.929x | 0.732x | 1.181x | 4 |
| 30 | `hash.robinhood.tagged.ultra-sparse.v1` | 0.841x | 0.699x | 1.015x | 3 |
| 31 | `hash.linear.touched-reset.tagged.half-load.v1` | 0.819x | 0.652x | 1.030x | 4 |
| 32 | `hash.bucket4.v1` | 0.814x | 0.634x | 1.079x | 24 |
| 33 | `hash.linear.tagged.ultra-sparse.tuned.miss-bounded.v1` | 0.812x | 0.622x | 1.062x | 3 |
| 34 | `hash.linear.touched-reset.v1` | 0.626x | 0.476x | 0.823x | 2 |
| 35 | `hash.linear.tagged.ultra-sparse.tuned.touched.fingerprint16.v1` | 0.621x | 0.470x | 0.822x | 2 |
| 36 | `hash.robinhood.tagged.ultra-sparse.tuned.swap-reduced.v1` | 0.619x | 0.449x | 0.856x | 2 |
| 37 | `hash.linear.tagged.ultra-sparse.tuned.fingerprint16.v1` | 0.608x | 0.451x | 0.820x | 2 |
| 38 | `hash.robinhood.tagged.ultra-sparse.tuned.swap-reduced.probe-reuse.meta-packed.reset-touched.v1` | 0.586x | 0.474x | 0.726x | 2 |
| 39 | `hash.robinhood.tagged.sparse.mixed-uniqueness-10-70.meta.v1` | 0.527x | 0.405x | 0.684x | 2 |
| 40 | `hash.robinhood.v1` | 0.469x | 0.368x | 0.597x | 2 |
| 41 | `tree.trie.v1` | 0.229x | 0.170x | 0.309x | 24 |
| 42 | `tree.sorted-array.length-first.first-unit.v1` | 0.043x | 0.034x | 0.055x | 4 |
| 43 | `tree.sorted-array.length-first.v1` | 0.037x | 0.031x | 0.044x | 4 |
| 44 | `tree.sorted-array.length-first.first-unit.length-ranges.btree.v1` | 0.023x | 0.019x | 0.027x | 4 |
| 45 | `tree.sorted-array.v1` | 0.019x | 0.018x | 0.021x | 4 |
| 46 | `tree.sorted-array.length-first.first-unit.length-ranges.v1` | 0.017x | 0.015x | 0.020x | 4 |
| 47 | `tree.sorted-array.first-unit.v1` | 0.010x | 0.009x | 0.012x | 4 |

## String Leaderboard

| Rank | Node | Mean string speedup vs .NET | Mean utf8 speedup | Mean merged speedup | Samples |
|---|---|---:|---:|---:|---:|
| 1 | `hash.linear.packed.ultra-sparse.touched.v1` | 1.101x | 1.718x | 1.370x | 14 |
| 2 | `hash.linear.packed.ultra-sparse.v1` | 1.056x | 1.777x | 1.369x | 15 |
| 3 | `hash.simd-group.ultra-sparse.v1` | 0.976x | 1.622x | 1.256x | 10 |
| 4 | `hash.linear.tagged.ultra-sparse.tuned.unrolled4.v1` | 0.961x | 1.696x | 1.274x | 12 |
| 5 | `hash.linear.tagged.ultra-sparse.v1` | 0.951x | 1.781x | 1.299x | 12 |
| 6 | `hash.simd-group.ultra-sparse.touched.v1` | 0.947x | 1.846x | 1.304x | 11 |
| 7 | `hash.robinhood.tagged.ultra-sparse.tuned.swap-reduced.probe-reuse.meta-packed.v1` | 0.901x | 1.556x | 1.183x | 9 |
| 8 | `hash.linear.tagged.sparse.v1` | 0.896x | 1.480x | 1.151x | 8 |
| 9 | `hash.linear.tagged.ultra-sparse.tuned.length-prefilter.noinline.v1` | 0.894x | 1.575x | 1.181x | 9 |
| 10 | `hash.linear.tagged.ultra-sparse.tuned.length-prefilter.v1` | 0.892x | 1.718x | 1.228x | 10 |
| 11 | `hash.robinhood.tagged.ultra-sparse.tuned.swap-reduced.probe-reuse.v1` | 0.890x | 1.594x | 1.189x | 7 |
| 12 | `hash.linear.tagged.ultra-sparse.tuned.v1` | 0.884x | 1.626x | 1.189x | 7 |
| 13 | `hash.cuckoo2.cached-hash.v1` | 0.883x | 1.561x | 1.173x | 12 |
| 14 | `hash.linear.tagged.ultra-sparse.tuned.touched.v1` | 0.878x | 1.758x | 1.228x | 10 |
| 15 | `hash.linear.touched-reset.tagged.v1` | 0.875x | 1.477x | 1.135x | 6 |
| 16 | `hash.linear.tagged.half-load.v1` | 0.868x | 1.316x | 1.065x | 5 |
| 17 | `hash.linear.tagged.v1` | 0.865x | 1.488x | 1.133x | 6 |
| 18 | `hash.cuckoo2.cached-hash.length-prefilter.v1` | 0.864x | 1.426x | 1.109x | 6 |
| 19 | `hash.cuckoo2.v1` | 0.836x | 1.411x | 1.084x | 8 |
| 20 | `hash.robinhood.tagged.v1` | 0.836x | 1.335x | 1.056x | 5 |
| 21 | `hash.linear.half-load.v1` | 0.825x | 1.391x | 1.068x | 5 |
| 22 | `hash.linear.v1` | 0.792x | 1.320x | 1.023x | 5 |
| 23 | `hash.linear.tagged.ultra-sparse.tuned.noinline.v1` | 0.788x | 1.203x | 0.972x | 4 |
| 24 | `hash.linear.tagged.sparse.touched.v1` | 0.785x | 1.372x | 1.037x | 6 |
| 25 | `hash.robinhood.tagged.ultra-sparse.tuned.v1` | 0.767x | 1.206x | 0.961x | 4 |
| 26 | `hash.linear.tagged.half-load.touched.v1` | 0.766x | 1.356x | 1.017x | 6 |
| 27 | `hash.linear.tagged.ultra-sparse.tuned.miss-bounded.unrolled4.v1` | 0.749x | 1.208x | 0.950x | 4 |
| 28 | `hash.robinhood.tagged.sparse.v1` | 0.732x | 1.181x | 0.929x | 4 |
| 29 | `hash.robinhood.tagged.ultra-sparse.v1` | 0.699x | 1.015x | 0.841x | 3 |
| 30 | `hash.chained.v1` | 0.696x | 1.254x | 0.933x | 24 |
| 31 | `hash.linear.touched-reset.tagged.half-load.v1` | 0.652x | 1.030x | 0.819x | 4 |
| 32 | `hash.bucket4.v1` | 0.634x | 1.079x | 0.814x | 24 |
| 33 | `hash.linear.tagged.ultra-sparse.tuned.miss-bounded.v1` | 0.622x | 1.062x | 0.812x | 3 |
| 34 | `hash.linear.touched-reset.v1` | 0.476x | 0.823x | 0.626x | 2 |
| 35 | `hash.robinhood.tagged.ultra-sparse.tuned.swap-reduced.probe-reuse.meta-packed.reset-touched.v1` | 0.474x | 0.726x | 0.586x | 2 |
| 36 | `hash.linear.tagged.ultra-sparse.tuned.touched.fingerprint16.v1` | 0.470x | 0.822x | 0.621x | 2 |
| 37 | `hash.linear.tagged.ultra-sparse.tuned.fingerprint16.v1` | 0.451x | 0.820x | 0.608x | 2 |
| 38 | `hash.robinhood.tagged.ultra-sparse.tuned.swap-reduced.v1` | 0.449x | 0.856x | 0.619x | 2 |
| 39 | `hash.robinhood.tagged.sparse.mixed-uniqueness-10-70.meta.v1` | 0.405x | 0.684x | 0.527x | 2 |
| 40 | `hash.robinhood.v1` | 0.368x | 0.597x | 0.469x | 2 |
| 41 | `tree.trie.v1` | 0.170x | 0.309x | 0.229x | 24 |
| 42 | `tree.sorted-array.length-first.first-unit.v1` | 0.034x | 0.055x | 0.043x | 4 |
| 43 | `tree.sorted-array.length-first.v1` | 0.031x | 0.044x | 0.037x | 4 |
| 44 | `tree.sorted-array.length-first.first-unit.length-ranges.btree.v1` | 0.019x | 0.027x | 0.023x | 4 |
| 45 | `tree.sorted-array.v1` | 0.018x | 0.021x | 0.019x | 4 |
| 46 | `tree.sorted-array.length-first.first-unit.length-ranges.v1` | 0.015x | 0.020x | 0.017x | 4 |
| 47 | `tree.sorted-array.first-unit.v1` | 0.009x | 0.012x | 0.010x | 4 |

## Utf8 Leaderboard

| Rank | Node | Mean utf8 speedup vs .NET | Mean string speedup | Mean merged speedup | Samples |
|---|---|---:|---:|---:|---:|
| 1 | `hash.simd-group.ultra-sparse.touched.v1` | 1.846x | 0.947x | 1.304x | 11 |
| 2 | `hash.linear.tagged.ultra-sparse.v1` | 1.781x | 0.951x | 1.299x | 12 |
| 3 | `hash.linear.packed.ultra-sparse.v1` | 1.777x | 1.056x | 1.369x | 15 |
| 4 | `hash.linear.tagged.ultra-sparse.tuned.touched.v1` | 1.758x | 0.878x | 1.228x | 10 |
| 5 | `hash.linear.packed.ultra-sparse.touched.v1` | 1.718x | 1.101x | 1.370x | 14 |
| 6 | `hash.linear.tagged.ultra-sparse.tuned.length-prefilter.v1` | 1.718x | 0.892x | 1.228x | 10 |
| 7 | `hash.linear.tagged.ultra-sparse.tuned.unrolled4.v1` | 1.696x | 0.961x | 1.274x | 12 |
| 8 | `hash.linear.tagged.ultra-sparse.tuned.v1` | 1.626x | 0.884x | 1.189x | 7 |
| 9 | `hash.simd-group.ultra-sparse.v1` | 1.622x | 0.976x | 1.256x | 10 |
| 10 | `hash.robinhood.tagged.ultra-sparse.tuned.swap-reduced.probe-reuse.v1` | 1.594x | 0.890x | 1.189x | 7 |
| 11 | `hash.linear.tagged.ultra-sparse.tuned.length-prefilter.noinline.v1` | 1.575x | 0.894x | 1.181x | 9 |
| 12 | `hash.cuckoo2.cached-hash.v1` | 1.561x | 0.883x | 1.173x | 12 |
| 13 | `hash.robinhood.tagged.ultra-sparse.tuned.swap-reduced.probe-reuse.meta-packed.v1` | 1.556x | 0.901x | 1.183x | 9 |
| 14 | `hash.linear.tagged.v1` | 1.488x | 0.865x | 1.133x | 6 |
| 15 | `hash.linear.tagged.sparse.v1` | 1.480x | 0.896x | 1.151x | 8 |
| 16 | `hash.linear.touched-reset.tagged.v1` | 1.477x | 0.875x | 1.135x | 6 |
| 17 | `hash.cuckoo2.cached-hash.length-prefilter.v1` | 1.426x | 0.864x | 1.109x | 6 |
| 18 | `hash.cuckoo2.v1` | 1.411x | 0.836x | 1.084x | 8 |
| 19 | `hash.linear.half-load.v1` | 1.391x | 0.825x | 1.068x | 5 |
| 20 | `hash.linear.tagged.sparse.touched.v1` | 1.372x | 0.785x | 1.037x | 6 |
| 21 | `hash.linear.tagged.half-load.touched.v1` | 1.356x | 0.766x | 1.017x | 6 |
| 22 | `hash.robinhood.tagged.v1` | 1.335x | 0.836x | 1.056x | 5 |
| 23 | `hash.linear.v1` | 1.320x | 0.792x | 1.023x | 5 |
| 24 | `hash.linear.tagged.half-load.v1` | 1.316x | 0.868x | 1.065x | 5 |
| 25 | `hash.chained.v1` | 1.254x | 0.696x | 0.933x | 24 |
| 26 | `hash.linear.tagged.ultra-sparse.tuned.miss-bounded.unrolled4.v1` | 1.208x | 0.749x | 0.950x | 4 |
| 27 | `hash.robinhood.tagged.ultra-sparse.tuned.v1` | 1.206x | 0.767x | 0.961x | 4 |
| 28 | `hash.linear.tagged.ultra-sparse.tuned.noinline.v1` | 1.203x | 0.788x | 0.972x | 4 |
| 29 | `hash.robinhood.tagged.sparse.v1` | 1.181x | 0.732x | 0.929x | 4 |
| 30 | `hash.bucket4.v1` | 1.079x | 0.634x | 0.814x | 24 |
| 31 | `hash.linear.tagged.ultra-sparse.tuned.miss-bounded.v1` | 1.062x | 0.622x | 0.812x | 3 |
| 32 | `hash.linear.touched-reset.tagged.half-load.v1` | 1.030x | 0.652x | 0.819x | 4 |
| 33 | `hash.robinhood.tagged.ultra-sparse.v1` | 1.015x | 0.699x | 0.841x | 3 |
| 34 | `hash.robinhood.tagged.ultra-sparse.tuned.swap-reduced.v1` | 0.856x | 0.449x | 0.619x | 2 |
| 35 | `hash.linear.touched-reset.v1` | 0.823x | 0.476x | 0.626x | 2 |
| 36 | `hash.linear.tagged.ultra-sparse.tuned.touched.fingerprint16.v1` | 0.822x | 0.470x | 0.621x | 2 |
| 37 | `hash.linear.tagged.ultra-sparse.tuned.fingerprint16.v1` | 0.820x | 0.451x | 0.608x | 2 |
| 38 | `hash.robinhood.tagged.ultra-sparse.tuned.swap-reduced.probe-reuse.meta-packed.reset-touched.v1` | 0.726x | 0.474x | 0.586x | 2 |
| 39 | `hash.robinhood.tagged.sparse.mixed-uniqueness-10-70.meta.v1` | 0.684x | 0.405x | 0.527x | 2 |
| 40 | `hash.robinhood.v1` | 0.597x | 0.368x | 0.469x | 2 |
| 41 | `tree.trie.v1` | 0.309x | 0.170x | 0.229x | 24 |
| 42 | `tree.sorted-array.length-first.first-unit.v1` | 0.055x | 0.034x | 0.043x | 4 |
| 43 | `tree.sorted-array.length-first.v1` | 0.044x | 0.031x | 0.037x | 4 |
| 44 | `tree.sorted-array.length-first.first-unit.length-ranges.btree.v1` | 0.027x | 0.019x | 0.023x | 4 |
| 45 | `tree.sorted-array.v1` | 0.021x | 0.018x | 0.019x | 4 |
| 46 | `tree.sorted-array.length-first.first-unit.length-ranges.v1` | 0.020x | 0.015x | 0.017x | 4 |
| 47 | `tree.sorted-array.first-unit.v1` | 0.012x | 0.009x | 0.010x | 4 |

## Root Branch Coverage

| Root node | Base | Samples |
|---|---|---:|
| `hash.bucket4.v1` | `base.hashtable` | 24 |
| `hash.chained.v1` | `base.hashtable` | 24 |
| `hash.cuckoo2.v1` | `base.hashtable` | 26 |
| `hash.linear.v1` | `base.hashtable` | 218 |
| `tree.sorted-array.v1` | `base.btree` | 24 |
| `tree.trie.v1` | `base.btree` | 24 |

## Node Approach Summary

| Node | Parent | Approach |
|---|---|---|
| `hash.bucket4.v1` | `base.hashtable` | 4-slot bucketized probing with bucket-wise scans before advancing. |
| `hash.chained.v1` | `base.hashtable` | String separate chaining with index-linked entry lists. |
| `hash.cuckoo2.cached-hash.length-prefilter.v1` | `hash.cuckoo2.cached-hash.v1` | Two-choice cuckoo hashing with cached hash and length prefilter before string equality. |
| `hash.cuckoo2.cached-hash.v1` | `hash.cuckoo2.v1` | Two-choice cuckoo hashing with cached per-entry hash and hash-prefiltered equality. |
| `hash.cuckoo2.v1` | `base.hashtable` | Two-choice cuckoo hashing with bounded eviction and resize fallback. |
| `hash.linear.half-load.v1` | `hash.linear.v1` | Linear probing with 50% max load factor. |
| `hash.linear.packed.ultra-sparse.touched.v1` | `hash.linear.packed.ultra-sparse.v1` | Tag+index packed in single uint, 25% load, touched-slot reset avoids clearing the full 4x table on Reset. |
| `hash.linear.packed.ultra-sparse.v1` | `hash.linear.tagged.ultra-sparse.v1` | Tag+index packed into a single uint per slot (top 8 bits = tag, low 24 bits = index+1), 25% load. One array read per probe eliminates the separate tags/slots cache miss split. |
| `hash.linear.tagged.half-load.touched.v1` | `hash.linear.tagged.half-load.v1` | Tagged half-load linear probing with touched reset. |
| `hash.linear.tagged.half-load.v1` | `hash.linear.tagged.v1` | Tagged linear probing with 50% max load. |
| `hash.linear.tagged.sparse.touched.v1` | `hash.linear.tagged.sparse.v1` | Tagged sparse linear probing with touched reset. |
| `hash.linear.tagged.sparse.v1` | `hash.linear.tagged.half-load.v1` | Tagged linear probing with 35% max load. |
| `hash.linear.tagged.ultra-sparse.tuned.fingerprint16.v1` | `hash.linear.tagged.ultra-sparse.tuned.v1` | Tagged ultra-sparse tuned probing with dual-byte fingerprints for balanced 50%-uniqueness hit/miss mixes. |
| `hash.linear.tagged.ultra-sparse.tuned.length-prefilter.noinline.v1` | `hash.linear.tagged.ultra-sparse.tuned.length-prefilter.v1` | Tagged ultra-sparse tuned length-prefilter probing with no-inline boundary to reduce caller code size. |
| `hash.linear.tagged.ultra-sparse.tuned.length-prefilter.v1` | `hash.linear.tagged.ultra-sparse.tuned.v1` | Tagged ultra-sparse tuned probing with per-slot length prefilter before string equality. |
| `hash.linear.tagged.ultra-sparse.tuned.miss-bounded.unrolled4.v1` | `hash.linear.tagged.ultra-sparse.tuned.miss-bounded.v1` | Tagged ultra-sparse tuned miss-bounded probing with 4-step unrolled probe loop. |
| `hash.linear.tagged.ultra-sparse.tuned.miss-bounded.v1` | `hash.linear.tagged.ultra-sparse.tuned.v1` | Tagged ultra-sparse tuned probing with per-home max-distance bounds to cut miss probes. |
| `hash.linear.tagged.ultra-sparse.tuned.noinline.v1` | `hash.linear.tagged.ultra-sparse.tuned.v1` | Tagged ultra-sparse tuned probing with no-inline boundary to reduce caller code size and register pressure. |
| `hash.linear.tagged.ultra-sparse.tuned.touched.fingerprint16.v1` | `hash.linear.tagged.ultra-sparse.tuned.touched.v1` | Tagged ultra-sparse tuned touched probing with a 16-bit fingerprint prefilter before full key compare. |
| `hash.linear.tagged.ultra-sparse.tuned.touched.v1` | `hash.linear.tagged.ultra-sparse.tuned.v1` | Tagged ultra-sparse tuned probing with touched-slot reset to avoid full clears. |
| `hash.linear.tagged.ultra-sparse.tuned.unrolled4.v1` | `hash.linear.tagged.ultra-sparse.tuned.v1` | Tagged ultra-sparse tuned probing with 4-step unrolled probe loop to reduce branch overhead. |
| `hash.linear.tagged.ultra-sparse.tuned.v1` | `hash.linear.tagged.ultra-sparse.v1` | Tagged ultra-sparse probing tuned for tag-first branch behavior. |
| `hash.linear.tagged.ultra-sparse.v1` | `hash.linear.tagged.sparse.v1` | Tagged linear probing with 25% max load. |
| `hash.linear.tagged.v1` | `hash.linear.v1` | Linear probing plus one-byte hash fingerprint tags. |
| `hash.linear.touched-reset.tagged.half-load.v1` | `hash.linear.touched-reset.tagged.v1` | Linear probing with touched-slot reset, one-byte tags, and 50% max load. |
| `hash.linear.touched-reset.tagged.v1` | `hash.linear.touched-reset.v1` | Linear probing with touched-slot reset and one-byte tag fingerprints. |
| `hash.linear.touched-reset.v1` | `hash.linear.v1` | Linear probing with touched-slot reset. |
| `hash.linear.v1` | `base.hashtable` | Classic linear probing over power-of-two table. |
| `hash.robinhood.tagged.sparse.mixed-uniqueness-10-70.meta.v1` | `hash.robinhood.tagged.sparse.v1` | Robin Hood tagged sparse probing with epoch-stamped recent-hit metadata tuned for mixed uniqueness throughput (10-70). |
| `hash.robinhood.tagged.sparse.v1` | `hash.robinhood.tagged.v1` | Robin Hood tagged probing with 40% max load. |
| `hash.robinhood.tagged.ultra-sparse.tuned.swap-reduced.probe-reuse.meta-packed.reset-touched.v1` | `hash.robinhood.tagged.ultra-sparse.tuned.swap-reduced.probe-reuse.meta-packed.v1` | Robin Hood tagged ultra-sparse probing with packed metadata and touched-slot reset clearing to reduce reset-path memory work. |
| `hash.robinhood.tagged.ultra-sparse.tuned.swap-reduced.probe-reuse.meta-packed.v1` | `hash.robinhood.tagged.ultra-sparse.tuned.swap-reduced.probe-reuse.v1` | Robin Hood tagged ultra-sparse probing that packs tag+distance metadata to reduce probe-path memory traffic. |
| `hash.robinhood.tagged.ultra-sparse.tuned.swap-reduced.probe-reuse.v1` | `hash.robinhood.tagged.ultra-sparse.tuned.swap-reduced.v1` | Robin Hood tagged ultra-sparse probing that reuses miss probe state to avoid duplicate insert-path scans. |
| `hash.robinhood.tagged.ultra-sparse.tuned.swap-reduced.v1` | `hash.robinhood.tagged.ultra-sparse.v1` | Robin Hood tagged ultra-sparse probing tuned to reduce swap and probe-distance churn during inserts. |
| `hash.robinhood.tagged.ultra-sparse.tuned.v1` | `hash.robinhood.tagged.ultra-sparse.tuned.swap-reduced.v1` | Robin Hood tagged ultra-sparse probing tuned for tag-first early checks. |
| `hash.robinhood.tagged.ultra-sparse.v1` | `hash.robinhood.tagged.sparse.v1` | Robin Hood tagged probing with 25% max load. |
| `hash.robinhood.tagged.v1` | `hash.robinhood.v1` | Robin Hood probing with one-byte tag fingerprint. |
| `hash.robinhood.v1` | `hash.linear.v1` | Robin Hood probing baseline. |
| `hash.simd-group.ultra-sparse.touched.v1` | `hash.simd-group.ultra-sparse.v1` | SIMD group probing for strings + touched-slot reset. |
| `hash.simd-group.ultra-sparse.v1` | `hash.linear.tagged.ultra-sparse.v1` | Swiss Table–style group probing for strings: Vector128<byte> checks 16 tag slots per instruction. |
| `tree.sorted-array.first-unit.v1` | `tree.sorted-array.v1` | Sorted array branch with first-char metadata short-circuiting compare. |
| `tree.sorted-array.length-first.first-unit.length-ranges.btree.v1` | `tree.sorted-array.length-first.first-unit.length-ranges.v1` | Sorted array length-first+first-char ranges with active-length indexing to reduce insert-path range maintenance. |
| `tree.sorted-array.length-first.first-unit.length-ranges.v1` | `tree.sorted-array.length-first.first-unit.v1` | Sorted array length-first+first-char branch with per-length ranges to narrow binary search and reduce compare work. |
| `tree.sorted-array.length-first.first-unit.v1` | `tree.sorted-array.length-first.v1` | Sorted array length-first branch with first-char metadata short-circuiting compare. |
| `tree.sorted-array.length-first.v1` | `tree.sorted-array.v1` | Sorted array branch with length metadata short-circuiting compare. |
| `tree.sorted-array.v1` | `base.btree` | Sorted array reference baseline. |
| `tree.trie.v1` | `base.btree` | Character trie with per-node linked child edges. |

## Exploration Graph (Graphviz DOT)

Diagram source: [DICTIONARY_LAB_EXPLORATION.dot](/home/kuinox/dev/Plank/benchmarks/report/DICTIONARY_LAB_EXPLORATION.dot)
