# Vendored LZ4 maintenance

`K4os.Compression.LZ4` is based on upstream tag 1.3.8. Plank builds only its
block codec and supporting sources. The fork's only source change is the
zero-offset rejection fix. `Plank.InternalGen/VendoredLz4Generator.cs` isolates
those types at build time: it changes their namespace to `Plank.Vendored.LZ4`
and makes top-level public types internal, avoiding collisions with consumers'
K4os references without modifying upstream sources.

For an upstream update, rebase the fix onto the new version, review the files
included by `Plank.csproj` and the generator's handling of new top-level types
and conditional declarations, and update the submodule pin. Rerun the full
suite, package/consumer checks, safety tests and benchmarks. Keep upstream's
MIT license included in the package.
