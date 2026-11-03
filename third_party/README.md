# Vendored LZ4 maintenance

`K4os.Compression.LZ4` is based on upstream tag 1.3.8. The fork contains an
offset-zero rejection fix, its regression test, a mechanical namespace
rename from `K4os.Compression.LZ4` to `Plank.K4os.LZ4` in the upstream library
sources, and a Plank-specific project configuration. Visibility stays as
upstream; the renamed types live in a separate assembly.

Plank references the fork's `src/K4os.Compression.LZ4/K4os.Compression.LZ4.csproj`,
which targets .NET 10 and builds `Plank.K4os.LZ4.dll` without signing or
additional package dependencies. The fork's `global.json` selects the .NET 10 SDK. Plank packs the resulting DLL inside its own
NuGet package, not as a separate dependency. The upstream MIT license remains
in the submodule and is included in the package. The upstream test projects
still target older frameworks and should be run on `plank/offset-zero-only`.

For an upstream update, rebase or reapply the fix, regression test, namespace
rename and project configuration in that order, review new source files and
namespace references, and update the submodule pin. Rerun the full suite,
package/consumer checks, safety tests and benchmarks.
