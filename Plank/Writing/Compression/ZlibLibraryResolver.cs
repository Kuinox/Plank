using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace Plank.Writing.Compression;

static class ZlibLibraryResolver
{
    static int _resolverRegistered;

    internal static void Register()
    {
        if (Interlocked.Exchange(ref _resolverRegistered, 1) != 0)
            return;

        NativeLibrary.SetDllImportResolver(typeof(ZlibNative).Assembly, Resolve);
    }

    static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != ZlibNative.LibraryName)
            return IntPtr.Zero;

        if (TryLoadRuntimeAsset(assembly, out var runtimeHandle))
            return runtimeHandle;

        foreach (var candidate in GetSystemLibraryCandidates())
            if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out var systemHandle))
                return systemHandle;

        throw new DllNotFoundException(CreateFailureMessage());
    }

    static bool TryLoadRuntimeAsset(Assembly assembly, out IntPtr handle)
    {
        foreach (var baseDirectory in GetBaseDirectories(assembly))
            foreach (var path in GetRuntimeAssetPaths())
            {
                var fullPath = Path.Combine(baseDirectory, path);
                if (NativeLibrary.TryLoad(fullPath, out handle))
                    return true;
            }

        handle = IntPtr.Zero;
        return false;
    }

    static string[] GetBaseDirectories(Assembly assembly)
    {
        var assemblyDirectory = Path.GetDirectoryName(assembly.Location);
        if (string.IsNullOrEmpty(assemblyDirectory))
            return [AppContext.BaseDirectory];

        if (string.Equals(assemblyDirectory, AppContext.BaseDirectory, StringComparison.Ordinal))
            return [assemblyDirectory];

        return [assemblyDirectory, AppContext.BaseDirectory];
    }

    static string[] GetRuntimeAssetPaths()
    {
        if (OperatingSystem.IsWindows())
        {
            if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
                return
                [
                    Path.Combine("runtimes", "win-x64", "native", "zlib.dll"),
                    "zlib.dll",
                ];
            if (RuntimeInformation.ProcessArchitecture == Architecture.X86)
                return
                [
                    Path.Combine("runtimes", "win-x86", "native", "zlib.dll"),
                    "zlib.dll",
                ];

            return [];
        }

        if (OperatingSystem.IsLinux())
        {
            if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
                return
                [
                    Path.Combine("runtimes", "linux-x64", "native", "libz.so"),
                    "libz.so",
                ];
            if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
                return
                [
                    Path.Combine("runtimes", "linux-arm64", "native", "libz.so"),
                    "libz.so",
                ];

            return [];
        }

        if (OperatingSystem.IsMacOS())
        {
            if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
                return
                [
                    Path.Combine("runtimes", "osx-x64", "native", "libz.dylib"),
                    "libz.dylib",
                ];
            if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
                return
                [
                    Path.Combine("runtimes", "osx-arm64", "native", "libz.dylib"),
                    "libz.dylib",
                ];

            return [];
        }

        return [];
    }

    static string[] GetSystemLibraryCandidates()
    {
        if (OperatingSystem.IsWindows())
            return ["zlib.dll", "zlib1.dll", "libz.dll", "z"];

        if (OperatingSystem.IsLinux())
            return ["libz.so.1", "libz.so", "libz", "z"];

        if (OperatingSystem.IsMacOS())
            return ["libz.dylib", "libz.1.dylib", "libz", "z"];

        return [];
    }

    static string CreateFailureMessage()
    {
        var os = RuntimeInformation.OSDescription;
        var arch = RuntimeInformation.ProcessArchitecture;
        return $"Unable to load native zlib library for OS '{os}' and architecture '{arch}'.";
    }
}
