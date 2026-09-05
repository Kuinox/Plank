using System.Runtime.InteropServices;
using Plank.Reading;
using Plank.Reading.Logical;
using Plank.RowApi;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Reading;

internal sealed class ReaderInitializationCleanupTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FailedStreamFactoryClosesStreamAndReturnsBuffers(bool corruptFile)
    {
        using var pool = new TrackingBufferPool();
        using var stream = new MemoryStream(CreateInput(corruptFile));
        var schema = CreateRequestedSchema();

        AssertInitializationFails(() => schema.CreateReader(stream, new ParquetReaderOptions { BufferPool = pool }),
            corruptFile);

        await Assert.That(stream.CanRead).IsFalse();
        await Assert.That(pool.Outstanding).IsEqualTo(0);
        if (!corruptFile)
            await Assert.That(pool.RentCount).IsGreaterThan(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FailedSourceFactoryReturnsBuffersAndPreservesCallerOwnership(bool corruptFile)
    {
        using var pool = new TrackingBufferPool();
        using var source = new TrackingReadSource(CreateInput(corruptFile));
        var schema = CreateRequestedSchema();

        AssertInitializationFails(() => schema.CreateReader(source, new ParquetReaderOptions { BufferPool = pool }),
            corruptFile);

        await Assert.That(source.DisposeCount).IsEqualTo(0);
        await Assert.That(source.Length).IsGreaterThan(0UL);
        await Assert.That(pool.Outstanding).IsEqualTo(0);
        if (!corruptFile)
            await Assert.That(pool.RentCount).IsGreaterThan(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FailedRowReaderConstructionReturnsBuffersAndPreservesCallerOwnership(bool corruptFile)
    {
        using var pool = new TrackingBufferPool();
        using var source = new TrackingReadSource(CreateInput(corruptFile));

        AssertInitializationFails(() => ReaderAllocationRowSchema.CreateRowReader(source,
            options: new RowReaderOptions { BufferPool = pool }), corruptFile);

        await Assert.That(source.DisposeCount).IsEqualTo(0);
        await Assert.That(source.Length).IsGreaterThan(0UL);
        await Assert.That(pool.Outstanding).IsEqualTo(0);
        if (!corruptFile)
            await Assert.That(pool.RentCount).IsGreaterThan(0);
    }

    static void AssertInitializationFails(Func<IDisposable> createReader, bool corruptFile)
    {
        try
        {
            using var reader = createReader();
        }
        catch (CorruptParquetException) when (corruptFile)
        {
            return;
        }
        catch (InvalidOperationException) when (!corruptFile)
        {
            return;
        }

        throw new InvalidOperationException("Expected reader initialization to reject the input.");
    }

    static ParquetSchema CreateRequestedSchema()
        => new([ColumnDefinition.RequiredLeaf("Value", ParquetPhysicalType.Int32)]);

    static byte[] CreateInput(bool corruptFile)
    {
        if (corruptFile)
            return [0, 1, 2, 3];

        var schema = new ParquetSchema([ColumnDefinition.RequiredLeaf("Other", ParquetPhysicalType.Int32)]);
        using var output = new MemoryStream();
        var writer = schema.CreateWriter(output, new ParquetWriterOptions { Compression = CompressionKind.None });
        var column = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);
        column.Serialize([42]);
        writer.StartRowGroup().Write(column);
        writer.CloseFile();
        return output.ToArray();
    }

    sealed class TrackingReadSource(byte[] bytes) : IParquetReadSource
    {
        public int DisposeCount { get; private set; }

        public ulong Length
        {
            get
            {
                ObjectDisposedException.ThrowIf(DisposeCount != 0, this);
                return (ulong)bytes.Length;
            }
        }

        public void ReadExactly(ulong offset, Span<byte> destination)
        {
            ObjectDisposedException.ThrowIf(DisposeCount != 0, this);
            bytes.AsSpan(checked((int)offset), destination.Length).CopyTo(destination);
        }

        public void Dispose() => DisposeCount++;
    }

    sealed class TrackingBufferPool : IParquetBufferPool, IDisposable
    {
        readonly HashSet<nint> _allocations = [];
        public int RentCount { get; private set; }
        public int Outstanding => _allocations.Count;

        public ParquetBuffer Rent(uint minimumByteLength)
        {
            var payloadLength = checked((int)minimumByteLength);
            var allocationLength = checked(payloadLength + 128);
            var allocation = Marshal.AllocHGlobal(allocationLength);
            _allocations.Add(allocation);
            RentCount++;
            return ParquetBuffer.Create(allocation, allocationLength, 128, payloadLength, Return);
        }

        void Return(nint allocation)
        {
            if (!_allocations.Remove(allocation))
                throw new InvalidOperationException("Buffer allocation was returned more than once.");
            Marshal.FreeHGlobal(allocation);
        }

        public void Dispose()
        {
            // Keep a failed regression from leaking unmanaged memory into other tests.
            foreach (var allocation in _allocations)
                Marshal.FreeHGlobal(allocation);
            _allocations.Clear();
        }
    }
}
