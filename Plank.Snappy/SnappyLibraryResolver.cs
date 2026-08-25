using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace Plank.Snappy;

static class SnappyLibraryResolver
{
    static int _resolverRegistered;

    internal static void Register()
    {
        if (Interlocked.Exchange(ref _resolverRegistered, 1) != 0)
            return;

        NativeLibrary.SetDllImportResolver(typeof(SnappyLibraryResolver).Assembly, Resolve);
    }

    static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != SnappyNative.LibraryName)
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
                    Path.Combine("runtimes", "win-x64", "native", "libsnappy.dll"),
                ];
            if (RuntimeInformation.ProcessArchitecture == Architecture.X86)
                return
                [
                    Path.Combine("runtimes", "win-x86", "native", "libsnappy.dll"),
                ];
            if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
                return
                [
                    Path.Combine("runtimes", "win-arm64", "native", "libsnappy.dll"),
                ];

            return [];
        }

        if (OperatingSystem.IsLinux())
        {
            if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
                return
                [
                    Path.Combine("runtimes", "linux-x64", "native", "centos7_libsnappy.so"),
                    Path.Combine("runtimes", "linux-x64", "native", "libsnappy.so"),
                ];
            if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
                return
                [
                    Path.Combine("runtimes", "linux-arm64", "native", "libsnappy.so"),
                    Path.Combine("runtimes", "linux-arm64", "native", "centos7_libsnappy.so"),
                ];

            return [];
        }

        return [];
    }

    static string[] GetSystemLibraryCandidates()
    {
        if (OperatingSystem.IsWindows())
            return ["libsnappy.dll", "snappy.dll", "libsnappy", "snappy"];

        if (OperatingSystem.IsLinux())
            return ["centos7_libsnappy.so", "libsnappy.so.1", "libsnappy.so", "libsnappy", "snappy"];

        if (OperatingSystem.IsMacOS())
            return ["libsnappy.dylib", "libsnappy.1.dylib", "libsnappy", "snappy"];

        return [];
    }

    static string CreateFailureMessage()
    {
        var os = RuntimeInformation.OSDescription;
        var arch = RuntimeInformation.ProcessArchitecture;
        return $"Unable to load native Snappy library for OS '{os}' and architecture '{arch}'.";
    }
}
