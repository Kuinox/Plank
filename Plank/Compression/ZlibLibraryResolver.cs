using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace Plank.Internal.Compression;

static class ZlibLibraryResolver
{
    static readonly OSPlatform Android = OSPlatform.Create("ANDROID");
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

        foreach (var candidate in GetSystemLibraryCandidates(GetCurrentPlatform()))
            if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out var systemHandle))
                return systemHandle;

        throw new DllNotFoundException(CreateFailureMessage());
    }

    static bool TryLoadRuntimeAsset(Assembly assembly, out IntPtr handle)
    {
        foreach (var baseDirectory in GetBaseDirectories(assembly))
            foreach (var path in GetRuntimeAssetPaths(GetCurrentPlatform(), RuntimeInformation.ProcessArchitecture))
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

    static OSPlatform GetCurrentPlatform()
    {
        // Android is a distinct .NET platform; IsLinux() deliberately excludes it.
        if (OperatingSystem.IsAndroid())
            return Android;
        if (OperatingSystem.IsWindows())
            return OSPlatform.Windows;
        if (OperatingSystem.IsLinux())
            return OSPlatform.Linux;
        if (OperatingSystem.IsMacOS())
            return OSPlatform.OSX;

        return default;
    }

    internal static string[] GetRuntimeAssetPaths(OSPlatform platform, Architecture architecture)
    {
        string runtimePrefix;
        string libraryFileName;
        if (platform == OSPlatform.Windows)
        {
            runtimePrefix = "win";
            libraryFileName = "zlib.dll";
        }
        else if (platform == OSPlatform.Linux || platform == Android)
        {
            runtimePrefix = platform == Android ? "android" : "linux";
            libraryFileName = "libz.so";
        }
        else if (platform == OSPlatform.OSX)
        {
            runtimePrefix = "osx";
            libraryFileName = "libz.dylib";
        }
        else
            return [];

        var architectureName = architecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 when platform == OSPlatform.Windows => "x86",
            _ => null,
        };
        if (architectureName is null)
            return [];

        return
        [
            Path.Combine("runtimes", $"{runtimePrefix}-{architectureName}", "native", libraryFileName),
            libraryFileName,
        ];
    }

    internal static string[] GetSystemLibraryCandidates(OSPlatform platform)
    {
        if (platform == OSPlatform.Windows)
            return ["zlib.dll", "zlib1.dll", "libz.dll", "z"];

        // Android exposes the unversioned NDK library, not Linux's libz.so.1 SONAME.
        // Loading by name also lets the Android runtime find libraries packaged in the APK.
        if (platform == Android)
            return ["libz.so", "libz", "z"];

        if (platform == OSPlatform.Linux)
            return ["libz.so.1", "libz.so", "libz", "z"];

        if (platform == OSPlatform.OSX)
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
