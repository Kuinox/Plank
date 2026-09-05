using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Plank.Internal.Compression;
using Plank.Reading;
using Plank.Writing;
using Plank.Writing.Compression;

namespace Plank.Tests.NativeInterop;

internal sealed class ZlibNativeTests
{
    [Test]
    public void EveryZlibImportUsesTheCdeclAbi()
    {
        var imports = typeof(ZlibNative).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(method => method.GetCustomAttribute<LibraryImportAttribute>() is not null)
            .ToArray();
        if (imports.Length == 0)
            throw new InvalidOperationException("No zlib imports were inspected.");

        foreach (var method in imports)
        {
            var conventions = method.GetCustomAttribute<UnmanagedCallConvAttribute>()?.CallConvs;
            if (conventions is null || !conventions.Contains(typeof(CallConvCdecl)))
                throw new InvalidOperationException($"{method.Name} must use zlib's Cdecl calling convention.");
        }
    }

    [Test]
    public void GzipInteroperatesWithTheBclInBothDirections()
    {
        // The Windows smoke job must actually run a 32-bit process to exercise this ABI.
        var expectedArchitecture = Environment.GetEnvironmentVariable("PLANK_EXPECTED_ARCHITECTURE");
        if (expectedArchitecture is not null &&
            !string.Equals(expectedArchitecture, RuntimeInformation.ProcessArchitecture.ToString(),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Expected a {expectedArchitecture} process, got {RuntimeInformation.ProcessArchitecture}.");

        byte[][] payloads = [[], "native zlib interoperability"u8.ToArray(), new byte[131_073]];
        new Random(1927).NextBytes(payloads[2]);
        foreach (var payload in payloads)
            foreach (var level in new[] { 0, 1, 9 })
            {
                // Repeated initialization, calls and disposal exercise every native import.
                var nativeCompressed = CompressWithZlib(payload, level);
                using var compressedStream = new MemoryStream(nativeCompressed);
                using var gzip = new GZipStream(compressedStream, CompressionMode.Decompress);
                using var bclDecoded = new MemoryStream();
                gzip.CopyTo(bclDecoded);
                if (!payload.AsSpan().SequenceEqual(bclDecoded.ToArray()))
                    throw new InvalidOperationException("The BCL could not decode zlib's gzip payload.");

                AssertZlibDecodes(payload, nativeCompressed);
                // GZipStream may emit no member when nothing is written. The native
                // empty member above still verifies both decoders on empty data.
                if (payload.Length == 0)
                    continue;

                using var bclCompressed = new MemoryStream();
                using (var encoder = new GZipStream(bclCompressed, CompressionLevel.SmallestSize, leaveOpen: true))
                    encoder.Write(payload);
                AssertZlibDecodes(payload, bclCompressed.ToArray());
            }
    }

    static byte[] CompressWithZlib(byte[] payload, int level)
    {
        var destination = new BufferWriter(DefaultParquetBufferPool.Shared, 4096, 4096);
        try
        {
            // Force several deflate calls for an incompressible input.
            GzipDeflater.Compress(level, payload, new byte[1024], ref destination);
            var compressed = new byte[destination.WrittenLength];
            destination.CopyTo(compressed);
            return compressed;
        }
        finally
        {
            destination.Dispose();
        }
    }

    static void AssertZlibDecodes(byte[] expected, byte[] compressed)
    {
        var decoded = new byte[expected.Length];
        var written = GzipInflater.Decompress(compressed, decoded);
        if (written != expected.Length || !expected.AsSpan().SequenceEqual(decoded))
            throw new InvalidOperationException("zlib did not reproduce the original gzip payload.");
    }
}
