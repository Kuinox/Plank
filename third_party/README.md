# Vendored LZ4 maintenance

Plank pins a commit on `master` of the Kuinox K4os.Compression.LZ4 fork, based
on upstream master after tag 1.3.8. The fork contains an offset-zero rejection
fix, its regression test, a mechanical namespace rename from
`K4os.Compression.LZ4` to `Plank.K4os.LZ4` in the library sources, and
Plank-specific build and package configuration. Visibility stays as upstream.

Plank references the fork's `src/K4os.Compression.LZ4/K4os.Compression.LZ4.csproj`,
which targets .NET 10 and produces the unsigned `Plank.K4os.LZ4` package. Its
fixed version `1.3.8.1` tracks upstream 1.3.8 plus the first Plank revision;
Plank's NuGet package declares a dependency on it. The fork package includes
its README and declares the upstream MIT license. The upstream test projects
should be run on the upstream-pure `plank/offset-zero-only` branch.

For an upstream update, rebase or reapply the fix, regression test, namespace
rename and project configuration in that order, review new source files and
namespace references, and update the submodule pin and package version. Rerun
the full suite, release packaging/consumer checks, safety tests and benchmarks.
