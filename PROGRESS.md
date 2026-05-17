# Mutation Testing Progress

Branch: mutation-testing (worktree at /home/kuinox/dev/mutation-testing)
Date: 2026-05-17

## Score History
| Iteration | Score | Killed | Tests |
|---|---|---|---|
| Baseline | 24.55% | 1556 | 100 |
| 16 (latest) | 49.17% | 3157 | 534 |

## Setup
- Stryker 4.14, xUnit test project (TUnit incompatible with Stryker MTP)
- ZlibNative.cs uses DllImport (Stryker Roslyn can't run LibraryImportGenerator on .NET 10)
- global.json MTP runner removed to enable xUnit VSTest discovery
- ignore-mutations: ["string"] — ZlibNative LibraryImport workaround

## Bug Fixed
- ColumnChunkReader.cs DecodePlainInt32 for DateOnly: added UnixEpochDayNumber offset

## Key Remaining Gaps
- Encoding.cs: 279+ survived (EncodeOptional, FixedLenByteArray paths)
- ColumnChunkReader.cs: 200+ survived, 300+ NoCoverage
- RleBitPackingHybridEncoding.cs: 100+ survived
