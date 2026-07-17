using Plank.Writing;

namespace Plank.Tests.Writing;

[NotInParallel]
internal sealed class ParquetBufferTests
{
    [Test]
    public void GenericRentUsesBytePoolAndProjectsAsTypedSpan()
    {
        var pool = new TrackingBufferPool();
        using var buffer = pool.Rent<int>(17);
        var values = buffer.AsSpan<int>();
        values[16] = 42;

        if (pool.MinimumByteLength != 17U * sizeof(int))
            throw new InvalidOperationException($"Expected 68 requested bytes, got {pool.MinimumByteLength}.");
        if (values.Length < 17 || values[16] != 42)
            throw new InvalidOperationException("The rented buffer was not projected as an int span.");
    }

    [Test]
    public void GenericRentChecksByteLengthOverflow()
    {
        try
        {
            using var _ = DefaultParquetBufferPool.Shared.Rent<long>(uint.MaxValue);
        }
        catch (OverflowException)
        {
            return;
        }

        throw new InvalidOperationException("Expected typed byte-length calculation to overflow.");
    }

    [Test]
    public void RentReturnsAlignedPowerOfTwoStorage()
    {
        using var buffer = DefaultParquetBufferPool.Shared.Rent(17);

        if (buffer.Length != 32)
            throw new InvalidOperationException($"Expected a 32-byte bucket, got {buffer.Length} bytes.");
        if (buffer.DangerousGetAddress() % 64 != 0)
            throw new InvalidOperationException("Expected the payload to be aligned to 64 bytes.");
    }

    [Test]
    public void TypedSpanProjectsTheSameStorage()
    {
        using var buffer = DefaultParquetBufferPool.Shared.Rent(4 * sizeof(int));
        var values = buffer.AsSpan<int>();
        values[0] = 10;
        values[1] = 20;
        values[2] = 30;
        values[3] = 40;

        if (!values.SequenceEqual([10, 20, 30, 40]))
            throw new InvalidOperationException("Typed buffer projection did not preserve values.");
    }

    [Test]
    public void RetainedSliceSurvivesOriginalOwnerDisposal()
    {
        var buffer = DefaultParquetBufferPool.Shared.Rent(64);
        for (var i = 0; i < buffer.Length; i++)
            buffer.Span[i] = (byte)i;

        using var retained = buffer.RetainSlice(8, 16);
        buffer.Dispose();

        for (var i = 0; i < retained.Length; i++)
            if (retained.Span[i] != i + 8)
                throw new InvalidOperationException("Retained slice contents changed after disposing the original owner.");
    }

    [Test]
    public void DisposingTheSameVariableTwiceIsHarmless()
    {
        var buffer = DefaultParquetBufferPool.Shared.Rent(64);
        buffer.Dispose();
        buffer.Dispose();
    }

    [Test]
    public void ReturnedBlockIsReusedFromTheSameBucket()
    {
        var first = DefaultParquetBufferPool.Shared.Rent(64);
        var address = first.DangerousGetAddress();
        first.Dispose();

        using var second = DefaultParquetBufferPool.Shared.Rent(64);
        if (second.DangerousGetAddress() != address)
            throw new InvalidOperationException("Expected the most recently returned bucket block to be reused.");
    }

    [Test]
    public void RetainAndReleaseAreThreadSafe()
    {
        using var buffer = DefaultParquetBufferPool.Shared.Rent(64);
        var address = buffer.DangerousGetAddress();

        Parallel.For(0, 4096, _ =>
        {
            var retained = buffer.Retain();
            try
            {
                if (retained.DangerousGetAddress() != address)
                    throw new InvalidOperationException("Retain changed the payload address.");
            }
            finally
            {
                retained.Dispose();
            }
        });
    }

    [Test]
    public void RetainAndReleaseDoNotAllocateManagedMemory()
    {
        using var buffer = DefaultParquetBufferPool.Shared.Rent(64);
        for (var i = 0; i < 16; i++)
        {
            var warmup = buffer.Retain();
            warmup.Dispose();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1024; i++)
        {
            var retained = buffer.Retain();
            retained.Dispose();
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        if (allocated != 0)
            throw new InvalidOperationException($"Expected retain/release to allocate zero bytes, saw {allocated}.");
    }

    sealed class TrackingBufferPool : IParquetBufferPool
    {
        internal uint MinimumByteLength;

        public ParquetBuffer Rent(uint minimumByteLength)
        {
            MinimumByteLength = minimumByteLength;
            return DefaultParquetBufferPool.Shared.Rent(minimumByteLength);
        }
    }
}
