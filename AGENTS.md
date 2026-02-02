Project Style Guide

- One top-level type per file. Nested types are OK.
- Omit implicit accessibility (e.g., no `private` on members where it is default).
- Avoid braces for single-statement `if`.
- Prefer expression-bodied members for trivial returns (especially builder no-ops).
