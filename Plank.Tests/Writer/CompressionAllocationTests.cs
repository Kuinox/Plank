using Plank.Schema;
using Plank.Writing;
using Plank.Writing.Compression;

namespace Plank.Tests.Writer;

[NotInParallel]
internal sealed class CompressionAllocationTests
{
    // Snappier 1.3.1 creates per-operation compressor state. Remove this budget when its reusable API ships.
    const long ManagedSnappyAllocationBudget = 48;

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
    public void CompressionCodecsStayWithinAllocationBudgetAfterWarmupForContiguousInput()
    {
        var failures = new List<string>();
        for (var i = 0; i < _compressionKinds.Length; i++)
        {
            var codec = _compressionKinds[i];
            var allocated = MeasureSteadyStateAllocations(codec, multiSegmentInput: false);
            var budget = GetAllocationBudget(codec);
            if (allocated > budget)
                failures.Add($"codec '{codec}' allocated {allocated} bytes with a {budget}-byte budget.");
        }

        if (failures.Count > 0)
            throw new InvalidOperationException(
                $"Compression allocation budgets were exceeded for contiguous input. Failures: {string.Join(' ', failures)}");
    }

    [Test]
    public void CompressionCodecsStayWithinAllocationBudgetAfterWarmupForSegmentedInput()
    {
        var failures = new List<string>();
        for (var i = 0; i < _compressionKinds.Length; i++)
        {
            var codec = _compressionKinds[i];
            var allocated = MeasureSteadyStateAllocations(codec, multiSegmentInput: true);
            var budget = GetAllocationBudget(codec);
            if (allocated > budget)
                failures.Add($"codec '{codec}' allocated {allocated} bytes with a {budget}-byte budget.");
        }

        if (failures.Count > 0)
            throw new InvalidOperationException(
                $"Compression allocation budgets were exceeded for segmented input. Failures: {string.Join(' ', failures)}");
    }

    static long GetAllocationBudget(CompressionKind codec)
        => codec == CompressionKind.Snappy ? ManagedSnappyAllocationBudget : 0;

    static long MeasureSteadyStateAllocations(CompressionKind codec, bool multiSegmentInput)
    {
        uint chunkSize = multiSegmentInput ? 1024U : 128U * 1024;
        uint initialBuffer = multiSegmentInput ? 1024U : 128U * 1024;
        var factory = new BufferWriterFactory(DefaultParquetBufferPool.Shared, chunkSize, initialBuffer, initialBuffer, initialBuffer);
        var context = new CompressionContext(factory);
        var source = factory.CreatePageBufferWriter();
        var destination = factory.CreatePageBufferWriter();
        try
        {
            PopulateSource(ref source, multiSegmentInput ? 48 * 1024 : 32 * 1024);

            for (var i = 0; i < 8; i++)
                Compression.Compress(codec, GetDefaultCompressionLevel(codec), context, ref source, ref destination);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var before = GC.GetAllocatedBytesForCurrentThread();
            Compression.Compress(codec, GetDefaultCompressionLevel(codec), context, ref source, ref destination);
            var after = GC.GetAllocatedBytesForCurrentThread();
            return after - before;
        }
        finally
        {
            source.Dispose();
            destination.Dispose();
            context.Dispose();
        }
    }

    static int GetDefaultCompressionLevel(CompressionKind codec)
        => codec switch
        {
            CompressionKind.Gzip => 1,
            CompressionKind.Zstd => 1,
            CompressionKind.Brotli => 4,
            _ => 0
        };

    static void PopulateSource(ref BufferWriter source, int size)
    {
        var destination = source.GetSpan(size);
        for (var i = 0; i < size; i++)
            destination[i] = (byte)(i * 31 + 17);
        source.Advance(size);
    }
}
