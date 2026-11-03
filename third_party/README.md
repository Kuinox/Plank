# Vendored LZ4 maintenance

`K4os.Compression.LZ4` is based on upstream tag 1.3.8. The fork contains an
offset-zero rejection fix, its regression test, and a mechanical namespace
rename from `K4os.Compression.LZ4` to `Plank.K4os.LZ4` in the upstream library
sources. Visibility stays as upstream; the renamed types live in a separate
assembly.

`Plank.K4os.LZ4/Plank.K4os.LZ4.csproj` compiles the upstream library sources,
excluding only generated `obj` files. This small project avoids the upstream
project's multi-targeting, signing, packaging, and PolySharp configuration.
Plank packs the resulting DLL inside its own NuGet package, not as a separate
dependency. The upstream MIT license remains in the submodule and is included
in the package.

For an upstream update, rebase or reapply the fix, regression test and namespace
rename in that order, review new source files and namespace references, and
update the submodule pin. Rerun the full suite, package/consumer checks, safety
tests and benchmarks.
