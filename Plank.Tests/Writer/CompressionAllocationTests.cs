using Plank.Writing;
using Plank.Writing.Compression;

namespace Plank.Tests.Writer;

internal sealed class CompressionAllocationTests
{
    static readonly CompressionKind[] _compressionKinds =
    [
        CompressionKind.None,
        CompressionKind.Snappy,
        CompressionKind.Gzip,
        CompressionKind.Zstd,
        CompressionKind.Lz4,
        CompressionKind.Brotli
    ];

    [Test]
    public void CompressionCodecsDoNotAllocateAfterWarmupForContiguousInput()
    {
        var failures = new List<string>();
        for (var i = 0; i < _compressionKinds.Length; i++)
        {
            var codec = _compressionKinds[i];
            var allocated = MeasureSteadyStateAllocations(codec, multiSegmentInput: false);
            if (allocated != 0)
                failures.Add($"codec '{codec}' allocated {allocated} bytes.");
        }

        if (failures.Count > 0)
            throw new InvalidOperationException(
                $"Expected zero allocations for contiguous input after warmup. Failures: {string.Join(' ', failures)}");
    }

    [Test]
    public void CompressionCodecsDoNotAllocateAfterWarmupForSegmentedInput()
    {
        var failures = new List<string>();
        for (var i = 0; i < _compressionKinds.Length; i++)
        {
            var codec = _compressionKinds[i];
            var allocated = MeasureSteadyStateAllocations(codec, multiSegmentInput: true);
            if (allocated != 0)
                failures.Add($"codec '{codec}' allocated {allocated} bytes.");
        }

        if (failures.Count > 0)
            throw new InvalidOperationException(
                $"Expected zero allocations for segmented input after warmup. Failures: {string.Join(' ', failures)}");
    }

    static long MeasureSteadyStateAllocations(CompressionKind codec, bool multiSegmentInput)
    {
        uint chunkSize = multiSegmentInput ? 1024U : 128U * 1024;
        uint initialBuffer = multiSegmentInput ? 1024U : 128U * 1024;
        var factory = new BufferWriterFactory(DefaultParquetBufferPool.Shared, chunkSize, initialBuffer, initialBuffer, initialBuffer);
        var context = new CompressionContext(factory);
        var source = factory.CreatePageBufferWriter();
        var destination = factory.CreatePageBufferWriter();
        PopulateSource(ref source, multiSegmentInput ? 48 * 1024 : 32 * 1024);

        for (var i = 0; i < 8; i++)
            Compression.Compress(codec, context, ref source, ref destination);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        Compression.Compress(codec, context, ref source, ref destination);
        var after = GC.GetAllocatedBytesForCurrentThread();
        return after - before;
    }

    static void PopulateSource(ref BufferWriter source, int size)
    {
        var destination = source.GetSpan(size);
        for (var i = 0; i < size; i++)
            destination[i] = (byte)(i * 31 + 17);
        source.Advance(size);
    }
}
