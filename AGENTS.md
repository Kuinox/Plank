Project Style Guide

- Keep namespaces under `Plank` (no `Plank.Schema` or `Plank.Writing` namespaces).
- One top-level type per file. Nested types are OK.
- Omit implicit accessibility (e.g., no `private` on members where it is default).
- Avoid braces for single-statement `if`.
- Prefer expression-bodied members for trivial returns (especially builder no-ops).
- Column-group API is generator-free and expression-free.
- Row-based API uses expression builders for source-generator input only.
- Public writer API is `Serialize(...).WriteAsync()` (no exposed encode/compress stages).
