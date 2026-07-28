Project Style Guide

- One top-level type per file. Nested types are OK.
- Omit implicit accessibility (e.g., no `private` on members where it is default).
- Avoid braces for single-statement `if`.
- Prefer expression-bodied members for trivial returns (especially builder no-ops).
- Do not add internal forwarding properties for fields; expose an internal field directly when internal shared access is needed.
- Keep tests red for known defects; do not hide or weaken failing tests just to make the suite green.


Architecture directives

- Backward compatibility is not required yet (library is not released); prefer the cleanest design over compatibility shims.
