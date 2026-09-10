# Startup performance

Plank's decoders and encoders are straight-line SIMD loops. Under .NET's default
tiered compilation they run in *quick JIT* code until the runtime promotes them,
and quick JIT performs **no inlining at all** — so every `Vector128<T>` /
`Vector256<T>` / `Vector512<T>` operation on a hot path becomes an out-of-line
call with argument marshalling instead of a single instruction.

The result is a warmup cliff. Measured on an idle Ryzen 9 7900X reading a
real-world nullable INT32 delta-binary-packed column, one CPU, 100 ordered
cold-start iterations with no warmup:

| iterations | per-iteration time |
|---|---:|
| 2–40 | ~48 ms |
| 41–100 | ~5.1 ms |

That is a **9x** difference, and the fast state does not arrive until roughly the
fortieth call.

## Why it lasts so long on one CPU

.NET's default tiering delay is 100 ms, but the runtime multiplies it by **10**
when the process sees a single processor, and restarts it whenever new methods
keep arriving. A container with a one-CPU limit, or a process started with
`DOTNET_PROCESSOR_COUNT=1`, therefore stays in quick-JIT code for seconds rather
than milliseconds.

## If your process is short-lived

A job that opens one file, decodes it and exits may spend its entire life in the
slow state. For that shape, disable tiered compilation in the **entry
application's** project file:

```xml
<PropertyGroup>
  <TieredCompilation>false</TieredCompilation>
</PropertyGroup>
```

or equivalently in `runtimeconfig.template.json`:

```json
{ "configProperties": { "System.Runtime.TieredCompilation": false } }
```

Every method is then compiled with full optimization on first call. Measured
against the default on the same machine and configuration, comparing the settled
plateau (iterations 80–100) and the early window (2–20):

| workload | default, early | default, settled | tiering off |
|---|---:|---:|---:|
| INT32 delta column read | 48.7 ms | 5.14 ms | **4.92 ms** |
| INT32 delta column write | 47.2 ms | 17.89 ms | **16.23 ms** |
| INT64 byte-stream-split column read | 7.86 ms | 7.87 ms | **7.19 ms** |
| INT64 byte-stream-split column write | 77.4 ms | 20.10 ms | **20.37 ms** |

The cliff disappears — the first iteration is already at plateau speed — and the
plateau itself is equal or slightly better, because Plank's hot paths contain no
virtual, interface or delegate calls for dynamic PGO to devirtualize.

## If your process is long-lived

Leave the default alone. Tiered compilation and dynamic PGO exist to make startup
cheap and to optimize code that Plank cannot see — including other libraries in
your application, where PGO's guarded devirtualization does real work. Disabling
it globally to speed up Plank can easily cost more elsewhere than it gains here.
The warmup above is paid once per process; for a server it is noise.

## Why this is not a library setting

`TieredCompilation`, `TieredPGO` and `TieredCompilationQuickJitForLoops` are read
from the entry application's `runtimeconfig.json`. A library cannot set them for
its consumer, and Plank deliberately does not try to — a package that silently
changes your application's global JIT policy would affect all of your code, not
just its own.

Applying `MethodImplOptions.AggressiveOptimization` to the hot methods instead
does not work either. It removes those methods from tiered compilation, and with
them the promotion that used to carry their callers to Tier1; the callers are
then stranded in quick-JIT code permanently, which costs more than the marked
methods gain. This was measured and rejected.
