using System.Reflection;
using System.Runtime.InteropServices;
using Plank.Internal.Compression;

namespace Plank.Tests.NativeInterop;

internal sealed class ZlibLibraryResolverTests
{
    [Test]
    [Arguments("WINDOWS", Architecture.X86, "win-x86", "zlib.dll")]
    [Arguments("WINDOWS", Architecture.X64, "win-x64", "zlib.dll")]
    [Arguments("WINDOWS", Architecture.Arm64, "win-arm64", "zlib.dll")]
    [Arguments("LINUX", Architecture.X64, "linux-x64", "libz.so")]
    [Arguments("LINUX", Architecture.Arm64, "linux-arm64", "libz.so")]
    [Arguments("OSX", Architecture.X64, "osx-x64", "libz.dylib")]
    [Arguments("OSX", Architecture.Arm64, "osx-arm64", "libz.dylib")]
    [Arguments("ANDROID", Architecture.X64, "android-x64", "libz.so")]
    [Arguments("ANDROID", Architecture.Arm64, "android-arm64", "libz.so")]
    public void PackagedAndFlattenedPathsMatchTheProcessPlatform(string platform, Architecture architecture,
        string runtimeIdentifier, string libraryName)
    {
        var paths = ZlibLibraryResolver.GetRuntimeAssetPaths(OSPlatform.Create(platform), architecture);
        string[] expected = [Path.Combine("runtimes", runtimeIdentifier, "native", libraryName), libraryName];
        if (!paths.SequenceEqual(expected))
            throw new InvalidOperationException(
                $"Wrong zlib candidates for {runtimeIdentifier}: {string.Join(", ", paths)}.");
    }

    [Test]
    public void ResolverCoversExactlyTheShippedNativeAssets()
    {
        var root = typeof(ZlibLibraryResolverTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "ZlibRuntimeAssetsRoot").Value!;
        var shipped = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.Combine("runtimes", Path.GetRelativePath(root, path)))
            .ToHashSet(StringComparer.Ordinal);
        if (shipped.Count == 0)
            throw new InvalidOperationException("No native runtime assets were inspected.");

        var resolved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var platform in new[] { "WINDOWS", "LINUX", "OSX", "ANDROID" })
            foreach (var architecture in Enum.GetValues<Architecture>())
            {
                var paths = ZlibLibraryResolver.GetRuntimeAssetPaths(OSPlatform.Create(platform), architecture);
                if (paths.Length != 0)
                    resolved.Add(paths[0]);
            }

        if (!shipped.SetEquals(resolved))
            throw new InvalidOperationException(
                $"Native package/resolver mismatch. Unreachable assets: {string.Join(", ", shipped.Except(resolved))}. " +
                $"Missing assets: {string.Join(", ", resolved.Except(shipped))}.");
    }

    [Test]
    [Arguments("ANDROID", "libz.so")]
    [Arguments("LINUX", "libz.so.1")]
    [Arguments("WINDOWS", "zlib.dll")]
    [Arguments("OSX", "libz.dylib")]
    public void SystemFallbackUsesThePlatformsLibraryName(string platform, string preferredName)
    {
        var candidates = ZlibLibraryResolver.GetSystemLibraryCandidates(OSPlatform.Create(platform));
        if (candidates.Length == 0 || candidates[0] != preferredName || candidates[^1] != ZlibNative.LibraryName)
            throw new InvalidOperationException($"No valid zlib fallback for {platform}.");
        if (platform == "ANDROID" && candidates.Contains("libz.so.1", StringComparer.Ordinal))
            throw new InvalidOperationException("Android must not probe Linux's versioned zlib SONAME.");
    }

    [Test]
    public void UnsupportedArchitecturesDoNotSelectAnotherArchitecturesBinary()
    {
        foreach (var platform in new[] { "WINDOWS", "LINUX", "OSX", "ANDROID" })
        {
            var os = OSPlatform.Create(platform);
            if (ZlibLibraryResolver.GetRuntimeAssetPaths(os, Architecture.Arm).Length != 0 ||
                ZlibLibraryResolver.GetRuntimeAssetPaths(os, (Architecture)int.MaxValue).Length != 0)
                throw new InvalidOperationException($"Selected an incompatible zlib runtime for {platform}.");
            if (platform != "WINDOWS" && ZlibLibraryResolver.GetRuntimeAssetPaths(os, Architecture.X86).Length != 0)
                throw new InvalidOperationException($"Selected an unshipped x86 zlib runtime for {platform}.");
        }
    }

    [Test]
    public void UnknownPlatformsHaveNoCandidates()
    {
        var unknown = OSPlatform.Create("UNKNOWN");
        if (ZlibLibraryResolver.GetRuntimeAssetPaths(unknown, Architecture.X64).Length != 0 ||
            ZlibLibraryResolver.GetSystemLibraryCandidates(unknown).Length != 0)
            throw new InvalidOperationException("An unknown platform selected an incompatible zlib library.");
    }
}
