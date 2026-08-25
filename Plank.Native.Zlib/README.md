# Plank.Native.Zlib

Native zlib runtime assets packaged for .NET consumers.

The package intentionally exposes no managed API. It exists so managed projects can reference one NuGet package and receive zlib native binaries through the standard `runtimes/{rid}/native` asset layout.

## Runtime Assets

- `runtimes/win-x64/native/zlib.dll`
- `runtimes/win-x86/native/zlib.dll`
- `runtimes/win-arm64/native/zlib.dll`
- `runtimes/linux-x64/native/libz.so`
- `runtimes/linux-arm64/native/libz.so`
- `runtimes/osx-x64/native/libz.dylib`
- `runtimes/osx-arm64/native/libz.dylib`
- `runtimes/android-arm64/native/libz.so`
- `runtimes/android-x64/native/libz.so`

## Notes

The current native binaries are zlib 1.3.1 compatible builds. Plank consumes these assets through its own P/Invoke declarations and resolver.
