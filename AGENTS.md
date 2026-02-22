Project Style Guide

- One top-level type per file. Nested types are OK.
- Omit implicit accessibility (e.g., no `private` on members where it is default).
- Avoid braces for single-statement `if`.
- Prefer expression-bodied members for trivial returns (especially builder no-ops).
- Do not add internal forwarding properties for fields; expose an internal field directly when internal shared access is needed.
- Never block on async (no `GetAwaiter().GetResult()`, `.Result`, or `.Wait()`); use async all the way.
- Keep tests red for known defects; do not hide or weaken failing tests just to make the suite green.

Subagent responsiveness

- Do not block waiting on subagents unless their result is strictly required to proceed.
- Use subagents for parallel side work; continue main-thread edits and execution immediately.
- When the user redirects, interrupt/pause subagent work and switch instantly.
- Keep subagents alive across task switches; prefer retasking over closing so concurrent work streams stay active.
