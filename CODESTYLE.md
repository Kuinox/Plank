# Code Style

## Core Rules

- Keep one top-level type per file.
- Omit implicit accessibility modifiers when default accessibility applies.
- Avoid braces for single-statement `if`.
- Prefer expression-bodied members for trivial returns.
- Never block on async (`.Result`, `.Wait()`, `GetAwaiter().GetResult()`).

## Numeric Rules

- Use `uint` for parameters/properties that are semantically non-negative:
  `size`, `count`, `capacity`, `bytes`, `length`, `rows`.
- Do not add negative checks for those `uint` values.
- Keep `int` only when required by external constraints, for example:
  - framework interfaces that require `int` (for example `IBufferWriter<byte>`),
  - APIs that require `int` lengths/indexes (array/span access, `ArrayPool`, etc.),
  - sentinel values (for example `-1`) used as state markers.
- When converting `uint` to `int`, use `checked` casts at the boundary.
