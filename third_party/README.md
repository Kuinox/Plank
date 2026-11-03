# Vendored LZ4 maintenance

Plank pins a commit on `master` of the Kuinox K4os.Compression.LZ4 fork, based
on upstream master after tag 1.3.8. The fork contains an offset-zero rejection fix,
its regression test, a mechanical namespace rename from `K4os.Compression.LZ4`
to `Plank.K4os.LZ4` in the library sources, and a Plank-specific project
configuration. Visibility stays as upstream; the renamed types live in a
separate assembly.

Plank references the fork's `src/K4os.Compression.LZ4/K4os.Compression.LZ4.csproj`,
which targets .NET 10 and builds `Plank.K4os.LZ4.dll` without signing or
additional package dependencies. Plank packs the resulting DLL inside its own
NuGet package, not as a separate dependency. The upstream MIT license remains
in the submodule and is included in the package. The upstream test projects
should be run on the upstream-pure `plank/offset-zero-only` branch.

For an upstream update, rebase or reapply the fix, regression test, namespace
rename and project configuration in that order, review new source files and
namespace references, and update the submodule pin. Rerun the full suite,
package/consumer checks, safety tests and benchmarks.
