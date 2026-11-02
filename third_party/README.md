# Vendored LZ4 maintenance

`K4os.Compression.LZ4` is based on upstream tag 1.3.8. Plank compiles only
its block codec and supporting sources. The fork has a mechanical namespace and
visibility commit followed by the zero-offset rejection fix (and possibly later
Plank-specific fixes/optimizations).

For an upstream update, rebase or reapply the mechanical commit first: rename
`K4os.Compression.LZ4` to `Plank.Vendored.LZ4` in the files listed by
`Plank.csproj`, and make their top-level public types internal. Review any new
source files and conditional declarations. Reapply the behavioral commits
separately, resolve conflicts, update the submodule pin, and rerun the full
suite, package/consumer checks, safety tests and benchmarks. Keep upstream's MIT
license included in the package.
